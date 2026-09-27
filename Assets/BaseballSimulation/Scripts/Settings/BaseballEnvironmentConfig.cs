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

        [Header("스트라이크존 (규칙 판정)")]
        [Tooltip("존 아래 높이(m, 지면 기준). 규칙상 무릎 아래 오목한 곳. 기본값은 Statcast 리그 평균 약 1.57 ft.")]
        [SerializeField, Min(0f)]
        private float strikeZoneBottom = 0.48f;

        [Tooltip("존 위 높이(m, 지면 기준). 규칙상 어깨 윗부분과 바지 윗선의 중간. 기본값은 Statcast 리그 평균 약 3.37 ft.")]
        [SerializeField, Min(0.1f)]
        private float strikeZoneTop = 1.03f;

        [Header("투구 위치")]
        [Tooltip("MachineTarget: 항상 PitchTarget으로 던진다(피칭머신). RandomAroundZone: 존 중심 둘레 정규분포.")]
        [SerializeField]
        private PitchLocationMode pitchLocationMode = PitchLocationMode.MachineTarget;

        [Tooltip("RandomAroundZone의 좌우/높이 표준편차(m). 0.28 m면 존 통과율이 MLB와 비슷한 약 46%다.")]
        [SerializeField]
        private Vector2 pitchLocationSpread = new Vector2(0.28f, 0.28f);

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

        [Header("주루 (m/s, m)")]
        [Tooltip("타자주자 이동 속력(m/s). 가속 없이 일정하다.")]
        [SerializeField, Min(0.1f)] private float runnerSpeed = 7f;
        [Tooltip("베이스 중심에서 이 거리 안에 들어오면 베이스를 밟은 것으로 본다(m).")]
        [SerializeField, Min(0.01f)] private float runnerArrivalRadius = 0.3f;
        public float RunnerSpeed => runnerSpeed;
        public float RunnerArrivalRadius => runnerArrivalRadius;

        [Header("수비 (3단계, m/s, m)")]
        [Tooltip("수비수 최고 이동 속력(m/s).")]
        [SerializeField, Min(0.1f)] private float fielderSpeed = 7.5f;
        [Tooltip("수비수 가속도(m/s²). 방향 전환도 이 한도 안에서 한다.")]
        [SerializeField, Min(0.1f)] private float fielderAcceleration = 15f;
        [Tooltip("공이 수비수 몸 중심 축에서 이 수평 거리 안으로 지나면 잡는다(m).")]
        [SerializeField, Min(0.05f)] private float catchRadius = 1.0f;
        [Tooltip("잡을 수 있는 공의 최고 높이(m, 지면 기준).")]
        [SerializeField, Min(0.1f)] private float catchReachHeight = 2.4f;
        [Tooltip("송구 속력(m/s).")]
        [SerializeField, Min(1f)] private float throwSpeed = 30f;
        [Tooltip("공을 가진 수비수가 베이스 중심에서 이 거리 안에 있으면 베이스를 밟은 것으로 본다(m).")]
        [SerializeField, Min(0.05f)] private float baseCoverRadius = 0.6f;
        [Tooltip("공을 가진 수비수와 베이스를 벗어난 주자의 거리가 이 값 안이면 태그 아웃이다(m).")]
        [SerializeField, Min(0.05f)] private float tagRadius = 0.8f;
        [Tooltip("모든 살아 있는 주자가 베이스에 멈추고 수비가 공을 가진 상태가 이 시간(s) 이어지면 플레이가 죽는다. " +
            "그 사이 주자는 태그업·추가 진루를 시도할 수 있다.")]
        [SerializeField, Min(0f)] private float playDeadSeconds = 1f;
        public float PlayDeadSeconds => playDeadSeconds;
        public float FielderSpeed => fielderSpeed;
        public float FielderAcceleration => fielderAcceleration;
        public float CatchRadius => catchRadius;
        public float CatchReachHeight => catchReachHeight;
        public float ThrowSpeed => throwSpeed;
        public float BaseCoverRadius => baseCoverRadius;
        public float TagRadius => tagRadius;

        [Header("타격 파워와 난수")]
        [Tooltip("스윙마다 추첨하는 타구 속도 배율 최소/최대. 같은 값이면 배율이 고정된다(학습 재현성 권장).")]
        [SerializeField] private Vector2 swingPowerMultiplierRange = new Vector2(1.2f, 2f);
        [Tooltip("파워 배율 난수원의 시드. 같은 시드와 같은 스윙 순서면 같은 배율이 나온다.")]
        [SerializeField] private int randomSeed = 12345;
        public Vector2 SwingPowerMultiplierRange => swingPowerMultiplierRange;
        public int RandomSeed => randomSeed;

        [Header("공기 역학 (항력 + 역회전 양력, Statcast 비거리 기준 보정)")]
        [Tooltip("끄면 진공 탄도(기존 동작)로 돌아간다.")]
        [SerializeField] private bool aerodynamicsEnabled = true;
        [Tooltip("공기 밀도(kg/m³). 해수면 약 20°C는 1.2, 고지대 구장은 더 낮다.")]
        [SerializeField, Min(0.1f)] private float airDensity = 1.2f;
        [Tooltip("항력 계수. 0.40은 역회전 모델과 함께 타구 비거리 기준값에 맞춘 값이다.")]
        [SerializeField, Range(0.05f, 1f)] private float dragCoefficient = 0.4f;
        [Tooltip("피칭머신 직구 역회전(rpm). MLB 포심 평균은 약 2300 rpm.")]
        [SerializeField, Range(0f, 3500f)] private float pitchBackspinRpm = 2200f;
        [Tooltip("타구 발사각 1°당 역회전(rpm). 음수 발사각은 전진 회전(탑스핀)이 된다.")]
        [SerializeField, Range(0f, 200f)] private float battedBackspinRpmPerDegree = 70f;
        [Tooltip("타구 회전 크기 상한(rpm).")]
        [SerializeField, Range(0f, 5000f)] private float maxBattedSpinRpm = 2500f;
        [Header("구종 (우투수 기준, 2단계 투수와 1단계 직구가 쓴다)")]
        [Tooltip("구종별 구속 범위·회전수·휘는 방향·회전 효율. 다섯 구종이 모두 있어야 한다.")]
        [SerializeField] private PitchTypeProfile[] pitchProfiles = DefaultPitchProfiles();
        public PitchTypeProfile GetPitchProfile(PitchType type)
        {
            if (pitchProfiles != null)
                foreach (PitchTypeProfile profile in pitchProfiles)
                    if (profile.Type == type) return profile;
            foreach (PitchTypeProfile profile in DefaultPitchProfiles())
                if (profile.Type == type) return profile;
            return default;
        }

        /// <summary>
        /// 우투수 기본값. 휘는 방향은 포수 시점 위 0°, 팔 쪽(3루 쪽) 90°, 아래 180°, 글러브 쪽 270°다.
        /// 방향·효율은 구속 범위 가운데에서 존 중앙으로 던질 때 회전 없는 공 대비 변화량이 MLB 우투수 평균
        /// (포심 -18/+41, 투심 -38/+20, 커브 +23/-25, 슬라이더 +20/+2, 체인지업 -36/+18 cm; 좌우/상하)에 맞도록 보정했다.
        /// 이 양력 근사와 투수판 기준 비행 거리에서는 효율이 실제 회전 효율보다 작게 나온다(docs/pitcher-agent.md).
        /// </summary>
        public static PitchTypeProfile[] DefaultPitchProfiles() => new[]
        {
            new PitchTypeProfile(PitchType.FourSeam, new Vector2(135f, 155f), 2300f, 22f, 0.42f),
            new PitchTypeProfile(PitchType.TwoSeam, new Vector2(130f, 150f), 2150f, 62f, 0.40f),
            new PitchTypeProfile(PitchType.Curve, new Vector2(110f, 130f), 2500f, 233f, 0.29f),
            new PitchTypeProfile(PitchType.Slider, new Vector2(125f, 140f), 2400f, 276f, 0.22f),
            new PitchTypeProfile(PitchType.Changeup, new Vector2(120f, 138f), 1750f, 63f, 0.42f),
        };

        public bool AerodynamicsEnabled => aerodynamicsEnabled;
        public float AirDensity => airDensity;
        public float DragCoefficient => dragCoefficient;
        public float PitchBackspinRpm => pitchBackspinRpm;
        public float BattedBackspinRpmPerDegree => battedBackspinRpmPerDegree;
        public float MaxBattedSpinRpm => maxBattedSpinRpm;

        [Header("지면·펜스")]
        [Tooltip("공-지면/펜스 반발 계수. 천연 잔디 근사.")]
        [SerializeField, Range(0f, 1f)] private float groundRestitution = 0.4f;
        [Tooltip("공-지면/펜스 마찰 계수.")]
        [SerializeField, Range(0f, 1.5f)] private float groundFriction = 0.5f;
        [Tooltip("잔디 구름 저항 계수. 감속도 = 계수 × g. 0이면 구름 저항 없음.")]
        [SerializeField, Range(0f, 1f)] private float rollingResistance = 0.25f;
        [Tooltip("외야 펜스 높이(m). 펜스는 경기 경계 반경에 선다. MLB 일반 높이 8 ft = 2.44 m.")]
        [SerializeField, Min(0.1f)] private float fenceHeight = 2.44f;
        [Tooltip("공이 이 속력(m/s) 미만으로 정지 시간 동안 머물면 멈춘 것으로 본다.")]
        [SerializeField, Min(0.001f)] private float ballStopSpeed = 0.15f;
        [SerializeField, Min(0.02f)] private float ballStopSeconds = 1f;
        /// <summary>외야 펜스 두께(m). 펜스 생성 메뉴와 홈런 판정이 같은 값을 쓴다.</summary>
        public const float FenceThickness = 0.3f;
        public float GroundRestitution => groundRestitution;
        public float GroundFriction => groundFriction;
        public float RollingResistance => rollingResistance;
        public float FenceHeight => fenceHeight;
        public float BallStopSpeed => ballStopSpeed;
        public float BallStopSeconds => ballStopSeconds;

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

        public float StrikeZoneBottom => strikeZoneBottom;
        public float StrikeZoneTop => strikeZoneTop;
        public PitchLocationMode PitchLocationMode => pitchLocationMode;
        public Vector2 PitchLocationSpread => pitchLocationSpread;

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
            if (!IsFinite(runnerSpeed) || runnerSpeed <= 0f || !IsFinite(runnerArrivalRadius) || runnerArrivalRadius <= 0f)
                problems.Add("주자 속력과 베이스 도착 반경은 유한한 양수여야 한다.");
            foreach (float value in new[] { fielderSpeed, fielderAcceleration, catchRadius, catchReachHeight, throwSpeed, baseCoverRadius, tagRadius })
                if (!IsFinite(value) || value <= 0f) { problems.Add("수비 속력·가속도·포구·송구·베이스·태그 값은 유한한 양수여야 한다."); break; }
            if (!IsFinite(swingPowerMultiplierRange.x) || !IsFinite(swingPowerMultiplierRange.y) ||
                swingPowerMultiplierRange.x <= 0f || swingPowerMultiplierRange.x > swingPowerMultiplierRange.y)
                problems.Add("파워 배율 범위는 0 < 최소 <= 최대여야 한다.");
            foreach (float value in new[] { airDensity, dragCoefficient, fenceHeight, ballStopSpeed, ballStopSeconds })
                if (!IsFinite(value) || value <= 0f) problems.Add("공기 밀도·항력 계수·펜스 높이·정지 기준은 유한한 양수여야 한다.");
            foreach (float value in new[] { pitchBackspinRpm, battedBackspinRpmPerDegree, maxBattedSpinRpm,
                groundRestitution, groundFriction, rollingResistance })
                if (!IsFinite(value) || value < 0f) problems.Add("회전·반발·마찰·구름 저항 값은 0 이상이어야 한다.");
            foreach (PitchType type in (PitchType[])System.Enum.GetValues(typeof(PitchType)))
            {
                bool found = false;
                if (pitchProfiles != null)
                    foreach (PitchTypeProfile profile in pitchProfiles)
                        if (profile.Type == type) { found = true; if (!profile.IsValid) problems.Add($"{type} 구종 설정이 잘못됐다(구속 0 < 최소 <= 최대, 회전수 0 이상, 효율 0~1)."); }
                if (!found) problems.Add($"{type} 구종 설정이 없다.");
            }

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

            if (!IsFinite(strikeZoneBottom) || !IsFinite(strikeZoneTop) || strikeZoneBottom < 0f ||
                strikeZoneTop <= strikeZoneBottom)
            {
                problems.Add($"스트라이크존은 0 <= 아래({strikeZoneBottom} m) < 위({strikeZoneTop} m)여야 한다.");
            }

            if (!IsFinite(pitchLocationSpread.x) || !IsFinite(pitchLocationSpread.y) ||
                pitchLocationSpread.x < 0f || pitchLocationSpread.y < 0f)
            {
                problems.Add("투구 위치 표준편차는 0 이상의 유한한 값이어야 한다.");
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

    /// <summary>한 구종의 물리 기본값. 회전축은 휘는 방향과 회전 효율로 정한다(<see cref="PitchPhysics.SpinVector"/>).</summary>
    [System.Serializable]
    public struct PitchTypeProfile
    {
        [SerializeField] private PitchType type;
        [Tooltip("구속 범위(km/h) 최소/최대. 투수 Agent의 구속 행동이 이 범위에 대응한다.")]
        [SerializeField] private Vector2 speedRangeKmh;
        [SerializeField, Min(0f)] private float spinRpm;
        [Tooltip("마그누스 힘 방향(°). 포수 시점 위 0°, 팔 쪽(3루 쪽) 90°, 아래 180°, 글러브 쪽 270°.")]
        [SerializeField] private float movementDegrees;
        [Tooltip("회전 효율 0~1. 나머지 회전은 진행 방향과 나란한 자이로 회전이라 공을 휘게 하지 않는다.")]
        [SerializeField, Range(0f, 1f)] private float spinEfficiency;

        public PitchTypeProfile(PitchType type, Vector2 speedRangeKmh, float spinRpm, float movementDegrees, float spinEfficiency)
        {
            this.type = type;
            this.speedRangeKmh = speedRangeKmh;
            this.spinRpm = spinRpm;
            this.movementDegrees = movementDegrees;
            this.spinEfficiency = spinEfficiency;
        }

        public PitchType Type => type;
        public Vector2 SpeedRangeKmh => speedRangeKmh;
        public float MinSpeed => speedRangeKmh.x / 3.6f;
        public float MaxSpeed => speedRangeKmh.y / 3.6f;
        public float SpinRpm => spinRpm;
        public float MovementDegrees => movementDegrees;
        public float SpinEfficiency => spinEfficiency;
        public bool IsValid => BaseballEnvironmentConfig.IsFinite(speedRangeKmh.x) && BaseballEnvironmentConfig.IsFinite(speedRangeKmh.y) &&
            speedRangeKmh.x > 0f && speedRangeKmh.x <= speedRangeKmh.y && BaseballEnvironmentConfig.IsFinite(spinRpm) && spinRpm >= 0f &&
            BaseballEnvironmentConfig.IsFinite(movementDegrees) && spinEfficiency >= 0f && spinEfficiency <= 1f;
    }
}
