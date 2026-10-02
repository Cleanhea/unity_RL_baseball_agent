using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 환경 공통 계약 값이 모이는 파일이다.
    /// docs/architecture.md 3.1은 상태/결과 열거형, 명령 값, 이벤트 값, 읽기 전용 스냅샷을
    /// 이 파일이 소유하도록 정의한다.
    ///
    /// 구현 계획 단계 1(야구장과 기본 배치)에서 실제로 사용하는 값은 베이스 식별자뿐이었다.
    /// 이번 피칭머신 작업(직구 중앙 통과, 재투구, 초기화)에서 <see cref="PlayState"/>,
    /// <see cref="PitchEndReason"/>, <see cref="PitchSnapshot"/>을 추가한다. 타격·주루·수비가
    /// 쓰는 값(SwingCommand, BaseReached 등)은 해당 단계에서 추가한다.
    /// </summary>
    public readonly struct SwingCommand
    {
        public SwingCommand(float sprayDegrees, float launchDegrees)
        { SprayDegrees = sprayDegrees; LaunchDegrees = launchDegrees; }
        public float SprayDegrees { get; }
        public float LaunchDegrees { get; }
    }

    /// <summary>Offsets from the task's reference stance and body-relative grip, in metres.</summary>
    public readonly struct BatterSetupCommand
    {
        public BatterSetupCommand(Vector2 stanceOffset, Vector3 gripOffset)
        { StanceOffset = stanceOffset; GripOffset = gripOffset; }
        public Vector2 StanceOffset { get; }
        public Vector3 GripOffset { get; }
    }

    /// <summary>Task diagnostics, not an RL reward. Unavailable timing/distance must use their flags.</summary>
    public readonly struct BattingEvaluation
    {
        public BattingEvaluation(BatterSetupCommand setup, Vector3 position, Vector3 grip,
            Vector3 tip, bool swung, bool hit, bool timingAvailable, float timingError,
            float angleError, bool distanceAvailable, float closestDistance, float contactTime,
            Vector3 exitVelocity, Vector4 scores)
        {
            Setup = setup; BatterPosition = position; GripPosition = grip; BatTipPosition = tip;
            HasSwung = swung; HasContact = hit; HasTimingReference = timingAvailable;
            TimingErrorSeconds = timingError; SwingAngleErrorDegrees = angleError;
            HasClosestDistance = distanceAvailable; ClosestDistance = closestDistance;
            ContactTime = contactTime; ExitVelocity = exitVelocity; Scores = scores;
        }
        public BatterSetupCommand Setup { get; }
        public Vector3 BatterPosition { get; }
        public Vector3 GripPosition { get; }
        public Vector3 BatTipPosition { get; }
        public bool HasSwung { get; }
        public bool HasContact { get; }
        public bool HasTimingReference { get; }
        public float StanceErrorMetres => Setup.StanceOffset.magnitude;
        public float GripErrorMetres => Setup.GripOffset.magnitude;
        /// <summary>Negative = early; positive = late, relative to central target arrival.</summary>
        public float TimingErrorSeconds { get; }
        public float SwingAngleErrorDegrees { get; }
        public bool HasClosestDistance { get; }
        public float ClosestDistance { get; }
        public float ContactTime { get; }
        public Vector3 ExitVelocity { get; }
        /// <summary>x stance, y grip, z timing, w angle; each 0..1. No aggregate reward.</summary>
        public Vector4 Scores { get; }
    }

    public enum BaseId
    {
        /// <summary>홈 플레이트. 주루 경로의 시작이자 득점 검증 모드의 최종 목표다.</summary>
        Home = 0,

        /// <summary>1루. 기본 판정 모드의 최종 목표다.</summary>
        First = 1,

        /// <summary>2루.</summary>
        Second = 2,

        /// <summary>3루.</summary>
        Third = 3,
    }

    /// <summary>
    /// 투구와 타구 관찰 상태. 주루·수비가 없어 RunnerDefense 대신 BattedBallInFlight를 쓴다.
    /// </summary>
    public enum PlayState
    {
        /// <summary>공이 투구 시작점에 정지해 있고 새 투구를 받을 수 있다.</summary>
        Ready = 0,

        /// <summary>공이 발사되어 비행 중이다.</summary>
        PitchInFlight = 1,

        /// <summary>이번 투구가 끝났다. 다음 투구 전에 초기화가 필요하다.</summary>
        Ended = 2,
        BattedBallInFlight = 3,
    }

    /// <summary>한 번의 투구가 끝난 이유.</summary>
    public enum PitchEndReason
    {
        /// <summary>아직 끝나지 않았거나 초기화된 상태다.</summary>
        None = 0,

        /// <summary>목표 평면을 지나 뒤쪽 여유 거리까지 통과했다.</summary>
        PassedTarget = 1,

        /// <summary>경기 경계를 벗어났다.</summary>
        OutOfPlay = 2,

        /// <summary>투구 제한 시간을 넘겼다.</summary>
        Timeout = 3,

        /// <summary>타자주자가 3루를 거쳐 홈을 밟았다.</summary>
        RunScored = 4,

        /// <summary>파울 타구. 공이 죽고 플레이가 끝난다.</summary>
        Foul = 5,

        /// <summary>페어 지역 위로 펜스를 넘어 공중으로 경기장을 벗어났다.</summary>
        HomeRun = 6,

        /// <summary>페어 타구가 바운드된 뒤 펜스를 넘었다. 타자에게 2루가 주어진다.</summary>
        GroundRuleDouble = 7,

        /// <summary>타구가 땅이나 펜스에 닿기 전에 수비수가 잡았다(파울 지역 포함). 타자 아웃.</summary>
        FlyOut = 8,

        /// <summary>타자주자가 1루를 밟기 전에 공을 가진 수비수가 1루를 밟았다.</summary>
        ForceOut = 9,

        /// <summary>베이스를 벗어난 주자를 공을 가진 수비수가 태그했다.</summary>
        TagOut = 10,

        /// <summary>주자가 베이스에 멈춰 있고 수비수가 공을 가져 플레이가 죽었다. 주자는 그 베이스에서 세이프다.</summary>
        RunnerSafe = 11,
    }

    /// <summary>
    /// 3단계 수비수 9명의 역할(실제 야구 수비 번호 순서). 값은 수비수 Agent 관측의 역할 원-핫 위치다.
    /// 투수 역할은 투구가 아니라 타구 뒤 수비만 맡는다(투구는 PitcherAgent).
    /// </summary>
    public enum FielderRole
    {
        Pitcher = 0,
        Catcher = 1,
        FirstBase = 2,
        SecondBase = 3,
        ThirdBase = 4,
        Shortstop = 5,
        LeftField = 6,
        CenterField = 7,
        RightField = 8,
    }

    /// <summary>수비수의 송구 목표. None은 송구하지 않는다.</summary>
    public enum ThrowTarget
    {
        None = 0,
        First = 1,
        Second = 2,
        Third = 3,
        Home = 4,
    }

    /// <summary>
    /// 구종. 회전량·회전 방향·구속 범위는 <see cref="BaseballEnvironmentConfig.GetPitchProfile"/>이 정한다.
    /// 투수는 우투수로 가정한다(팔 쪽 = 3루 쪽 -X).
    /// </summary>
    public enum PitchType
    {
        FourSeam = 0,
        TwoSeam = 1,
        Curve = 2,
        Slider = 3,
        Changeup = 4,
    }

    /// <summary>
    /// 구종·구속·홈플레이트 목표 위치를 지정한 투구 명령. 위치는 (좌우 x, 지면 기준 높이) m이며 x 양수가 1루 쪽이다.
    /// 스크립트 투수와 투수 Agent가 같은 <see cref="PlayDirector.RequestThrowPitch(PitchCommand)"/>를 쓴다.
    /// </summary>
    public readonly struct PitchCommand
    {
        public PitchCommand(PitchType type, float speed, Vector2 plateLocation)
        { Type = type; Speed = speed; PlateLocation = plateLocation; }
        public PitchType Type { get; }
        /// <summary>발사 속력(m/s).</summary>
        public float Speed { get; }
        public Vector2 PlateLocation { get; }
    }

    /// <summary>학습 단계. 단계마다 씬과 참여 Agent가 다르다(docs/training-curriculum.md).</summary>
    public enum TrainingStage
    {
        /// <summary>1단계: 타자만 학습. 스크립트 투수가 존 중앙으로 직구를 던지고 구속만 바꾼다.</summary>
        Batter = 1,

        /// <summary>2단계: 타자와 투수 Agent를 함께 학습한다.</summary>
        BatterPitcher = 2,

        /// <summary>3단계: 타자·주자·투수·수비 5명을 함께 학습한다.</summary>
        FullTeam = 3,
    }

    /// <summary>설정의 기본 투구 위치 방식. 명령으로 위치를 지정하면 이 설정보다 우선한다.</summary>
    public enum PitchLocationMode
    {
        /// <summary>항상 PitchTarget으로 던진다(피칭머신, 기존 동작).</summary>
        MachineTarget = 0,

        /// <summary>규칙 존 중심 둘레 정규분포로 목표를 뽑는다(투수의 제구 산포 근사).</summary>
        RandomAroundZone = 1,
    }

    /// <summary>한 투구의 판정. 투구가 끝나거나 타구가 판정될 때 정해진다.</summary>
    public enum PitchCall
    {
        None = 0,

        /// <summary>스윙하지 않았고 공이 존에 닿지 않았다(바운드 후 존 통과 포함).</summary>
        Ball = 1,

        /// <summary>스윙하지 않았고 공의 일부가 존에 닿았다.</summary>
        CalledStrike = 2,

        /// <summary>스윙했지만 맞히지 못했다. 위치와 무관하다.</summary>
        SwingingStrike = 3,

        /// <summary>맞힌 타구가 파울로 판정됐다.</summary>
        Foul = 4,

        /// <summary>맞힌 타구가 페어(홈런·인정 2루타 포함)로 판정됐다.</summary>
        InPlay = 5,
    }

    /// <summary>
    /// 투구 판정과 위치. 위치는 (좌우 x, 지면 기준 높이)이며 x 양수가 1루 쪽이다.
    /// 목표 위치는 PitchTarget 평면 기준, 실제 위치는 홈플레이트 앞 모서리 평면(Statcast 방식)을 지날 때 측정한다.
    /// </summary>
    public readonly struct PitchCallSnapshot
    {
        public PitchCallSnapshot(PitchCall call, bool inZone, bool swingOffered, Vector2 aimLocation,
            bool hasPlateLocation, Vector2 plateLocation, float zoneBottom, float zoneTop)
        {
            Call = call; InZone = inZone; SwingOffered = swingOffered; AimLocation = aimLocation;
            HasPlateLocation = hasPlateLocation; PlateLocation = plateLocation; ZoneBottom = zoneBottom; ZoneTop = zoneTop;
        }
        /// <summary>판정에 쓴 존 아래/위 높이(m). 존 좌우는 플레이트 폭 17 in(<see cref="StrikeZone.PlateWidth"/>)이다.</summary>
        public float ZoneBottom { get; }
        public float ZoneTop { get; }
        public PitchCall Call { get; }
        /// <summary>바운드 전에 공의 일부가 규칙 존에 닿았으면 true다.</summary>
        public bool InZone { get; }
        /// <summary>공이 홈플레이트를 완전히 지나기 전에 스윙을 시작했으면 true다. 그 뒤의 스윙은 판정에 쓰지 않는다.</summary>
        public bool SwingOffered { get; }
        public Vector2 AimLocation { get; }
        public bool HasPlateLocation { get; }
        public Vector2 PlateLocation { get; }
    }

    /// <summary>타구 판정. 접촉 전과 판정 대기 중은 None이다.</summary>
    public enum BattedBallCall
    {
        None = 0,

        /// <summary>페어. 플레이는 계속된다.</summary>
        Fair = 1,
        Foul = 2,
        HomeRun = 3,
        GroundRuleDouble = 4,

        /// <summary>펜스 Collider 없이 펜스 높이 아래로 경계를 넘었다. 씬에 펜스가 없을 때만 생긴다.</summary>
        OutOfPlay = 5,
    }

    /// <summary>
    /// 타구 관측값(Statcast 방식). 방향 각은 +Z 기준이며 양수가 1루 쪽이다.
    /// 첫 닿음은 지면 또는 펜스이며, 홈런은 펜스를 넘은 지점을 기록한다.
    /// </summary>
    public readonly struct BattedBallSnapshot
    {
        public BattedBallSnapshot(BattedBallCall call, Vector3 exitVelocity, float backspinRpm, float apexHeight,
            bool hasFirstTouch, Vector3 firstTouchPoint, float hangTime, Vector3 homePosition = default)
        {
            Call = call; ExitVelocity = exitVelocity; BackspinRpm = backspinRpm; ApexHeight = apexHeight;
            HasFirstTouch = hasFirstTouch; FirstTouchPoint = firstTouchPoint; HangTime = hangTime;
            HomePosition = homePosition;
        }
        public BattedBallCall Call { get; }
        public Vector3 ExitVelocity { get; }
        public float ExitSpeed => ExitVelocity.magnitude;
        public float LaunchAngleDegrees => ExitSpeed > 0f ? Mathf.Asin(ExitVelocity.y / ExitSpeed) * Mathf.Rad2Deg : 0f;
        public float SprayAngleDegrees => Mathf.Atan2(ExitVelocity.x, ExitVelocity.z) * Mathf.Rad2Deg;
        /// <summary>양수 역회전, 음수 전진 회전.</summary>
        public float BackspinRpm { get; }
        public float ApexHeight { get; }
        /// <summary>지면·펜스에 처음 닿았거나 펜스를 넘었으면 true다.</summary>
        public bool HasFirstTouch { get; }
        /// <summary>첫 닿음의 월드 좌표.</summary>
        public Vector3 FirstTouchPoint { get; }
        /// <summary>비거리 측정 기준이 되는 홈의 월드 좌표.</summary>
        public Vector3 HomePosition { get; }
        /// <summary>홈에서 첫 닿음 지점까지 수평 거리(m). 뜬공의 비거리다.</summary>
        public float Distance => HasFirstTouch
            ? new Vector2(FirstTouchPoint.x - HomePosition.x, FirstTouchPoint.z - HomePosition.z).magnitude : 0f;
        /// <summary>접촉부터 첫 닿음까지 시간(s).</summary>
        public float HangTime { get; }
    }

    /// <summary>타자주자의 이동 단계. 이동 자체는 환경이 제공하고 판단만 명령으로 받는다.</summary>
    public enum RunnerPhase
    {
        /// <summary>타구 접촉 전. 주자가 없다.</summary>
        Inactive = 0,

        /// <summary>다음 베이스로 달리는 중.</summary>
        Advancing = 1,

        /// <summary>마지막으로 밟은 베이스로 되돌아가는 중.</summary>
        Returning = 2,

        /// <summary>베이스에 멈춰 판단을 기다린다.</summary>
        Holding = 3,

        /// <summary>홈을 밟았다.</summary>
        Scored = 4,

        /// <summary>이번 플레이에서 아웃됐다. 초기화 전까지 경로에서 빠진다.</summary>
        Out = 5,
    }

    /// <summary>1루 이후 주자가 내리는 판단. 수동·스크립트·향후 학습 입력이 같은 값을 쓴다.</summary>
    public enum RunnerDecision
    {
        /// <summary>진루. 멈춰 있으면 다음 베이스로 출발하고, 달리는 중이면 앞 베이스를 멈추지 않고 돈다.</summary>
        Advance = 0,

        /// <summary>귀루. 마지막으로 밟은 베이스로 돌아간다. 1루 첫 구간에서는 1루 이후 진루만 취소한다.</summary>
        Return = 1,
    }

    /// <summary>
    /// 타자주자 읽기 전용 상태. <see cref="BaseId.Home"/>은 첫 구간(Advancing)의 출발과
    /// 득점(Scored)에 모두 쓰이므로 <see cref="Phase"/>로 구분한다.
    /// </summary>
    public readonly struct RunnerSnapshot
    {
        public RunnerSnapshot(RunnerPhase phase, Vector3 position, BaseId lastTouchedBase,
            BaseId nextBase, BaseId targetBase, float lastTouchTime, BaseId startBase = BaseId.Home)
        {
            Phase = phase; Position = position; LastTouchedBase = lastTouchedBase;
            NextBase = nextBase; TargetBase = targetBase; LastTouchTime = lastTouchTime; StartBase = startBase;
        }
        public RunnerPhase Phase { get; }
        /// <summary>이번 플레이를 시작한 베이스. 타자주자는 홈, 누상 주자는 그 베이스다.</summary>
        public BaseId StartBase { get; }
        /// <summary>경로 위에서 살아 있으면(진루·귀루·멈춤) true다.</summary>
        public bool IsLive => Phase == RunnerPhase.Advancing || Phase == RunnerPhase.Returning || Phase == RunnerPhase.Holding;
        public Vector3 Position { get; }
        public BaseId LastTouchedBase { get; }
        /// <summary>지금 향하는 베이스. 멈춰 있으면 서 있는 베이스다.</summary>
        public BaseId NextBase { get; }
        /// <summary>판단 없이 멈출 베이스.</summary>
        public BaseId TargetBase { get; }
        /// <summary>마지막으로 베이스를 밟은 시각(투구 시작 기준 s). 아직 없으면 -1.</summary>
        public float LastTouchTime { get; }
    }

    /// <summary>
    /// <see cref="PlayDirector"/>가 제공하는 읽기 전용 상태 스냅샷(docs/architecture.md 4.2).
    /// 외부 코드(DebugPresenter 등)는 이 값을 통해서만 상태를 읽고, 내부 필드를 직접 바꿀 수 없다.
    /// </summary>
    public readonly struct PitchSnapshot
    {
        public PitchSnapshot(
            PlayState state,
            float elapsedSeconds,
            float ballSpeed,
            Vector3 ballPosition,
            Vector3 ballVelocity,
            bool hasCenterPassError,
            float centerPassError,
            PitchEndReason endReason,
            string lastRejectionReason,
            int completedPitchCount,
            bool autoRepeatEnabled,
            float autoRepeatRemainingSeconds)
        {
            State = state;
            ElapsedSeconds = elapsedSeconds;
            BallSpeed = ballSpeed;
            BallPosition = ballPosition;
            BallVelocity = ballVelocity;
            HasCenterPassError = hasCenterPassError;
            CenterPassError = centerPassError;
            EndReason = endReason;
            LastRejectionReason = lastRejectionReason ?? string.Empty;
            CompletedPitchCount = completedPitchCount;
            AutoRepeatEnabled = autoRepeatEnabled;
            AutoRepeatRemainingSeconds = autoRepeatRemainingSeconds;
        }

        public PlayState State { get; }
        public float ElapsedSeconds { get; }
        public float BallSpeed { get; }
        public Vector3 BallPosition { get; }
        public Vector3 BallVelocity { get; }

        /// <summary>목표 평면 통과 오차가 이번 투구에서 아직 측정되지 않았으면 false다.</summary>
        public bool HasCenterPassError { get; }

        /// <summary>목표 평면을 지나는 순간 공 중심과 목표 중심 사이의 거리(m).</summary>
        public float CenterPassError { get; }

        public PitchEndReason EndReason { get; }

        /// <summary>가장 최근 투구 명령이 거부된 이유. 성공했으면 빈 문자열이다.</summary>
        public string LastRejectionReason { get; }

        public int CompletedPitchCount { get; }
        public bool AutoRepeatEnabled { get; }

        /// <summary>자동 반복이 켜져 있고 대기 중일 때 다음 투구까지 남은 시간(초).</summary>
        public float AutoRepeatRemainingSeconds { get; }
    }
}
