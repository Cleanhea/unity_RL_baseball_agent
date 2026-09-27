using System;
using UnityEditor;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    /// <summary>
    /// 볼카운트·아웃·주자 규칙(GameSituation)과 타석·플레이 결과 보상 식을 Play Mode 없이 확인한다(docs/game-situation.md).
    /// </summary>
    public static class GameSituationVerification
    {
        [MenuItem("Tools/Baseball Simulation/Verify Count And Situation Rules")]
        public static void RunMenu() => Debug.Log(Run());

        public static string Run()
        {
            var s = new GameSituation();
            // 볼넷: 4볼로 타석 종료, 타자 1루.
            foreach (PitchCall call in new[] { PitchCall.Ball, PitchCall.Ball, PitchCall.CalledStrike, PitchCall.Ball }) s.RecordPitchCall(call);
            Require(s.Balls == 3 && s.Strikes == 1 && !s.PlateAppearanceOver, "3-1 count");
            s.RecordPitchCall(PitchCall.Ball);
            Require(s.PlateAppearanceOver && s.Result == PlateAppearanceResult.Walk && s.IsOccupied(1) && s.Outs == 0, "ball four walks the batter");
            s.RecordPitchCall(PitchCall.CalledStrike);
            Require(s.Strikes == 1, "calls after the plate appearance ended are ignored");

            // 새 타석: 카운트만 지운다. 파울은 2스트라이크에서 늘지 않는다. 삼진은 아웃 +1.
            s.BeginPlateAppearanceIfNeeded();
            Require(s.Balls == 0 && s.Strikes == 0 && s.IsOccupied(1), "new plate appearance keeps runners");
            foreach (PitchCall call in new[] { PitchCall.Foul, PitchCall.Foul, PitchCall.Foul, PitchCall.Foul }) s.RecordPitchCall(call);
            Require(s.Strikes == 2 && !s.PlateAppearanceOver, "fouls stop adding strikes at two");
            s.RecordPitchCall(PitchCall.SwingingStrike);
            Require(s.Result == PlateAppearanceResult.Strikeout && s.Outs == 1, "strike three is an out");

            // 만루 볼넷은 1점. 밀려나지 않는 주자는 제자리.
            s.SetSituation(true, false, true, 1);
            for (int i = 0; i < 4; i++) s.RecordPitchCall(PitchCall.Ball);
            Require(s.IsOccupied(1) && s.IsOccupied(2) && s.IsOccupied(3) && s.RunsThisHalfInning == 0, "walk with 1st and 3rd loads the bases without a run");
            s.BeginPlateAppearanceIfNeeded();
            for (int i = 0; i < 4; i++) s.RecordPitchCall(PitchCall.Ball);
            Require(s.RunsThisHalfInning == 1 && s.IsOccupied(3), "bases-loaded walk forces in a run");

            // 인플레이는 타석만 끝내고, 플레이 결과로 아웃·득점·주자를 반영한다. 3아웃이면 다음 타석에 반 이닝을 새로 연다.
            s.BeginPlateAppearanceIfNeeded();
            s.RecordPitchCall(PitchCall.InPlay);
            Require(s.PlateAppearanceOver && s.Result == PlateAppearanceResult.InPlay, "ball in play ends the plate appearance");
            s.ApplyPlayResult(false, true, false, 2, 1);
            Require(s.Outs == 3 && s.HalfInningOver && s.RunsThisHalfInning == 2, "double play makes the third out");
            s.BeginPlateAppearanceIfNeeded();
            Require(s.Outs == 0 && !s.IsOccupied(2) && s.RunsThisHalfInning == 0, "new half-inning clears outs, bases and runs");

            // 주자 오브젝트가 없는 씬은 베이스를 넘기지 않는다.
            var noBases = new GameSituation { TrackBases = false };
            for (int i = 0; i < 4; i++) noBases.RecordPitchCall(PitchCall.Ball);
            Require(noBases.Result == PlateAppearanceResult.Walk && !noBases.IsOccupied(1), "walk without runner objects keeps bases empty");

            // 결과 보상 식.
            var single = new PlaySummary(true, PitchEndReason.RunnerSafe, 0, 1, 1, 0, 3);
            var doublePlay = new PlaySummary(true, PitchEndReason.ForceOut, 2, 0, 0, 2, 0);
            Require(Mathf.Approximately(PlayOutcomeRewards.BatterPlateAppearance(PlateAppearanceResult.Walk, false, default), 1f) &&
                Mathf.Approximately(PlayOutcomeRewards.BatterPlateAppearance(PlateAppearanceResult.Strikeout, false, default), -1f) &&
                Mathf.Approximately(PlayOutcomeRewards.BatterPlateAppearance(PlateAppearanceResult.InPlay, true, single), 1f) &&
                Mathf.Approximately(PlayOutcomeRewards.BatterPlateAppearance(PlateAppearanceResult.InPlay, true, doublePlay), -1f) &&
                Mathf.Approximately(PlayOutcomeRewards.BatterPlateAppearance(PlateAppearanceResult.InPlay, false, single), 0f),
                "batter plate-appearance rewards: walk +1, strikeout -1, RBI single +1, double play -1, unresolved 0");
            Require(Mathf.Approximately(PlayOutcomeRewards.PitcherPlateAppearance(PlateAppearanceResult.InPlay, true, single), -1f), "pitcher mirrors the batter");
            Require(Mathf.Approximately(PlayOutcomeRewards.Runners(single), 1.75f) && Mathf.Approximately(PlayOutcomeRewards.Defense(single), -1.75f) &&
                Mathf.Approximately(PlayOutcomeRewards.Runners(doublePlay), -2f) && Mathf.Approximately(PlayOutcomeRewards.Defense(doublePlay), 2f),
                "runner and defense group rewards");
            return "PASS count and situation: walk/strikeout/foul-with-two-strikes, bases-loaded walk run, in-play result, third out clears the half-inning, " +
                "no-runner scenes keep bases empty, outcome reward formulas.";
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception("Situation verification failed: " + message);
        }
    }
}
