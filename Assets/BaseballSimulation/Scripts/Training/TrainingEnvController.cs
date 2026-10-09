using Unity.InferenceEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>한 타석의 상대(docs/training-curriculum.md "고정 상대 평가"). Live 외에는 한쪽을 고정한 평가 타석이다.</summary>
    public enum PlateAppearanceOpponent
    {
        /// <summary>타자 Agent와 투수 Agent(1단계는 스크립트 투수)가 대결한다.</summary>
        Live,
        /// <summary>기준 스크립트 투수(<see cref="BenchmarkPitcher"/>)가 던진다. 투수 Agent는 결정하지 않는다.</summary>
        ScriptedPitcher,
        /// <summary>타자가 고정 타자 모델로 추론한다. 이 타석의 타자 데이터는 학습기로 가지 않는다.</summary>
        FrozenBatter,
    }

    /// <summary>Opt-in bootstrap: timing, then height, angles, and position. Phase 9 matches preparation phase 0.</summary>
    public static class BatterSkills
    {
        public const int PhaseCount = 10;
        public static float HeightScale(int phase) => phase <= 0 ? 0f : phase == 1 ? 0.25f : phase == 2 ? 0.5f : 1f;
        public static float AngleScale(int phase) => phase < 4 ? 0f : phase == 4 ? 0.25f : phase == 5 ? 0.5f : 1f;
        public static float PositionScale(int phase) => phase < 7 ? 0f : phase == 7 ? 0.25f : phase == 8 ? 0.5f : 1f;
        public static Vector2 LocationSpread(int phase) => new Vector2(0.07f * PositionScale(phase), 0.08f * HeightScale(phase));
        public static float GripHeight(float action, float limit, float centerHeight, int phase)
        {
            float height = HeightScale(phase);
            float center = Mathf.Clamp(centerHeight, -limit, limit);
            // Start at the physically verified centered lesson-1 pose. Move its
            // neutral height gradually to the existing preparation anchor.
            return BatterPreparation.GripHeight(action * height, limit, centerHeight, 0.5f) +
                (1f - height) * 0.5f * center;
        }
    }

    /// <summary>Saved lesson-2 compatible preparation controls and script-pitch sampling.</summary>
    public static class BatterPreparation
    {
        public const int PhaseCount = 16;
        public static float SwingGate(int phase) => phase < 6 ? Mathf.Max(0f, 0.30f - 0.05f * phase) : 0f;
        public static float ControlScale(int phase) => phase <= 6 ? 0.5f : phase <= 10 ? 0.5f + 0.125f * (phase - 6) : 1f;
        public static Vector2 LocationSpread(int phase) => phase < 11 ? new Vector2(0.07f, 0.08f)
            : phase == 11 ? new Vector2(0.15f, 0.17f) : BenchmarkPitcher.LocationSpread;

        // Exactly matches the saved lesson-2 mapping at scale .5. Unlock both legal
        // endpoints around that same neutral height; zero action never moves the bat.
        public static float GripHeight(float action, float limit, float centerHeight, float scale)
        {
            float anchor = 0.5f * Mathf.Clamp(centerHeight, -limit, limit);
            float t = Mathf.Clamp01(2f * scale - 1f);
            float endpoint = action >= 0f ? Mathf.Lerp(anchor + 0.5f * limit, limit, t)
                : Mathf.Lerp(anchor - 0.5f * limit, -limit, t);
            return Mathf.Lerp(anchor, endpoint, Mathf.Abs(Mathf.Clamp(action, -1f, 1f)));
        }

        public static PitchCommand SamplePitch(int phase, System.Random random, BaseballEnvironmentConfig config,
            Vector2 center, Vector2 straightSpeedRangeKmh, int skillPhase = -1)
        {
            bool easy = phase >= 11 && random.NextDouble() < 0.25;
            float mix = phase < 13 ? 0f : phase == 13 ? 0.25f : phase == 14 ? 0.5f : 1f;
            if (!easy && mix > 0f && random.NextDouble() < mix)
                return BenchmarkPitcher.Next(random, config, center);
            float kmh = Mathf.Lerp(straightSpeedRangeKmh.x, straightSpeedRangeKmh.y, (float)random.NextDouble());
            Vector2 aim = BenchmarkPitcher.SampleLocation(random, config, center, skillPhase >= 0
                ? BatterSkills.LocationSpread(skillPhase) : easy ? Vector2.zero : LocationSpread(phase));
            return new PitchCommand(PitchType.FourSeam, kmh / 3.6f, aim);
        }
    }

    /// <summary>
    /// 한 학습 씬의 투구 순서와 에피소드 경계를 맡는다(docs/training-curriculum.md).
    /// Academy 한 단계 직전(AgentPreStep)에 PlayDirector 상태를 보고 필요한 Agent에게만 결정을 요청한다.
    /// 1) Ready: 타자 자세 결정 → 투구(1단계 스크립트 투수, 2단계부터 투수 Agent) 2) PitchInFlight: 스윙 전까지 타자 결정
    /// 3) BattedBallInFlight(3단계): 수비 5명·살아 있는 주자 결정 4) 투구가 끝나면 초기화한다.
    /// 타자·투수 에피소드는 한 타석이고, 타석이 끝날 때 결과 보상을 주고 닫는다. 수비·주자 그룹 에피소드는 한 인플레이 플레이다.
    /// 2단계부터 새 타석의 일부는 고정 상대 평가 타석(<see cref="PlateAppearanceOpponent"/>)이다.
    /// 경기 규칙·볼카운트는 PlayDirector가, 보상 규칙은 각 보상 계산기와 PlayOutcomeRewards가 정한다.
    /// 투구·타석·플레이가 끝날 때 <see cref="TrainingStats"/>로 TensorBoard 지표를 보낸다.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TrainingEnvController : MonoBehaviour
    {
        [SerializeField] private TrainingStage stage = TrainingStage.Batter;
        [SerializeField] private PlayDirector director;
        [SerializeField] private BatterAgent batter;
        [Tooltip("2단계부터 투구를 고르는 투수 Agent. 1단계에서는 비워 둔다.")]
        [SerializeField] private PitcherAgent pitcher;
        [Tooltip("3단계 주자 Agent. 0 타자주자, 1~3 누상 주자 슬롯 순서다.")]
        [SerializeField] private RunnerAgent[] runners = new RunnerAgent[0];
        [Tooltip("3단계 수비수 5명. PlayDirector 수비수 목록과 같은 순서다.")]
        [SerializeField] private FielderAgent[] fielders = new FielderAgent[0];

        [Header("1단계 스크립트 투수")]
        [Tooltip("포심 직구의 구속 범위(km/h). 투구마다 균등 분포로 뽑는다.")]
        [SerializeField] private Vector2 scriptedSpeedRangeKmh = new Vector2(120f, 150f);
        [Tooltip("존 중앙 기준 목표 위치 표준편차(m, 좌우·높이). 기본값은 기준 투수와 같아 약 절반이 볼이다. 0이면 항상 존 중앙이다.")]
        [SerializeField] private Vector2 scriptedLocationSpread = BenchmarkPitcher.LocationSpread;
        [Tooltip("스크립트 투수 구속·위치 난수원의 시드.")]
        [SerializeField] private int scriptedPitchSeed = 777;

        [Header("3단계")]
        [Tooltip("타구가 살아 있는 동안 수비·주자가 새 결정을 내리는 고정 단계 간격. 사이 단계는 마지막 행동을 반복한다.")]
        [SerializeField, Min(1)] private int fieldDecisionInterval = 5;
        [Tooltip("새 타석을 시작할 때 누상 주자·아웃을 무작위로 정할 확률. 0이면 앞 타석 결과를 그대로 이어 간다.")]
        [SerializeField, Range(0f, 1f)] private float randomSituationProbability = 0.3f;
        [SerializeField] private int situationSeed = 4242;

        [Header("고정 상대 평가 (2단계부터)")]
        [Tooltip("새 타석을 기준 스크립트 투수가 던질 확률. 이 타석에서 투수 Agent는 결정하지 않고, 타자 성적은 'Benchmark Batter/' 지표로 따로 기록한다.")]
        [SerializeField, Range(0f, 1f)] private float scriptedPitcherBenchmarkProbability = 0.1f;
        [Tooltip("새 타석을 고정 타자 모델이 추론으로 칠 확률. 이 타석의 타자 데이터는 학습기로 가지 않고, 투수 성적은 'Benchmark Pitcher/' 지표로 따로 기록한다. 모델이 없으면 쓰지 않는다.")]
        [SerializeField, Range(0f, 1f)] private float frozenBatterBenchmarkProbability = 0.1f;
        [Tooltip("고정 상대 평가 타석의 타자 모델(ONNX). 타자 Agent와 관측·행동 계약이 같아야 한다.")]
        [SerializeField] private ModelAsset benchmarkBatterModel;
        [Tooltip("평가 타석 선택과 기준 스크립트 투수의 구종·구속·위치 난수원의 시드.")]
        [SerializeField] private int benchmarkSeed = 9091;

        [Header("병렬 경기장")]
        [Tooltip("한 씬에 경기장을 여러 개 복제했을 때의 번호(0부터). 스크립트 투수·상황·투구 위치·스윙 파워 난수원 시드를 경기장마다 다르게 한다. 0은 단일 경기장과 같다.")]
        [SerializeField, Min(0)] private int arenaIndex;
        private const int ArenaSeedStride = 7919;

        [Header("안전 장치")]
        [Tooltip("한 투구가 이 고정 단계 수를 넘기면 모든 에피소드를 중단하고 초기화한다.")]
        [SerializeField, Min(50)] private int maxPlaySteps = 1500;

        private System.Random scriptedRandom;
        private int batterLessonOverride = -1;
        private int batterLesson = -1;
        private int preparationOverride = -1;
        private int preparationPhase = -1;
        private int skillPhase = -1;
        private int skillOverride = -1;
        private System.Random situationRandom;
        private System.Random benchmarkRandom;
        private readonly TrainingStats stats = new TrainingStats();
        private PlateAppearanceOpponent opponent;
        private BehaviorParameters batterBehavior;
        private bool batterSwapped;
        private BehaviorType savedBatterType;
        private ModelAsset savedBatterModel;
        private SimpleMultiAgentGroup defense;
        private SimpleMultiAgentGroup offense;
        private bool subscribed;
        private bool playActive;
        private bool resetPending;
        private bool pitchRequested;
        private bool fieldersActed;
        private bool runnersActed;
        private bool situationPending;
        private bool situationSettling;
        private int pitchWaitSteps;
        private int playSteps;
        private int fieldSteps;
        private int completedPlays;
        private int abortedPlays;
        private int plateAppearancePitches;
        private float defensePotential;
        private float defenseShaping;
        // 수비수별 개인 쫓기·베이스 커버·자리 유지·포구 보상. 인덱스는 fielders 배열 순서다.
        private float[] chasePotential = new float[0];
        private float[] chaseShaping = new float[0];
        private float[] lastChaseShaping = new float[0];
        private float[] coverPotential = new float[0];
        private float[] coverShaping = new float[0];
        private float[] lastCoverShaping = new float[0];
        private float[] positionReward = new float[0];
        private float[] lastPositionReward = new float[0];
        private float[] fieldingReward = new float[0];
        private float[] lastFieldingReward = new float[0];
        private bool fieldingPaid;
        // 베이스별 커버 지표. 인덱스는 BaseId(홈 0, 1·2·3루)다.
        private readonly bool[] baseInPlay = new bool[4];
        private readonly bool[] baseInPlayPrevious = new bool[4];
        private readonly bool[] baseCovered = new bool[4];

        public TrainingStage Stage => stage;
        public PlayDirector Director => director;
        public BatterAgent Batter => batter;
        public PitcherAgent Pitcher => pitcher;
        public RunnerAgent[] Runners => runners;
        public FielderAgent[] Fielders => fielders;
        public Vector2 ScriptedSpeedRangeKmh => scriptedSpeedRangeKmh;
        public Vector2 ScriptedLocationSpread => scriptedLocationSpread;
        /// <summary>정상 종료 또는 중단으로 초기화한 투구(플레이) 수.</summary>
        public int CompletedPlays => completedPlays;
        public int AbortedPlays => abortedPlays;
        /// <summary>TensorBoard로 보내는 야구 지표. 학습기 없이도 키별 마지막 값을 읽을 수 있다.</summary>
        public TrainingStats Stats => stats;
        /// <summary>PlayReset 뒤 초기화 요청 전까지 true다.</summary>
        public bool PlayActive => playActive;
        /// <summary>새 타석의 무작위 상황을 적용하는 중이면 true다(첫 결정 전).</summary>
        public bool SettingSituation => situationPending || situationSettling;
        /// <summary>마지막으로 끝난 투구·플레이의 결과(검증·표시용).</summary>
        public PitchEndReason LastEndReason { get; private set; }
        public bool LastPlayHadFielding { get; private set; }
        public PlaySummary LastPlaySummary { get; private set; }
        public float LastDefenseOutcomeReward { get; private set; }
        /// <summary>마지막 수비 플레이에서 준 보조 보상의 합(<see cref="PlayOutcomeRewards.DefensePotential"/>).</summary>
        public float LastDefenseShapingReward { get; private set; }
        /// <summary>마지막 수비 플레이의 개인 쫓기 보상 총합. 그룹 보상에는 들어가지 않는다.</summary>
        public float LastChaseShapingReward { get; private set; }
        /// <summary>마지막 수비 플레이에서 그 수비수(fielders 배열 순서)에게 준 개인 쫓기 보상 합.</summary>
        public float GetLastChaseShapingReward(int index) => index >= 0 && index < lastChaseShaping.Length ? lastChaseShaping[index] : 0f;
        /// <summary>마지막 수비 플레이에서 그 수비수(fielders 배열 순서)에게 준 베이스 커버 개인 보조 보상의 합(<see cref="PlayOutcomeRewards.CoverPotential"/>).</summary>
        public float GetLastCoverShapingReward(int index) => index >= 0 && index < lastCoverShaping.Length ? lastCoverShaping[index] : 0f;
        /// <summary>마지막 플레이의 자리 이탈 개인 감점 총합(<see cref="PlayOutcomeRewards.PositionReward"/>). 포텐셜 정산과 별도다.</summary>
        public float LastPositionReward { get; private set; }
        public float GetLastPositionReward(int index) => index >= 0 && index < lastPositionReward.Length ? lastPositionReward[index] : 0f;
        /// <summary>마지막 플레이의 개인 포구 보상 총합(<see cref="PlayOutcomeRewards.FieldingReward"/>, 첫 포구 수비수 한 명).</summary>
        public float LastFieldingReward { get; private set; }
        public float GetLastFieldingReward(int index) => index >= 0 && index < lastFieldingReward.Length ? lastFieldingReward[index] : 0f;
        public float LastRunnerOutcomeReward { get; private set; }
        /// <summary>수비·주자가 새 결정을 내리는 고정 단계 간격.</summary>
        // Serialized step limits were configured at 50 Hz. Preserve their duration at 100 Hz.
        private int ScaledSteps(int steps) => Mathf.Max(1, Mathf.RoundToInt(steps * 0.02f / Time.fixedDeltaTime));
        public int FieldDecisionInterval => ScaledSteps(fieldDecisionInterval);
        public PlateAppearanceResult LastPlateAppearanceResult { get; private set; }
        public float LastBatterOutcomeReward { get; private set; }
        /// <summary>이번 타석의 상대. 새 타석을 시작할 때 정한다.</summary>
        public PlateAppearanceOpponent Opponent => opponent;
        /// <summary>마지막으로 끝난 타석의 상대(검증·표시용).</summary>
        public PlateAppearanceOpponent LastPlateAppearanceOpponent { get; private set; }
        public ModelAsset BenchmarkBatterModel => benchmarkBatterModel;
        public float ScriptedPitcherBenchmarkProbability => scriptedPitcherBenchmarkProbability;
        public float FrozenBatterBenchmarkProbability => frozenBatterBenchmarkProbability;
        public int BatterLesson => batterLesson;
        public int BatterLessonOverride => batterLessonOverride;
        public int PreparationPhase => preparationPhase;
        public int PreparationOverride => preparationOverride;
        public int SkillPhase => skillPhase;
        public void SetSkillOverride(int phase) => skillOverride = Mathf.Clamp(phase, -1, BatterSkills.PhaseCount - 1);
        public void SetPreparationOverride(int phase) => preparationOverride = Mathf.Clamp(phase, -1, BatterPreparation.PhaseCount - 1);
        /// <summary>검증용. -1이면 Python 환경 파라미터를 읽는다. 변경은 다음 타석부터 적용한다.</summary>
        public void SetBatterLessonOverride(int value) => batterLessonOverride = Mathf.Clamp(value, -1, 3);

        private bool UsesPitcherAgent => stage >= TrainingStage.BatterPitcher;
        private bool UsesFielding => stage >= TrainingStage.FullTeam;

        public void Assign(TrainingStage value, PlayDirector playDirector, BatterAgent batterAgent, PitcherAgent pitcherAgent = null,
            RunnerAgent[] runnerAgents = null, FielderAgent[] fielderAgents = null)
        {
            stage = value;
            director = playDirector;
            batter = batterAgent;
            pitcher = pitcherAgent;
            runners = runnerAgents ?? new RunnerAgent[0];
            fielders = fielderAgents ?? new FielderAgent[0];
        }

        /// <summary>새 타석 무작위 상황 확률을 바꾼다(검증에서 결정적으로 진행할 때 0으로 둔다).</summary>
        public void SetRandomSituationProbability(float value) => randomSituationProbability = Mathf.Clamp01(value);

        /// <summary>새 타석의 고정 상대 평가 확률을 바꾼다. 진행 중인 타석은 그대로다(검증에서 0 또는 1로 둔다).</summary>
        public void SetBenchmarkProbabilities(float scriptedPitcher, float frozenBatter)
        {
            scriptedPitcherBenchmarkProbability = Mathf.Clamp01(scriptedPitcher);
            frozenBatterBenchmarkProbability = Mathf.Clamp01(frozenBatter);
        }

        public void AssignBenchmarkBatterModel(ModelAsset model) => benchmarkBatterModel = model;

        /// <summary>1단계 스크립트 투수의 위치 표준편차를 바꾼다. 다음 투구부터 적용된다(검증에서 존 중앙으로만 던질 때 0으로 둔다).</summary>
        public void SetScriptedLocationSpread(Vector2 value) => scriptedLocationSpread = Vector2.Max(value, Vector2.zero);

        public int ArenaIndex => arenaIndex;
        public void SetArenaIndex(int value) => arenaIndex = Mathf.Max(0, value);

        /// <summary>
        /// 경기장별 시드. 0번은 원래 시드 그대로다. 나머지는 해시로 섞는다. System.Random은 시드가 일정 간격이면
        /// 첫 값들이 거의 일직선으로 나와(검증에서 139.5/142.1/144.7/147.3 km/h) 경기장끼리 상관된 난수가 되기 때문이다.
        /// </summary>
        private int ArenaSeed(int baseSeed)
        {
            if (arenaIndex == 0) return baseSeed;
            unchecked
            {
                uint x = (uint)baseSeed + (uint)arenaIndex * (uint)ArenaSeedStride * 0x9E3779B9u;
                x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16;
                return (int)(x & 0x7FFFFFFF);
            }
        }

        private void Awake()
        {
            scriptedRandom = new System.Random(ArenaSeed(scriptedPitchSeed));
            situationRandom = new System.Random(ArenaSeed(situationSeed));
            benchmarkRandom = new System.Random(ArenaSeed(benchmarkSeed));
        }

        private void OnEnable()
        {
            if (director == null || batter == null)
            {
                Debug.LogError("[TrainingEnvController] PlayDirector와 BatterAgent 참조가 필요하다.", this);
                enabled = false;
                return;
            }
            if (UsesPitcherAgent && pitcher == null)
            {
                Debug.LogError($"[TrainingEnvController] {stage} 단계는 PitcherAgent 참조가 필요하다.", this);
                enabled = false;
                return;
            }
            if (UsesFielding && (runners == null || runners.Length != director.RunnerSlotCount || runners.Length == 0 ||
                fielders == null || fielders.Length != (director.SimplifiedFielding ? 1 : director.FielderCount) || fielders.Length == 0))
            {
                Debug.LogError($"[TrainingEnvController] 주자 슬롯과 수비 Agent 연결이 필요하다(단순 수비는 중견수 Agent 1개).", this);
                enabled = false;
                return;
            }
            if (UsesFielding && director.SimplifiedFielding &&
                (director.FielderCount != 5 || fielders[0] == null || fielders[0].Director != director ||
                 fielders[0].FielderIndex != director.FindFielder(FielderRole.CenterField)))
            {
                Debug.LogError("[TrainingEnvController] 단순 수비는 몸 5개와 같은 경기장의 중견수 Agent 1개가 필요하다.", this);
                enabled = false;
                return;
            }
            if (scriptedSpeedRangeKmh.x <= 0f || scriptedSpeedRangeKmh.x > scriptedSpeedRangeKmh.y)
            {
                Debug.LogError("[TrainingEnvController] 스크립트 투수 구속 범위는 0 < 최소 <= 최대여야 한다.", this);
                enabled = false;
                return;
            }
            if (!(scriptedLocationSpread.x >= 0f && scriptedLocationSpread.y >= 0f) ||
                float.IsInfinity(scriptedLocationSpread.x) || float.IsInfinity(scriptedLocationSpread.y))
            {
                Debug.LogError("[TrainingEnvController] 스크립트 투수 위치 표준편차는 0 이상의 유한한 값이어야 한다.", this);
                enabled = false;
                return;
            }
            director.SetManualInputEnabled(false);
            director.SetAutoRepeatEnabled(false);
            batterBehavior = batter.GetComponent<BehaviorParameters>();
            opponent = PlateAppearanceOpponent.Live;
            director.PlayReset += OnPlayReset;
            Academy.Instance.AgentPreStep += OnPreStep;
            if (UsesFielding)
            {
                defense = new SimpleMultiAgentGroup();
                foreach (FielderAgent fielder in fielders) defense.RegisterAgent(fielder);
                offense = new SimpleMultiAgentGroup();
                chasePotential = new float[fielders.Length];
                chaseShaping = new float[fielders.Length];
                lastChaseShaping = new float[fielders.Length];
                coverPotential = new float[fielders.Length];
                coverShaping = new float[fielders.Length];
                lastCoverShaping = new float[fielders.Length];
                positionReward = new float[fielders.Length];
                lastPositionReward = new float[fielders.Length];
                fieldingReward = new float[fielders.Length];
                lastFieldingReward = new float[fielders.Length];
                director.BallFielded += OnBallFielded;
            }
            subscribed = true;
            // 첫 초기화에서 Director의 투구 위치·스윙 파워 난수원을 경기장별 시드로 다시 만든다. 0번 경기장은 설정 시드 그대로다.
            resetPending = true;
            playActive = false;
            director.RequestResetPlay(ArenaSeed(director.EnvironmentConfig.RandomSeed));
        }

        private void OnDisable()
        {
            if (!subscribed) return;
            subscribed = false;
            EndOpponent();
            if (director != null)
            {
                director.PlayReset -= OnPlayReset;
                director.BallFielded -= OnBallFielded;
            }
            if (Academy.IsInitialized) Academy.Instance.AgentPreStep -= OnPreStep;
            defense?.Dispose();
            offense?.Dispose();
            defense = offense = null;
        }

        private void RequestReset()
        {
            resetPending = true;
            playActive = false;
            director.RequestResetPlay();
        }

        private void OnPlayReset()
        {
            if (!subscribed) return;
            bool newPlateAppearance = !batter.PlateAppearanceOpen;
            resetPending = false;
            playActive = true;
            pitchRequested = false;
            fieldersActed = runnersActed = false;
            pitchWaitSteps = 0;
            playSteps = 0;
            fieldSteps = 0;
            defensePotential = defenseShaping = 0f;
            System.Array.Clear(chasePotential, 0, chasePotential.Length);
            System.Array.Clear(chaseShaping, 0, chaseShaping.Length);
            System.Array.Clear(coverPotential, 0, coverPotential.Length);
            System.Array.Clear(coverShaping, 0, coverShaping.Length);
            System.Array.Clear(positionReward, 0, positionReward.Length);
            System.Array.Clear(fieldingReward, 0, fieldingReward.Length);
            fieldingPaid = false;
            System.Array.Clear(baseInPlay, 0, baseInPlay.Length);
            System.Array.Clear(baseInPlayPrevious, 0, baseInPlayPrevious.Length);
            System.Array.Clear(baseCovered, 0, baseCovered.Length);
            situationPending = UsesFielding && newPlateAppearance && situationRandom.NextDouble() < randomSituationProbability;
            situationSettling = false;
            if (newPlateAppearance)
            {
                BeginOpponent();
                float lesson = batterLessonOverride >= 0 ? batterLessonOverride : Academy.Instance.EnvironmentParameters.GetWithDefault("batter_lesson", -1f);
                batterLesson = stage == TrainingStage.Batter && BaseballEnvironmentConfig.IsFinite(lesson)
                    ? Mathf.Clamp(Mathf.FloorToInt(lesson), -1, 3) : -1;
                float preparation = preparationOverride >= 0 ? preparationOverride
                    : Academy.Instance.EnvironmentParameters.GetWithDefault("batter_preparation", -1f);
                preparationPhase = stage == TrainingStage.Batter && BaseballEnvironmentConfig.IsFinite(preparation)
                    ? Mathf.Clamp(Mathf.FloorToInt(preparation), -1, BatterPreparation.PhaseCount - 1) : -1;
                if (preparationPhase >= 0) batterLesson = BatterPreparation.ControlScale(preparationPhase) < 1f ? 2 : 3;
                float skill = skillOverride >= 0 ? skillOverride : Academy.Instance.EnvironmentParameters.GetWithDefault("batter_skill", -1f);
                skillPhase = stage == TrainingStage.Batter && preparationPhase == 0 && BaseballEnvironmentConfig.IsFinite(skill)
                    ? Mathf.Clamp(Mathf.FloorToInt(skill), -1, BatterSkills.PhaseCount - 1) : -1;
                batter.ConfigureTrainingLesson(batterLesson);
                batter.ConfigurePreparation(preparationPhase,
                    Academy.Instance.EnvironmentParameters.GetWithDefault("batter_prepared", 0f) >= 0.5f);
                batter.ConfigureSkills(skillPhase);
            }
            batter.BeginPitch();
            if (UsesPitcherAgent && opponent != PlateAppearanceOpponent.ScriptedPitcher) pitcher.BeginPitch();
        }

        /// <summary>
        /// 새 타석의 상대를 정한다(2단계부터). 난수는 모델 유무와 관계없이 타석마다 하나만 쓴다. 고정 타자 타석이면 타자 정책을
        /// 모델 추론으로 바꾼다. 타자의 이전 타석 에피소드는 이미 닫혔으므로 학습기에는 빈 타석으로 보이지 않는다.
        /// </summary>
        private void BeginOpponent()
        {
            EndOpponent();
            if (!UsesPitcherAgent) return;
            double roll = benchmarkRandom.NextDouble();
            if (roll < scriptedPitcherBenchmarkProbability)
            {
                opponent = PlateAppearanceOpponent.ScriptedPitcher;
            }
            else if (roll < scriptedPitcherBenchmarkProbability + frozenBatterBenchmarkProbability && benchmarkBatterModel != null)
            {
                opponent = PlateAppearanceOpponent.FrozenBatter;
                savedBatterType = batterBehavior.BehaviorType;
                savedBatterModel = batterBehavior.Model;
                batterBehavior.Model = benchmarkBatterModel;
                batterBehavior.BehaviorType = BehaviorType.InferenceOnly;
                batterSwapped = true;
            }
        }

        /// <summary>고정 타자 타석에서 바꾼 타자 정책을 되돌린다. 타자 타석 에피소드를 닫은 뒤에 부른다.</summary>
        private void EndOpponent()
        {
            if (batterSwapped && batterBehavior != null)
            {
                batterBehavior.BehaviorType = savedBatterType;
                batterBehavior.Model = savedBatterModel;
            }
            batterSwapped = false;
            opponent = PlateAppearanceOpponent.Live;
        }

        private string BenchmarkGroup => opponent == PlateAppearanceOpponent.ScriptedPitcher ? TrainingStats.BenchmarkBatterGroup
            : opponent == PlateAppearanceOpponent.FrozenBatter ? TrainingStats.BenchmarkPitcherGroup : null;

        private void OnPreStep(int academyStep)
        {
            if (!playActive || resetPending) return;
            playSteps++;
            PlayState state = director.State;

            if (state == PlayState.Ready)
            {
                // 무작위 상황은 적용된 뒤(다음 고정 단계) 첫 결정을 요청해 관측에 반영되게 한다.
                if (situationPending)
                {
                    situationPending = false;
                    situationSettling = true;
                    director.RequestSetSituation(situationRandom.NextDouble() < 0.5, situationRandom.NextDouble() < 0.4,
                        situationRandom.NextDouble() < 0.3, situationRandom.Next(0, 3));
                    return;
                }
                if (situationSettling)
                {
                    situationSettling = false;
                    return;
                }
                if (!batter.SetupSubmitted)
                {
                    batter.RequestDecision();
                    return;
                }
                if (!pitchRequested)
                {
                    RequestPitch();
                    pitchRequested = true;
                    return;
                }
                // 투구 요청이 다음 고정 단계에도 반영되지 않으면 거부된 것이다.
                if (++pitchWaitSteps > 3)
                {
                    Debug.LogWarning($"[TrainingEnvController] 투구가 시작되지 않아 플레이를 중단한다: " +
                        director.GetSnapshot().LastRejectionReason, this);
                    AbortPlay();
                }
                return;
            }

            if (state == PlayState.PitchInFlight && batter.PitchActive && !director.HasSwung)
                batter.RequestDecision();
            if (state == PlayState.BattedBallInFlight && UsesFielding) StepFielding();

            if (IsPitchFinished(state)) FinishPitch();
            else if (playSteps > ScaledSteps(maxPlaySteps)) AbortPlay();
        }

        /// <summary>
        /// 타구가 살아 있는 동안 수비 Agent(단순 수비는 CF 한 명)와 살아 있는 주자에게 결정을 요청한다. 사이 단계는 마지막 행동을 반복한다.
        /// 수비 결정마다 그룹(포구·포스 커버) 보조 보상과 개인(쫓기·베이스 커버) 보조 보상 γΦ(지금) − Φ(직전 결정)를 준다.
        /// 결정 전에 더한 보상은 직전 결정의 행동 결과로 전달된다.
        /// </summary>
        private void StepFielding()
        {
            // 첫 결정 전에는 행동 결과가 없으므로 감점하지 않는다. 이후에는 결정 간격과 무관하게 실제 고정 단계 시간으로 누적한다.
            if (fieldSteps > 0)
                for (int i = 0; i < fielders.Length; i++)
                    AddPositionReward(i, PlayOutcomeRewards.PositionReward(director, fielders[i].FielderIndex, Time.fixedDeltaTime));
            bool decide = fieldSteps % FieldDecisionInterval == 0;
            // 포구 뒤에는 정기 결정 간격을 기다리지 않고 새 관측으로 송구 베이스를 고른다.
            bool fielderDecide = decide || (director.SimplifiedFielding && director.CanThrow(fielders[0].FielderIndex));
            if (fieldSteps == 0)
            {
                // 타구 순간 살아 있는 주자만 이번 플레이의 주자 그룹에 넣는다.
                foreach (RunnerAgent runner in runners)
                    if (runner.IsRunning) offense.RegisterAgent(runner);
            }
            if (fielderDecide)
            {
                float potential = PlayOutcomeRewards.DefensePotential(director);
                if (fieldSteps > 0) AddDefenseShaping(PlayOutcomeRewards.DefenseShapingGamma * potential - defensePotential);
                defensePotential = potential;
                for (int i = 0; i < fielders.Length; i++)
                {
                    float chase = PlayOutcomeRewards.ChasePotential(director, fielders[i].FielderIndex);
                    if (fieldSteps > 0) AddChaseShaping(i, PlayOutcomeRewards.DefenseShapingGamma * chase - chasePotential[i]);
                    chasePotential[i] = chase;
                    float cover = PlayOutcomeRewards.CoverPotential(director, fielders[i].FielderIndex);
                    if (fieldSteps > 0) AddCoverShaping(i, PlayOutcomeRewards.DefenseShapingGamma * cover - coverPotential[i]);
                    coverPotential[i] = cover;
                }
            }
            TrackBaseCover();
            fieldSteps++;
            foreach (FielderAgent fielder in fielders)
            {
                if (director.SimplifiedFielding && director.BattedBallFielded && !director.CanThrow(fielder.FielderIndex)) continue;
                if (fielderDecide) fielder.RequestDecision();
                else fielder.RequestAction();
            }
            fieldersActed = true;
            defense.AddGroupReward(PlayOutcomeRewards.DefenseStepPenalty);
            foreach (RunnerAgent runner in runners)
            {
                if (!runner.IsRunning) continue;
                if (decide) runner.RequestDecision();
                else runner.RequestAction();
                runnersActed = true;
            }
        }

        /// <summary>
        /// 1단계는 스크립트 투수가 존 중앙 주변(<see cref="BenchmarkPitcher.SampleLocation"/>)으로 포심 직구를 던지고,
        /// 2단계부터는 투수 Agent가 한 번 결정한다. 기준 스크립트 투수 타석은 <see cref="BenchmarkPitcher"/>가 같은 명령 경계로 던진다.
        /// </summary>
        private void RequestPitch()
        {
            if (opponent == PlateAppearanceOpponent.ScriptedPitcher)
            {
                director.RequestThrowPitch(BenchmarkPitcher.Next(benchmarkRandom, director.EnvironmentConfig, director.StrikeZoneCenter));
                return;
            }
            if (UsesPitcherAgent)
            {
                pitcher.RequestDecision();
                return;
            }
            if (preparationPhase >= 0)
            {
                director.RequestThrowPitch(BatterPreparation.SamplePitch(preparationPhase, scriptedRandom,
                    director.EnvironmentConfig, director.StrikeZoneCenter, scriptedSpeedRangeKmh, skillPhase));
                return;
            }
            float kmh = Mathf.Lerp(scriptedSpeedRangeKmh.x, scriptedSpeedRangeKmh.y, (float)scriptedRandom.NextDouble());
            Vector2 spread = batterLesson == 0 || batterLesson == 1 ? Vector2.zero : batterLesson == 2 ? new Vector2(0.07f, 0.08f) : scriptedLocationSpread;
            Vector2 location = BenchmarkPitcher.SampleLocation(scriptedRandom, director.EnvironmentConfig, director.StrikeZoneCenter, spread);
            director.RequestThrowPitch(new PitchCommand(PitchType.FourSeam, kmh / 3.6f, location));
        }

        /// <summary>
        /// 1·2단계는 타자(와 투수)의 이번 투구 보상이 확정되면 끝난다. 타구가 굴러가는 시간은 기다리지 않는다.
        /// 3단계는 수비·주루가 이어지므로 PlayDirector가 플레이를 끝낼(Ended) 때까지 기다린다.
        /// </summary>
        private bool IsPitchFinished(PlayState state)
        {
            if (state == PlayState.Ended) return true;
            if (UsesFielding) return false;
            return !batter.PitchActive && (!UsesPitcherAgent || !pitcher.PitchActive);
        }

        private void FinishPitch()
        {
            LastEndReason = director.GetSnapshot().EndReason;
            if (UsesFielding && LastEndReason == PitchEndReason.Timeout)
            {
                AbortPlay();
                return;
            }
            if (UsesFielding) stats.RecordFieldingTimeout(false);
            bool resolved = director.TryGetPlaySummary(out PlaySummary play);
            // 고정 상대 평가 타석은 일반 지표(Agent끼리 대결)에 섞지 않고 평가 묶음에 따로 기록한다.
            string benchmark = BenchmarkGroup;
            if (benchmark == null) stats.RecordPitch(director, batter, UsesPitcherAgent);
            else stats.RecordBenchmarkPitch(benchmark, director);
            plateAppearancePitches++;
            if (UsesFielding)
            {
                LastPlayHadFielding = fieldersActed;
                LastPlaySummary = resolved ? play : default;
                LastDefenseOutcomeReward = fieldersActed && resolved ? PlayOutcomeRewards.Defense(play) : 0f;
                LastRunnerOutcomeReward = runnersActed && resolved ? PlayOutcomeRewards.Runners(play) : 0f;
                if (fieldersActed)
                {
                    // 끝난 상태의 포텐셜은 0이다. 그래야 보조 보상 합이 수비 행동과 무관해진다.
                    AddDefenseShaping(-defensePotential);
                    LastDefenseShapingReward = defenseShaping;
                    for (int i = 0; i < fielders.Length; i++)
                    {
                        AddChaseShaping(i, -chasePotential[i]);
                        AddCoverShaping(i, -coverPotential[i]);
                    }
                    SaveChaseShaping();
                    System.Array.Copy(coverShaping, lastCoverShaping, coverShaping.Length);
                    SavePositionAndFieldingRewards();
                    // 마지막 고정 단계에 베이스를 밟아 아웃을 만들고 끝난 플레이도 커버로 센다.
                    TrackBaseCover();
                    defense.AddGroupReward(LastDefenseOutcomeReward);
                    defense.EndGroupEpisode();
                    stats.RecordDefensePlay(director.BattedBallFielded, LastDefenseOutcomeReward, defenseShaping, LastChaseShapingReward,
                        LastPositionReward, LastFieldingReward);
                    for (int b = 0; b < baseInPlay.Length; b++)
                        if (baseInPlay[b]) stats.RecordBaseCover((BaseId)b, baseCovered[b]);
                }
                if (runnersActed)
                {
                    offense.AddGroupReward(LastRunnerOutcomeReward);
                    offense.EndGroupEpisode();
                }
                ClearOffense();
            }

            SituationSnapshot situation = director.GetSituation();
            if (situation.PlateAppearanceOver)
            {
                LastPlateAppearanceResult = situation.Result;
                LastPlateAppearanceOpponent = opponent;
                LastBatterOutcomeReward = PlayOutcomeRewards.BatterPlateAppearance(situation.Result, resolved, play);
                batter.EndPlateAppearance(LastBatterOutcomeReward);
                if (UsesPitcherAgent) pitcher.EndPlateAppearance(PlayOutcomeRewards.PitcherPlateAppearance(situation.Result, resolved, play));
                if (benchmark != null)
                {
                    stats.RecordBenchmarkPlateAppearance(benchmark, situation.Result, LastBatterOutcomeReward, UsesFielding, resolved, play);
                }
                else
                {
                    stats.RecordPlateAppearance(situation.Result, plateAppearancePitches, LastBatterOutcomeReward, UsesFielding, resolved, play,
                        fieldSteps * Time.fixedDeltaTime);
                    if (batter.QualityRewardEnabled || batter.TrainingLesson >= 0) stats.RecordTrainingPlateAppearance(batter);
                    if (preparationPhase >= 0) stats.RecordPreparationPlateAppearance(batter);
                    if (UsesPitcherAgent) stats.RecordMatchup(LastBatterOutcomeReward);
                }
                plateAppearancePitches = 0;
                EndOpponent();
            }
            if (UsesFielding && situation.HalfInningOver) stats.RecordHalfInning(situation.RunsThisHalfInning);
            stats.RecordPlayEnd(false);
            completedPlays++;
            RequestReset();
        }

        private void AbortPlay()
        {
            LastEndReason = director.GetSnapshot().EndReason;
            LastPlaySummary = default;
            LastPlayHadFielding = fieldersActed;
            LastBatterOutcomeReward = LastDefenseOutcomeReward = LastRunnerOutcomeReward = 0f;
            if (UsesFielding) stats.RecordFieldingTimeout(LastEndReason == PitchEndReason.Timeout);
            if (UsesFielding)
            {
                if (fieldersActed)
                {
                    // 중단은 끝이 아니라 마지막 관측에서 가치를 이어 받으므로, 지금 상태의 포텐셜로 마지막 차분을 준다.
                    AddDefenseShaping(PlayOutcomeRewards.DefenseShapingGamma * PlayOutcomeRewards.DefensePotential(director) - defensePotential);
                    LastDefenseShapingReward = defenseShaping;
                    for (int i = 0; i < fielders.Length; i++)
                    {
                        AddChaseShaping(i, PlayOutcomeRewards.DefenseShapingGamma *
                            PlayOutcomeRewards.ChasePotential(director, fielders[i].FielderIndex) - chasePotential[i]);
                        AddCoverShaping(i, PlayOutcomeRewards.DefenseShapingGamma *
                            PlayOutcomeRewards.CoverPotential(director, fielders[i].FielderIndex) - coverPotential[i]);
                    }
                    SaveChaseShaping();
                    System.Array.Copy(coverShaping, lastCoverShaping, coverShaping.Length);
                    SavePositionAndFieldingRewards();
                    defense.GroupEpisodeInterrupted();
                }
                if (runnersActed) offense.GroupEpisodeInterrupted();
                ClearOffense();
            }
            batter.AbortPlay();
            if (UsesPitcherAgent) pitcher.AbortPlay();
            director.RequestNewPlateAppearance();
            EndOpponent();
            plateAppearancePitches = 0;
            stats.RecordPlayEnd(true);
            abortedPlays++;
            completedPlays++;
            RequestReset();
        }

        private void ClearOffense()
        {
            foreach (RunnerAgent runner in runners) offense.UnregisterAgent(runner);
        }

        private void AddDefenseShaping(float reward)
        {
            defense.AddGroupReward(reward);
            defenseShaping += reward;
        }

        /// <summary>쫓기 차분은 해당 수비수에게만 지급한다. 담당이 바뀌면 이전 담당의 포텐셜도 0으로 정산한다.</summary>
        private void AddChaseShaping(int index, float reward)
        {
            if (reward == 0f) return;
            fielders[index].AddReward(reward);
            chaseShaping[index] += reward;
        }

        private void SaveChaseShaping()
        {
            System.Array.Copy(chaseShaping, lastChaseShaping, chaseShaping.Length);
            LastChaseShapingReward = 0f;
            foreach (float reward in chaseShaping) LastChaseShapingReward += reward;
        }

        /// <summary>베이스 커버 개인 보조 보상은 그룹 보상이 아니라 그 수비수에게만 준다(MA-POCA는 개인 보상과 그룹 보상을 더해 학습한다).</summary>
        private void AddCoverShaping(int index, float reward)
        {
            if (reward == 0f) return;
            fielders[index].AddReward(reward);
            coverShaping[index] += reward;
        }

        private void AddPositionReward(int index, float reward)
        {
            if (reward == 0f) return;
            fielders[index].AddReward(reward);
            positionReward[index] += reward;
        }

        /// <summary>
        /// 타구에 처음 닿은 수비수에게 개인 포구 보상을 한 번 준다. 송구를 받은 포구는 해당하지 않는다.
        /// Director 고정 단계 안에서 불리며, 같은 단계에 플레이가 끝나도 컨트롤러의 종료 처리보다 먼저다.
        /// </summary>
        private void OnBallFielded(int fielderIndex, bool inAir)
        {
            if (!subscribed || !UsesFielding || fieldingPaid || director.State != PlayState.BattedBallInFlight) return;
            fieldingPaid = true;
            for (int i = 0; i < fielders.Length; i++)
            {
                if (fielders[i].FielderIndex != fielderIndex) continue;
                fielders[i].AddReward(PlayOutcomeRewards.FieldingReward);
                fieldingReward[i] += PlayOutcomeRewards.FieldingReward;
            }
        }

        private void SavePositionAndFieldingRewards()
        {
            System.Array.Copy(positionReward, lastPositionReward, positionReward.Length);
            LastPositionReward = 0f;
            foreach (float reward in positionReward) LastPositionReward += reward;
            System.Array.Copy(fieldingReward, lastFieldingReward, fieldingReward.Length);
            LastFieldingReward = 0f;
            foreach (float reward in fieldingReward) LastFieldingReward += reward;
        }

        /// <summary>
        /// 베이스 커버 지표: 그 베이스로 주자가 오는 동안(또는 그 직전 고정 단계에) 어느 수비수든 베이스 반경 안에 있었는지 센다.
        /// 공을 쥔 채 베이스를 밟아 포스 아웃을 만들면 같은 단계에 주자가 아웃되므로 직전 단계의 상태도 본다.
        /// </summary>
        private void TrackBaseCover()
        {
            for (int b = 0; b < baseInPlay.Length; b++)
            {
                bool inPlay = PlayOutcomeRewards.IsBaseInPlay(director, (BaseId)b);
                if ((inPlay || baseInPlayPrevious[b]) && AnyFielderOn((BaseId)b)) baseCovered[b] = true;
                baseInPlay[b] |= inPlay;
                baseInPlayPrevious[b] = inPlay;
            }
        }

        private bool AnyFielderOn(BaseId baseId)
        {
            Vector3 basePosition = director.FieldLayout.GetBasePosition(baseId);
            for (int i = 0; i < director.FielderCount; i++)
            {
                Vector3 offset = director.GetFielder(i).Position - basePosition;
                if (new Vector2(offset.x, offset.z).magnitude <= director.EnvironmentConfig.BaseCoverRadius) return true;
            }
            return false;
        }
    }
}
