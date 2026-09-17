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
