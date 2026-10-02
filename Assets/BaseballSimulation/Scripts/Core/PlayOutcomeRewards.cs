using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 타석·플레이 결과 보상(docs/fielding-agents.md, docs/game-situation.md). 투구 단위 보상(타자·투수 보상 계산기)에 더해
    /// 타석이 끝날 때 타자·투수에게, 인플레이 플레이가 끝날 때 주자 그룹과 수비 그룹에게 준다.
    /// 수비 그룹에는 결과 보상을 찾아가도록 돕는 포텐셜 기반 보조 보상(<see cref="DefensePotential"/>)도 있다.
    /// 쫓는 수비수에게는 개인 쫓기 보상(<see cref="ChasePotential"/>), 처음 공을 잡은 수비수에게는 개인 포구 보상(<see cref="FieldingReward"/>)을 준다.
    /// 공을 처리하지 않는 수비수는 역할별 임무(<see cref="TryGetPositionDuty"/>: 베이스 커버 또는 자기 구역)를 받아, 베이스 커버면
    /// 개인 보조 보상(<see cref="CoverPotential"/>), 모든 임무에 자리 이탈 개인 감점(<see cref="PositionReward"/>)을 받는다. ML-Agents 타입에 의존하지 않는다.
    /// </summary>
    public static class PlayOutcomeRewards
    {
        public const float WalkValue = 1f;
        public const float StrikeoutValue = 1f;
        public const float BatterBaseValue = 0.5f;
        public const float BatterRunValue = 0.5f;
        public const float BatterOutValue = 0.5f;
        public const float RunnerBaseValue = 0.25f;
        public const float RunValue = 1f;
        public const float OutValue = 1f;
        /// <summary>타구가 살아 있는 고정 단계마다 수비 그룹에 주는 시간 감점. 빠른 처리를 유도한다.</summary>
        public const float DefenseStepPenalty = -0.0005f;

        /// <summary>수비 보조 보상의 할인율. Training/config 3단계 설정의 BaseballFielder extrinsic gamma와 같아야 최적 정책이 바뀌지 않는다.</summary>
        public const float DefenseShapingGamma = 0.99f;
        /// <summary>첫 포구 전: 공(뜬공은 예상 낙하 지점)과 가장 가까운 수비수의 수평 거리 1 m당 포텐셜 감소.</summary>
        public const float DefenseChasePerMeter = 0.05f;
        /// <summary>타구를 한 번이라도 잡은 뒤의 포텐셜. 첫 포구 순간 보조 보상이 된다.</summary>
        public const float DefenseFieldedValue = 0.25f;
        /// <summary>아직 밟히지 않은 포스 베이스마다, 그 베이스와 가장 가까운 수비수의 수평 거리 1 m당 포텐셜 감소.</summary>
        public const float DefenseCoverPerMeter = 0.02f;
        /// <summary>
        /// 개인 포구 보상: 타구에 처음 닿은 수비수에게 한 번 준다. 포텐셜이 아니라 끝에서 회수하지 않는 실제 목표다.
        /// 그룹 포텐셜의 <see cref="DefenseFieldedValue"/>는 플레이 끝에 상쇄되어 공을 잡아도 순이익이 없었기 때문에 더했다.
        /// </summary>
        public const float FieldingReward = 0.3f;
        /// <summary>
        /// 베이스 커버 임무의 개인 보조 보상: 맡은 베이스와의 수평 거리 1 m당 포텐셜 감소.
        /// 주자가 오는지에 따라 계수를 바꾸지 않는다. 바꾸면 주자가 세이프로 도착하는 순간 계수가 줄어 멀리 있던 수비수가 오히려 보상을 받는다.
        /// </summary>
        public const float CoverPerMeter = 0.02f;
        /// <summary>자리 유지 허용 반경: 베이스 커버 임무는 그 베이스 3 m 안이다.</summary>
        public const float BaseDutyRadius = 3f;
        /// <summary>자리 유지 허용 반경: 베이스를 맡지 않은 투수·2루수·유격수는 시작 위치 6 m 안이다.</summary>
        public const float InfieldZoneRadius = 6f;
        /// <summary>자리 유지 허용 반경: 외야수는 시작 위치 12 m 안이다.</summary>
        public const float OutfieldZoneRadius = 12f;
        /// <summary>허용 반경 바깥 1 m당 초당 감점. 포텐셜이 아닌 실제 역할 목표다.</summary>
        public const float PositionPenaltyPerMeterSecond = 0.005f;
        /// <summary>멀리 이탈해도 초당 감점은 0.1을 넘지 않는다. 12 s 시간 초과 플레이 전체로 최대 −1.2다.</summary>
        public const float PositionMaxPenaltyPerSecond = 0.1f;

        public static bool IsOut(PitchEndReason reason) =>
            reason == PitchEndReason.FlyOut || reason == PitchEndReason.ForceOut || reason == PitchEndReason.TagOut;

        /// <summary>
        /// 타자의 타석 결과 보상: 볼넷 +1, 삼진 -1. 판정까지 끝난 인플레이는 타자가 얻은 베이스마다 +0.5, 득점(타점)마다 +0.5,
        /// 그 플레이의 아웃마다 -0.5. 수비가 없어 판정 전에 초기화한 인플레이(1·2단계)는 0이다.
        /// </summary>
        public static float BatterPlateAppearance(PlateAppearanceResult result, bool resolved, PlaySummary play)
        {
            switch (result)
            {
                case PlateAppearanceResult.Walk: return WalkValue;
                case PlateAppearanceResult.Strikeout: return -StrikeoutValue;
                case PlateAppearanceResult.InPlay:
                    return resolved && play.Resolved
                        ? BatterBaseValue * play.BatterBases + BatterRunValue * play.Runs - BatterOutValue * play.Outs
                        : 0f;
                default: return 0f;
            }
        }

        /// <summary>투수의 타석 결과 보상은 타자 결과 보상의 반대 부호다.</summary>
        public static float PitcherPlateAppearance(PlateAppearanceResult result, bool resolved, PlaySummary play) =>
            -BatterPlateAppearance(result, resolved, play);

        /// <summary>주자 그룹: 전진한 베이스마다 +0.25, 득점마다 +1, 베이스 위 아웃마다 -1(타자 뜬공 아웃은 제외).</summary>
        public static float Runners(PlaySummary play) =>
            RunnerBaseValue * play.BasesAdvanced + RunValue * play.Runs - OutValue * play.RunnerOuts;

        /// <summary>수비 그룹: 아웃마다 +1, 내준 베이스마다 -0.25, 득점마다 -1.</summary>
        public static float Defense(PlaySummary play) =>
            OutValue * play.Outs - RunnerBaseValue * play.BasesAdvanced - RunValue * play.Runs;

        /// <summary>
        /// 수비 그룹 보조 보상의 포텐셜 Φ. 컨트롤러가 수비 결정마다 γΦ(지금) − Φ(직전 결정)를, 플레이가 끝나면 −Φ(마지막 결정)를 준다.
        /// 그래서 한 플레이의 할인 합은 −Φ(첫 결정)이 되어 수비 행동과 무관하고 최적 정책을 바꾸지 않는다. 결과 보상까지 가는 중간 단계만 알려 준다.
        /// 첫 포구 뒤: +0.25. 아직 밟히지 않은 포스 베이스마다: −0.02 × (그 베이스와 가장 가까운 수비수의 수평 거리).
        /// 쫓기는 그룹에 중복 지급하지 않고 <see cref="ChasePotential"/>로 쫓는 수비수 개인에게 준다.
        /// </summary>
        public static float DefensePotential(PlayDirector director)
        {
            if (director == null || director.FielderCount == 0) return 0f;
            float potential = director.BattedBallFielded ? DefenseFieldedValue : 0f;
            FieldLayout field = director.FieldLayout;
            for (int slot = 0; slot < director.RunnerSlotCount; slot++)
            {
                RunnerSnapshot runner = director.GetRunnerSnapshot(slot);
                if (!runner.IsLive || !director.IsRunnerForced(slot)) continue;
                BaseId forcedBase = (BaseId)(((int)runner.StartBase + 1) % 4);
                potential -= DefenseCoverPerMeter * NearestFielderDistance(director, field.GetBasePosition(forcedBase));
            }
            return potential;
        }

        /// <summary>
        /// 진루·귀루 중인 살아 있는 주자가 그 베이스로 향하면 true다. 포스·태그·리터치 아웃이 날 수 있는 베이스다.
        /// 포스된 주자는 타구 순간 자동으로 진루하므로 포함된다. 보상이 아니라 역할별 베이스 커버 지표에 쓴다.
        /// </summary>
        public static bool IsBaseInPlay(PlayDirector director, BaseId baseId)
        {
            for (int slot = 0; slot < director.RunnerSlotCount; slot++)
            {
                RunnerSnapshot runner = director.GetRunnerSnapshot(slot);
                if ((runner.Phase == RunnerPhase.Advancing || runner.Phase == RunnerPhase.Returning) && runner.NextBase == baseId) return true;
            }
            return false;
        }

        /// <summary>공을 쫓는 수비수: 첫 포구 전, 예상 지점과 가장 가까운 수비수. 첫 포구 뒤에는 -1이다.</summary>
        public static int DefenseChaser(PlayDirector director)
        {
            if (director == null || director.FielderCount == 0 || director.BattedBallFielded) return -1;
            return NearestFielder(director, ChasePoint(director), out _);
        }

        /// <summary>
        /// 개인 쫓기 포텐셜. 첫 포구 전 예상 지점과 가장 가까운 수비수 한 명만 −0.05 × 수평 거리, 나머지는 0이다.
        /// 결정마다 γΦ_i' − Φ_i, 정상 종료 −Φ_i, 중단 γΦ_i(지금) − Φ_i(직전 결정)를 그 수비수에게만 준다.
        /// 쫓는 수비수가 바뀌어도 개인별 포텐셜을 유지해 할인 합이 −Φ_i(첫 결정)이 되게 한다.
        /// </summary>
        public static float ChasePotential(PlayDirector director, int fielderIndex)
        {
            if (director == null || fielderIndex < 0 || fielderIndex >= director.FielderCount || director.BattedBallFielded) return 0f;
            Vector3 point = ChasePoint(director);
            int chaser = NearestFielder(director, point, out float distance);
            return chaser == fielderIndex ? -DefenseChasePerMeter * distance : 0f;
        }

        /// <summary>
        /// 공을 처리하지 않는 수비수의 역할 임무. 공을 쫓는 수비수(<see cref="DefenseChaser"/>), 공을 쥔 수비수, 자기가 던진 공이 아직
        /// 날아가는(잡히지 않은) 수비수는 임무가 없다(false). 나머지는 다음과 같다.
        /// <list type="bullet">
        /// <item>포수 → 홈, 1루수 → 1루, 3루수 → 3루 커버.</item>
        /// <item>투수 → 1루수가 공을 처리하는 중이면 1루 커버, 아니면 자기 구역(시작 위치 6 m).</item>
        /// <item>2루수·유격수 → 타구가 3루 쪽(분사각 ≤ 0)이면 2루수, 1루 쪽이면 유격수가 2루 커버. 그 수비수가 공을 처리 중이면 다른 쪽이 맡는다.
        /// 2루를 맡지 않은 쪽은 자기 구역(6 m).</item>
        /// <item>외야수 → 자기 구역(시작 위치 12 m).</item>
        /// </list>
        /// </summary>
        public static bool TryGetPositionDuty(PlayDirector director, int fielderIndex, out PositionDuty duty)
        {
            duty = default;
            if (director == null || fielderIndex < 0 || fielderIndex >= director.FielderCount) return false;
            int chaser = DefenseChaser(director);
            if (IsBusy(director, fielderIndex, chaser)) return false;
            FielderController body = director.GetFielder(fielderIndex);
            FieldLayout field = director.FieldLayout;
            switch (body.Role)
            {
                case FielderRole.Catcher: duty = BaseDuty(field, BaseId.Home); return true;
                case FielderRole.FirstBase: duty = BaseDuty(field, BaseId.First); return true;
                case FielderRole.ThirdBase: duty = BaseDuty(field, BaseId.Third); return true;
                case FielderRole.Pitcher:
                    duty = IsRoleBusy(director, FielderRole.FirstBase, chaser) ? BaseDuty(field, BaseId.First) : ZoneDuty(body, InfieldZoneRadius);
                    return true;
                case FielderRole.SecondBase:
                case FielderRole.Shortstop:
                    duty = SecondBaseCoverRole(director, chaser) == body.Role ? BaseDuty(field, BaseId.Second) : ZoneDuty(body, InfieldZoneRadius);
                    return true;
                default:
                    duty = ZoneDuty(body, OutfieldZoneRadius);
                    return true;
            }
        }

        /// <summary>
        /// 2루를 맡는 역할. 타구 분사각이 양수(1루 쪽)면 유격수, 아니면 2루수가 먼저다. 먼저인 쪽이 공을 처리 중이면 다른 쪽이다.
        /// 분사각은 타구 순간 정해지므로 한 플레이 안에서 바뀌지 않는다.
        /// </summary>
        public static FielderRole SecondBaseCoverRole(PlayDirector director, int chaser)
        {
            bool rightSide = director.GetBattedBallSnapshot().SprayAngleDegrees > 0f;
            FielderRole first = rightSide ? FielderRole.Shortstop : FielderRole.SecondBase;
            FielderRole other = rightSide ? FielderRole.SecondBase : FielderRole.Shortstop;
            return IsRoleBusy(director, first, chaser) && !IsRoleBusy(director, other, chaser) ? other : first;
        }

        /// <summary>
        /// 베이스 커버 임무의 개인 포텐셜 Φ_i = −0.02 × (맡은 베이스와의 수평 거리). 자기 구역 임무와 임무가 없는 수비수는 0이다.
        /// 컨트롤러가 그 수비수에게만 그룹 보조 보상과 같은 방식(γΦ_i' − Φ_i, 끝 −Φ_i)으로 준다. 한 플레이의 할인 합은 −Φ_i(첫 결정)이라
        /// 최적 정책을 바꾸지 않는다. 송구한 수비수를 빼는 것은 던지는 순간 자기 베이스와 먼 만큼 감점되어 송구를 꺼리게 되지 않도록 하기 위해서다.
        /// </summary>
        public static float CoverPotential(PlayDirector director, int fielderIndex)
        {
            if (!TryGetPositionDuty(director, fielderIndex, out PositionDuty duty) || !duty.CoversBase) return 0f;
            return -CoverPerMeter * HorizontalDistance(director.GetFielder(fielderIndex).Position, duty.Anchor);
        }

        /// <summary>
        /// 타구가 살아 있는 동안, 임무가 있는 수비수의 자리 이탈 개인 감점. 임무 지점(베이스 또는 시작 위치)에서 허용 반경 안은 0,
        /// 밖은 −min(0.1, 0.005 × 초과 거리) × 경과 초다. 차분·종료 정산 없이 누적하므로 오래 멀리 머무는 행동이 실제로 불리하다.
        /// </summary>
        public static float PositionReward(PlayDirector director, int fielderIndex, float elapsedSeconds)
        {
            if (director == null || director.State != PlayState.BattedBallInFlight || !(elapsedSeconds > 0f) ||
                float.IsInfinity(elapsedSeconds) || !TryGetPositionDuty(director, fielderIndex, out PositionDuty duty)) return 0f;
            float distance = HorizontalDistance(director.GetFielder(fielderIndex).Position, duty.Anchor);
            float excess = Mathf.Max(0f, distance - duty.Radius);
            return -Mathf.Min(PositionMaxPenaltyPerSecond, PositionPenaltyPerMeterSecond * excess) * elapsedSeconds;
        }

        private static bool IsBusy(PlayDirector director, int fielderIndex, int chaser)
        {
            int holder = director.BallHolder;
            return fielderIndex == chaser || holder == fielderIndex || (holder < 0 && director.LastThrower == fielderIndex);
        }

        /// <summary>그 역할의 수비수가 공을 처리 중이거나 없으면 true다.</summary>
        private static bool IsRoleBusy(PlayDirector director, FielderRole role, int chaser)
        {
            for (int i = 0; i < director.FielderCount; i++)
                if (director.GetFielder(i).Role == role) return IsBusy(director, i, chaser);
            return true;
        }

        private static PositionDuty BaseDuty(FieldLayout field, BaseId baseId) =>
            new PositionDuty(field.GetBasePosition(baseId), BaseDutyRadius, true, baseId);

        private static PositionDuty ZoneDuty(FielderController body, float radius) =>
            new PositionDuty(body.HomeSpot, radius, false, BaseId.Home);

        /// <summary>쫓을 지점. 떠 있는 공은 지금 속도로 중력만 받아 지면(홈 높이)에 닿는 지점, 굴러가는 공은 지금 위치다.</summary>
        private static Vector3 ChasePoint(PlayDirector director)
        {
            PitchSnapshot ball = director.GetSnapshot();
            float g = -Physics.gravity.y;
            float height = ball.BallPosition.y - director.FieldLayout.HomePosition.y - director.EnvironmentConfig.BallRadius;
            if (height <= 0f || g <= 0f) return ball.BallPosition;
            float vy = ball.BallVelocity.y;
            float t = (vy + Mathf.Sqrt(vy * vy + 2f * g * height)) / g;
            return ball.BallPosition + new Vector3(ball.BallVelocity.x, 0f, ball.BallVelocity.z) * t;
        }

        private static float NearestFielderDistance(PlayDirector director, Vector3 point)
        {
            NearestFielder(director, point, out float distance);
            return distance;
        }

        private static int NearestFielder(PlayDirector director, Vector3 point, out float distance)
        {
            int nearest = -1;
            distance = float.PositiveInfinity;
            for (int i = 0; i < director.FielderCount; i++)
            {
                float d = HorizontalDistance(director.GetFielder(i).Position, point);
                if (d >= distance) continue;
                distance = d;
                nearest = i;
            }
            return nearest;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;
    }

    /// <summary>수비수 한 명의 역할 임무: 지점, 감점 없는 반경, 베이스 커버인지와 그 베이스(구역 임무면 의미 없음).</summary>
    public readonly struct PositionDuty
    {
        public PositionDuty(Vector3 anchor, float radius, bool coversBase, BaseId baseId)
        {
            Anchor = anchor; Radius = radius; CoversBase = coversBase; Base = baseId;
        }
        public Vector3 Anchor { get; }
        public float Radius { get; }
        public bool CoversBase { get; }
        public BaseId Base { get; }
    }
}
