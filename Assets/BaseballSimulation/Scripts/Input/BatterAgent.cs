using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 한 타석을 한 에피소드로 제어하는 타자 Agent. 공은 자식 레이 센서로만 보고,
    /// CollectObservations의 16값 벡터에는 타자 자신의 상태와 볼카운트·아웃·주자 상황만 넣는다.
    /// 투구마다 <see cref="BatterRewardTracker"/>의 보상을 더하고, 타석이 끝나면 <see cref="TrainingEnvController"/>가
    /// 결과 보상(<see cref="PlayOutcomeRewards.BatterPlateAppearance"/>)과 함께 에피소드를 닫는다.
    /// 관측·행동 계약은 모든 학습 단계에서 같아 이전 단계 모델을 이어 쓸 수 있다.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BehaviorParameters))]
    public sealed class BatterAgent : Agent
    {
        public const int ObservationSize = 16;
        public const int ContinuousActionCount = 7;
        public const int DiscreteActionCount = 1;
        public const string BallTag = "Ball";

        [SerializeField] private PlayDirector director;

        private BatterRewardTracker rewards;
        private bool plateAppearanceOpen;
        private bool pitchActive;
        private bool setupSubmitted;
        private bool swingRequested;
        private float pitchAddedReward;
        private float plateAppearanceAddedReward;
        private bool actedThisPlateAppearance;
        private float lastPlateAppearanceAddedReward;
        private int completedEpisodes;
        private int completedPitches;
        private BatterRewardSnapshot lastPitchReward;
        private float lastPitchAddedReward;
        private float lastOutcomeReward;
        private float lastCompletedCumulativeReward;

        /// <summary>끝난 타석(에피소드) 수.</summary>
        public int CompletedEpisodes => completedEpisodes;
        /// <summary>보상이 확정된 투구 수.</summary>
        public int CompletedPitches => completedPitches;
        /// <summary>마지막으로 확정된 투구의 보상 성분.</summary>
        public BatterRewardSnapshot LastPitchReward => lastPitchReward;
        /// <summary>마지막 투구에서 Agent에 더한 보상 합(보상 계산기 합계와 같아야 한다).</summary>
        public float LastPitchAddedReward => lastPitchAddedReward;
        public float LastOutcomeReward => lastOutcomeReward;
        /// <summary>마지막 타석에서 이 Agent가 한 번이라도 결정했는지. 결정이 없던 타석은 에피소드로 닫지 않는다.</summary>
        public bool LastPlateAppearanceHadDecisions { get; private set; }
        /// <summary>마지막 타석에서 투구 보상으로 더한 합(결과 보상 제외).</summary>
        public float LastPlateAppearanceAddedReward => lastPlateAppearanceAddedReward;
        /// <summary>마지막으로 끝난 타석 에피소드의 누적 보상(투구 보상 + 결과 보상).</summary>
        public float LastCompletedCumulativeReward => lastCompletedCumulativeReward;
        public PlayDirector Director => director;
        /// <summary>이번 투구의 보상이 아직 확정되지 않았으면 true다.</summary>
        public bool PitchActive => pitchActive;
        public bool PlateAppearanceOpen => plateAppearanceOpen;
        /// <summary>이번 투구의 투구 전 자세 결정을 이미 냈으면 true다.</summary>
        public bool SetupSubmitted => setupSubmitted;

        public void AssignDirector(PlayDirector value) => director = value;

        public override void Initialize()
        {
            base.Initialize();
            if (director == null || director.EnvironmentConfig == null)
            {
                Debug.LogError("[BatterAgent] PlayDirector와 설정 에셋 참조가 필요하다.", this);
                enabled = false;
                return;
            }

            var behavior = GetComponent<BehaviorParameters>().BrainParameters;
            if (behavior.VectorObservationSize != ObservationSize ||
                behavior.ActionSpec.NumContinuousActions != ContinuousActionCount ||
                behavior.ActionSpec.NumDiscreteActions != DiscreteActionCount ||
                behavior.ActionSpec.BranchSizes[0] != 2)
            {
                Debug.LogError($"[BatterAgent] Behavior Parameters는 관측 {ObservationSize}, 연속 행동 7, 이산 분기 [2]여야 한다.", this);
                enabled = false;
                return;
            }

            RayPerceptionSensorComponent3D eye = GetComponentInChildren<RayPerceptionSensorComponent3D>();
            if (eye == null || !eye.DetectableTags.Contains(BallTag))
            {
                Debug.LogError($"[BatterAgent] 공을 볼 자식 레이 센서(감지 태그 '{BallTag}')가 필요하다.", this);
                enabled = false;
                return;
            }

            rewards = new BatterRewardTracker(director);
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
            pitchAddedReward = 0f;
            setupSubmitted = false;
            swingRequested = false;
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

        /// <summary>플레이가 중단되면 컨트롤러가 호출한다. 타석 에피소드를 중단으로 닫는다.</summary>
        public void AbortPlay()
        {
            if (!plateAppearanceOpen) return;
            plateAppearanceOpen = pitchActive = false;
            if (actedThisPlateAppearance) EpisodeInterrupted();
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            PitchSnapshot pitch = director != null ? director.GetSnapshot() : default;
            BattingEvaluation batting = director != null ? director.GetBattingEvaluation() : default;
            BaseballEnvironmentConfig config = director != null ? director.EnvironmentConfig : null;
            SituationSnapshot count = director != null ? director.GetSituation() : default;
            Vector2 stance = batting.Setup.StanceOffset;
            Vector3 grip = batting.Setup.GripOffset;
            Vector3 limits = config != null ? config.GripOffsetLimits : Vector3.one;
            float stanceLimit = config != null ? config.StanceOffsetLimit : 1f;

            // 공 위치·속도는 넣지 않는다. 공은 자식 레이 센서로만 관측한다.
            sensor.AddObservation(pitchActive && pitch.State == PlayState.Ready && !setupSubmitted ? 1f : 0f);
            sensor.AddObservation(pitchActive && pitch.State == PlayState.PitchInFlight ? 1f : 0f);
            sensor.AddObservation(Mathf.Clamp01(pitch.ElapsedSeconds / 2f));
            sensor.AddObservation(Normalize(stance.x, stanceLimit));
            sensor.AddObservation(Normalize(stance.y, stanceLimit));
            sensor.AddObservation(Normalize(grip.x, limits.x));
            sensor.AddObservation(Normalize(grip.y, limits.y));
            sensor.AddObservation(Normalize(grip.z, limits.z));
            sensor.AddObservation(batting.HasSwung ? 1f : 0f);
            sensor.AddObservation(batting.HasContact ? 1f : 0f);
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
            if (!pitchActive || director == null) return;
            var continuous = actions.ContinuousActions;
            if (!setupSubmitted)
            {
                if (director.State != PlayState.Ready) return;
                BaseballEnvironmentConfig config = director.EnvironmentConfig;
                Vector3 gripLimit = config.GripOffsetLimits;
                float stanceLimit = config.StanceOffsetLimit;
                director.RequestBatterSetup(new BatterSetupCommand(
                    new Vector2(Clamped(continuous[0]) * stanceLimit, Clamped(continuous[1]) * stanceLimit),
                    new Vector3(Clamped(continuous[2]) * gripLimit.x,
                        Clamped(continuous[3]) * gripLimit.y, Clamped(continuous[4]) * gripLimit.z)));
                setupSubmitted = true;
                return;
            }

            if (director.State != PlayState.PitchInFlight || swingRequested || director.HasSwung ||
                actions.DiscreteActions[0] != 1) return;
            director.RequestSwing(new SwingCommand(45f * Clamped(continuous[5]),
                10f + 40f * Clamped(continuous[6])));
            swingRequested = true;
        }

        /// <summary>
        /// 학습기·모델이 없을 때의 중립 행동이다. 기준 자세로 서서 스윙하지 않는다.
        /// 기본 구현은 매 결정마다 경고를 남기므로 재정의만 해 둔다.
        /// </summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            actionsOut.Clear();
        }

        private void OnRewardAdded(float amount)
        {
            if (!pitchActive) return;
            AddReward(amount);
            pitchAddedReward += amount;
            plateAppearanceAddedReward += amount;
        }

        private void OnPitchCompleted(BatterRewardSnapshot snapshot)
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
