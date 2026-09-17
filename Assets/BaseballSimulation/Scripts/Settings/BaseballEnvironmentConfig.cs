using System.Collections.Generic;
using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 환경 설정 데이터(docs/architecture.md 7장).
    /// 거리·좌표·경계 같은 정적 기본값만 보관하고, 런타임 플레이 상태는 절대 쓰지 않는다.
    ///
    /// 구현 계획 단계 1(필드 좌표, 경기 경계, Scene 뷰 디버그 토글)에 이어, 피칭머신 작업에서
    /// 공 물리·투구·스트라이크존 표시 기본값을 추가했다. 스윙, 주자/수비 속도, 송구, 시드는
    /// 실제로 사용하는 단계(타격 이후)에서 추가한다.
    ///
    /// 단위: 길이는 m, 1 Unity unit = 1 m (docs/README.md 공통 설계 가정).
    /// 좌표: 홈 지면 중심이 원점, +Y 위, +Z 중견수 방향, +X 1루 방향
    /// (docs/environment-spec.md 2.1).
    /// </summary>
    [CreateAssetMenu(
        fileName = "DefaultBaseballEnvironment",
        menuName = "Baseball Simulation/Environment Config",
        order = 0)]
    public sealed class BaseballEnvironmentConfig : ScriptableObject
    {
        /// <summary>위치 비교와 검증에 사용하는 길이 허용 오차(m).</summary>
        public const float PositionTolerance = 0.005f;

        [Header("기준 좌표 (m, 홈 = 원점)")]
        [Tooltip("홈 플레이트 지면 중심. 모든 배치의 원점이며 지면 높이라 Y는 0이어야 한다.")]
        [SerializeField]
        private Vector3 homePosition = new Vector3(0f, 0f, 0f);

        [Tooltip("1루 지면 중심. 홈 기준 +X 쪽이다.")]
        [SerializeField]
        private Vector3 firstBasePosition = new Vector3(19.40f, 0f, 19.40f);

        [Tooltip("2루 지면 중심. 홈-2루 축은 +Z다.")]
        [SerializeField]
        private Vector3 secondBasePosition = new Vector3(0f, 0f, 38.79f);

        [Tooltip("3루 지면 중심. 홈 기준 -X 쪽이다.")]
        [SerializeField]
        private Vector3 thirdBasePosition = new Vector3(-19.40f, 0f, 19.40f);

        [Tooltip("투수판 지면 중심. 홈에서 18.44 m다.")]
        [SerializeField]
        private Vector3 pitcherPlatePosition = new Vector3(0f, 0f, 18.44f);

        [Tooltip("투구 시작 위치. 투수 손 위치의 단순 근사다.")]
        [SerializeField]
        private Vector3 pitchOriginPosition = new Vector3(0f, 1.80f, 18.44f);

        [Tooltip("투구 목표 위치. 홈 앞 스트라이크 존 근사다.")]
        [SerializeField]
        private Vector3 pitchTargetPosition = new Vector3(0f, 1.00f, 0.50f);

        [Header("경기 경계 (m, 홈 기준)")]
        [Tooltip("홈 기준 수평 반경. 이 밖으로 나간 공은 OutOfPlay 후보다.")]
        [SerializeField, Min(1f)]
        private float playBoundaryHorizontalRadius = 110f;

        [Tooltip("경기 영역 상단 높이.")]
        [SerializeField]
        private float playBoundaryUpperY = 60f;

        [Tooltip("경기 영역 하단 높이. 비정상 낙하를 잡기 위해 지면보다 낮게 둔다.")]
        [SerializeField]
        private float playBoundaryLowerY = -2f;

        [Header("공과 투구 (docs/environment-spec.md 4장)")]
        [Tooltip("공 반지름(m).")]
        [SerializeField, Min(0.001f)]
        private float ballRadius = 0.037f;

        [Tooltip("공 질량(kg).")]
        [SerializeField, Min(0.001f)]
        private float ballMass = 0.145f;

        [Tooltip("기본 투구 속력(m/s). 현재 단계는 항상 이 속력의 직구 하나만 사용한다.")]
        [SerializeField, Min(0.1f)]
        private float pitchSpeed = 36f;

        [Tooltip("한 투구의 제한 시간(초). 넘기면 Timeout으로 끝낸다.")]
        [SerializeField, Min(0.1f)]
        private float pitchTimeLimitSeconds = 5f;

        [Tooltip("공이 목표 평면을 지난 뒤 이 거리(m)만큼 더 나아가면 투구를 끝낸다.")]
        [SerializeField, Min(0f)]
        private float behindTargetMargin = 3f;

        [Header("스트라이크존 표시 (간이 검증용 가정)")]
        [Tooltip(
            "PitchTarget 기준 좌우 절반 폭(m). 표시 전용이며 볼/스트라이크 판정에는 쓰지 않는다. " +
            "기본값은 홈 플레이트 폭(약 0.43 m)의 절반이다.")]
        [SerializeField, Min(0.05f)]
        private float strikeZoneHalfWidth = 0.215f;

        [Tooltip(
            "PitchTarget 기준 상하 절반 높이(m). 표시 전용이며 볼/스트라이크 판정에는 쓰지 않는다. " +
            "기본값은 검증 편의를 위한 가정으로 실제 타자 신장에 따른 존 산정 규칙을 따르지 않는다.")]
        [SerializeField, Min(0.05f)]
        private float strikeZoneHalfHeight = 0.30f;

        [Header("Scene 뷰 디버그 표시")]
        [Tooltip("아래 개별 표시의 상위 스위치.")]
        [SerializeField]
        private bool drawGizmos = true;

        [SerializeField]
        private bool drawAxes = true;

        [SerializeField]
        private bool drawBasePath = true;

        [SerializeField]
        private bool drawFoulLines = true;

        [SerializeField]
        private bool drawPlayBoundary = true;

        [Tooltip("홈-1루, 홈-3루, 홈-투수판 거리 등 측정 라벨을 Scene 뷰에 표시한다.")]
        [SerializeField]
        private bool drawDistanceLabels = true;

        [Header("타격 (m, s, m/s)")]
        [SerializeField, Min(0.01f)] private float swingDuration = 0.25f;
        [Tooltip("스윙 평면 안 +X 기준 회전각. 중앙 접촉 자세(0도)를 지나도록 양수로 설정.")]
        [SerializeField, Range(-180f, 180f)] private float swingStartDegrees = 80f;
        [Tooltip("스윙 평면 안 종료 회전각. 중앙 접촉 자세(0도)를 지나도록 음수로 설정.")]
        [SerializeField, Range(-180f, 180f)] private float swingEndDegrees = -80f;
        [SerializeField, Min(0.001f)] private float contactHalfWindow = 0.05f;
        [SerializeField, Min(0.001f)] private float batContactRadius = 0.09f;
        [SerializeField, Min(0.1f)] private float batLength = 1f;
        [SerializeField, Min(0.1f)] private float minExitSpeed = 8f;
        [SerializeField, Min(0.1f)] private float maxExitSpeed = 32f;
        [SerializeField, Min(0.1f)] private float battedBallTimeLimit = 12f;
        public float SwingDuration => swingDuration;
        public float SwingStartDegrees => swingStartDegrees;
        public float SwingEndDegrees => swingEndDegrees;
        public float ContactHalfWindow => contactHalfWindow;
        public float BatContactRadius => batContactRadius;
        public float BatLength => batLength;
        public float MinExitSpeed => minExitSpeed;
        public float MaxExitSpeed => maxExitSpeed;
        public float BattedBallTimeLimit => battedBallTimeLimit;

        [Header("타자 평가 기준과 조절 한계")]
        [Tooltip("중앙 목표의 지면점 기준 타자 발 위치 X/Z(m). 생체역학적 정답이 아닌 과제 기준 자세.")]
        [SerializeField] private Vector2 referenceStanceOffset = new Vector2(-1.1f, 0f);
        [SerializeField, Range(0.1f, 0.95f)] private float batSweetSpotFraction = 0.8f;
        [SerializeField, Min(0.01f)] private float stanceOffsetLimit = 0.75f;
        [SerializeField] private Vector3 gripOffsetLimits = new Vector3(0.25f, 0.4f, 0.35f);
        [SerializeField, Min(0.01f)] private float stanceErrorScale = 0.5f;
        [SerializeField, Min(0.01f)] private float gripErrorScale = 0.3f;
        [Tooltip("평가 목표인 스윙 진행 방향의 좌우각/상향각. 기본 중앙 방향 15도 상향.")]
        [SerializeField] private Vector2 referenceSwingAngles = new Vector2(0f, 15f);
        [SerializeField, Min(1f)] private float swingAngleErrorScale = 45f;
        public Vector2 ReferenceStanceOffset => referenceStanceOffset;
        public float BatSweetSpotFraction => batSweetSpotFraction;
        public float StanceOffsetLimit => stanceOffsetLimit;
        public Vector3 GripOffsetLimits => gripOffsetLimits;
        public float StanceErrorScale => stanceErrorScale;
        public float GripErrorScale => gripErrorScale;
        public Vector2 ReferenceSwingAngles => referenceSwingAngles;
        public float SwingAngleErrorScale => swingAngleErrorScale;

        public Vector3 HomePosition => homePosition;
        public Vector3 FirstBasePosition => firstBasePosition;
        public Vector3 SecondBasePosition => secondBasePosition;
        public Vector3 ThirdBasePosition => thirdBasePosition;
        public Vector3 PitcherPlatePosition => pitcherPlatePosition;
        public Vector3 PitchOriginPosition => pitchOriginPosition;
        public Vector3 PitchTargetPosition => pitchTargetPosition;

        public float PlayBoundaryHorizontalRadius => playBoundaryHorizontalRadius;
        public float PlayBoundaryUpperY => playBoundaryUpperY;
        public float PlayBoundaryLowerY => playBoundaryLowerY;

        public float BallRadius => ballRadius;
        public float BallMass => ballMass;
        public float PitchSpeed => pitchSpeed;
        public float PitchTimeLimitSeconds => pitchTimeLimitSeconds;
        public float BehindTargetMargin => behindTargetMargin;

        public float StrikeZoneHalfWidth => strikeZoneHalfWidth;
        public float StrikeZoneHalfHeight => strikeZoneHalfHeight;

        public bool DrawGizmos => drawGizmos;
        public bool DrawAxes => drawAxes;
        public bool DrawBasePath => drawBasePath;
        public bool DrawFoulLines => drawFoulLines;
        public bool DrawPlayBoundary => drawPlayBoundary;
        public bool DrawDistanceLabels => drawDistanceLabels;

        /// <summary>
        /// <paramref name="baseId"/>에 해당하는 설정 좌표를 돌려준다.
        /// </summary>
        public Vector3 GetBasePosition(BaseId baseId)
        {
            switch (baseId)
            {
                case BaseId.Home: return homePosition;
                case BaseId.First: return firstBasePosition;
                case BaseId.Second: return secondBasePosition;
                case BaseId.Third: return thirdBasePosition;
                default: return homePosition;
            }
        }

        /// <summary>
        /// 설정값이 docs/environment-spec.md 2.1~2.3의 좌표 규칙을 지키는지 확인한다.
        /// 플레이를 시작하기 전에 호출해 잘못된 값으로 실행되는 것을 막는다.
        /// </summary>
        /// <param name="error">실패 사유. 성공하면 빈 문자열이다.</param>
        public bool TryValidate(out string error)
        {
            var problems = new List<string>();
            foreach (float value in new[] { stanceOffsetLimit, gripOffsetLimits.x, gripOffsetLimits.y,
                gripOffsetLimits.z, stanceErrorScale, gripErrorScale, swingAngleErrorScale })
                if (!IsFinite(value) || value <= 0f) problems.Add("타자 조절 한계/평가 척도는 유한한 양수여야 한다.");
            if (!IsFinite(referenceStanceOffset.x) || !IsFinite(referenceStanceOffset.y) ||
                !IsFinite(batSweetSpotFraction) || batSweetSpotFraction < 0.1f || batSweetSpotFraction > 0.95f ||
                !IsFinite(referenceSwingAngles.x) || Mathf.Abs(referenceSwingAngles.x) > 45f ||
                !IsFinite(referenceSwingAngles.y) || referenceSwingAngles.y < -30f || referenceSwingAngles.y > 50f)
                problems.Add("타자 기준 자세/스윙 각도 설정이 잘못됐다.");
            if (float.IsNaN(swingStartDegrees) || float.IsInfinity(swingStartDegrees) ||
                float.IsNaN(swingEndDegrees) || float.IsInfinity(swingEndDegrees) ||
                swingStartDegrees < -180f || swingStartDegrees > 180f ||
                swingEndDegrees < -180f || swingEndDegrees > 180f ||
                swingStartDegrees <= 0f || swingEndDegrees >= 0f)
                problems.Add("중앙 타격 스윙은 시작 0~180도(0 제외), 끝 -180~0도(0 제외)여야 한다.");
            foreach (float value in new[] { swingDuration, contactHalfWindow, batContactRadius, batLength, minExitSpeed, maxExitSpeed, battedBallTimeLimit })
                if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
                    problems.Add("타격 설정은 유한한 양수여야 한다.");
            if (minExitSpeed > maxExitSpeed || contactHalfWindow > swingDuration * 0.5f)
                problems.Add("타격 속도 범위 또는 접촉 시간 폭이 잘못됐다.");

            if (playBoundaryHorizontalRadius <= 0f)
            {
                problems.Add($"경기 경계 수평 반경은 0보다 커야 한다. 현재 {playBoundaryHorizontalRadius} m.");
            }

            if (playBoundaryUpperY <= playBoundaryLowerY)
            {
                problems.Add(
                    $"경기 경계 상단 Y({playBoundaryUpperY} m)는 하단 Y({playBoundaryLowerY} m)보다 커야 한다.");
            }

            if (Mathf.Abs(homePosition.y) > PositionTolerance)
            {
                problems.Add($"홈은 지면 높이여야 한다. 현재 Y = {homePosition.y} m.");
            }

            if (firstBasePosition.x <= homePosition.x)
            {
                problems.Add("1루는 홈 기준 +X 쪽이어야 한다.");
            }

            if (thirdBasePosition.x >= homePosition.x)
            {
                problems.Add("3루는 홈 기준 -X 쪽이어야 한다.");
            }

            if (secondBasePosition.z <= homePosition.z)
            {
                problems.Add("2루는 홈 기준 +Z 쪽이어야 한다.");
            }

            if (pitcherPlatePosition.z <= homePosition.z)
            {
                problems.Add("투수판은 홈 기준 +Z 쪽이어야 한다.");
            }

            if (pitchOriginPosition.y <= 0f)
            {
                problems.Add($"투구 시작 높이는 0보다 커야 한다. 현재 {pitchOriginPosition.y} m.");
            }

            if (pitchTargetPosition.y <= 0f)
            {
                problems.Add($"투구 목표 높이는 0보다 커야 한다. 현재 {pitchTargetPosition.y} m.");
            }

            if (ballRadius <= 0f)
            {
                problems.Add($"공 반지름은 0보다 커야 한다. 현재 {ballRadius} m.");
            }

            if (ballMass <= 0f)
            {
                problems.Add($"공 질량은 0보다 커야 한다. 현재 {ballMass} kg.");
            }

            if (pitchSpeed <= 0f)
            {
                problems.Add($"투구 속력은 0보다 커야 한다. 현재 {pitchSpeed} m/s.");
            }

            if (pitchTimeLimitSeconds <= 0f)
            {
                problems.Add($"투구 제한 시간은 0보다 커야 한다. 현재 {pitchTimeLimitSeconds} s.");
            }

            if (behindTargetMargin < 0f)
            {
                problems.Add($"목표 뒤쪽 여유 거리는 0 이상이어야 한다. 현재 {behindTargetMargin} m.");
            }

            if (strikeZoneHalfWidth <= 0f)
            {
                problems.Add($"스트라이크존 절반 폭은 0보다 커야 한다. 현재 {strikeZoneHalfWidth} m.");
            }

            if (strikeZoneHalfHeight <= 0f)
            {
                problems.Add($"스트라이크존 절반 높이는 0보다 커야 한다. 현재 {strikeZoneHalfHeight} m.");
            }

            AddIfOutsideBoundary(problems, "1루", firstBasePosition);
            AddIfOutsideBoundary(problems, "2루", secondBasePosition);
            AddIfOutsideBoundary(problems, "3루", thirdBasePosition);
            AddIfOutsideBoundary(problems, "투수판", pitcherPlatePosition);
            AddIfOutsideBoundary(problems, "투구 시작점", pitchOriginPosition);
            AddIfOutsideBoundary(problems, "투구 목표점", pitchTargetPosition);

            error = problems.Count == 0 ? string.Empty : string.Join("\n", problems);
            return problems.Count == 0;
        }

        private void AddIfOutsideBoundary(List<string> problems, string label, Vector3 position)
        {
            float dx = position.x - homePosition.x;
            float dz = position.z - homePosition.z;
            float horizontalDistance = Mathf.Sqrt((dx * dx) + (dz * dz));
            if (horizontalDistance > playBoundaryHorizontalRadius)
            {
                problems.Add(
                    $"{label}이(가) 경기 경계 밖이다. 홈에서 {horizontalDistance:F2} m, " +
                    $"경계 {playBoundaryHorizontalRadius:F2} m.");
            }
        }

        private void OnValidate()
        {
            if (playBoundaryHorizontalRadius < 1f)
            {
                playBoundaryHorizontalRadius = 1f;
            }

            if (playBoundaryUpperY <= playBoundaryLowerY)
            {
                playBoundaryUpperY = playBoundaryLowerY + 1f;
            }
        }

        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
