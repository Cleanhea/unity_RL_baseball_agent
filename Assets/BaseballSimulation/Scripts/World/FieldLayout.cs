using System.Collections.Generic;
using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 야구장 기준점의 단일 소유자(docs/architecture.md 3.1).
    /// 홈·베이스·투구점 Transform 참조, 주루 경로, 페어/파울·경계 질의를 제공한다.
    /// 결과 확정과 선수 이동은 담당하지 않는다.
    ///
    /// 런타임 위치의 기준은 씬의 Transform이고, <see cref="BaseballEnvironmentConfig"/>는
    /// 배치 기본값과 경계·표시 설정을 제공한다. 두 값이 어긋나면
    /// 컨텍스트 메뉴 "설정값과 현재 위치 비교"로 확인할 수 있다.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FieldLayout : MonoBehaviour
    {
        private static readonly BaseId[] RunningOrder =
        {
            BaseId.Home, BaseId.First, BaseId.Second, BaseId.Third,
        };

        [Header("설정 데이터")]
        [SerializeField]
        private BaseballEnvironmentConfig config;

        [Header("기준 Transform (Inspector에서 명시적으로 연결)")]
        [SerializeField]
        private Transform home;

        [SerializeField]
        private Transform firstBase;

        [SerializeField]
        private Transform secondBase;

        [SerializeField]
        private Transform thirdBase;

        [SerializeField]
        private Transform pitcherPlate;

        [SerializeField]
        private Transform pitchOrigin;

        [SerializeField]
        private Transform pitchTarget;

        public BaseballEnvironmentConfig Config => config;

        public Vector3 HomePosition => home != null ? home.position : Vector3.zero;
        public Vector3 FirstBasePosition => firstBase != null ? firstBase.position : Vector3.zero;
        public Vector3 SecondBasePosition => secondBase != null ? secondBase.position : Vector3.zero;
        public Vector3 ThirdBasePosition => thirdBase != null ? thirdBase.position : Vector3.zero;
        public Vector3 PitcherPlatePosition => pitcherPlate != null ? pitcherPlate.position : Vector3.zero;
        public Vector3 PitchOriginPosition => pitchOrigin != null ? pitchOrigin.position : Vector3.zero;
        public Vector3 PitchTargetPosition => pitchTarget != null ? pitchTarget.position : Vector3.zero;

        public float HomeToFirstDistance => Vector3.Distance(HomePosition, FirstBasePosition);
        public float HomeToThirdDistance => Vector3.Distance(HomePosition, ThirdBasePosition);
        public float HomeToSecondDistance => Vector3.Distance(HomePosition, SecondBasePosition);
        public float HomeToPitcherPlateDistance => Vector3.Distance(HomePosition, PitcherPlatePosition);

        /// <summary>홈에서 1루 파울선 방향(수평 단위 벡터).</summary>
        public Vector3 FirstBaseFoulLineDirection => new Vector3(1f, 0f, 1f).normalized;

        /// <summary>홈에서 3루 파울선 방향(수평 단위 벡터).</summary>
        public Vector3 ThirdBaseFoulLineDirection => new Vector3(-1f, 0f, 1f).normalized;

        /// <summary>
        /// <paramref name="baseId"/>의 현재 월드 위치.
        /// </summary>
        public Vector3 GetBasePosition(BaseId baseId)
        {
            switch (baseId)
            {
                case BaseId.Home: return HomePosition;
                case BaseId.First: return FirstBasePosition;
                case BaseId.Second: return SecondBasePosition;
                case BaseId.Third: return ThirdBasePosition;
                default: return HomePosition;
            }
        }

        /// <summary>
        /// 주루 경로 홈 → 1루 → 2루 → 3루 → 홈에서 다음 베이스를 돌려준다
        /// (docs/environment-spec.md 5.3).
        /// </summary>
        public BaseId GetNextBase(BaseId baseId)
        {
            switch (baseId)
            {
                case BaseId.Home: return BaseId.First;
                case BaseId.First: return BaseId.Second;
                case BaseId.Second: return BaseId.Third;
                case BaseId.Third: return BaseId.Home;
                default: return BaseId.First;
            }
        }

        /// <summary>
        /// 지면 투영 기준 간이 페어 영역 판정(docs/environment-spec.md 2.1).
        /// 홈을 원점으로 옮긴 좌표에서 <c>z &gt;= 0</c>이고 <c>z &gt;= |x|</c>면 페어다.
        /// 수평 위치만 보므로 높이는 판정에 쓰지 않는다.
        /// </summary>
        public bool IsFairGroundPoint(Vector3 worldPosition)
        {
            Vector3 local = worldPosition - HomePosition;
            return local.z >= 0f && local.z >= Mathf.Abs(local.x);
        }

        /// <summary>
        /// 홈 기준 수평 반경과 상·하단 높이로 정의한 경기 영역 안인지 확인한다
        /// (docs/environment-spec.md 2.3).
        /// </summary>
        public bool IsInsidePlayBoundary(Vector3 worldPosition)
        {
            if (config == null)
            {
                return false;
            }

            Vector3 local = worldPosition - HomePosition;
            float radius = config.PlayBoundaryHorizontalRadius;
            if (((local.x * local.x) + (local.z * local.z)) > (radius * radius))
            {
                return false;
            }

            return local.y <= config.PlayBoundaryUpperY && local.y >= config.PlayBoundaryLowerY;
        }

        /// <summary>
        /// 필수 참조와 설정값을 확인한다. 플레이 시작 전에 호출한다
        /// (docs/architecture.md 3.2).
        /// </summary>
        /// <param name="error">실패 사유. 성공하면 빈 문자열이다.</param>
        public bool TryValidate(out string error)
        {
            var problems = new List<string>();

            if (config == null)
            {
                problems.Add("설정 데이터(BaseballEnvironmentConfig)가 연결되지 않았다.");
            }
            else if (!config.TryValidate(out string configError))
            {
                problems.Add($"설정 데이터 검증 실패:\n{configError}");
            }

            AddIfMissing(problems, home, "홈");
            AddIfMissing(problems, firstBase, "1루");
            AddIfMissing(problems, secondBase, "2루");
            AddIfMissing(problems, thirdBase, "3루");
            AddIfMissing(problems, pitcherPlate, "투수판");
            AddIfMissing(problems, pitchOrigin, "투구 시작점");
            AddIfMissing(problems, pitchTarget, "투구 목표점");

            error = problems.Count == 0 ? string.Empty : string.Join("\n", problems);
            return problems.Count == 0;
        }

        /// <summary>
        /// Editor 빌더가 생성 직후 설정 데이터를 연결할 때 쓴다. 필드에 직접 대입해 명확하게
        /// 만든다. 커스텀 <see cref="BaseballEnvironmentConfig"/> 타입을 막 재컴파일한 직후에는
        /// Unity가 그 에셋을 잠깐 "파괴된 네이티브 객체"로 취급할 수 있어(관리 필드 읽기는
        /// 되지만 다른 오브젝트의 참조 필드에는 대입해도 <c>{fileID: 0}</c>으로 저장됨),
        /// <c>BaseballPlaygroundBuilder.LoadConfigWithRetries</c>가 실제로 살아있는 참조를
        /// 확보한 뒤 이 메서드로 넘긴다. Transform 참조는 씬에 이미 속한 오브젝트라 이 문제가
        /// 없어 그대로 <c>SerializedObject</c>를 쓴다.
        /// </summary>
        public void AssignConfig(BaseballEnvironmentConfig value)
        {
            config = value;
        }

        /// <summary>
        /// 설정 데이터의 좌표로 기준 Transform 위치를 맞춘다. 배치 정리용 편의 기능이다.
        /// </summary>
        [ContextMenu("설정값으로 기준점 위치 맞추기")]
        public void ApplyConfigPositions()
        {
            if (config == null)
            {
                Debug.LogError("[FieldLayout] 설정 데이터가 없어 기준점 위치를 맞출 수 없다.", this);
                return;
            }

            SetPosition(home, config.HomePosition);
            SetPosition(firstBase, config.FirstBasePosition);
            SetPosition(secondBase, config.SecondBasePosition);
            SetPosition(thirdBase, config.ThirdBasePosition);
            SetPosition(pitcherPlate, config.PitcherPlatePosition);
            SetPosition(pitchOrigin, config.PitchOriginPosition);
            SetPosition(pitchTarget, config.PitchTargetPosition);
        }

        /// <summary>
        /// 설정 좌표와 현재 Transform 위치의 차이를 로그로 남긴다.
        /// 씬에서 의도적으로 옮긴 경우와 실수로 어긋난 경우를 구분하기 위한 확인용이다.
        /// </summary>
        [ContextMenu("설정값과 현재 위치 비교")]
        public void LogConfigPositionDifferences()
        {
            if (config == null)
            {
                Debug.LogError("[FieldLayout] 설정 데이터가 없어 위치를 비교할 수 없다.", this);
                return;
            }

            var differences = new List<string>();
            AddIfDifferent(differences, "홈", home, config.HomePosition);
            AddIfDifferent(differences, "1루", firstBase, config.FirstBasePosition);
            AddIfDifferent(differences, "2루", secondBase, config.SecondBasePosition);
            AddIfDifferent(differences, "3루", thirdBase, config.ThirdBasePosition);
            AddIfDifferent(differences, "투수판", pitcherPlate, config.PitcherPlatePosition);
            AddIfDifferent(differences, "투구 시작점", pitchOrigin, config.PitchOriginPosition);
            AddIfDifferent(differences, "투구 목표점", pitchTarget, config.PitchTargetPosition);

            if (differences.Count == 0)
            {
                Debug.Log("[FieldLayout] 모든 기준점이 설정값과 일치한다.", this);
                return;
            }

            Debug.LogWarning(
                "[FieldLayout] 설정값과 다른 기준점이 있다.\n" + string.Join("\n", differences), this);
        }

        private void Awake()
        {
            if (!TryValidate(out string error))
            {
                Debug.LogError($"[FieldLayout] 기준점 검증 실패. 환경을 시작할 수 없다.\n{error}", this);
            }
        }

        private static void AddIfMissing(List<string> problems, Transform target, string label)
        {
            if (target == null)
            {
                problems.Add($"{label} 기준 Transform이 연결되지 않았다.");
            }
        }

        private static void AddIfDifferent(
            List<string> differences, string label, Transform target, Vector3 expected)
        {
            if (target == null)
            {
                differences.Add($"{label}: Transform이 없다.");
                return;
            }

            float distance = Vector3.Distance(target.position, expected);
            if (distance > BaseballEnvironmentConfig.PositionTolerance)
            {
                differences.Add(
                    $"{label}: 설정 {expected}, 현재 {target.position} (차이 {distance:F3} m)");
            }
        }

        private static void SetPosition(Transform target, Vector3 position)
        {
            if (target != null)
            {
                target.position = position;
            }
        }

        private void OnDrawGizmos()
        {
            if (config == null || !config.DrawGizmos)
            {
                return;
            }

            Vector3 origin = HomePosition;

            if (config.DrawAxes)
            {
                DrawAxes(origin);
            }

            if (config.DrawBasePath)
            {
                DrawBasePath();
            }

            if (config.DrawFoulLines)
            {
                DrawFoulLines(origin);
            }

            if (config.DrawPlayBoundary)
            {
                DrawPlayBoundary(origin);
            }

            DrawPitchPoints();

            if (config.DrawDistanceLabels)
            {
                DrawDistanceLabels(origin);
            }
        }

        private void DrawAxes(Vector3 origin)
        {
            const float axisLength = 5f;

            Gizmos.color = Color.red;
            Gizmos.DrawLine(origin, origin + (Vector3.right * axisLength));

            Gizmos.color = Color.green;
            Gizmos.DrawLine(origin, origin + (Vector3.up * axisLength));

            Gizmos.color = Color.blue;
            Gizmos.DrawLine(origin, origin + (Vector3.forward * axisLength));
        }

        private void DrawBasePath()
        {
            Gizmos.color = new Color(1f, 0.85f, 0.2f);
            for (int i = 0; i < RunningOrder.Length; i++)
            {
                BaseId from = RunningOrder[i];
                BaseId to = GetNextBase(from);
                Gizmos.DrawLine(GetBasePosition(from), GetBasePosition(to));
            }

            Gizmos.color = new Color(1f, 0.6f, 0.1f);
            foreach (BaseId baseId in RunningOrder)
            {
                Gizmos.DrawWireSphere(GetBasePosition(baseId), 0.4f);
            }
        }

        private void DrawFoulLines(Vector3 origin)
        {
            float length = config.PlayBoundaryHorizontalRadius;
            Gizmos.color = Color.white;
            Gizmos.DrawLine(origin, origin + (FirstBaseFoulLineDirection * length));
            Gizmos.DrawLine(origin, origin + (ThirdBaseFoulLineDirection * length));
        }

        private void DrawPlayBoundary(Vector3 origin)
        {
            float radius = config.PlayBoundaryHorizontalRadius;
            float upper = origin.y + config.PlayBoundaryUpperY;
            float lower = origin.y + config.PlayBoundaryLowerY;

            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.9f);
            DrawHorizontalCircle(new Vector3(origin.x, origin.y, origin.z), radius);

            Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.35f);
            DrawHorizontalCircle(new Vector3(origin.x, upper, origin.z), radius);
            DrawHorizontalCircle(new Vector3(origin.x, lower, origin.z), radius);

            for (int i = 0; i < 4; i++)
            {
                float angle = i * Mathf.PI * 0.5f;
                var offset = new Vector3(Mathf.Sin(angle) * radius, 0f, Mathf.Cos(angle) * radius);
                Gizmos.DrawLine(
                    new Vector3(origin.x, lower, origin.z) + offset,
                    new Vector3(origin.x, upper, origin.z) + offset);
            }
        }

        private void DrawPitchPoints()
        {
            Gizmos.color = new Color(1f, 0.35f, 0.35f);
            Gizmos.DrawWireSphere(PitchOriginPosition, 0.15f);
            Gizmos.DrawWireSphere(PitchTargetPosition, 0.15f);
            Gizmos.DrawLine(PitchOriginPosition, PitchTargetPosition);
        }

        private static void DrawHorizontalCircle(Vector3 center, float radius)
        {
            const int segments = 72;
            Vector3 previous = center + new Vector3(0f, 0f, radius);
            for (int i = 1; i <= segments; i++)
            {
                float angle = (i / (float)segments) * Mathf.PI * 2f;
                Vector3 current = center + new Vector3(
                    Mathf.Sin(angle) * radius, 0f, Mathf.Cos(angle) * radius);
                Gizmos.DrawLine(previous, current);
                previous = current;
            }
        }

        private void DrawDistanceLabels(Vector3 origin)
        {
#if UNITY_EDITOR
            UnityEditor.Handles.color = Color.white;
            UnityEditor.Handles.Label(origin + (Vector3.up * 0.6f), "Home (0, 0, 0)");
            UnityEditor.Handles.Label(
                Vector3.Lerp(origin, FirstBasePosition, 0.5f) + (Vector3.up * 0.6f),
                $"홈-1루 {HomeToFirstDistance:F2} m");
            UnityEditor.Handles.Label(
                Vector3.Lerp(origin, ThirdBasePosition, 0.5f) + (Vector3.up * 0.6f),
                $"홈-3루 {HomeToThirdDistance:F2} m");
            UnityEditor.Handles.Label(
                Vector3.Lerp(origin, PitcherPlatePosition, 0.5f) + (Vector3.up * 0.6f),
                $"홈-투수판 {HomeToPitcherPlateDistance:F2} m");
            UnityEditor.Handles.Label(
                SecondBasePosition + (Vector3.up * 0.6f),
                $"2루 (홈에서 {HomeToSecondDistance:F2} m)");
            UnityEditor.Handles.Label(PitchOriginPosition + (Vector3.up * 0.4f), "PitchOrigin");
            UnityEditor.Handles.Label(PitchTargetPosition + (Vector3.up * 0.4f), "PitchTarget");
            UnityEditor.Handles.Label(
                origin + (FirstBaseFoulLineDirection * config.PlayBoundaryHorizontalRadius)
                    + (Vector3.up * 0.6f),
                $"경계 {config.PlayBoundaryHorizontalRadius:F0} m");
#endif
        }
    }
}
