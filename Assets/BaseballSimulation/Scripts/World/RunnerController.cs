using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 타자주자 1명의 베이스 경로 이동(docs/environment-spec.md 5.3).
    ///
    /// 달리기 자체는 환경이 제공한다. 접촉하면 타석에서 1루까지 자동으로 달리고, 베이스를
    /// 밟으면 멈춘다. 1루 이후의 판단(진루/귀루)만 <see cref="RunnerDecision"/>으로 받는다.
    /// 도착 사실만 보고하고 아웃/세이프·결과 확정은 <see cref="PlayDirector"/>가 맡는다.
    /// 이동은 Director의 FixedUpdate가 <see cref="Tick"/>으로 진행한다.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RunnerController : MonoBehaviour
    {
        // 경로 인덱스: 0 홈(타석 출발), 1 1루, 2 2루, 3 3루, 4 홈(득점).
        private const int ScoreIndex = 4;

        [Tooltip("주자 표시 도형. 주루 중에만 보인다.")]
        [SerializeField] private GameObject visual;

        private BaseballEnvironmentConfig config;
        private FieldLayout field;
        private int startIndex;
        private int lastTouchedIndex;
        private int nextIndex;
        private int targetIndex;
        private float lastTouchTime = -1f;

        public RunnerPhase Phase { get; private set; }
        /// <summary>이번 플레이를 시작한 경로 인덱스(0 홈 = 타자주자, 1~3 누상 주자).</summary>
        public int StartIndex => startIndex;
        public int LastTouchedIndex => lastTouchedIndex;
        public bool IsLive => Phase == RunnerPhase.Advancing || Phase == RunnerPhase.Returning || Phase == RunnerPhase.Holding;

        /// <summary>베이스를 밟을 때마다(진루·귀루 모두) 한 번 발생한다.</summary>
        public event System.Action<BaseId> BaseReached;

        public void Initialize(BaseballEnvironmentConfig settings, FieldLayout layout)
        {
            config = settings;
            field = layout;
            ResetState();
        }

        public void AssignVisual(GameObject value) => visual = value;

        public void ResetState()
        {
            Phase = RunnerPhase.Inactive;
            startIndex = lastTouchedIndex = nextIndex = targetIndex = 0;
            lastTouchTime = -1f;
            SetVisible(false);
        }

        /// <summary>타구 접촉 순간 타자 위치에서 1루를 향해 출발한다.</summary>
        public void Begin(Vector3 start)
        {
            transform.SetPositionAndRotation(new Vector3(start.x, BasePosition(0).y, start.z), Quaternion.identity);
            startIndex = lastTouchedIndex = 0;
            nextIndex = targetIndex = 1;
            lastTouchTime = -1f;
            Phase = RunnerPhase.Advancing;
            SetVisible(true);
        }

        /// <summary>누상 주자로 <paramref name="baseIndex"/>(1~3) 위에 멈춘 채 타석을 시작한다.</summary>
        public void BeginOnBase(int baseIndex)
        {
            startIndex = lastTouchedIndex = nextIndex = targetIndex = Mathf.Clamp(baseIndex, 1, 3);
            transform.SetPositionAndRotation(BasePosition(startIndex), Quaternion.identity);
            lastTouchTime = -1f;
            Phase = RunnerPhase.Holding;
            SetVisible(true);
        }

        /// <summary>이번 플레이에서 아웃됐다. 경로에서 빠지고 보이지 않는다.</summary>
        public void MarkOut()
        {
            if (!IsLive) return;
            Phase = RunnerPhase.Out;
            SetVisible(false);
        }

        /// <summary>
        /// 뜬공이 잡혔을 때 <paramref name="baseIndex"/>까지 되돌아가 리터치하게 한다. 이미 그 베이스에 멈춰 있으면 그대로 둔다.
        /// 여러 베이스를 지났으면 거꾸로 하나씩 밟으며 돌아간다.
        /// </summary>
        public void ForceReturnTo(int baseIndex)
        {
            if (!IsLive) return;
            if (Phase == RunnerPhase.Holding && lastTouchedIndex == baseIndex) return;
            if (Phase == RunnerPhase.Advancing) nextIndex = lastTouchedIndex;
            else if (Phase == RunnerPhase.Holding) nextIndex = lastTouchedIndex - 1;
            targetIndex = baseIndex;
            Phase = RunnerPhase.Returning;
        }

        public bool TryApplyDecision(RunnerDecision decision, out string error)
        {
            error = string.Empty;
            if (!IsLive)
            {
                error = Phase == RunnerPhase.Scored ? "이미 득점한 주자다." : Phase == RunnerPhase.Out ? "이미 아웃된 주자다." : "타구 접촉 전에는 주자가 없다.";
                return false;
            }

            if (decision == RunnerDecision.Advance)
            {
                if (Phase == RunnerPhase.Advancing)
                {
                    if (targetIndex >= ScoreIndex) { error = "이미 홈까지 진루하도록 지정됐다."; return false; }
                    targetIndex++; // 다가오는 베이스를 멈추지 않고 돈다.
                    return true;
                }
                // 멈춰 있거나 귀루 중이면 마지막으로 밟은 베이스의 다음 베이스로 출발한다.
                nextIndex = targetIndex = lastTouchedIndex + 1;
                Phase = RunnerPhase.Advancing;
                return true;
            }

            if (Phase != RunnerPhase.Advancing)
            {
                error = Phase == RunnerPhase.Holding ? "이미 베이스에 멈춰 있다." : "이미 귀루 중이다.";
                return false;
            }
            if (lastTouchedIndex == 0)
            {
                // 타자주자는 홈으로 돌아가지 않는다. 첫 구간의 귀루는 1루 이후 진루 취소다.
                if (targetIndex == 1) { error = "타자주자는 1루까지 자동으로 달린다."; return false; }
                targetIndex = 1;
                return true;
            }
            nextIndex = targetIndex = lastTouchedIndex;
            Phase = RunnerPhase.Returning;
            return true;
        }

        /// <summary>
        /// 고정 시간 단계 한 번만큼 경로를 따라 이동한다. 도착 반경을 넘는 순간의 시각은
        /// 단계 안에서 보간해 기록하고, 남은 이동 거리는 다음 구간에 이어서 쓴다.
        /// </summary>
        public void Tick(float fromTime, float dt)
        {
            float step = config.RunnerSpeed * dt;
            float time = fromTime;
            while (IsLive)
            {
                Vector3 goal = BasePosition(nextIndex);
                float untilTouch = Vector3.Distance(transform.position, goal) - config.RunnerArrivalRadius;
                if (Phase == RunnerPhase.Holding || untilTouch > step)
                {
                    // 멈춘 주자는 베이스 중심으로 마저 들어가고, 달리는 주자는 이번 단계만큼 이동한다.
                    transform.position = Vector3.MoveTowards(transform.position, goal, step);
                    return;
                }
                float moved = Mathf.Max(0f, untilTouch);
                transform.position = Vector3.MoveTowards(transform.position, goal, moved);
                step -= moved;
                time += moved / config.RunnerSpeed;
                Touch(time);
            }
        }

        private void Touch(float time)
        {
            lastTouchedIndex = nextIndex;
            lastTouchTime = time;
            if (Phase == RunnerPhase.Advancing && lastTouchedIndex == ScoreIndex) Phase = RunnerPhase.Scored;
            else if (Phase == RunnerPhase.Advancing && targetIndex > lastTouchedIndex) nextIndex++;
            // 리터치처럼 여러 베이스를 거꾸로 돌아갈 때는 목표 베이스까지 이어서 돌아간다.
            else if (Phase == RunnerPhase.Returning && targetIndex < lastTouchedIndex) nextIndex--;
            else Phase = RunnerPhase.Holding;
            BaseReached?.Invoke(ToBaseId(lastTouchedIndex));
        }

        public RunnerSnapshot GetSnapshot() => new RunnerSnapshot(Phase, transform.position,
            ToBaseId(lastTouchedIndex), ToBaseId(nextIndex), ToBaseId(targetIndex), lastTouchTime, ToBaseId(startIndex));

        private Vector3 BasePosition(int index) => field.GetBasePosition(ToBaseId(index));

        private static BaseId ToBaseId(int index) => (BaseId)(index % ScoreIndex);

        private void SetVisible(bool value)
        {
            if (visual != null) visual.SetActive(value);
        }

        private void OnDrawGizmosSelected()
        {
            if (config == null || field == null || Phase == RunnerPhase.Inactive) return;
            Vector3 goal = BasePosition(nextIndex);
            Gizmos.color = Phase == RunnerPhase.Returning ? Color.red : Color.green;
            Gizmos.DrawLine(transform.position, goal);
            Gizmos.DrawWireSphere(goal, config.RunnerArrivalRadius);
        }
    }
}
