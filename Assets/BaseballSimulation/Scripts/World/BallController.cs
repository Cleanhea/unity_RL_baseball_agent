using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 공 Rigidbody의 단일 소유자(docs/architecture.md 3.1).
    /// 위치·속도 조회, 발사, 초기화를 제공한다. 플레이 종료 규칙과 소유권(포구) 판정은
    /// 아직 이 단계 범위가 아니라서 <see cref="PlayDirector"/>가 담당한다.
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

        /// <summary>공 중심의 현재 월드 위치.</summary>
        public Vector3 Position => body.position;

        /// <summary>공의 현재 선속도(m/s).</summary>
        public Vector3 Velocity => body.linearVelocity;

        /// <summary>발사된 뒤 아직 초기화되거나 종료 처리되지 않았으면 true다.</summary>
        public bool IsInFlight { get; private set; }

        private void Awake()
        {
            body = GetComponent<Rigidbody>();

            if (config != null)
            {
                body.mass = config.BallMass;
            }

            body.useGravity = true;
            // 빠른 공이 얇은 물체를 그냥 통과하지 않도록 연속 충돌 검사를 쓴다
            // (docs/environment-spec.md 4장).
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.interpolation = RigidbodyInterpolation.Interpolate;
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
        /// <paramref name="velocity"/>로 발사한다. 호출 전에 위치가 발사 시작점과 일치해야 한다.
        /// </summary>
        public void Launch(Vector3 velocity)
        {
            body.isKinematic = false;
            body.linearVelocity = velocity;
            body.angularVelocity = Vector3.zero;
            IsInFlight = true;
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
    }
}
