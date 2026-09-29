using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 한 타석을 한 에피소드로 투구를 고르는 투수 Agent(docs/pitcher-agent.md). 매 투구 타자가 자세를 잡은 뒤
    /// <see cref="TrainingEnvController"/>가 한 번 결정을 요청하고, 구종·구속·목표 위치를
    /// <see cref="PlayDirector.RequestThrowPitch(PitchCommand)"/>로 전달한다. 투구 보상은 <see cref="PitcherRewardTracker"/>,
    /// 타석 결과 보상은 <see cref="PlayOutcomeRewards.PitcherPlateAppearance"/>가 정한다.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BehaviorParameters))]
    public sealed class PitcherAgent : Agent
    {
        public const int ObservationSize = 11;
        public const int ContinuousActionCount = 1;
        public const int PitchTypeCount = 5;
        /// <summary>
        /// 목표 위치 격자의 좌우 칸·높이 칸 수. 칸 간격은 존 폭·높이의 1/3이라 가운데 3×3은 존을 3등분한 칸의 중앙이고,
        /// 바깥 테두리 16칸은 존 밖(볼)이다. 연속 좌표는 평균이 행동 경계 밖으로 포화되면 모든 투구가 같은 모서리 볼이 되어
        /// 되돌아올 신호가 사라지므로(2단계 첫 학습) 이산 칸으로 고른다.
        /// </summary>
        public const int LocationCells = 5;
        /// <summary>격자 가운데 칸(존 중앙). 중립 행동이다.</summary>
        public const int CenterCell = LocationCells / 2;
        /// <summary>
        /// 맨 윗줄 바깥 칸만 한 칸 간격보다 더 올리는 높이(m). 내려오며 들어오는 공은 플레이트 뒤쪽에서 입체 존 윗면에 닿으므로,
        /// 앞 모서리 여유만으로는 느린 변화구가 스트라이크가 된다. 실측(2026-09-29) 최악은 커브 110 km/h 가운데 열로
        /// 목표 1.152 m부터 볼이었다. 기본 설정의 윗줄 1.122 m에 여유 2 cm를 더해 5 cm 올린다.
        /// </summary>
        public const float TopRowLift = 0.05f;

        [SerializeField] private PlayDirector director;

        private PitcherRewardTracker rewards;
        private bool plateAppearanceOpen;
        private bool pitchActive;
        private bool pitchSubmitted;
        private float pitchAddedReward;
        private float plateAppearanceAddedReward;
        private bool actedThisPlateAppearance;
        private float lastPlateAppearanceAddedReward;
        private int completedEpisodes;
        private int completedPitches;
        private PitcherRewardSnapshot lastPitchReward;
        private float lastPitchAddedReward;
        private float lastOutcomeReward;
        private float lastCompletedCumulativeReward;

        public PlayDirector Director => director;
        public bool PitchActive => pitchActive;
        public bool PlateAppearanceOpen => plateAppearanceOpen;
        public bool PitchSubmitted => pitchSubmitted;
        public int CompletedEpisodes => completedEpisodes;
        public int CompletedPitches => completedPitches;
        public PitcherRewardSnapshot LastPitchReward => lastPitchReward;
        public float LastPitchAddedReward => lastPitchAddedReward;
        public float LastOutcomeReward => lastOutcomeReward;
        /// <summary>마지막 타석에서 이 Agent가 한 번이라도 결정했는지. 결정이 없던 타석은 에피소드로 닫지 않는다.</summary>
        public bool LastPlateAppearanceHadDecisions { get; private set; }
        /// <summary>마지막 타석에서 투구 보상으로 더한 합(결과 보상 제외).</summary>
        public float LastPlateAppearanceAddedReward => lastPlateAppearanceAddedReward;
        public float LastCompletedCumulativeReward => lastCompletedCumulativeReward;

        public void AssignDirector(PlayDirector value) => director = value;

        public override void Initialize()
        {
            base.Initialize();
            if (director == null || director.EnvironmentConfig == null)
            {
                Debug.LogError("[PitcherAgent] PlayDirector와 설정 에셋 참조가 필요하다.", this);
                enabled = false;
                return;
            }
            var brain = GetComponent<BehaviorParameters>().BrainParameters;
            if (brain.VectorObservationSize != ObservationSize ||
                brain.ActionSpec.NumContinuousActions != ContinuousActionCount ||
                brain.ActionSpec.NumDiscreteActions != 3 || brain.ActionSpec.BranchSizes[0] != PitchTypeCount ||
                brain.ActionSpec.BranchSizes[1] != LocationCells || brain.ActionSpec.BranchSizes[2] != LocationCells)
            {
                Debug.LogError($"[PitcherAgent] Behavior Parameters는 관측 {ObservationSize}, 연속 행동 {ContinuousActionCount}, " +
                    $"이산 분기 [{PitchTypeCount}, {LocationCells}, {LocationCells}]여야 한다.", this);
                enabled = false;
                return;
            }
            rewards = new PitcherRewardTracker(director);
            rewards.RewardAdded += OnRewardAdded;
            rewards.EpisodeCompleted += OnPitchCompleted;
        }

        /// <summary>컨트롤러가 PlayReset 직후(매 투구) 호출한다. 타석이 닫혀 있으면 새 타석 에피소드를 연다.</summary>
        public void BeginPitch()
        {
            if (rewards == null) return;
            if (!plateAppearanceOpen)
            {
                plateAppearanceAddedReward = 0f;
                actedThisPlateAppearance = false;
            }
            plateAppearanceOpen = true;
            rewards.BeginEpisode();
            pitchActive = true;
            pitchSubmitted = false;
            pitchAddedReward = 0f;
        }

        /// <summary>타석이 끝나면 컨트롤러가 결과 보상을 주고 에피소드를 닫는다.</summary>
        public void EndPlateAppearance(float outcomeReward)
        {
            if (!plateAppearanceOpen) return;
            lastOutcomeReward = outcomeReward;
            lastPlateAppearanceAddedReward = plateAppearanceAddedReward;
            LastPlateAppearanceHadDecisions = actedThisPlateAppearance;
            plateAppearanceOpen = pitchActive = false;
            // 결정 없이 끝난 타석(검증용 시나리오 타구)은 에피소드가 아니다. ML-Agents는 결정 없이 연속으로 부른 EndEpisode를 무시하고 누적 보상을 이어 가므로 닫지 않는다.
            if (!actedThisPlateAppearance) return;
            AddReward(outcomeReward);
            lastCompletedCumulativeReward = GetCumulativeReward();
            completedEpisodes++;
            EndEpisode();
        }

        /// <summary>플레이가 중단되면 컨트롤러가 호출한다.</summary>
        public void AbortPlay()
        {
            if (!plateAppearanceOpen) return;
            plateAppearanceOpen = pitchActive = false;
            if (actedThisPlateAppearance) EpisodeInterrupted();
        }

        /// <summary>투수가 볼 수 있는 타자 자세(발 위치 2 + 손잡이 3)와 볼카운트·아웃·주자(6)를 넣는다.</summary>
        public override void CollectObservations(VectorSensor sensor)
        {
            BattingEvaluation batting = director != null ? director.GetBattingEvaluation() : default;
            BaseballEnvironmentConfig config = director != null ? director.EnvironmentConfig : null;
            SituationSnapshot count = director != null ? director.GetSituation() : default;
            Vector2 stance = batting.Setup.StanceOffset;
            Vector3 grip = batting.Setup.GripOffset;
            Vector3 limits = config != null ? config.GripOffsetLimits : Vector3.one;
            float stanceLimit = config != null ? config.StanceOffsetLimit : 1f;
            sensor.AddObservation(Normalize(stance.x, stanceLimit));
            sensor.AddObservation(Normalize(stance.y, stanceLimit));
            sensor.AddObservation(Normalize(grip.x, limits.x));
            sensor.AddObservation(Normalize(grip.y, limits.y));
            sensor.AddObservation(Normalize(grip.z, limits.z));
            sensor.AddObservation(Mathf.Clamp01(count.Balls / 3f));
            sensor.AddObservation(Mathf.Clamp01(count.Strikes / 2f));
            sensor.AddObservation(Mathf.Clamp01(count.Outs / 2f));
            sensor.AddObservation(count.OnFirst ? 1f : 0f);
            sensor.AddObservation(count.OnSecond ? 1f : 0f);
            sensor.AddObservation(count.OnThird ? 1f : 0f);
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            actedThisPlateAppearance = true;
            if (!pitchActive || pitchSubmitted || director == null || director.State != PlayState.Ready) return;
            director.RequestThrowPitch(ToCommand(actions, director.EnvironmentConfig, director.StrikeZoneCenter));
            pitchSubmitted = true;
        }

        /// <summary>
        /// 행동을 투구 명령으로 바꾼다. 이산 0 = 구종, 이산 1 = 좌우 칸(0 3루 쪽 … 4 1루 쪽), 이산 2 = 높이 칸(0 아래 … 4 위),
        /// 연속 0 = 구종 구속 범위(-1 최소, 1 최대). 비유한 값은 0, 범위 밖 값은 경계로 본다.
        /// </summary>
        public static PitchCommand ToCommand(ActionBuffers actions, BaseballEnvironmentConfig config, Vector2 zoneCenter)
        {
            var discrete = actions.DiscreteActions;
            var type = (PitchType)Mathf.Clamp(discrete[0], 0, PitchTypeCount - 1);
            PitchTypeProfile profile = config.GetPitchProfile(type);
            float speed = Mathf.Lerp(profile.MinSpeed, profile.MaxSpeed, 0.5f * (Clamped(actions.ContinuousActions[0]) + 1f));
            return new PitchCommand(type, speed, CellLocation(discrete[1], discrete[2], config, zoneCenter));
        }

        /// <summary>
        /// 격자 칸의 홈플레이트 목표 위치. 칸 간격은 플레이트 폭·존 높이의 1/3이다. 가운데 칸이 존 중앙이고,
        /// 바깥 칸은 존 가장자리 칸 중앙에서 한 칸 더 나간 곳이다(기본 설정에서 공 가장자리가 좌우 3.5 cm, 아래 5.5 cm 존 밖).
        /// 맨 윗줄은 <see cref="TopRowLift"/>만큼 더 올라가 공 가장자리가 앞 모서리에서 10.5 cm 존 위다.
        /// </summary>
        public static Vector2 CellLocation(int column, int row, BaseballEnvironmentConfig config, Vector2 zoneCenter)
        {
            int x = Mathf.Clamp(column, 0, LocationCells - 1) - CenterCell;
            int y = Mathf.Clamp(row, 0, LocationCells - 1) - CenterCell;
            float height = zoneCenter.y + y * (config.StrikeZoneTop - config.StrikeZoneBottom) / 3f;
            if (y == CenterCell) height += TopRowLift;
            return new Vector2(x * StrikeZone.PlateWidth / 3f, height);
        }

        /// <summary>학습기·모델이 없을 때의 중립 행동: 포심 직구를 구속 범위 가운데로 존 중앙에 던진다.</summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            actionsOut.Clear();
            var discrete = actionsOut.DiscreteActions;
            discrete[1] = CenterCell;
            discrete[2] = CenterCell;
        }

        private void OnRewardAdded(float amount)
        {
            if (!pitchActive) return;
            AddReward(amount);
            pitchAddedReward += amount;
            plateAppearanceAddedReward += amount;
        }

        private void OnPitchCompleted(PitcherRewardSnapshot snapshot)
        {
            if (!pitchActive) return;
            pitchActive = false;
            lastPitchReward = snapshot;
            lastPitchAddedReward = pitchAddedReward;
            completedPitches++;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (rewards == null) return;
            rewards.RewardAdded -= OnRewardAdded;
            rewards.EpisodeCompleted -= OnPitchCompleted;
            rewards.Dispose();
            rewards = null;
        }

        private static float Clamped(float value) => float.IsNaN(value) || float.IsInfinity(value)
            ? 0f : Mathf.Clamp(value, -1f, 1f);

        private static float Normalize(float value, float limit) => limit > 0f
            ? Mathf.Clamp(value / limit, -1f, 1f) : 0f;
    }
}
