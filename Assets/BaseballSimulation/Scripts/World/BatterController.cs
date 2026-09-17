using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>Body-relative bat geometry, swept contact and four independent task diagnostics.</summary>
    [DisallowMultipleComponent]
    public sealed class BatterController : MonoBehaviour
    {
        [SerializeField] private Transform bat;
        private BaseballEnvironmentConfig config;
        private Vector3 referencePosition;
        private Vector3 referenceGripLocal;
        private Vector3 targetPosition;
        private BatterSetupCommand setup;
        private float startedAt = -1f;
        private float expectedArrival = -1f;
        private float displayedAge;
        private float closestDistance = float.PositiveInfinity;
        private float contactTime = -1f;
        private Vector3 exitVelocity;
        private SwingCommand command;
        public bool HasSwung => startedAt >= 0f;
        public bool HasContact { get; private set; }
        public float ContactQuality { get; private set; }
        // Root stays upright, facing the field. Offsets are metres in field axes.
        public Vector3 GripPosition => transform.position + referenceGripLocal + setup.GripOffset;

        public void Initialize(BaseballEnvironmentConfig settings, Vector3 target)
        {
            config = settings;
            targetPosition = target;
            ResetState();
        }

        public void AssignBat(Transform value) => bat = value;

        public void ResetState()
        {
            if (config == null) return;
            startedAt = -1f;
            expectedArrival = -1f;
            closestDistance = float.PositiveInfinity;
            contactTime = -1f;
            exitVelocity = Vector3.zero;
            HasContact = false;
            ContactQuality = 0f;
            command = new SwingCommand(config.ReferenceSwingAngles.x, config.ReferenceSwingAngles.y);
            referencePosition = new Vector3(targetPosition.x + config.ReferenceStanceOffset.x,
                config.HomePosition.y, targetPosition.z + config.ReferenceStanceOffset.y);
            referenceGripLocal = targetPosition - referencePosition - (SwingPlane * Vector3.right) *
                (config.BatLength * config.BatSweetSpotFraction);
            ApplySetup(default);
        }

        public bool TrySetSetup(BatterSetupCommand value, out string error)
        {
            error = string.Empty;
            if (HasSwung) { error = "스윙 시작 후에는 자세를 바꿀 수 없다."; return false; }
            Vector2 stance = value.StanceOffset;
            Vector3 grip = value.GripOffset;
            if (!BaseballEnvironmentConfig.IsFinite(stance.x) || !BaseballEnvironmentConfig.IsFinite(stance.y) ||
                !BaseballEnvironmentConfig.IsFinite(grip.x) || !BaseballEnvironmentConfig.IsFinite(grip.y) ||
                !BaseballEnvironmentConfig.IsFinite(grip.z))
            { error = "타자/배트 위치는 유한한 값이어야 한다."; return false; }
            if (Mathf.Abs(stance.x) > config.StanceOffsetLimit || Mathf.Abs(stance.y) > config.StanceOffsetLimit ||
                Mathf.Abs(grip.x) > config.GripOffsetLimits.x || Mathf.Abs(grip.y) > config.GripOffsetLimits.y ||
                Mathf.Abs(grip.z) > config.GripOffsetLimits.z)
            { error = "타자/배트 위치가 설정된 조절 범위를 벗어났다."; return false; }
            ApplySetup(value);
            return true;
        }

        private void ApplySetup(BatterSetupCommand value)
        {
            setup = value;
            transform.SetPositionAndRotation(referencePosition + new Vector3(value.StanceOffset.x, 0f,
                value.StanceOffset.y), Quaternion.identity);
            Pose(0f);
        }

        public void SetPitchReference(float arrivalSeconds) => expectedArrival = arrivalSeconds;

        public void BeginSwing(SwingCommand value, float time)
        {
            command = value;
            startedAt = time;
            Pose(0f);
        }

        private float SweepAngle(float age) => Mathf.Lerp(config.SwingStartDegrees, config.SwingEndDegrees,
            Mathf.Clamp01(age / config.SwingDuration));

        // Existing command field names are retained. They now rotate the actual swing plane.
        private Quaternion SwingPlane => Quaternion.Euler(-command.LaunchDegrees, command.SprayDegrees, 0f);
        public Vector3 GetBatDirection(float age) => SwingPlane *
            (Quaternion.Euler(0f, SweepAngle(age), 0f) * Vector3.right);
        public Vector3 GetSwingDirection(float age) => SwingPlane *
            (Quaternion.Euler(0f, SweepAngle(age), 0f) * Vector3.forward);

        private void Pose(float age)
        {
            displayedAge = age;
            if (bat == null || config == null) return;
            Vector3 direction = GetBatDirection(age);
            bat.SetPositionAndRotation(GripPosition + direction * (config.BatLength * 0.5f),
                Quaternion.FromToRotation(Vector3.up, direction));
            bat.localScale = new Vector3(0.06f, config.BatLength * 0.5f, 0.06f);
        }

        // Sample both objects at the same substep time. No artificial timing gate:
        // early/late edge hits are physical contacts with separate quality diagnostics.
        public bool Tick(float fromTime, float toTime, Vector3 previousBall, Vector3 currentBall,
            out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (!HasSwung) return false;
            Pose(toTime - startedAt);
            if (HasContact || fromTime > startedAt + config.SwingDuration) return false;
            float travel = Vector3.Distance(previousBall, currentBall) + config.BatLength *
                Mathf.Abs(config.SwingEndDegrees - config.SwingStartDegrees) * Mathf.Deg2Rad *
                (toTime - fromTime) / config.SwingDuration;
            int steps = Mathf.Max(1, Mathf.CeilToInt(travel / (config.BatContactRadius * 0.25f)));
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                float time = Mathf.Lerp(fromTime, toTime, t);
                float age = time - startedAt;
                if (age < 0f || age > config.SwingDuration) continue;
                Vector3 direction = GetBatDirection(age);
                Vector3 ballPoint = Vector3.Lerp(previousBall, currentBall, t);
                float alongBat = Mathf.Clamp(Vector3.Dot(ballPoint - GripPosition, direction), 0f, config.BatLength);
                Vector3 closest = GripPosition + direction * alongBat;
                float distance = Vector3.Distance(ballPoint, closest);
                closestDistance = Mathf.Min(closestDistance, distance);
                if (distance > config.BatContactRadius) continue;
                HasContact = true;
                contactTime = time;
                float sweetError = Mathf.Abs(alongBat / config.BatLength - config.BatSweetSpotFraction);
                float timingQuality = expectedArrival >= 0f ? Quality(TimingError(), config.ContactHalfWindow) : 0f;
                ContactQuality = timingQuality * Quality(sweetError, 0.5f);
                // Actual bat tangent drives exit direction, rather than an independent aim override.
                exitVelocity = GetSwingDirection(age) * Mathf.Lerp(config.MinExitSpeed, config.MaxExitSpeed, ContactQuality);
                velocity = exitVelocity;
                return true;
            }
            return false;
        }

        private float TimingError()
        {
            // Reference contact pose is sweep angle zero, even for asymmetric ranges.
            float centerPhase = config.SwingStartDegrees / (config.SwingStartDegrees - config.SwingEndDegrees);
            return startedAt + centerPhase * config.SwingDuration - expectedArrival;
        }

        private static float Quality(float error, float scale) => Mathf.Clamp01(1f - Mathf.Abs(error) / scale);

        public BattingEvaluation GetEvaluation()
        {
            if (config == null) return default;
            bool timingAvailable = HasSwung && expectedArrival >= 0f;
            float timingError = timingAvailable ? TimingError() : 0f;
            float angleError = Vector3.Angle(SwingPlane * Vector3.forward,
                ComputeExitVelocity(config.ReferenceSwingAngles.x, config.ReferenceSwingAngles.y, 1f));
            var scores = new Vector4(Quality(setup.StanceOffset.magnitude, config.StanceErrorScale),
                Quality(setup.GripOffset.magnitude, config.GripErrorScale),
                timingAvailable ? Quality(timingError, config.ContactHalfWindow) : 0f,
                HasSwung ? Quality(angleError, config.SwingAngleErrorScale) : 0f);
            return new BattingEvaluation(setup, transform.position, GripPosition,
                GripPosition + GetBatDirection(displayedAge) * config.BatLength, HasSwung, HasContact,
                timingAvailable, timingError, angleError, !float.IsPositiveInfinity(closestDistance),
                float.IsPositiveInfinity(closestDistance) ? 0f : closestDistance, contactTime, exitVelocity, scores);
        }

        public static Vector3 ComputeExitVelocity(float spray, float launch, float speed)
        {
            float elevation = launch * Mathf.Deg2Rad;
            return ((Quaternion.Euler(0f, spray, 0f) * Vector3.forward) * Mathf.Cos(elevation)
                + Vector3.up * Mathf.Sin(elevation)) * speed;
        }

        private void OnDrawGizmosSelected()
        {
            if (config == null) return;
            Vector3 grip = GripPosition;
            Gizmos.color = Color.cyan;
            Gizmos.DrawSphere(grip, 0.035f);
            Vector3 previous = grip + GetBatDirection(0f) * config.BatLength;
            for (int i = 1; i <= 32; i++)
            {
                Vector3 next = grip + GetBatDirection(config.SwingDuration * i / 32f) * config.BatLength;
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(grip + GetBatDirection(displayedAge) *
                (config.BatLength * config.BatSweetSpotFraction), config.BatContactRadius);
        }
    }
}
