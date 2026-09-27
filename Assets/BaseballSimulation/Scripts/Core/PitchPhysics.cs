using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 구종별 회전과 조준 해석기. 실제 비행과 같은 <see cref="BallController.AerodynamicAcceleration"/>과
    /// 같은 고정 단계 적분(<see cref="PlayDirector.TrySolvePitch"/>와 같은 순서)을 쓴다.
    /// 옆으로 휘는 공은 좌우·상하 두 각을 함께 보정해 목표를 지나게 한다.
    /// </summary>
    public static class PitchPhysics
    {
        private const int MaxIterations = 40;
        private const float Tolerance = 0.0005f;

        /// <summary>
        /// 발사 속도 <paramref name="velocity"/>에 대한 구종 회전(rad/s). 우투수 기준으로 팔 쪽은 포수 시점 왼쪽(3루 쪽)이다.
        /// 휘는 방향 m에 대해 회전축을 v × m으로 두면 마그누스 힘(ω × v)이 m을 향한다.
        /// 효율이 1보다 작으면 나머지는 진행 방향과 나란한 자이로 회전이다.
        /// </summary>
        public static Vector3 SpinVector(Vector3 velocity, PitchTypeProfile profile)
        {
            float speed = velocity.magnitude;
            if (speed < 1e-4f) return Vector3.zero;
            Vector3 forward = velocity / speed;
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, forward).normalized;
            Vector3 catcherRight = Vector3.Cross(forward, up);
            float radians = profile.MovementDegrees * Mathf.Deg2Rad;
            Vector3 movement = Mathf.Cos(radians) * up - Mathf.Sin(radians) * catcherRight;
            float efficiency = Mathf.Clamp01(profile.SpinEfficiency);
            Vector3 axis = efficiency * Vector3.Cross(forward, movement) +
                Mathf.Sqrt(1f - efficiency * efficiency) * forward;
            return axis * (profile.SpinRpm * Mathf.PI / 30f);
        }

        /// <summary>
        /// <paramref name="target"/>을 지나는 발사 속도와 회전을 찾는다. 직선 방향에서 시작해 목표 평면 통과 오차만큼
        /// 좌우각·상하각을 되먹임으로 고치므로 가장 평평한 해로 수렴한다. 도달하지 못하거나 수렴하지 않으면 false다.
        /// 적분은 시작점 기준 좌표로 한다. 원점에서 먼 병렬 경기장(예: x = 1200 m)은 float 간격이 0.1 mm를 넘는다.
        /// 월드 좌표로 적분하면 반올림이 쌓여 허용 오차 0.5 mm 아래로 수렴하지 못하는 속도가 생긴다.
        /// </summary>
        public static bool TrySolve(Vector3 origin, Vector3 target, float speed, PitchTypeProfile profile,
            BaseballEnvironmentConfig config, float angularDamping, float dt,
            out Vector3 velocity, out Vector3 spin, out float arrivalSeconds, out string rejectionReason)
        {
            velocity = spin = Vector3.zero;
            arrivalSeconds = 0f;
            rejectionReason = string.Empty;
            Vector3 offset = target - origin;
            Vector3 flat = new Vector3(offset.x, 0f, offset.z);
            float distance = flat.magnitude;
            if (distance < 0.001f || !BaseballEnvironmentConfig.IsFinite(speed) || speed <= 0f || dt <= 0f)
            {
                rejectionReason = "투구 시작점-목표 수평 거리와 속력은 0보다 커야 한다.";
                return false;
            }
            Vector3 direction = flat / distance;
            // 양의 좌우각이 통과점을 옮기는 방향.
            Vector3 lateralAxis = Quaternion.AngleAxis(90f, Vector3.up) * direction;

            float flightTime = distance / speed;
            float elevation = Mathf.Atan2(offset.y + 0.5f * Physics.gravity.magnitude * flightTime * flightTime, distance);
            float yaw = 0f;
            float miss = float.PositiveInfinity;
            for (int i = 0; i < MaxIterations; i++)
            {
                Vector3 v = Launch(yaw, elevation);
                Vector3 w = SpinVector(v, profile);
                if (!SimulateCrossingOffset(v, w, offset, config, angularDamping, dt, out Vector3 crossing, out float time))
                {
                    rejectionReason = $"{profile.Type} {speed * 3.6f:F0} km/h로는 목표에 도달할 수 없다 (수평 거리 {distance:F2} m).";
                    return false;
                }
                Vector3 error = crossing - offset;
                float vertical = error.y;
                float lateral = Vector3.Dot(error, lateralAxis);
                miss = Mathf.Sqrt(vertical * vertical + lateral * lateral);
                velocity = v;
                spin = w;
                arrivalSeconds = time;
                if (miss < Tolerance) return true;
                elevation -= vertical / distance;
                yaw -= lateral / distance;
                if (Mathf.Abs(elevation) > 1.2f || Mathf.Abs(yaw) > 1.2f) break;
            }
            rejectionReason = $"{profile.Type} 투구 조준이 수렴하지 않았다 (남은 오차 {miss:F3} m).";
            return false;

            Vector3 Launch(float yawRadians, float elevationRadians)
            {
                Vector3 heading = Quaternion.AngleAxis(yawRadians * Mathf.Rad2Deg, Vector3.up) * direction;
                return (heading * Mathf.Cos(elevationRadians) + Vector3.up * Mathf.Sin(elevationRadians)) * speed;
            }
        }

        /// <summary>
        /// 발사 속도·회전으로 비행을 적분해 <paramref name="target"/>을 지나고 수평 투구 방향에 수직인 평면의 통과점을 구한다.
        /// 해석기와 검증(회전 없는 공과의 차이로 구한 변화량)이 같은 적분을 쓴다. 적분은 시작점 기준 좌표로 한다.
        /// </summary>
        public static bool TrySimulateCrossing(Vector3 origin, Vector3 velocity, Vector3 spin, Vector3 target,
            BaseballEnvironmentConfig config, float angularDamping, float dt, out Vector3 crossing, out float time)
        {
            bool reached = SimulateCrossingOffset(velocity, spin, target - origin, config, angularDamping, dt, out Vector3 offset, out time);
            crossing = origin + offset;
            return reached;
        }

        /// <summary><see cref="TrySimulateCrossing"/>을 시작점 기준 좌표로 푼다. 목표와 통과점 모두 시작점에서의 변위다.</summary>
        private static bool SimulateCrossingOffset(Vector3 velocity, Vector3 spin, Vector3 target,
            BaseballEnvironmentConfig config, float angularDamping, float dt, out Vector3 crossing, out float time)
        {
            Vector3 flat = new Vector3(target.x, 0f, target.z);
            float distance = flat.magnitude;
            Vector3 direction = distance > 1e-4f ? flat / distance : Vector3.forward;
            Vector3 p = Vector3.zero, v = velocity, w = spin;
            crossing = Vector3.zero;
            time = 0f;
            for (int step = 0; step < 1000; step++)
            {
                Vector3 acceleration = Physics.gravity;
                if (config.AerodynamicsEnabled) acceleration += BallController.AerodynamicAcceleration(v, w, config);
                v += acceleration * dt;
                w *= Mathf.Max(0f, 1f - angularDamping * dt);
                Vector3 next = p + v * dt;
                float before = Vector3.Dot(p, direction);
                float after = Vector3.Dot(next, direction);
                if (after >= distance)
                {
                    float f = (distance - before) / (after - before);
                    crossing = Vector3.Lerp(p, next, f);
                    time = (step + f) * dt;
                    return true;
                }
                if (next.y < -50f || after < before) return false;
                p = next;
            }
            return false;
        }
    }
}
