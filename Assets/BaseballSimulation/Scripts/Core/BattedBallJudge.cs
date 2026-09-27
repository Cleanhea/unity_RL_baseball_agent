using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 타구의 페어/파울·홈런·인정 2루타 판정. 공식 야구 규칙의 정의를 필드 기하로 옮겼다.
    ///
    /// - 1·3루를 지난 곳에 처음 떨어지거나 펜스에 처음 닿으면 그 지점의 지역으로 즉시 판정한다.
    /// - 1·3루 앞(내야)에 떨어진 공은 1·3루를 지나는 순간의 위치, 또는 멈춘 위치로 판정한다.
    /// - 공중으로 펜스를 넘으면 펜스선을 넘은 지점이 페어일 때 홈런, 아니면 파울이다.
    ///   바운드된 페어 타구가 넘으면 인정 2루타다.
    ///
    /// 베이스·선수·심판에 닿는 경우와 파울 폴은 다루지 않는다. 결과 확정은 <see cref="PlayDirector"/>가 한다.
    /// </summary>
    public sealed class BattedBallJudge
    {
        private readonly FieldLayout field;
        private Vector3 previous;
        private Vector3 fenceLinePoint;
        private float stoppedFor;
        private float contactTime;

        public BattedBallJudge(FieldLayout layout) => field = layout;

        public BattedBallCall Call { get; private set; }
        public bool HasFirstTouch { get; private set; }
        public Vector3 FirstTouchPoint { get; private set; }
        public float HangTime { get; private set; }
        public bool IsFinal => Call != BattedBallCall.None && Call != BattedBallCall.Fair;

        public void Begin(Vector3 contactPosition, float time)
        {
            Clear();
            previous = contactPosition;
            contactTime = time;
        }

        public void Clear()
        {
            Call = BattedBallCall.None;
            HasFirstTouch = false;
            FirstTouchPoint = Vector3.zero;
            HangTime = 0f;
            stoppedFor = 0f;
            fenceLinePoint = Vector3.zero;
        }

        /// <summary>고정 단계마다 호출한다. 판정이 바뀌었으면 true를 돌려준다.</summary>
        public bool Step(BallController ball, float time, float dt)
        {
            if (IsFinal) return false;
            BaseballEnvironmentConfig config = field.Config;
            BattedBallCall before = Call;
            Vector3 position = ball.Position;
            float fenceRadius = config.PlayBoundaryHorizontalRadius;

            // 펜스선(안쪽 면)을 넘은 지점이 페어/파울 기준이고, 바깥 면을 공 전체가 넘어야 경기장을 벗어난 것이다.
            if (TryCrossRadius(previous, position, fenceRadius, out Vector3 linePoint)) fenceLinePoint = linePoint;
            if (TryCrossRadius(previous, position, fenceRadius + BaseballEnvironmentConfig.FenceThickness + config.BallRadius, out _))
            {
                bool fair = field.IsFairGroundPoint(fenceLinePoint);
                if (!ball.HasFirstTouch)
                {
                    Touch(fenceLinePoint, time);
                    // 펜스 Collider가 있으면 펜스 높이 아래로는 넘을 수 없다. 없을 때만 생기는 경우다.
                    Call = fenceLinePoint.y < config.FenceHeight ? BattedBallCall.OutOfPlay
                        : fair ? BattedBallCall.HomeRun : BattedBallCall.Foul;
                }
                else
                {
                    bool fairBall = Call == BattedBallCall.Fair || (Call == BattedBallCall.None && fair);
                    Call = fairBall ? BattedBallCall.GroundRuleDouble : BattedBallCall.Foul;
                }
            }
            else
            {
                if (ball.HasFirstTouch && !HasFirstTouch)
                {
                    Touch(ball.FirstTouchPoint, time);
                    if (ball.FirstTouchWasWall || IsPastBase(ball.FirstTouchPoint)) Decide(ball.FirstTouchPoint);
                }
                if (Call == BattedBallCall.None && HasFirstTouch)
                {
                    stoppedFor = ball.Velocity.magnitude < config.BallStopSpeed ? stoppedFor + dt : 0f;
                    if (IsPastBase(position) || stoppedFor >= config.BallStopSeconds) Decide(position);
                }
            }

            previous = position;
            return Call != before;
        }

        /// <summary>홈에서 가까운 쪽 파울선을 따라 1루 또는 3루보다 멀리 있으면 true다.</summary>
        public bool IsPastBase(Vector3 point)
        {
            Vector3 home = field.HomePosition;
            Vector3 local = Flat(point - home);
            Vector3 first = Flat(field.FirstBasePosition - home);
            Vector3 third = Flat(field.ThirdBasePosition - home);
            float alongFirst = Vector3.Dot(local, first.normalized);
            float alongThird = Vector3.Dot(local, third.normalized);
            return alongFirst >= alongThird ? alongFirst >= first.magnitude : alongThird >= third.magnitude;
        }

        /// <summary>
        /// 판정 전 수비수가 타구를 먼저 잡으면 잡은 지점의 지역으로 페어/파울을 정한다(3단계 수비).
        /// 공중에서 잡은 공은 아웃이라 이 판정을 쓰지 않는다.
        /// </summary>
        public void DecideOnFielderTouch(Vector3 point)
        {
            if (Call == BattedBallCall.None) Decide(point);
        }

        private void Decide(Vector3 point) =>
            Call = field.IsFairGroundPoint(point) ? BattedBallCall.Fair : BattedBallCall.Foul;

        private void Touch(Vector3 point, float time)
        {
            HasFirstTouch = true;
            FirstTouchPoint = point;
            HangTime = time - contactTime;
        }

        private bool TryCrossRadius(Vector3 from, Vector3 to, float radius, out Vector3 point)
        {
            Vector3 home = field.HomePosition;
            float a = Flat(from - home).magnitude;
            float b = Flat(to - home).magnitude;
            point = to;
            if (a >= radius || b < radius) return false;
            point = Vector3.Lerp(from, to, (radius - a) / (b - a));
            return true;
        }

        private static Vector3 Flat(Vector3 value) => new Vector3(value.x, 0f, value.z);
    }
}
