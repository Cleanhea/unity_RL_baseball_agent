using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 환경 명령의 단일 입구이자 결과의 단일 소유자(docs/architecture.md 3.1).
    ///
    /// 현재 범위는 피칭머신 직구 하나다. 목표(<see cref="FieldLayout.PitchTargetPosition"/>)를
    /// 통과하도록 중력 낙하를 보정한 초기 속도를 계산해 발사하고, 목표 평면 통과 오차를
    /// 측정하며, 스윙·타격·타구 관찰·타자주자 이동과 재투구·초기화·자동 반복을 관리한다.
    /// 수비·아웃/세이프 판정·볼카운트는 후속 단계다.
    ///
    /// 수동 입력(<see cref="ManualPlayController"/>)과 자동 반복 모두 <see cref="RequestThrowPitch"/>,
    /// <see cref="RequestResetPlay"/>, <see cref="RequestSwing"/>, <see cref="RequestRunnerDecision"/> 명령을 사용한다. 실제 변경은 <see cref="FixedUpdate"/>에서만
    /// 일어나 물리 시점과 어긋나지 않는다(docs/architecture.md 5.2).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayDirector : MonoBehaviour
    {
        [Header("필수 참조 (Inspector에서 명시적으로 연결)")]
        [SerializeField]
        private FieldLayout fieldLayout;

        [SerializeField]
        private BallController ball;

        [Header("자동 반복 투구")]
        [Tooltip("켜면 한 투구가 끝난 뒤 간격을 기다렸다가 공을 초기화하고 다시 던진다. 기본은 꺼짐이다.")]
        [SerializeField]
        private bool autoRepeatEnabled;

        [Tooltip("자동 반복 투구 사이의 대기 시간(초). 기본 3초다.")]
        [SerializeField, Min(0.1f)]
        private float autoRepeatIntervalSeconds = 3f;
        public bool AutoRepeatEnabled => autoRepeatEnabled;
        public void SetAutoRepeatEnabled(bool value) => autoRepeatEnabled = value;

        [SerializeField] private BatterController batter;
        private bool swingRequested;
        private SwingCommand pendingSwing;
        private bool setupRequested;
        private BatterSetupCommand pendingSetup;
        public BattingEvaluation GetBattingEvaluation() => batter != null ? batter.GetEvaluation() : default;
        public event System.Action<BattingEvaluation> BattingEvaluated;
        private bool evaluationPublished;
        [Tooltip("학습/스크립트 입력을 연결할 때 끈다. 수동 입력과의 경합 방지.")]
        [SerializeField] private bool manualInputEnabled = true;
        public bool ManualInputEnabled => manualInputEnabled;
        public void SetManualInputEnabled(bool value) => manualInputEnabled = value;
        public void RequestBatterSetup(BatterSetupCommand value)
        {
            pendingSetup = value;
            setupRequested = true;
        }
        public bool HasSwung => batter != null && batter.HasSwung;
        public bool HasContact => batter != null && batter.HasContact;
        public float ContactQuality => batter != null ? batter.ContactQuality : 0f;
        public event System.Action SwingStarted;
        public event System.Action<Vector3> BallBatContact;

        public void RequestSwing(SwingCommand command)
        {
            if (swingRequested) return;
            pendingSwing = command;
            swingRequested = true;
        }

        [Tooltip("타자주자. 없으면 접촉 후 주루 없이 기존처럼 타구만 관찰한다.")]
        [SerializeField] private RunnerController runner;
        [Tooltip("3단계 누상 주자 슬롯. 0은 1루, 1은 2루, 2는 3루에서 타석을 시작한다. 비어 있으면 주자 상황을 다음 타석으로 넘기지 않는다.")]
        [SerializeField] private RunnerController[] baseRunners = new RunnerController[0];
        private bool runnerDecisionRequested;
        private RunnerDecision pendingRunnerDecision;
        private readonly bool[] indexedDecisionRequested = new bool[4];
        private readonly RunnerDecision[] indexedDecisions = new RunnerDecision[4];
        public RunnerSnapshot GetRunnerSnapshot() => runner != null ? runner.GetSnapshot() : default;

        private readonly GameSituation situation = new GameSituation();
        private readonly bool[] forced = new bool[4];
        private readonly bool[] mustRetouch = new bool[4];
        private readonly bool[] scoreCounted = new bool[4];
        private System.Action<BaseId>[] baseRunnerHandlers = new System.Action<BaseId>[0];
        private bool liveBattedPlay;
        private bool playResolved;
        private int outsOnPlay;
        private int runsOnPlay;
        private int runnerOutsOnPlay;
        private bool batterOutBeforeFirst;
        private PitchEndReason lastOutReason = PitchEndReason.None;
        private PlaySummary lastPlaySummary;
        private bool newPlateAppearanceRequested;
        private bool situationRequested;
        private bool[] pendingBases = new bool[3];
        private int pendingOuts;

        /// <summary>주자 슬롯 수. 0은 타자주자, 1~3은 1·2·3루에서 시작하는 누상 주자다.</summary>
        public int RunnerSlotCount => runner == null ? 0 : 1 + (baseRunners != null ? baseRunners.Length : 0);
        private RunnerController RunnerAt(int index) => index == 0 ? runner : baseRunners[index - 1];
        public RunnerSnapshot GetRunnerSnapshot(int index) =>
            index >= 0 && index < RunnerSlotCount && RunnerAt(index) != null ? RunnerAt(index).GetSnapshot() : default;
        /// <summary>이번 플레이에서 아직 포스 상태(다음 베이스로 밀려나야 함)인 주자인지.</summary>
        public bool IsRunnerForced(int index) => index >= 0 && index < forced.Length && forced[index];
        /// <summary>뜬공 포구 뒤 시작 베이스를 다시 밟아야 하는 주자인지.</summary>
        public bool RunnerNeedsRetouch(int index) => index >= 0 && index < mustRetouch.Length && mustRetouch[index];
        public bool HasBaseRunners => runner != null && baseRunners != null && baseRunners.Length >= 3;

        /// <summary>볼카운트·아웃·주자 상황(docs/game-situation.md).</summary>
        public SituationSnapshot GetSituation() => situation.GetSnapshot();

        /// <summary>이번 투구의 인플레이 플레이가 판정까지 끝났으면 요약을 돌려준다. 초기화 전까지 유지된다.</summary>
        public bool TryGetPlaySummary(out PlaySummary summary)
        {
            summary = lastPlaySummary;
            return playResolved;
        }

        /// <summary>인플레이 플레이가 판정까지 끝날 때 발생한다.</summary>
        public event System.Action<PlaySummary> PlayResolved;

        public void AssignBaseRunners(RunnerController[] value)
        {
            baseRunners = value ?? new RunnerController[0];
        }

        /// <summary>주자 슬롯별 진루/귀루 판단(0 타자주자, 1~3 누상 주자). 같은 고정 단계에서는 마지막 판단만 적용한다.</summary>
        public void RequestRunnerDecision(int index, RunnerDecision decision)
        {
            if (index == 0)
            {
                RequestRunnerDecision(decision);
                return;
            }
            if (index < 1 || index > 3) return;
            indexedDecisions[index] = decision;
            indexedDecisionRequested[index] = true;
        }

        /// <summary>다음 초기화에서 새 타석을 시작한다(카운트만 지운다).</summary>
        public void RequestNewPlateAppearance() => newPlateAppearanceRequested = true;

        /// <summary>
        /// 새 반 이닝 상황(누상 주자·아웃)을 정한다. Ready 상태에서 다음 고정 단계에 적용하며 카운트도 지운다.
        /// 검증과 학습 상황 다양화에 쓴다. 누상 주자 슬롯이 없으면 주자는 무시한다.
        /// </summary>
        public void RequestSetSituation(bool onFirst, bool onSecond, bool onThird, int outs)
        {
            situationRequested = true;
            pendingBases = new[] { onFirst, onSecond, onThird };
            pendingOuts = outs;
        }

        private BattedBallJudge judge;
        private Vector3 battedExitVelocity;
        private float battedBackspinRpm;
        private float battedApexHeight;
        /// <summary>타구가 페어·파울·홈런·인정 2루타로 판정될 때 발생한다. 처리기에서는 명령만 요청한다.</summary>
        public event System.Action<BattedBallCall> BattedBallCalled;
        public BattedBallSnapshot GetBattedBallSnapshot() => judge == null || battedExitVelocity == Vector3.zero
            ? default
            : new BattedBallSnapshot(judge.Call, battedExitVelocity, battedBackspinRpm, battedApexHeight,
                judge.HasFirstTouch, judge.FirstTouchPoint, judge.HangTime, fieldLayout.HomePosition);
        /// <summary>주자가 베이스를 밟을 때마다 발생한다. 처리기에서는 명령만 요청한다.</summary>
        public event System.Action<BaseId> RunnerBaseReached;

        /// <summary>1루 이후 진루/귀루 판단. 수동·스크립트·향후 학습 입력이 공통으로 호출한다.</summary>
        public void RequestRunnerDecision(RunnerDecision decision)
        {
            // 같은 고정 단계 안에서는 마지막 판단만 적용한다.
            pendingRunnerDecision = decision;
            runnerDecisionRequested = true;
        }

        [Tooltip("3단계 수비수. 비어 있으면 수비 없이 기존처럼 타구만 관찰한다.")]
        [SerializeField] private FielderController[] fielders = new FielderController[0];
        private Vector2[] pendingFielderMoves = new Vector2[0];
        private ThrowTarget[] pendingThrows = new ThrowTarget[0];
        private bool battedBallFielded;
        private int lastThrower = -1;
        private float lastThrowTime = -1f;
        private bool scriptedBattedBallRequested;
        private Vector3 pendingScriptedExitVelocity;
        private bool scriptedBattedBall;
        private const float ThrowerRecatchDelay = 0.3f;

        private bool HasFielders => fielders != null && fielders.Length > 0;
        public int FielderCount => fielders != null ? fielders.Length : 0;
        public FielderController GetFielder(int index) => fielders[index];
        /// <summary>공을 잡고 있는 수비수 인덱스. 없으면 -1이다.</summary>
        public int BallHolder => ball != null ? ball.HolderIndex : -1;
        /// <summary>이번 타구에 수비수가 한 번이라도 닿았으면 true다. 이후 페어/파울·홈런 판정은 하지 않는다.</summary>
        public bool BattedBallFielded => battedBallFielded;
        /// <summary>수비수가 공을 잡을 때 발생한다. (수비수 인덱스, 타구가 땅·펜스에 닿기 전에 잡았는지)</summary>
        public event System.Action<int, bool> BallFielded;

        public void AssignFielders(FielderController[] value)
        {
            fielders = value ?? new FielderController[0];
        }

        /// <summary>수비수 이동 명령(필드 XZ, 크기 1 = 최고 속력). 새 명령이 올 때까지 유지한다.</summary>
        public void RequestFielderMove(int index, Vector2 direction)
        {
            if (index < 0 || index >= pendingFielderMoves.Length) return;
            pendingFielderMoves[index] = direction;
        }

        /// <summary>공을 가진 수비수의 송구 명령. 다음 고정 단계에 한 번 처리한다.</summary>
        public void RequestFielderThrow(int index, ThrowTarget target)
        {
            if (index < 0 || index >= pendingThrows.Length) return;
            pendingThrows[index] = target;
        }

        /// <summary>
        /// 검증·시나리오용: 투구 없이 타석 앞(PitchTarget)에서 <paramref name="exitVelocity"/>로 타구를 시작한다.
        /// Ready 상태에서만 받는다. 타자 접촉 이벤트는 발생하지 않지만 주루·수비·판정은 실제 타구와 같다.
        /// </summary>
        public void RequestScriptedBattedBall(Vector3 exitVelocity)
        {
            scriptedBattedBallRequested = true;
            pendingScriptedExitVelocity = exitVelocity;
        }

        private bool referencesValid;

        // 프레임 입력을 담아 뒀다가 FixedUpdate에서만 적용한다(docs/architecture.md 4.1).
        private bool pitchRequested;
        private bool resetRequested;

        private PlayState state = PlayState.Ready;
        private float elapsedSeconds;
        private float autoRepeatTimer;
        private int completedPitchCount;

        private Vector3 previousBallPosition;
        private Vector3 pitchTargetSnapshot;
        private Vector3 pitchTravelDirection = Vector3.forward;

        private bool centerPassRecorded;
        private float centerPassError;
        private PitchEndReason lastEndReason = PitchEndReason.None;
        private string lastRejectionReason = string.Empty;

        public PlayState State => state;
        public BaseballEnvironmentConfig EnvironmentConfig => Config;
        public FieldLayout FieldLayout => fieldLayout;
        /// <summary>초기화가 고정 단계에 적용되고 Ready 상태가 된 뒤 발행한다.</summary>
        public event System.Action PlayReset;
        public float AimSprayDegrees { get; set; }
        public float AimLaunchDegrees { get; set; } = 15f;

        private BaseballEnvironmentConfig Config => fieldLayout != null ? fieldLayout.Config : null;

        private void Awake()
        {
            int fielderCount = FielderCount;
            pendingFielderMoves = new Vector2[fielderCount];
            pendingThrows = new ThrowTarget[fielderCount];
            referencesValid = TryValidateReferences(out string error);
            if (!referencesValid)
            {
                Debug.LogError($"[PlayDirector] 필수 참조 검증 실패. 투구를 시작할 수 없다.\n{error}", this);
            }
        }

        private void Start()
        {
            if (referencesValid)
            {
                judge = new BattedBallJudge(fieldLayout);
                // 투구 위치 난수원은 스윙 파워 난수원(시드 그대로)과 겹치지 않게 시드 + 1로 만든다.
                pitchRandom = new System.Random(unchecked(Config.RandomSeed + 1));
                if (batter != null) batter.Initialize(Config, fieldLayout.PitchTargetPosition);
                if (runner != null)
                {
                    runner.Initialize(Config, fieldLayout);
                    runner.BaseReached += OnRunnerBaseReached;
                }
                situation.TrackBases = HasBaseRunners;
                if (HasBaseRunners)
                {
                    baseRunnerHandlers = new System.Action<BaseId>[baseRunners.Length];
                    for (int i = 0; i < baseRunners.Length; i++)
                    {
                        int slot = i + 1;
                        baseRunners[i].Initialize(Config, fieldLayout);
                        baseRunnerHandlers[i] = _ => OnSlotBaseReached(slot);
                        baseRunners[i].BaseReached += baseRunnerHandlers[i];
                    }
                }
                PerformReset();
            }
        }

        private void OnDestroy()
        {
            if (runner != null) runner.BaseReached -= OnRunnerBaseReached;
            for (int i = 0; i < baseRunnerHandlers.Length; i++)
                if (baseRunners[i] != null) baseRunners[i].BaseReached -= baseRunnerHandlers[i];
        }

        private void OnRunnerBaseReached(BaseId baseId)
        {
            OnSlotBaseReached(0);
            RunnerBaseReached?.Invoke(baseId);
        }

        /// <summary>포스 베이스에 닿으면 포스가 풀리고, 리터치해야 하는 주자가 시작 베이스를 밟으면 리터치가 끝난다.</summary>
        private void OnSlotBaseReached(int slot)
        {
            RunnerController reached = RunnerAt(slot);
            int touched = reached.LastTouchedIndex;
            if (touched >= reached.StartIndex + 1) forced[slot] = false;
            if (mustRetouch[slot] && touched == reached.StartIndex) mustRetouch[slot] = false;
        }

        /// <summary>수동·자동 입력이 공통으로 호출하는 투구 요청. 위치는 설정의 투구 위치 방식을 따른다.</summary>
        public void RequestThrowPitch()
        {
            pitchRequested = true;
            pitchLocationRequested = false;
            pitchCommandRequested = false;
        }

        /// <summary>
        /// 목표 위치를 지정한 투구 요청. (좌우 x, 지면 기준 높이) m이며 x 양수가 1루 쪽이다.
        /// PitchTarget 평면에서 이 위치를 지나도록 조준한다.
        /// </summary>
        public void RequestThrowPitch(Vector2 plateLocation)
        {
            pitchRequested = true;
            pitchLocationRequested = true;
            pitchCommandRequested = false;
            pendingPitchLocation = plateLocation;
        }

        /// <summary>
        /// 구종·구속·위치를 지정한 투구 요청. 스크립트 투수와 투수 Agent가 쓴다.
        /// 회전은 설정의 구종 프로필을 따르고, 휘는 공도 목표 위치를 지나도록 좌우·상하를 함께 조준한다.
        /// </summary>
        public void RequestThrowPitch(PitchCommand command)
        {
            pitchRequested = true;
            pitchLocationRequested = true;
            pitchCommandRequested = true;
            pendingPitchLocation = command.PlateLocation;
            pendingPitchCommand = command;
        }

        private bool pitchCommandRequested;
        private PitchCommand pendingPitchCommand;
        private bool hasLastPitch;
        private PitchCommand lastPitch;

        /// <summary>이번 투구의 구종·실제 발사 속력·목표 위치. 투구 전이나 초기화 뒤에는 false다.</summary>
        public bool TryGetLastPitch(out PitchCommand pitch)
        {
            pitch = lastPitch;
            return hasLastPitch;
        }

        /// <summary>
        /// 이번 투구가 PitchTarget 평면에 닿을 것으로 해석기가 계산한 시각(투구 시작 기준 s). 투구 전에는 0이다.
        /// 타격 타이밍 진단·검증용이며 Agent 관측에는 넣지 않는다.
        /// </summary>
        public float PitchArrivalSeconds { get; private set; }

        /// <summary>규칙 스트라이크존 중앙(좌우 0, 존 아래·위 높이의 가운데)의 플레이트 위치.</summary>
        public Vector2 StrikeZoneCenter => Config != null
            ? new Vector2(0f, 0.5f * (Config.StrikeZoneBottom + Config.StrikeZoneTop)) : Vector2.zero;

        /// <summary>수동·자동 입력이 공통으로 호출하는 초기화 요청.</summary>
        public void RequestResetPlay()
        {
            resetRequested = true;
        }

        /// <summary>초기화하면서 투구 위치·스윙 파워 난수원을 <paramref name="seed"/>로 다시 만든다(에피소드 재현용).</summary>
        public void RequestResetPlay(int seed)
        {
            resetRequested = true;
            pendingSeed = seed;
        }

        /// <summary>투구마다 볼/스트라이크 판정이 정해질 때 한 번 발생한다. 처리기에서는 명령만 요청한다.</summary>
        public event System.Action<PitchCall> PitchCalled;
        private bool pitchLocationRequested;
        private Vector2 pendingPitchLocation;
        private int? pendingSeed;
        private System.Random pitchRandom;
        private PitchCall pitchCall;
        private bool pitchInZone;
        private bool swingOffered;
        private bool pitchPassedPlate;
        private bool hasPlateLocation;
        private Vector2 aimLocation;
        private Vector2 plateLocation;

        public PitchCallSnapshot GetPitchCall() =>
            new PitchCallSnapshot(pitchCall, pitchInZone, swingOffered, aimLocation, hasPlateLocation, plateLocation,
                Config != null ? Config.StrikeZoneBottom : 0f, Config != null ? Config.StrikeZoneTop : 0f);

        private void FixedUpdate()
        {
            if (!referencesValid)
            {
                return;
            }

            // 초기화는 다른 명령보다 먼저 처리한다(docs/architecture.md 5.2).
            if (resetRequested)
            {
                resetRequested = false;
                pitchRequested = false;
                if (pendingSeed.HasValue)
                {
                    pitchRandom = new System.Random(unchecked(pendingSeed.Value + 1));
                    if (batter != null) batter.Reseed(pendingSeed.Value);
                    pendingSeed = null;
                }
                PerformReset();
                return;
            }

            // Read the completed physics interval before applying this tick's commands.
            AdvanceSimulation(Time.fixedDeltaTime);
            if (setupRequested)
            {
                setupRequested = false;
                if (state != PlayState.Ready || batter == null)
                    lastRejectionReason = "타자/배트 위치는 투구 전 Ready 상태에서만 설정할 수 있다.";
                else if (!batter.TrySetSetup(pendingSetup, out string error))
                    lastRejectionReason = error;
                else lastRejectionReason = string.Empty;
            }

            if (pitchRequested)
            {
                pitchRequested = false;
                PerformThrow();
                pitchLocationRequested = false;
                pitchCommandRequested = false;
            }

            if (swingRequested)
            {
                swingRequested = false;
                if (state != PlayState.PitchInFlight || batter == null || batter.HasSwung)
                    lastRejectionReason = "투구 비행 중 한 번만 스윙할 수 있다. 타자 참조도 필요하다.";
                else if (float.IsNaN(pendingSwing.SprayDegrees) || float.IsInfinity(pendingSwing.SprayDegrees) ||
                    float.IsNaN(pendingSwing.LaunchDegrees) || float.IsInfinity(pendingSwing.LaunchDegrees))
                    lastRejectionReason = "스윙 각도는 유한한 값이어야 한다.";
                else
                {
                    batter.BeginSwing(new SwingCommand(Mathf.Clamp(pendingSwing.SprayDegrees, -45f, 45f),
                        Mathf.Clamp(pendingSwing.LaunchDegrees, -30f, 50f)), elapsedSeconds);
                    // 공이 플레이트를 다 지난 뒤 시작한 스윙은 공을 치려는 시도로 보지 않는다.
                    swingOffered = !pitchPassedPlate;
                    lastRejectionReason = string.Empty;
                    SwingStarted?.Invoke();
                }
            }

            if (runnerDecisionRequested)
            {
                runnerDecisionRequested = false;
                if (state != PlayState.BattedBallInFlight || runner == null)
                    lastRejectionReason = "주루 판단은 타구 후(BattedBallInFlight) 주자가 있을 때만 줄 수 있다.";
                else if (!runner.TryApplyDecision(pendingRunnerDecision, out string error))
                    lastRejectionReason = error;
                else lastRejectionReason = string.Empty;
            }
            for (int slot = 1; slot < indexedDecisionRequested.Length; slot++)
            {
                if (!indexedDecisionRequested[slot]) continue;
                indexedDecisionRequested[slot] = false;
                if (state != PlayState.BattedBallInFlight || slot >= RunnerSlotCount)
                    lastRejectionReason = "누상 주자 판단은 타구 후 그 슬롯의 주자가 있을 때만 줄 수 있다.";
                else if (!RunnerAt(slot).TryApplyDecision(indexedDecisions[slot], out string error))
                    lastRejectionReason = error;
            }

            if (situationRequested)
            {
                situationRequested = false;
                if (state != PlayState.Ready)
                    lastRejectionReason = "주자·아웃 상황은 투구 전 Ready 상태에서만 정할 수 있다.";
                else
                {
                    situation.SetSituation(pendingBases[0], pendingBases[1], pendingBases[2], pendingOuts);
                    PlaceBaseRunners();
                }
            }

            if (scriptedBattedBallRequested)
            {
                scriptedBattedBallRequested = false;
                if (state != PlayState.Ready || batter == null)
                    lastRejectionReason = "시나리오 타구는 Ready 상태에서 타자가 있을 때만 시작할 수 있다.";
                else if (!BaseballEnvironmentConfig.IsFinite(pendingScriptedExitVelocity.x) ||
                    !BaseballEnvironmentConfig.IsFinite(pendingScriptedExitVelocity.y) ||
                    !BaseballEnvironmentConfig.IsFinite(pendingScriptedExitVelocity.z) || pendingScriptedExitVelocity.sqrMagnitude < 0.01f)
                    lastRejectionReason = "시나리오 타구 속도는 0이 아닌 유한한 값이어야 한다.";
                else
                {
                    scriptedBattedBall = true;
                    elapsedSeconds = 0f;
                    Vector3 contact = fieldLayout.PitchTargetPosition;
                    ball.ResetTo(contact, Quaternion.identity);
                    StartBattedBall(contact, pendingScriptedExitVelocity);
                    lastRejectionReason = string.Empty;
                }
            }

            if (HasFielders)
            {
                for (int i = 0; i < fielders.Length; i++) fielders[i].SetMoveCommand(pendingFielderMoves[i]);
                if (state == PlayState.BattedBallInFlight && ball.IsHeld && pendingThrows[ball.HolderIndex] != ThrowTarget.None)
                    PerformFielderThrow(ball.HolderIndex, pendingThrows[ball.HolderIndex]);
                System.Array.Clear(pendingThrows, 0, pendingThrows.Length);
            }

            // 발사한 단계를 포함해 물리 단계 직전에 공기 역학·구름 저항을 준다(투구 해석기와 같은 순서).
            ball.ApplyFlightForces();
        }

        private void AdvanceSimulation(float dt)
        {
            BaseballEnvironmentConfig config = Config;

            if (state == PlayState.PitchInFlight)
            {
                elapsedSeconds += dt;
                Vector3 currentPosition = ball.Position;

                if (!centerPassRecorded && TryComputePlaneCrossing(
                        previousBallPosition, currentPosition,
                        pitchTargetSnapshot, pitchTravelDirection,
                        out Vector3 crossingPoint))
                {
                    centerPassRecorded = true;
                    centerPassError = Vector3.Distance(crossingPoint, pitchTargetSnapshot);
                }
                TrackPitchOverPlate(previousBallPosition, currentPosition, config);

                if (batter != null && batter.Tick(elapsedSeconds - dt, elapsedSeconds,
                        previousBallPosition, currentPosition, out Vector3 exitVelocity))
                {
                    StartBattedBall(currentPosition, exitVelocity);
                    BallBatContact?.Invoke(exitVelocity);
                    return;
                }

                float distancePastTarget = Vector3.Dot(currentPosition - pitchTargetSnapshot, pitchTravelDirection);
                bool passedTarget = distancePastTarget > config.BehindTargetMargin;
                bool outsideBoundary = !fieldLayout.IsInsidePlayBoundary(currentPosition);
                bool timedOut = elapsedSeconds >= config.PitchTimeLimitSeconds;

                if (passedTarget)
                {
                    EndPitch(PitchEndReason.PassedTarget);
                }
                else if (outsideBoundary)
                {
                    EndPitch(PitchEndReason.OutOfPlay);
                }
                else if (timedOut)
                {
                    EndPitch(PitchEndReason.Timeout);
                }

                previousBallPosition = currentPosition;
            }
            else if (state == PlayState.BattedBallInFlight)
            {
                elapsedSeconds += dt;
                Vector3 ballFrom = previousBallPosition;
                batter.Tick(elapsedSeconds - dt, elapsedSeconds, previousBallPosition, ball.Position, out _);
                previousBallPosition = ball.Position;
                TickRunners(dt);
                if (HasFielders && StepFielding(ballFrom, ball.Position, dt, config)) return;
                if (!battedBallFielded)
                {
                    battedApexHeight = Mathf.Max(battedApexHeight, ball.Position.y);
                    if (judge.Step(ball, elapsedSeconds, dt)) BattedBallCalled?.Invoke(judge.Call);
                }
                if (pitchCall == PitchCall.None && judge.Call != BattedBallCall.None)
                    SetPitchCall(judge.Call == BattedBallCall.Foul ? PitchCall.Foul : PitchCall.InPlay);

                // 같은 틱이면 공이 죽는 판정(파울·홈런·인정 2루타)이 득점보다, 득점이 시간 초과보다 우선한다.
                switch (judge.Call)
                {
                    case BattedBallCall.Foul: EndPitch(PitchEndReason.Foul); return;
                    case BattedBallCall.HomeRun: EndPitch(PitchEndReason.HomeRun); return;
                    case BattedBallCall.GroundRuleDouble: EndPitch(PitchEndReason.GroundRuleDouble); return;
                    case BattedBallCall.OutOfPlay:
                        Debug.LogWarning("[PlayDirector] 타구가 펜스 높이 아래로 경계를 통과했다. 펜스 Collider가 없다면 " +
                            "Tools > Baseball Simulation > Add Outfield Fence To Current Scene을 실행한다.", this);
                        EndPitch(PitchEndReason.OutOfPlay);
                        return;
                }
                // 살아 있는 주자가 모두 득점했거나 아웃되면 끝난다(주자 한 명이면 기존 득점 종료와 같다).
                if (runner != null && NoLiveRunners()) EndPitch(outsOnPlay > 0 ? lastOutReason : PitchEndReason.RunScored);
                // 지면 아래로 빠지는 비정상 이탈만 안전 종료한다. 높은 뜬공은 상단 경계로 끊지 않는다.
                else if (ball.Position.y < fieldLayout.HomePosition.y + config.PlayBoundaryLowerY) EndPitch(PitchEndReason.OutOfPlay);
                else if (elapsedSeconds >= config.BattedBallTimeLimit) EndPitch(PitchEndReason.Timeout);
            }
            else if (state == PlayState.Ended && autoRepeatEnabled)
            {
                autoRepeatTimer += dt;
                if (autoRepeatTimer >= autoRepeatIntervalSeconds)
                {
                    autoRepeatTimer = 0f;
                    PerformReset();
                    PerformThrow();
                }
            }
        }

        private void TickRunners(float dt)
        {
            for (int slot = 0; slot < RunnerSlotCount; slot++)
            {
                RunnerController running = RunnerAt(slot);
                running.Tick(elapsedSeconds - dt, dt);
                if (running.Phase == RunnerPhase.Scored && !scoreCounted[slot])
                {
                    scoreCounted[slot] = true;
                    runsOnPlay++;
                }
            }
        }

        private bool NoLiveRunners()
        {
            for (int slot = 0; slot < RunnerSlotCount; slot++)
                if (RunnerAt(slot).IsLive) return false;
            return true;
        }

        /// <summary>주자 한 명을 아웃시킨다. 그 주자 뒤에서 밀어내던 포스가 사라지므로 앞 주자의 포스도 푼다.</summary>
        private void RecordOut(int slot, PitchEndReason reason)
        {
            RunnerController outRunner = RunnerAt(slot);
            if (slot == 0 && outRunner.LastTouchedIndex == 0) batterOutBeforeFirst = true;
            outRunner.MarkOut();
            outsOnPlay++;
            if (!(slot == 0 && reason == PitchEndReason.FlyOut)) runnerOutsOnPlay++;
            lastOutReason = reason;
            forced[slot] = false;
            mustRetouch[slot] = false;
            for (int ahead = slot + 1; ahead < forced.Length; ahead++) forced[ahead] = false;
        }

        private bool IsThirdOut => situation.Outs + outsOnPlay >= 3;

        /// <summary>새 타석 시작의 누상 상황대로 누상 주자를 베이스 위에 세운다.</summary>
        private void PlaceBaseRunners()
        {
            if (!HasBaseRunners) return;
            for (int i = 0; i < baseRunners.Length; i++)
            {
                if (situation.IsOccupied(i + 1)) baseRunners[i].BeginOnBase(i + 1);
                else baseRunners[i].ResetState();
            }
        }

        /// <summary>
        /// 인플레이 플레이 결과를 정리해 상황(아웃·득점·베이스)에 반영하고 요약을 남긴다.
        /// 홈런은 모든 주자, 인정 2루타는 모든 주자 2베이스를 준다. 파울은 상황을 바꾸지 않는다.
        /// 세 번째 아웃이 포스 아웃이거나 타자주자가 1루 전에 아웃되면 그 플레이의 득점은 인정하지 않는다.
        /// </summary>
        private void ResolvePlay(PitchEndReason reason)
        {
            var newBases = new bool[3];
            int runs = runsOnPlay, batterBases = 0, advanced = 0;
            if (reason == PitchEndReason.Foul)
            {
                for (int b = 1; b <= 3; b++) newBases[b - 1] = situation.IsOccupied(b);
                runs = 0;
            }
            else
            {
                for (int slot = 0; slot < RunnerSlotCount; slot++)
                {
                    RunnerController running = RunnerAt(slot);
                    if (running.Phase == RunnerPhase.Inactive || running.Phase == RunnerPhase.Out) continue;
                    int start = running.StartIndex;
                    int final = running.Phase == RunnerPhase.Scored ? 4 : running.LastTouchedIndex;
                    if (reason == PitchEndReason.HomeRun) final = 4;
                    else if (reason == PitchEndReason.GroundRuleDouble) final = Mathf.Min(4, Mathf.Max(final, start + 2));
                    if (final >= 4 && running.Phase != RunnerPhase.Scored) runs++;
                    else if (final >= 1 && final <= 3) newBases[final - 1] = true;
                    advanced += Mathf.Max(0, final - start);
                    if (slot == 0) batterBases = final;
                }
                if (IsThirdOut && (lastOutReason == PitchEndReason.ForceOut || batterOutBeforeFirst)) runs = 0;
            }
            situation.ApplyPlayResult(newBases[0], newBases[1], newBases[2], outsOnPlay, runs);
            lastPlaySummary = new PlaySummary(true, reason, outsOnPlay, runs, batterBases, runnerOutsOnPlay, advanced);
            playResolved = true;
            PlayResolved?.Invoke(lastPlaySummary);
        }

        /// <summary>타자 접촉과 시나리오 타구가 같이 쓰는 타구 시작. 공은 <paramref name="contactPosition"/>에 있어야 한다.</summary>
        private void StartBattedBall(Vector3 contactPosition, Vector3 exitVelocity)
        {
            BaseballEnvironmentConfig config = Config;
            // 타구 회전은 발사각에 비례하는 역회전(음수 발사각은 전진 회전)으로 근사한다.
            float launchDegrees = Mathf.Asin(Mathf.Clamp(exitVelocity.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            battedBackspinRpm = Mathf.Clamp(launchDegrees * config.BattedBackspinRpmPerDegree,
                -config.MaxBattedSpinRpm, config.MaxBattedSpinRpm);
            battedExitVelocity = exitVelocity;
            battedApexHeight = contactPosition.y;
            ball.Launch(exitVelocity, BallController.BackspinVector(exitVelocity, battedBackspinRpm));
            judge.Begin(contactPosition, elapsedSeconds);
            state = PlayState.BattedBallInFlight;
            liveBattedPlay = true;
            if (runner != null)
            {
                // 타자가 타석 위치에서 타자주자로 바뀐다. 이동은 다음 고정 단계부터다.
                runner.Begin(batter.transform.position);
                batter.SetVisible(false);
                // 포스: 타자주자는 1루로, 뒤 베이스가 모두 차 있는 누상 주자는 다음 베이스로 밀려나 자동으로 달린다.
                forced[0] = true;
                for (int slot = 1; slot < RunnerSlotCount; slot++)
                {
                    RunnerController onBase = RunnerAt(slot);
                    forced[slot] = forced[slot - 1] && onBase.IsLive;
                    if (forced[slot]) onBase.TryApplyDecision(RunnerDecision.Advance, out _);
                }
            }
            PublishEvaluation();
            previousBallPosition = contactPosition;
        }

        /// <summary>
        /// 3단계 수비 한 고정 단계: 수비수 이동 → 포구(이번 단계 공 경로 스윕) → 잡은 공 따라가기 → 아웃·세이프 판정.
        /// 플레이가 끝났으면 true다.
        /// </summary>
        private bool StepFielding(Vector3 ballFrom, Vector3 ballTo, float dt, BaseballEnvironmentConfig config)
        {
            Vector3 home = fieldLayout.HomePosition;
            foreach (FielderController fielder in fielders) fielder.Tick(dt, config, home);

            if (!ball.IsHeld && TryFindCatch(ballFrom, ballTo, config, out int catcher, out Vector3 catchPoint))
            {
                bool firstTouch = !battedBallFielded;
                bool caughtInAir = firstTouch && !ball.HasFirstTouch;
                battedBallFielded = true;
                ball.Hold(catcher, fielders[catcher].GlovePosition);
                BallFielded?.Invoke(catcher, caughtInAir);
                if (caughtInAir && CatchFlyBall()) return true;
                // 페어/파울이 정해지기 전에 잡은 땅볼은 잡은 지점으로 정한다(페어 지역에서 수비수가 먼저 닿으면 페어).
                if (firstTouch && judge.Call == BattedBallCall.None)
                {
                    judge.DecideOnFielderTouch(catchPoint);
                    BattedBallCalled?.Invoke(judge.Call);
                    if (pitchCall == PitchCall.None)
                        SetPitchCall(judge.Call == BattedBallCall.Foul ? PitchCall.Foul : PitchCall.InPlay);
                    if (judge.Call == BattedBallCall.Foul)
                    {
                        EndPitch(PitchEndReason.Foul);
                        return true;
                    }
                }
            }
            if (!ball.IsHeld)
            {
                deadBallTimer = 0f;
                return false;
            }
            FielderController holder = fielders[ball.HolderIndex];
            ball.MoveHeld(holder.GlovePosition);
            if (runner == null) return false;

            // 공을 가진 수비수 기준 아웃: 포스(밀려나는 베이스를 먼저 밟음), 태그(베이스를 벗어난 주자), 리터치 전 원래 베이스 태그.
            for (int slot = 0; slot < RunnerSlotCount; slot++)
            {
                RunnerController running = RunnerAt(slot);
                if (!running.IsLive) continue;
                int forcedBase = running.StartIndex + 1;
                bool offBase = running.Phase == RunnerPhase.Advancing || running.Phase == RunnerPhase.Returning;
                PitchEndReason outType = PitchEndReason.None;
                if (forced[slot] && running.LastTouchedIndex < forcedBase && IsOnBase(holder, forcedBase, config)) outType = PitchEndReason.ForceOut;
                else if (offBase && Flat(holder.Position - running.transform.position).magnitude <= config.TagRadius) outType = PitchEndReason.TagOut;
                else if (mustRetouch[slot] && IsOnBase(holder, running.StartIndex, config)) outType = PitchEndReason.TagOut;
                if (outType == PitchEndReason.None) continue;
                RecordOut(slot, outType);
                if (IsThirdOut)
                {
                    EndPitch(outType);
                    return true;
                }
            }
            if (NoLiveRunners())
            {
                EndPitch(outsOnPlay > 0 ? lastOutReason : PitchEndReason.RunScored);
                return true;
            }
            for (int slot = 0; slot < RunnerSlotCount; slot++)
            {
                if (RunnerAt(slot).IsLive && (RunnerAt(slot).Phase != RunnerPhase.Holding || mustRetouch[slot]))
                {
                    deadBallTimer = 0f;
                    return false;
                }
            }
            // 살아 있는 주자가 모두 베이스에 멈춰 있고 수비가 공을 가진 상태가 잠시 이어지면 플레이가 죽는다.
            // 그 사이 주자는 태그업·추가 진루를 시도할 수 있다.
            deadBallTimer += dt;
            if (deadBallTimer + 1e-4f < config.PlayDeadSeconds) return false;
            EndPitch(PitchEndReason.RunnerSafe);
            return true;
        }

        private float deadBallTimer;

        private bool IsOnBase(FielderController fielder, int baseIndex, BaseballEnvironmentConfig config) =>
            Flat(fielder.Position - fieldLayout.GetBasePosition((BaseId)(baseIndex % 4))).magnitude <= config.BaseCoverRadius;

        /// <summary>
        /// 뜬공 포구: 타자 아웃, 모든 포스가 풀리고, 베이스를 벗어났던 누상 주자는 시작 베이스로 돌아가 리터치해야 한다.
        /// 세 번째 아웃이거나 남은 주자가 없으면 플레이를 끝내고 true를 돌려준다.
        /// </summary>
        private bool CatchFlyBall()
        {
            if (runner == null)
            {
                outsOnPlay++;
                lastOutReason = PitchEndReason.FlyOut;
                EndPitch(PitchEndReason.FlyOut);
                return true;
            }
            RecordOut(0, PitchEndReason.FlyOut);
            System.Array.Clear(forced, 0, forced.Length);
            for (int slot = 1; slot < RunnerSlotCount; slot++)
            {
                RunnerController onBase = RunnerAt(slot);
                if (!onBase.IsLive) continue;
                if (onBase.Phase == RunnerPhase.Holding && onBase.LastTouchedIndex == onBase.StartIndex) continue;
                mustRetouch[slot] = true;
                onBase.ForceReturnTo(onBase.StartIndex);
            }
            if (IsThirdOut || NoLiveRunners())
            {
                EndPitch(PitchEndReason.FlyOut);
                return true;
            }
            return false;
        }

        /// <summary>이번 단계 공 경로가 수비수 몸 축에 포구 반경 안으로 가장 먼저 다가간 수비수를 찾는다.</summary>
        private bool TryFindCatch(Vector3 from, Vector3 to, BaseballEnvironmentConfig config, out int catcher, out Vector3 point)
        {
            catcher = -1;
            point = to;
            float bestT = float.PositiveInfinity;
            Vector2 a = new Vector2(from.x, from.z), b = new Vector2(to.x, to.z);
            Vector2 ab = b - a;
            for (int i = 0; i < fielders.Length; i++)
            {
                if (i == lastThrower && elapsedSeconds - lastThrowTime < ThrowerRecatchDelay) continue;
                Vector3 body = fielders[i].Position;
                Vector2 c = new Vector2(body.x, body.z);
                float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector2.Dot(c - a, ab) / ab.sqrMagnitude) : 0f;
                if ((a + ab * t - c).magnitude > config.CatchRadius) continue;
                float height = Mathf.Lerp(from.y, to.y, t) - body.y;
                if (height < -config.BallRadius || height > config.CatchReachHeight) continue;
                if (t >= bestT) continue;
                bestT = t;
                catcher = i;
                point = Vector3.Lerp(from, to, t);
            }
            return catcher >= 0;
        }

        /// <summary>공을 가진 수비수가 베이스 위 글러브 높이를 향해 회전 없이 던진다. 닿지 못하는 거리면 거부한다.</summary>
        private void PerformFielderThrow(int index, ThrowTarget target)
        {
            BaseballEnvironmentConfig config = Config;
            Vector3 glove = fielders[index].GlovePosition;
            Vector3 aim = fieldLayout.GetBasePosition(ToBase(target)) + Vector3.up * FielderController.GloveHeight;
            Vector3 flat = Flat(aim - glove);
            if (flat.magnitude < config.BaseCoverRadius + 1f)
            {
                lastRejectionReason = "이미 목표 베이스 가까이 있어 송구하지 않는다.";
                return;
            }
            // 던진 수비수 몸 앞에서 출발해 바로 다시 잡지 않게 한다.
            Vector3 from = glove + flat.normalized * (config.CatchRadius + 0.1f);
            var throwProfile = new PitchTypeProfile(PitchType.FourSeam, new Vector2(1f, 200f), 0f, 0f, 0f);
            if (!PitchPhysics.TrySolve(from, aim, config.ThrowSpeed, throwProfile, config, ball.AngularDamping, Time.fixedDeltaTime,
                    out Vector3 velocity, out _, out _, out string reason))
            {
                lastRejectionReason = "송구 거부: " + reason;
                return;
            }
            ball.Throw(from, velocity);
            lastThrower = index;
            lastThrowTime = elapsedSeconds;
            previousBallPosition = from;
            lastRejectionReason = string.Empty;
        }

        private static BaseId ToBase(ThrowTarget target)
        {
            switch (target)
            {
                case ThrowTarget.First: return BaseId.First;
                case ThrowTarget.Second: return BaseId.Second;
                case ThrowTarget.Third: return BaseId.Third;
                default: return BaseId.Home;
            }
        }

        private void PerformThrow()
        {
            if (state != PlayState.Ready)
            {
                lastRejectionReason = $"Ready 상태가 아니라 투구를 거부한다 (현재 상태: {state}).";
                Debug.LogWarning($"[PlayDirector] {lastRejectionReason}", this);
                return;
            }

            BaseballEnvironmentConfig config = Config;
            if (config == null)
            {
                lastRejectionReason = "설정 데이터(BaseballEnvironmentConfig)가 없어 투구를 거부한다.";
                Debug.LogError($"[PlayDirector] {lastRejectionReason}", this);
                return;
            }
            if (!config.TryValidate(out string configError))
            {
                lastRejectionReason = configError;
                return;
            }

            Vector3 origin = fieldLayout.PitchOriginPosition;
            Vector3 machineTarget = fieldLayout.PitchTargetPosition;
            Vector3 machinePlate = StrikeZone.ToPlate(fieldLayout, machineTarget);
            Vector2 aim;
            if (pitchLocationRequested)
            {
                aim = pendingPitchLocation;
                if (!BaseballEnvironmentConfig.IsFinite(aim.x) || !BaseballEnvironmentConfig.IsFinite(aim.y) ||
                    Mathf.Abs(aim.x) > 2f || aim.y < 0f || aim.y > 3f)
                {
                    lastRejectionReason = "투구 목표 위치는 좌우 ±2 m, 높이 0~3 m 안의 유한한 값이어야 한다.";
                    return;
                }
            }
            else if (config.PitchLocationMode == PitchLocationMode.RandomAroundZone)
            {
                Vector2 spread = config.PitchLocationSpread;
                aim = new Vector2(spread.x * Gaussian(),
                    0.5f * (config.StrikeZoneBottom + config.StrikeZoneTop) + spread.y * Gaussian());
            }
            else
            {
                aim = new Vector2(machinePlate.x, machinePlate.z);
            }
            // PitchTarget 평면에서 목표 위치를 지나도록 옮긴다. 기계 목표면 PitchTarget 그대로다.
            Vector3 plateRight = Vector3.Cross(Vector3.up, Flat(fieldLayout.PitcherPlatePosition - fieldLayout.HomePosition).normalized);
            Vector3 target = machineTarget + plateRight * (aim.x - machinePlate.x) + Vector3.up * (aim.y - machinePlate.z);

            Vector3 velocity, spin;
            float arrivalSeconds;
            string rejection;
            PitchCommand thrown;
            if (pitchCommandRequested)
            {
                PitchCommand command = pendingPitchCommand;
                if (!BaseballEnvironmentConfig.IsFinite(command.Speed) || command.Speed < 5f || command.Speed > 60f)
                {
                    lastRejectionReason = "투구 속력은 5~60 m/s 안의 유한한 값이어야 한다.";
                    return;
                }
                bool solved = PitchPhysics.TrySolve(origin, target, command.Speed, config.GetPitchProfile(command.Type), config,
                    ball.AngularDamping, Time.fixedDeltaTime, out velocity, out spin, out arrivalSeconds, out rejection);
                thrown = command;
                if (!solved) velocity = Vector3.zero;
            }
            else
            {
                if (!TrySolvePitch(origin, target, config.PitchSpeed, config, ball.AngularDamping, Time.fixedDeltaTime,
                        out velocity, out spin, out arrivalSeconds, out rejection))
                    velocity = Vector3.zero;
                thrown = new PitchCommand(PitchType.FourSeam, config.PitchSpeed, aim);
            }
            if (velocity == Vector3.zero)
            {
                lastRejectionReason = rejection;
                Debug.LogWarning($"[PlayDirector] 투구 거부: {rejection}", this);
                return;
            }
            lastPitch = thrown;
            hasLastPitch = true;

            // 발사구와 공 시작 위치를 맞춘 뒤 발사한다.
            ball.ResetTo(origin, Quaternion.identity);
            ball.Launch(velocity, spin);
            if (batter != null) batter.SetPitchReference(arrivalSeconds);
            PitchArrivalSeconds = arrivalSeconds;

            state = PlayState.PitchInFlight;
            elapsedSeconds = 0f;
            centerPassRecorded = false;
            centerPassError = 0f;
            lastEndReason = PitchEndReason.None;
            lastRejectionReason = string.Empty;
            previousBallPosition = origin;
            ClearPitchCall();
            aimLocation = aim;

            pitchTargetSnapshot = target;
            Vector3 travel = target - origin;
            travel.y = 0f;
            pitchTravelDirection = travel.sqrMagnitude > 0.0001f ? travel.normalized : Vector3.forward;
        }

        private void EndPitch(PitchEndReason reason)
        {
            state = PlayState.Ended;
            lastEndReason = reason;
            autoRepeatTimer = 0f;
            completedPitchCount++;
            ball.MarkFlightEnded();
            PublishEvaluation();
            if (pitchCall == PitchCall.None)
            {
                // 판정이 나지 않은 타구(시간 초과 등)는 인플레이, 치지 못한 공은 스윙 여부와 존 통과로 정한다.
                SetPitchCall((batter != null && batter.HasContact) || scriptedBattedBall ? PitchCall.InPlay
                    : swingOffered ? PitchCall.SwingingStrike
                    : pitchInZone ? PitchCall.CalledStrike : PitchCall.Ball);
            }
            if (liveBattedPlay && !playResolved) ResolvePlay(reason);
        }

        /// <summary>투구 판정을 확정하고 볼카운트에 반영한 뒤 알린다(처리기는 갱신된 카운트를 읽는다).</summary>
        private void SetPitchCall(PitchCall call)
        {
            pitchCall = call;
            situation.RecordPitchCall(call);
            PitchCalled?.Invoke(call);
        }

        private void ClearPitchCall()
        {
            pitchCall = PitchCall.None;
            pitchInZone = swingOffered = pitchPassedPlate = hasPlateLocation = false;
            aimLocation = plateLocation = Vector2.zero;
        }

        /// <summary>
        /// 이번 고정 단계의 공 경로가 규칙 존에 닿았는지 공 반지름 1/4 간격으로 검사하고,
        /// 플레이트 앞 모서리 평면을 지나는 위치(Statcast 방식)를 기록한다. 바운드된 공은 존을 지나도 볼이다.
        /// </summary>
        private void TrackPitchOverPlate(Vector3 from, Vector3 to, BaseballEnvironmentConfig config)
        {
            Vector3 a = StrikeZone.ToPlate(fieldLayout, from);
            Vector3 b = StrikeZone.ToPlate(fieldLayout, to);
            float radius = config.BallRadius;
            if (!hasPlateLocation && a.y >= StrikeZone.PlateDepth && b.y < StrikeZone.PlateDepth)
            {
                float t = (a.y - StrikeZone.PlateDepth) / (a.y - b.y);
                plateLocation = new Vector2(Mathf.Lerp(a.x, b.x, t), Mathf.Lerp(a.z, b.z, t));
                hasPlateLocation = true;
            }
            if (b.y < -radius) pitchPassedPlate = true;
            if (pitchInZone || ball.HasTouchedGround) return;
            if (Mathf.Max(a.y, b.y) < -radius || Mathf.Min(a.y, b.y) > StrikeZone.PlateDepth + radius) return;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / (radius * 0.25f)));
            for (int i = 0; i <= steps && !pitchInZone; i++)
                pitchInZone = StrikeZone.Intersects(Vector3.Lerp(a, b, (float)i / steps), radius,
                    config.StrikeZoneBottom, config.StrikeZoneTop);
        }

        /// <summary>표준 정규분포 표본(Box-Muller). 환경 전용 난수원을 쓴다.</summary>
        private float Gaussian()
        {
            double u1 = 1.0 - pitchRandom.NextDouble();
            double u2 = pitchRandom.NextDouble();
            return (float)(System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2));
        }

        private static Vector3 Flat(Vector3 value) => new Vector3(value.x, 0f, value.z);

        private void PublishEvaluation()
        {
            if (evaluationPublished || batter == null) return;
            evaluationPublished = true;
            BattingEvaluated?.Invoke(batter.GetEvaluation());
        }

        private void PerformReset()
        {
            resetRequested = false;
            pitchRequested = false;
            pitchLocationRequested = false;
            pitchCommandRequested = false;
            hasLastPitch = false;
            PitchArrivalSeconds = 0f;
            swingRequested = false;
            setupRequested = false;
            runnerDecisionRequested = false;
            evaluationPublished = false;
            if (judge != null) judge.Clear();
            battedExitVelocity = Vector3.zero;
            battedBackspinRpm = 0f;
            battedApexHeight = 0f;
            ClearPitchCall();
            if (batter != null) batter.ResetState();
            if (runner != null) runner.ResetState();
            battedBallFielded = false;
            scriptedBattedBall = false;
            scriptedBattedBallRequested = false;
            lastThrower = -1;
            lastThrowTime = -1f;
            if (HasFielders)
            {
                foreach (FielderController fielder in fielders) fielder.ResetState(fieldLayout.HomePosition);
                System.Array.Clear(pendingFielderMoves, 0, pendingFielderMoves.Length);
                System.Array.Clear(pendingThrows, 0, pendingThrows.Length);
            }

            // 한 투구의 플레이 기록을 지우고, 끝난 타석·반 이닝이면 새로 연 뒤 누상 주자를 세운다.
            liveBattedPlay = playResolved = false;
            deadBallTimer = 0f;
            outsOnPlay = runsOnPlay = runnerOutsOnPlay = 0;
            batterOutBeforeFirst = false;
            lastOutReason = PitchEndReason.None;
            System.Array.Clear(forced, 0, forced.Length);
            System.Array.Clear(mustRetouch, 0, mustRetouch.Length);
            System.Array.Clear(scoreCounted, 0, scoreCounted.Length);
            System.Array.Clear(indexedDecisionRequested, 0, indexedDecisionRequested.Length);
            if (newPlateAppearanceRequested)
            {
                newPlateAppearanceRequested = false;
                situation.EndPlateAppearance();
            }
            situation.BeginPlateAppearanceIfNeeded();
            PlaceBaseRunners();

            Vector3 originPosition = fieldLayout != null ? fieldLayout.PitchOriginPosition : Vector3.zero;
            if (ball != null)
            {
                ball.ResetTo(originPosition, Quaternion.identity);
            }

            state = PlayState.Ready;
            elapsedSeconds = 0f;
            autoRepeatTimer = 0f;
            centerPassRecorded = false;
            centerPassError = 0f;
            lastEndReason = PitchEndReason.None;
            lastRejectionReason = string.Empty;
            previousBallPosition = originPosition;
            PlayReset?.Invoke();
        }

        /// <summary>읽기 전용 상태 스냅샷(docs/architecture.md 4.2). HUD 등 외부 읽기 전용이다.</summary>
        public PitchSnapshot GetSnapshot()
        {
            float autoRepeatRemaining = autoRepeatEnabled && state == PlayState.Ended
                ? Mathf.Max(0f, autoRepeatIntervalSeconds - autoRepeatTimer)
                : 0f;

            return new PitchSnapshot(
                state,
                elapsedSeconds,
                ball != null ? ball.Velocity.magnitude : 0f,
                ball != null ? ball.Position : Vector3.zero,
                ball != null ? ball.Velocity : Vector3.zero,
                centerPassRecorded,
                centerPassError,
                lastEndReason,
                lastRejectionReason,
                completedPitchCount,
                autoRepeatEnabled,
                autoRepeatRemaining);
        }

        private bool TryValidateReferences(out string error)
        {
            if (fieldLayout == null)
            {
                error = "FieldLayout 참조가 없다.";
                return false;
            }

            if (ball == null)
            {
                error = "BallController 참조가 없다.";
                return false;
            }

            if (!fieldLayout.TryValidate(out string fieldError))
            {
                error = $"FieldLayout 검증 실패:\n{fieldError}";
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// 이전·현재 물리 위치를 잇는 선분이 <paramref name="planePoint"/>를 지나는
        /// <paramref name="planeNormal"/> 법선 평면을 지나는 교차점을 계산한다. 빠른 공이 한 틱
        /// 사이에 평면을 통과해도 놓치지 않기 위한 스윕 검사다(docs/environment-spec.md 5.1).
        /// </summary>
        public static bool TryComputePlaneCrossing(
            Vector3 previous, Vector3 current, Vector3 planePoint, Vector3 planeNormal,
            out Vector3 crossingPoint)
        {
            crossingPoint = current;
            Vector3 segment = current - previous;
            float denom = Vector3.Dot(segment, planeNormal);
            if (Mathf.Abs(denom) < 1e-6f)
            {
                return false;
            }

            float t = Vector3.Dot(planePoint - previous, planeNormal) / denom;
            if (t < 0f || t > 1f)
            {
                return false;
            }

            crossingPoint = previous + (segment * t);
            return true;
        }

        /// <summary>
        /// 실제 비행과 같은 고정 단계 적분(중력 + <see cref="BallController.AerodynamicAcceleration"/>,
        /// 반암시적 오일러)으로 <paramref name="target"/>을 지나는 가장 평평한 발사 속도를 찾는다.
        /// 공기 역학을 끄면 진공 탄도가 된다. <paramref name="arrivalSeconds"/>는 목표 평면 도달 시각이며
        /// 타격 타이밍 기준으로 쓴다. 목표에 닿을 수 없으면 false와 이유를 돌려준다.
        /// </summary>
        public static bool TrySolvePitch(Vector3 origin, Vector3 target, float speed, BaseballEnvironmentConfig config,
            float angularDamping, float dt, out Vector3 velocity, out Vector3 spin, out float arrivalSeconds,
            out string rejectionReason)
        {
            velocity = spin = Vector3.zero;
            arrivalSeconds = 0f;
            rejectionReason = string.Empty;
            Vector3 flat = new Vector3(target.x - origin.x, 0f, target.z - origin.z);
            float distance = flat.magnitude;
            if (distance < 0.001f || speed <= 0f || dt <= 0f)
            {
                rejectionReason = "투구 시작점-목표 수평 거리와 속력은 0보다 커야 한다.";
                return false;
            }
            Vector3 direction = flat / distance;

            // 낮은 각도부터 올려 목표 높이를 처음 넘는 구간을 찾고(가장 평평한 해), 이분법으로 좁힌다.
            float low = -30f, high = float.NaN;
            for (float angle = -30f; angle <= 60f; angle += 0.5f)
            {
                if (SimulatePitch(angle, out float height, out _) && height >= target.y) { high = angle; break; }
                low = angle;
            }
            if (float.IsNaN(high) || high <= -30f)
            {
                rejectionReason = $"현재 속력 {speed:F1} m/s로는 목표에 도달할 수 없다 (수평 거리 {distance:F2} m). " +
                    "속력을 높이거나 거리를 좁혀야 한다.";
                return false;
            }
            for (int i = 0; i < 40; i++)
            {
                float middle = 0.5f * (low + high);
                if (SimulatePitch(middle, out float height, out _) && height >= target.y) high = middle;
                else low = middle;
            }
            SimulatePitch(high, out _, out arrivalSeconds);
            velocity = LaunchVelocity(high);
            spin = BallController.BackspinVector(velocity, config.PitchBackspinRpm);
            return true;

            Vector3 LaunchVelocity(float degrees) =>
                (direction * Mathf.Cos(degrees * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(degrees * Mathf.Deg2Rad)) * speed;

            bool SimulatePitch(float degrees, out float heightAtTarget, out float time)
            {
                Vector3 v = LaunchVelocity(degrees);
                Vector3 w = BallController.BackspinVector(v, config.PitchBackspinRpm);
                Vector3 p = origin;
                heightAtTarget = time = 0f;
                for (int step = 0; step < 1000; step++)
                {
                    Vector3 acceleration = Physics.gravity;
                    if (config.AerodynamicsEnabled) acceleration += BallController.AerodynamicAcceleration(v, w, config);
                    v += acceleration * dt;
                    w *= Mathf.Max(0f, 1f - angularDamping * dt);
                    Vector3 next = p + v * dt;
                    float before = Vector3.Dot(p - origin, direction);
                    float after = Vector3.Dot(next - origin, direction);
                    if (after >= distance)
                    {
                        float f = (distance - before) / (after - before);
                        heightAtTarget = Mathf.Lerp(p.y, next.y, f);
                        time = (step + f) * dt;
                        return true;
                    }
                    if (next.y < origin.y - 50f || after < before) return false;
                    p = next;
                }
                return false;
            }
        }

        /// <summary>
        /// 진공 탄도 해석해. 빌더의 배치 검증 보고서가 쓰며, 실제 투구는 <see cref="TrySolvePitch"/>를 쓴다.
        /// <paramref name="speed"/> 크기의 초기 속도로 <paramref name="origin"/>에서 발사해
        /// Unity 중력 아래 포물선으로 <paramref name="target"/>을 지나는 속도 벡터를 계산한다.
        ///
        /// 같은 수평 거리·높이차에는 일반적으로 낮고 평평한 탄도와 높은 포물선(lob) 두 해가
        /// 있다. 빠르고 낮은 투구를 요구하므로(docs/environment-spec.md 5.1) 더 평평한 해
        /// (작은 발사각)를 선택한다. 목표에 도달할 수 없으면(판별식이 음수) false와 이유를 돌려준다.
        /// </summary>
        public static bool TryComputeLaunchVelocity(
            Vector3 origin, Vector3 target, float speed, float gravityMagnitude,
            out Vector3 velocity, out string rejectionReason)
        {
            velocity = Vector3.zero;
            rejectionReason = string.Empty;

            Vector3 horizontalDelta = new Vector3(target.x - origin.x, 0f, target.z - origin.z);
            float horizontalDistance = horizontalDelta.magnitude;
            float verticalDelta = target.y - origin.y;

            if (horizontalDistance < 0.001f)
            {
                rejectionReason = "발사 시작점과 목표점의 수평 거리가 0에 가까워 방향을 정할 수 없다.";
                return false;
            }

            if (speed <= 0f)
            {
                rejectionReason = $"투구 속력은 0보다 커야 한다. 현재 {speed} m/s.";
                return false;
            }

            Vector3 horizontalDirection = horizontalDelta / horizontalDistance;

            if (gravityMagnitude <= 1e-4f)
            {
                Vector3 direct = target - origin;
                if (direct.sqrMagnitude < 1e-6f)
                {
                    rejectionReason = "발사 시작점과 목표점이 같다.";
                    return false;
                }

                velocity = direct.normalized * speed;
                return true;
            }

            float d = horizontalDistance;
            float h = verticalDelta;
            float v2 = speed * speed;
            float a = (gravityMagnitude * d * d) / (2f * v2);

            if (a < 1e-6f)
            {
                rejectionReason = "속력이 비정상적으로 커서 탄도 계수를 계산할 수 없다.";
                return false;
            }

            float discriminant = (d * d) - (4f * a * (h + a));
            if (discriminant < 0f)
            {
                rejectionReason =
                    $"현재 속력 {speed:F1} m/s로는 목표에 도달할 수 없다 " +
                    $"(수평 거리 {d:F2} m, 높이차 {h:F2} m). 속력을 높이거나 거리를 좁혀야 한다.";
                return false;
            }

            float sqrtDiscriminant = Mathf.Sqrt(discriminant);
            // 두 해 중 작은 접선값이 더 평평하고 빠른(낮은 탄도) 해다.
            float tanThetaLow = (d - sqrtDiscriminant) / (2f * a);
            float theta = Mathf.Atan(tanThetaLow);

            float horizontalSpeed = speed * Mathf.Cos(theta);
            float verticalSpeed = speed * Mathf.Sin(theta);

            velocity = (horizontalDirection * horizontalSpeed) + (Vector3.up * verticalSpeed);
            return true;
        }
    }
}
