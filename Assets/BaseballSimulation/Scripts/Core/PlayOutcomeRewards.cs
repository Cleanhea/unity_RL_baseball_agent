namespace BaseballSimulation
{
    /// <summary>
    /// 타석·플레이 결과 보상(docs/fielding-agents.md, docs/game-situation.md). 투구 단위 보상(타자·투수 보상 계산기)에 더해
    /// 타석이 끝날 때 타자·투수에게, 인플레이 플레이가 끝날 때 주자 그룹과 수비 그룹에게 준다. ML-Agents 타입에 의존하지 않는다.
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
    }
}
