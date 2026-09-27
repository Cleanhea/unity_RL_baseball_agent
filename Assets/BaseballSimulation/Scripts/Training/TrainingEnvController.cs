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
        public float LastRunnerOutcomeReward { get; private set; }
        public PlateAppearanceResult LastPlateAppearanceResult { get; private set; }
        public float LastBatterOutcomeReward { get; private set; }
        /// <summary>이번 타석의 상대. 새 타석을 시작할 때 정한다.</summary>
        public PlateAppearanceOpponent Opponent => opponent;
        /// <summary>마지막으로 끝난 타석의 상대(검증·표시용).</summary>
        public PlateAppearanceOpponent LastPlateAppearanceOpponent { get; private set; }
        public ModelAsset BenchmarkBatterModel => benchmarkBatterModel;
        public float ScriptedPitcherBenchmarkProbability => scriptedPitcherBenchmarkProbability;
        public float FrozenBatterBenchmarkProbability => frozenBatterBenchmarkProbability;

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
                fielders == null || fielders.Length != director.FielderCount || fielders.Length == 0))
            {
                Debug.LogError($"[TrainingEnvController] {stage} 단계는 PlayDirector 주자 슬롯·수비수 수만큼의 RunnerAgent·FielderAgent가 필요하다.", this);
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
            if (director != null) director.PlayReset -= OnPlayReset;
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
            situationPending = UsesFielding && newPlateAppearance && situationRandom.NextDouble() < randomSituationProbability;
            situationSettling = false;
            if (newPlateAppearance) BeginOpponent();
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
            else if (playSteps > maxPlaySteps) AbortPlay();
        }

        /// <summary>타구가 살아 있는 동안 수비 5명과 살아 있는 주자에게 결정을 요청한다. 사이 단계는 마지막 행동을 반복한다.</summary>
        private void StepFielding()
        {
            bool decide = fieldSteps % fieldDecisionInterval == 0;
            if (fieldSteps == 0)
            {
                // 타구 순간 살아 있는 주자만 이번 플레이의 주자 그룹에 넣는다.
                foreach (RunnerAgent runner in runners)
                    if (runner.IsRunning) offense.RegisterAgent(runner);
            }
            fieldSteps++;
            foreach (FielderAgent fielder in fielders)
            {
                if (decide) fielder.RequestDecision();
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
            float kmh = Mathf.Lerp(scriptedSpeedRangeKmh.x, scriptedSpeedRangeKmh.y, (float)scriptedRandom.NextDouble());
            Vector2 location = BenchmarkPitcher.SampleLocation(scriptedRandom, director.EnvironmentConfig, director.StrikeZoneCenter, scriptedLocationSpread);
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
                    defense.AddGroupReward(LastDefenseOutcomeReward);
                    defense.EndGroupEpisode();
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
            if (UsesFielding)
            {
                if (fieldersActed) defense.GroupEpisodeInterrupted();
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
    }
}
