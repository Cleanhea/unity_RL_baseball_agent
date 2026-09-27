using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 공 Rigidbody의 단일 소유자(docs/architecture.md 3.1).
    /// 위치·속도 조회, 발사, 초기화, 비행 중 공기 역학·구름 저항, 지면/펜스 첫 닿음 기록을 맡는다.
    /// 플레이 종료 규칙과 페어/파울 판정은 <see cref="PlayDirector"/>와 <see cref="BattedBallJudge"/>가 담당한다.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(SphereCollider))]
    public sealed class BallController : MonoBehaviour
    {
        [Tooltip("공 질량과 물리 옵션을 가져올 설정 데이터.")]
        [SerializeField]
        private BaseballEnvironmentConfig config;

        private Rigidbody body;
        private PhysicsMaterial surface;
        private Collider groundContact;

        /// <summary>공 중심의 현재 월드 위치.</summary>
        public Vector3 Position => body.position;

        /// <summary>공의 현재 선속도(m/s).</summary>
        public Vector3 Velocity => body.linearVelocity;

        /// <summary>공의 현재 각속도(rad/s). 비행 중 양력(마그누스 힘)의 근원이다.</summary>
        public Vector3 AngularVelocity => body.angularVelocity;

        public float AngularDamping => body.angularDamping;

        /// <summary>발사된 뒤 아직 초기화되거나 종료 처리되지 않았으면 true다.</summary>
        public bool IsInFlight { get; private set; }

        /// <summary>지면(또는 위를 향한 면)에 지금 닿아 있으면 true다. 이때 양력 대신 구름 저항을 적용한다.</summary>
        public bool TouchingGround => groundContact != null;

        /// <summary>마지막 발사 이후 지면이나 펜스에 처음 닿은 사실. 페어/파울 판정의 근거다.</summary>
        public bool HasFirstTouch { get; private set; }
        public Vector3 FirstTouchPoint { get; private set; }
        public bool FirstTouchWasWall { get; private set; }
        public bool HasTouchedGround { get; private set; }

        private void Awake()
        {
            body = GetComponent<Rigidbody>();

            if (config != null)
            {
                body.mass = config.BallMass;
                // 공-지면/펜스 반발과 마찰은 공 쪽 재질이 정한다. 반발은 Maximum이라 설정값이 그대로 쓰이고,
                // 마찰은 Minimum이라 지면 Collider 기본값(0.6)보다 작은 설정값만 효과가 있다.
                surface = new PhysicsMaterial("BallSurface")
                {
                    bounciness = config.GroundRestitution,
                    dynamicFriction = config.GroundFriction,
                    staticFriction = config.GroundFriction,
                    bounceCombine = PhysicsMaterialCombine.Maximum,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                };
                GetComponent<SphereCollider>().sharedMaterial = surface;
            }

            body.useGravity = true;
            // 빠른 공이 얇은 물체를 그냥 통과하지 않도록 연속 충돌 검사를 쓴다
            // (docs/environment-spec.md 4장).
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            // 기본 7 rad/s 상한은 투구·타구 회전(수천 rpm)을 잘라 버린다.
            body.maxAngularVelocity = 500f;
        }

        private void OnDestroy()
        {
            if (surface != null) Destroy(surface);
        }

        /// <summary>
        /// Editor 빌더가 생성 직후 설정 데이터를 연결할 때 쓴다. 이유는
        /// <see cref="FieldLayout.AssignConfig"/> 문서를 참고한다.
        /// </summary>
        public void AssignConfig(BaseballEnvironmentConfig value)
        {
            config = value;
        }

        /// <summary>
        /// <paramref name="velocity"/>와 회전 <paramref name="spin"/>(rad/s)으로 발사한다.
        /// 지면·펜스 닿음 기록은 새로 시작한다.
        /// </summary>
        public void Launch(Vector3 velocity, Vector3 spin)
        {
            HolderIndex = -1;
            body.isKinematic = false;
            body.linearVelocity = velocity;
            body.angularVelocity = spin;
            IsInFlight = true;
            ClearContacts();
        }

        /// <summary>
        /// 위치·회전·선속도·각속도를 모두 복원한다(docs/environment-spec.md 10장).
        /// </summary>
        public void ResetTo(Vector3 position, Quaternion rotation)
        {
            body.isKinematic = false;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.position = position;
            body.rotation = rotation;
            transform.SetPositionAndRotation(position, rotation);
            body.isKinematic = true;
            IsInFlight = false;
            HolderIndex = -1;
            ClearContacts();
        }

        /// <summary>
        /// 현재 위치에서 물리를 고정하고 비행을 종료한다. 투구가 목표를 지났거나
        /// 경계를 벗어나거나 시간 초과됐을 때 <see cref="PlayDirector"/>가 호출한다.
        /// </summary>
        public void MarkFlightEnded()
        {
            body.isKinematic = true;
            IsInFlight = false;
        }

        /// <summary>공을 잡고 있는 수비수 인덱스. 아무도 잡지 않았으면 -1이다(3단계 수비).</summary>
        public int HolderIndex { get; private set; } = -1;
        public bool IsHeld => HolderIndex >= 0;

        /// <summary>
        /// 수비수가 공을 잡는다. 물리를 멈추고 <paramref name="position"/>(글러브)에 둔다.
        /// 지면·펜스 닿음 기록은 페어/파울 판정 근거라 그대로 둔다.
        /// </summary>
        public void Hold(int holderIndex, Vector3 position)
        {
            if (!body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            body.isKinematic = true;
            IsInFlight = false;
            groundContact = null;
            HolderIndex = holderIndex;
            MoveHeld(position);
        }

        /// <summary>잡힌 공을 수비수 글러브 위치로 옮긴다. <see cref="PlayDirector"/>가 고정 단계마다 호출한다.</summary>
        public void MoveHeld(Vector3 position)
        {
            if (!IsHeld) return;
            body.position = position;
            transform.position = position;
        }

        /// <summary>잡은 공을 <paramref name="from"/>에서 회전 없이 <paramref name="velocity"/>로 던진다.</summary>
        public void Throw(Vector3 from, Vector3 velocity)
        {
            HolderIndex = -1;
            body.isKinematic = false;
            body.position = from;
            transform.position = from;
            body.linearVelocity = velocity;
            body.angularVelocity = Vector3.zero;
            IsInFlight = true;
            groundContact = null;
        }

        /// <summary>
        /// 이번 고정 단계의 공기 역학(항력·양력)과 지면 구름 저항을 가한다. 물리 단계 직전에
        /// <see cref="PlayDirector"/>가 호출해 발사한 단계부터 같은 순서로 적용되게 한다.
        /// </summary>
        public void ApplyFlightForces()
        {
            if (!IsInFlight || body.isKinematic || config == null) return;
            if (config.AerodynamicsEnabled)
            {
                // 지면에서 구르는 동안의 회전은 양력이 아니라 구름이므로 항력만 준다.
                Vector3 spin = TouchingGround ? Vector3.zero : body.angularVelocity;
                body.AddForce(AerodynamicAcceleration(body.linearVelocity, spin, config), ForceMode.Acceleration);
            }
            if (!TouchingGround || config.RollingResistance <= 0f) return;
            Vector3 horizontal = body.linearVelocity;
            horizontal.y = 0f;
            float speed = horizontal.magnitude;
            if (speed < 1e-4f) return;
            Vector3 direction = horizontal / speed;
            float deceleration = Mathf.Min(config.RollingResistance * Physics.gravity.magnitude, speed / Time.fixedDeltaTime);
            body.AddForce(-direction * deceleration, ForceMode.Acceleration);
            // 구름 조건(v = ωr)을 함께 줄여 마찰이 저항을 되돌리지 않게 한다.
            body.AddTorque(-Vector3.Cross(Vector3.up, direction) * (deceleration / config.BallRadius), ForceMode.Acceleration);
        }

        /// <summary>
        /// 항력 + 마그누스 양력 가속도(m/s²). 항력 계수는 설정값, 양력 계수는 회전 계수
        /// S = rω/v에 대한 Sawicki 등(2003)의 근사(S &lt; 0.1이면 1.5S, 아니면 0.09 + 0.6S)다.
        /// 투구 해석기와 실제 비행이 같은 함수를 쓴다.
        /// </summary>
        public static Vector3 AerodynamicAcceleration(Vector3 velocity, Vector3 spin, BaseballEnvironmentConfig settings)
        {
            float speed = velocity.magnitude;
            if (speed < 1e-3f) return Vector3.zero;
            float k = 0.5f * settings.AirDensity * Mathf.PI * settings.BallRadius * settings.BallRadius / settings.BallMass;
            Vector3 acceleration = -k * settings.DragCoefficient * speed * velocity;
            // 진행 방향과 나란한 회전 성분(자이로 회전)은 양력을 만들지 않는다.
            Vector3 transverse = spin - (Vector3.Dot(spin, velocity) / (speed * speed)) * velocity;
            float omega = transverse.magnitude;
            if (omega > 1e-3f)
            {
                float s = settings.BallRadius * omega / speed;
                float lift = s < 0.1f ? 1.5f * s : 0.09f + 0.6f * s;
                acceleration += k * lift * speed * speed * Vector3.Cross(transverse, velocity).normalized;
            }
            return acceleration;
        }

        /// <summary>
        /// 진행 방향 기준 역회전 각속도(rad/s). 양수 rpm은 역회전(위로 뜨는 힘), 음수는 전진 회전이다.
        /// 회전축은 수평이고 진행 방향에 수직이다(좌우 회전 없음).
        /// </summary>
        public static Vector3 BackspinVector(Vector3 velocity, float rpm)
        {
            var horizontal = new Vector3(velocity.x, 0f, velocity.z);
            Vector3 axis = horizontal.sqrMagnitude > 1e-6f ? Vector3.Cross(horizontal.normalized, Vector3.up) : Vector3.left;
            return axis * (rpm * Mathf.PI / 30f);
        }

        private void OnCollisionEnter(Collision collision) => RecordContact(collision);

        private void OnCollisionStay(Collision collision) => RecordContact(collision);

        private void OnCollisionExit(Collision collision)
        {
            if (collision.collider == groundContact) groundContact = null;
        }

        private void RecordContact(Collision collision)
        {
            if (!IsInFlight || collision.contactCount == 0) return;
            ContactPoint contact = collision.GetContact(0);
            bool fromBelow = Mathf.Abs(contact.normal.y) > 0.7f;
            if (fromBelow)
            {
                groundContact = collision.collider;
                HasTouchedGround = true;
            }
            if (HasFirstTouch) return;
            HasFirstTouch = true;
            FirstTouchPoint = contact.point;
            FirstTouchWasWall = !fromBelow;
        }

        private void ClearContacts()
        {
            groundContact = null;
            HasFirstTouch = false;
            HasTouchedGround = false;
            FirstTouchWasWall = false;
            FirstTouchPoint = Vector3.zero;
        }
    }
}
