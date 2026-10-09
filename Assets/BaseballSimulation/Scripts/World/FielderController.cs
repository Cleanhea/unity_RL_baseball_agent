using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 3단계 수비수 한 명의 이동(docs/fielding-agents.md). 이동 명령(필드 XZ 방향과 세기)을 받아
    /// 최고 속력·가속도 한도 안에서 움직이고, 경기 경계 안에 머문다. 포구·송구·아웃 판정은
    /// <see cref="PlayDirector"/>가 맡는다. 이동은 Director의 FixedUpdate가 <see cref="Tick"/>으로 진행한다.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FielderController : MonoBehaviour
    {
        /// <summary>글러브(포구·송구 위치)의 지면 기준 높이(m).</summary>
        public const float GloveHeight = 1.2f;

        [SerializeField] private FielderRole role;
        [Tooltip("플레이 시작 위치(지면). 부모(Actors) 기준 로컬 좌표라 경기장을 옮기거나 복제해도 함께 따라간다.")]
        [SerializeField] private Vector3 homeSpot;
        [SerializeField] private bool movementLocked;

        private Vector2 moveCommand;

        public FielderRole Role => role;
        public bool MovementLocked => movementLocked;
        /// <summary>플레이 시작 위치의 월드 좌표.</summary>
        public Vector3 HomeSpot => transform.parent != null ? transform.parent.TransformPoint(homeSpot) : homeSpot;
        public Vector3 Position => transform.position;
        public Vector3 Velocity { get; private set; }
        public Vector3 GlovePosition => transform.position + Vector3.up * GloveHeight;

        /// <summary>역할과 시작 위치(월드)를 정한다. 부모 기준 로컬 좌표로 저장한다.</summary>
        public void Configure(FielderRole value, Vector3 worldSpot, bool lockMovement = false)
        {
            role = value;
            movementLocked = lockMovement;
            Vector3 spot = transform.parent != null ? transform.parent.InverseTransformPoint(worldSpot) : worldSpot;
            homeSpot = new Vector3(spot.x, 0f, spot.z);
        }

        /// <summary>시작 위치로 돌아가 <paramref name="home"/>(자기 경기장의 홈)을 바라본다.</summary>
        public void ResetState(Vector3 home)
        {
            moveCommand = Vector2.zero;
            Velocity = Vector3.zero;
            Vector3 spot = HomeSpot;
            Vector3 toHome = new Vector3(home.x - spot.x, 0f, home.z - spot.z);
            transform.SetPositionAndRotation(spot, toHome.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(toHome.normalized) : Quaternion.identity);
        }

        /// <summary>필드 XZ 이동 명령. 크기 1이 최고 속력이며, 1보다 크면 방향만 쓴다. 비유한 값은 멈춤이다.</summary>
        public void SetMoveCommand(Vector2 value)
        {
            if (movementLocked) { StopMoving(); return; }
            if (!BaseballEnvironmentConfig.IsFinite(value.x) || !BaseballEnvironmentConfig.IsFinite(value.y)) value = Vector2.zero;
            moveCommand = value.sqrMagnitude > 1f ? value.normalized : value;
        }

        public void StopMoving()
        {
            moveCommand = Vector2.zero;
            Velocity = Vector3.zero;
        }

        public void Tick(float dt, BaseballEnvironmentConfig config, Vector3 home)
        {
            if (movementLocked)
            {
                StopMoving();
                transform.position = HomeSpot;
                return;
            }
            Vector3 desired = new Vector3(moveCommand.x, 0f, moveCommand.y) * config.FielderSpeed;
            Velocity = Vector3.MoveTowards(Velocity, desired, config.FielderAcceleration * dt);
            Vector3 next = transform.position + Velocity * dt;
            Vector3 fromHome = new Vector3(next.x - home.x, 0f, next.z - home.z);
            float limit = config.PlayBoundaryHorizontalRadius - 1f;
            if (fromHome.magnitude > limit)
            {
                next = home + fromHome.normalized * limit;
                Velocity = Vector3.zero;
            }
            next.y = HomeSpot.y;
            transform.position = next;
            if (Velocity.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(Velocity.normalized);
        }
    }
}
