using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 환경 명령의 단일 입구이자 결과의 단일 소유자(docs/architecture.md 3.1).
    ///
    /// 현재 범위는 피칭머신 직구 하나다. 목표(<see cref="FieldLayout.PitchTargetPosition"/>)를
    /// 통과하도록 중력 낙하를 보정한 초기 속도를 계산해 발사하고, 목표 평면 통과 오차를
    /// 측정하며, 스윙·타격·타구 관찰과 재투구·초기화·자동 반복을 관리한다.
    /// 주루·수비·볼카운트는 후속 단계다.
    ///
    /// 수동 입력(<see cref="ManualPlayController"/>)과 자동 반복 모두 <see cref="RequestThrowPitch"/>,
    /// <see cref="RequestResetPlay"/>, <see cref="RequestSwing"/> 명령을 사용한다. 실제 변경은 <see cref="FixedUpdate"/>에서만
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
        public float AimSprayDegrees { get; set; }
        public float AimLaunchDegrees { get; set; } = 15f;

        private BaseballEnvironmentConfig Config => fieldLayout != null ? fieldLayout.Config : null;

        private void Awake()
        {
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
                if (batter != null) batter.Initialize(Config, fieldLayout.PitchTargetPosition);
                PerformReset();
            }
        }

        /// <summary>수동·자동 입력이 공통으로 호출하는 투구 요청.</summary>
        public void RequestThrowPitch()
        {
            pitchRequested = true;
        }

        /// <summary>수동·자동 입력이 공통으로 호출하는 초기화 요청.</summary>
        public void RequestResetPlay()
        {
            resetRequested = true;
        }

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
                    lastRejectionReason = string.Empty;
                    SwingStarted?.Invoke();
                }
            }
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

                if (batter != null && batter.Tick(elapsedSeconds - dt, elapsedSeconds,
                        previousBallPosition, currentPosition, out Vector3 exitVelocity))
                {
                    ball.Launch(exitVelocity);
                    state = PlayState.BattedBallInFlight;
                    PublishEvaluation();
                    BallBatContact?.Invoke(exitVelocity);
                    previousBallPosition = currentPosition;
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
                batter.Tick(elapsedSeconds - dt, elapsedSeconds, previousBallPosition, ball.Position, out _);
                previousBallPosition = ball.Position;
                if (!fieldLayout.IsInsidePlayBoundary(ball.Position)) EndPitch(PitchEndReason.OutOfPlay);
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
            Vector3 target = fieldLayout.PitchTargetPosition;
            float gravityMagnitude = Physics.gravity.magnitude;

            if (!TryComputeLaunchVelocity(
                    origin, target, config.PitchSpeed, gravityMagnitude,
                    out Vector3 velocity, out string rejection))
            {
                lastRejectionReason = rejection;
                Debug.LogWarning($"[PlayDirector] 투구 거부: {rejection}", this);
                return;
            }

            // 발사구와 공 시작 위치를 맞춘 뒤 발사한다.
            ball.ResetTo(origin, Quaternion.identity);
            ball.Launch(velocity);
            Vector3 horizontal = target - origin;
            horizontal.y = 0f;
            Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
            if (batter != null) batter.SetPitchReference(horizontal.magnitude / horizontalVelocity.magnitude);

            state = PlayState.PitchInFlight;
            elapsedSeconds = 0f;
            centerPassRecorded = false;
            centerPassError = 0f;
            lastEndReason = PitchEndReason.None;
            lastRejectionReason = string.Empty;
            previousBallPosition = origin;

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
        }

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
            swingRequested = false;
            setupRequested = false;
            evaluationPublished = false;
            if (batter != null) batter.ResetState();

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
