using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 타석·플레이 결과 보상(docs/fielding-agents.md, docs/game-situation.md). 투구 단위 보상(타자·투수 보상 계산기)에 더해
    /// 타석이 끝날 때 타자·투수에게, 인플레이 플레이가 끝날 때 주자 그룹과 수비 그룹에게 준다.
    /// 수비 그룹에는 결과 보상을 찾아가도록 돕는 포텐셜 기반 보조 보상(<see cref="DefensePotential"/>)도 있다. ML-Agents 타입에 의존하지 않는다.
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
        public const float DefenseChasePerMeter = 0.02f;
        /// <summary>타구를 한 번이라도 잡은 뒤의 포텐셜. 첫 포구 순간 보조 보상이 된다.</summary>
        public const float DefenseFieldedValue = 0.25f;
        /// <summary>아직 밟히지 않은 포스 베이스마다, 그 베이스와 가장 가까운 수비수의 수평 거리 1 m당 포텐셜 감소.</summary>
        public const float DefenseCoverPerMeter = 0.02f;

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
        /// ① 첫 포구 전: −0.02 × (공과 가장 가까운 수비수의 수평 거리). 뜬공은 공기 저항 없는 예상 낙하 지점을 쓴다.
        /// ② 첫 포구 뒤: +0.25. ③ 아직 밟히지 않은 포스 베이스마다: −0.02 × (그 베이스와 가장 가까운 수비수의 수평 거리).
        /// </summary>
        public static float DefensePotential(PlayDirector director)
        {
            if (director == null || director.FielderCount == 0) return 0f;
            float potential = director.BattedBallFielded
                ? DefenseFieldedValue
                : -DefenseChasePerMeter * NearestFielderDistance(director, ChasePoint(director));
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
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < director.FielderCount; i++)
            {
                Vector3 offset = director.GetFielder(i).Position - point;
                nearest = Mathf.Min(nearest, new Vector2(offset.x, offset.z).magnitude);
            }
            return nearest;
        }
    }
}
