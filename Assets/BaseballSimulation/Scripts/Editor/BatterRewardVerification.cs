using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    /// <summary>보상식 예시와 PlayDirector 이벤트 연결을 일시 정지한 Play Mode에서 검증한다.</summary>
    public static class BatterRewardVerification
    {
        [MenuItem("Tools/Baseball Simulation/Verify Batter Rewards (Paused Play Mode)")]
        public static void RunMenu() => Debug.Log(Run());

        public static string Run()
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
                throw new InvalidOperationException("Enter Play Mode and pause first.");
            var director = UnityEngine.Object.FindFirstObjectByType<PlayDirector>();
            if (director == null) throw new InvalidOperationException("Missing PlayDirector.");

            SimulationMode previousMode = Physics.simulationMode;
            bool manual = director.ManualInputEnabled;
            var report = new StringBuilder();
            try
            {
                director.SetManualInputEnabled(false);
                Physics.simulationMode = SimulationMode.Script;
                VerifyFormula(report);
                VerifyDirectorEvents(report, director);
                report.AppendLine("PASS batter rewards: examples, duplicate guards, reset and Director event flow.");
                return report.ToString();
            }
            finally
            {
                director.RequestResetPlay();
                Step(director);
                Physics.simulationMode = previousMode;
                director.SetManualInputEnabled(manual);
            }
        }

        private static void VerifyFormula(StringBuilder report)
        {
            using (var rewards = new BatterRewardTracker())
            {
                int completed = 0;
                int additions = 0;
                float streamed = 0f;
                rewards.EpisodeCompleted += _ => completed++;
                rewards.RewardAdded += amount => { additions++; streamed += amount; };

                rewards.BeginEpisode();
                rewards.RecordPitchCall(PitchCall.SwingingStrike, false);
                Expect(rewards, -1f, "whiff");
                Require(rewards.GetSnapshot().Miss == -1f && completed == 1, "whiff component and completion");
                rewards.RecordPitchCall(PitchCall.SwingingStrike, false);
                Require(additions == 1, "duplicate pitch call is ignored");

                rewards.BeginEpisode();
                Require(rewards.GetSnapshot().Total == 0f && !rewards.GetSnapshot().Complete, "episode reset");
                rewards.RecordPitchCall(PitchCall.CalledStrike, false);
                Expect(rewards, -1f, "called strike");

                rewards.BeginEpisode();
                rewards.RecordPitchCall(PitchCall.Ball, false);
                Expect(rewards, 0f, "taken ball");

                rewards.BeginEpisode();
                rewards.RecordContact();
                rewards.RecordBattedBallCall(Hit(BattedBallCall.Fair, 25f, 10f, 2f));
                Expect(rewards, 0.5f, "soft fair ball");

                rewards.BeginEpisode();
                rewards.RecordContact();
                rewards.RecordBattedBallCall(Hit(BattedBallCall.Fair, 40f, 10f, 2f));
                Expect(rewards, 1.1f, "hard fair ball");
                rewards.RecordBattedBallCall(Hit(BattedBallCall.GroundRuleDouble, 40f, 10f, 2f));
                Expect(rewards, 1.1f, "fair then ground-rule double has no second speed reward");

                rewards.BeginEpisode();
                rewards.RecordContact();
                rewards.RecordBattedBallCall(Hit(BattedBallCall.Fair, 25f, 30f, 5f));
                Expect(rewards, -0.5f, "high long fair fly");
                Require(rewards.GetSnapshot().PopFly == -1f, "pop-fly component");

                rewards.BeginEpisode();
                rewards.RecordContact();
                rewards.RecordBattedBallCall(Hit(BattedBallCall.Foul, 50f, 30f, 5f));
                Expect(rewards, -1f, "fast foul");
                Require(rewards.GetSnapshot().ExitSpeed == 0f && rewards.GetSnapshot().PopFly == 0f,
                    "foul has no speed bonus or pop-fly penalty");

                rewards.BeginEpisode();
                rewards.RecordContact();
                rewards.RecordBattedBallCall(Hit(BattedBallCall.HomeRun, 50f, 30f, 5f));
                Expect(rewards, 4.5f, "home run");
                Require(rewards.GetSnapshot().PopFly == 0f, "home run has no pop-fly penalty");
                rewards.RecordContact();
                Require(completed == 8 && Mathf.Abs(streamed - 2.6f) < 0.0001f,
                    "eight episodes, one completion each, streamed deltas equal totals");
                report.AppendLine($"formula: 8 examples, {completed} completions, {additions} nonzero reward deltas");
            }
        }

        private static void VerifyDirectorEvents(StringBuilder report, PlayDirector director)
        {
            int bestTick = BattingEvaluationVerification.FindBestSwingTick(director);
            using (var rewards = new BatterRewardTracker(director))
            {
                float streamed = 0f;
                int completed = 0;
                rewards.RewardAdded += value => streamed += value;
                rewards.EpisodeCompleted += _ => completed++;

                Reset(director);
                rewards.BeginEpisode();
                director.RequestThrowPitch();
                for (int i = 0; i < 500 && !rewards.GetSnapshot().Complete; i++) Step(director);
                Require(director.GetPitchCall().Call == PitchCall.CalledStrike,
                    "reference pitch without swing is called strike");
                Expect(rewards, -1f, "live called strike");

                Reset(director);
                rewards.BeginEpisode();
                director.RequestThrowPitch();
                for (int i = 0; i < 1500 && !rewards.GetSnapshot().Complete; i++)
                {
                    if (i == bestTick) director.RequestSwing(new SwingCommand(0f, 15f));
                    Step(director);
                }
                BatterRewardSnapshot hit = rewards.GetSnapshot();
                Require(director.HasContact && hit.Complete && hit.Contact == 0.5f && hit.ExitSpeed >= 0f,
                    "live contact and fair-hit reward");
                Require(director.GetBattedBallSnapshot().Call == BattedBallCall.Fair ||
                    director.GetBattedBallSnapshot().Call == BattedBallCall.HomeRun,
                    "reference swing fair or home run");
                Require(completed == 2 && Mathf.Abs(streamed - (-1f + hit.Total)) < 0.0001f,
                    "Director event stream equals reward totals");
                report.AppendLine($"director: best swing tick {bestTick}, call {director.GetBattedBallSnapshot().Call}, " +
                    $"reward {hit.Total:F3}, contact {hit.Contact:F1}, speed {hit.ExitSpeed:F3}, " +
                    $"pop {hit.PopFly:F3}, home run {hit.HomeRun:F1}");
            }
        }

        private static BattedBallSnapshot Hit(BattedBallCall call, float speed, float apex, float hang) =>
            new BattedBallSnapshot(call, Vector3.forward * speed, 0f, apex, true,
                Vector3.forward * 40f, hang);

        private static void Reset(PlayDirector director)
        {
            director.RequestResetPlay(12345);
            Step(director);
        }

        private static void Step(PlayDirector director)
        {
            director.SendMessage("FixedUpdate");
            Physics.Simulate(Time.fixedDeltaTime);
        }

        private static void Expect(BatterRewardTracker rewards, float total, string label)
        {
            BatterRewardSnapshot snapshot = rewards.GetSnapshot();
            Require(snapshot.Complete && Mathf.Abs(snapshot.Total - total) < 0.0001f,
                $"{label}: expected {total:F3}, got {snapshot.Total:F3}");
        }

        private static void Require(bool condition, string label)
        {
            if (!condition) throw new Exception("Batter reward verification failed: " + label);
        }
    }
}
