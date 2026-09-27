using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    /// <summary>
    /// Runner rules on a temporary runner, then Director integration with real Rigidbody steps.
    /// Run in a paused Play Mode session; never edits scene or config assets.
    /// </summary>
    public static class RunnerVerification
    {
        [MenuItem("Tools/Baseball Simulation/Verify Runner (Paused Play Mode)")]
        public static void RunMenu() => Debug.Log(Run());

        public static string Run()
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
                throw new InvalidOperationException("Enter Play Mode and pause first.");
            var director = UnityEngine.Object.FindFirstObjectByType<PlayDirector>();
            var field = UnityEngine.Object.FindFirstObjectByType<FieldLayout>();
            if (director == null || field == null) throw new InvalidOperationException("Missing director or field.");
            var report = new StringBuilder();
            SimulationMode previousMode = Physics.simulationMode;
            bool manual = director.ManualInputEnabled;
            try
            {
                director.SetManualInputEnabled(false);
                Physics.simulationMode = SimulationMode.Script;
                VerifyRules(report, field);
                VerifyDirector(report, director, field);
                return report.ToString();
            }
            finally
            {
                Reset(director);
                Physics.simulationMode = previousMode;
                director.SetManualInputEnabled(manual);
            }
        }

        private static void VerifyRules(StringBuilder report, FieldLayout field)
        {
            BaseballEnvironmentConfig config = field.Config;
            var go = new GameObject("TemporaryRunnerRuleCheck");
            try
            {
                var runner = go.AddComponent<RunnerController>();
                runner.Initialize(config, field);
                var touches = new List<string>();
                runner.BaseReached += b => touches.Add($"{b}:{runner.Phase}");
                Vector3 start = field.PitchTargetPosition + new Vector3(config.ReferenceStanceOffset.x, 0f, config.ReferenceStanceOffset.y);
                start.y = field.HomePosition.y;
                float dt = Time.fixedDeltaTime;
                float time = 0f;

                Require(!runner.TryApplyDecision(RunnerDecision.Advance, out _), "No decision before contact");
                runner.Begin(start);
                Require(!runner.TryApplyDecision(RunnerDecision.Return, out _), "Batter-runner cannot return home");
                TickUntil(runner, ref time, dt, RunnerPhase.Holding);
                float expected = (Vector3.Distance(start, field.FirstBasePosition) - config.RunnerArrivalRadius) / config.RunnerSpeed;
                RunnerSnapshot first = runner.GetSnapshot();
                Require(Mathf.Abs(first.LastTouchTime - expected) < 0.001f, "Interpolated touch time");
                Tick(runner, ref time, dt, 10);
                Require(Vector3.Distance(runner.transform.position, field.FirstBasePosition) < 0.001f, "Holds on first base centre");
                Require(Joined(touches) == "First:Holding", "Stops at first without a decision");
                Require(!runner.TryApplyDecision(RunnerDecision.Return, out _), "Return rejected while holding");
                report.AppendLine($"rules: auto run to first, touch {first.LastTouchTime:F4}s (expected {expected:F4}s)");

                touches.Clear(); time = 0f;
                runner.Begin(start);
                Require(runner.TryApplyDecision(RunnerDecision.Advance, out _) && runner.GetSnapshot().TargetBase == BaseId.Second,
                    "Advance while running extends target");
                Require(runner.TryApplyDecision(RunnerDecision.Return, out _) && runner.GetSnapshot().TargetBase == BaseId.First,
                    "First-leg return cancels rounding");
                TickUntil(runner, ref time, dt, RunnerPhase.Holding);
                Require(Joined(touches) == "First:Holding", "Cancelled rounding holds at first");

                touches.Clear();
                Require(runner.TryApplyDecision(RunnerDecision.Advance, out _) && runner.GetSnapshot().NextBase == BaseId.Second,
                    "Advance from first");
                Tick(runner, ref time, dt, 50);
                Require(runner.TryApplyDecision(RunnerDecision.Return, out _) && runner.Phase == RunnerPhase.Returning &&
                    runner.GetSnapshot().NextBase == BaseId.First, "Return midway");
                Require(!runner.TryApplyDecision(RunnerDecision.Return, out _), "Duplicate return rejected");
                TickUntil(runner, ref time, dt, RunnerPhase.Holding);
                Require(Joined(touches) == "First:Holding", "Returned to first");

                touches.Clear();
                runner.TryApplyDecision(RunnerDecision.Advance, out _);
                Tick(runner, ref time, dt, 30);
                runner.TryApplyDecision(RunnerDecision.Return, out _);
                Tick(runner, ref time, dt, 5);
                Require(runner.TryApplyDecision(RunnerDecision.Advance, out _) && runner.GetSnapshot().NextBase == BaseId.Second,
                    "Advance turns a returning runner around");
                runner.TryApplyDecision(RunnerDecision.Advance, out _);
                TickUntil(runner, ref time, dt, RunnerPhase.Holding);
                Require(Joined(touches) == "Second:Advancing,Third:Holding", "Rounds second without stopping");

                touches.Clear();
                runner.TryApplyDecision(RunnerDecision.Advance, out _);
                TickUntil(runner, ref time, dt, RunnerPhase.Scored);
                Vector3 scoredAt = runner.transform.position;
                Tick(runner, ref time, dt, 5);
                Require(Joined(touches) == "Home:Scored" && runner.transform.position == scoredAt, "Scores and stops");
                Require(!runner.TryApplyDecision(RunnerDecision.Advance, out _) &&
                    !runner.TryApplyDecision(RunnerDecision.Return, out _), "No decision after scoring");

                runner.ResetState();
                Require(runner.Phase == RunnerPhase.Inactive && !runner.TryApplyDecision(RunnerDecision.Advance, out _),
                    "Reset deactivates runner");
                report.AppendLine("PASS rules: auto first, default hold, round/cancel, midway return, turn around, score, reset.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static void VerifyDirector(StringBuilder report, PlayDirector director, FieldLayout field)
        {
            var runner = new SerializedObject(director).FindProperty("runner").objectReferenceValue as RunnerController;
            if (runner == null)
            {
                report.AppendLine("SKIP director: no runner reference. Run Tools > Baseball Simulation > Add Batter-Runner To Current Scene.");
                return;
            }
            var batter = UnityEngine.Object.FindFirstObjectByType<BatterController>();
            // The probe pitch can make contact and move the runner, so measure it before listening for bases.
            int swingTick = BattingEvaluationVerification.FindBestSwingTick(director);
            var touches = new List<BaseId>();
            Action<BaseId> onBase = touches.Add;
            director.RunnerBaseReached += onBase;
            BaseballEnvironmentConfig original = field.Config;
            BaseballEnvironmentConfig longPlay = UnityEngine.Object.Instantiate(original);
            try
            {
                // Fixed low power keeps the reference hit a fair ball inside the park, so the play stays live.
                var serialized = new SerializedObject(longPlay);
                serialized.FindProperty("swingPowerMultiplierRange").vector2Value = new Vector2(1.2f, 1.2f);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                field.AssignConfig(longPlay);
                batter.Initialize(longPlay, field.PitchTargetPosition);
                Reset(director);
                director.RequestRunnerDecision(RunnerDecision.Advance);
                Step(director);
                Require(!string.IsNullOrEmpty(director.GetSnapshot().LastRejectionReason) &&
                    director.GetRunnerSnapshot().Phase == RunnerPhase.Inactive, "Reject decision before contact");

                Hit(director, swingTick);
                Require(director.GetRunnerSnapshot().Phase == RunnerPhase.Advancing && !AnyVisible(batter) && AnyVisible(runner),
                    "Batter hands off to visible runner at contact");
                StepUntil(director, () => director.GetRunnerSnapshot().Phase == RunnerPhase.Holding);
                Require(director.State == PlayState.BattedBallInFlight && director.GetRunnerSnapshot().LastTouchedBase == BaseId.First,
                    "Play continues while runner holds at first");
                float firstTouch = director.GetRunnerSnapshot().LastTouchTime;
                director.RequestRunnerDecision(RunnerDecision.Advance);
                for (int i = 0; i < 60; i++) Step(director);
                director.RequestRunnerDecision(RunnerDecision.Return);
                StepUntil(director, () => director.GetRunnerSnapshot().Phase == RunnerPhase.Holding);
                Require(touches.Count == 2 && touches[0] == BaseId.First && touches[1] == BaseId.First, "Advance then return to first");
                StepUntil(director, () => director.State == PlayState.Ended);
                PitchSnapshot timedOut = director.GetSnapshot();
                Require(timedOut.EndReason == PitchEndReason.Timeout, "Holding runner ends at time limit");
                BattedBallSnapshot liveHit = director.GetBattedBallSnapshot();
                report.AppendLine($"director: first touch {firstTouch:F3}s, batted ball {liveHit.Call} {liveHit.Distance:F1} m, " +
                    $"end {timedOut.EndReason} at {timedOut.ElapsedSeconds:F2}s");

                Reset(director);
                Require(director.State == PlayState.Ready && director.GetRunnerSnapshot().Phase == RunnerPhase.Inactive &&
                    director.GetBattedBallSnapshot().ExitSpeed == 0f && AnyVisible(batter) && !AnyVisible(runner),
                    "Reset restores batter, runner and batted-ball record");

                serialized.FindProperty("battedBallTimeLimit").floatValue = 30f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                touches.Clear();
                Hit(director, swingTick);
                for (int i = 0; i < 3; i++)
                {
                    director.RequestRunnerDecision(RunnerDecision.Advance);
                    Step(director);
                }
                Require(director.GetRunnerSnapshot().TargetBase == BaseId.Home, "Three advances target home");
                StepUntil(director, () => director.State == PlayState.Ended);
                PitchSnapshot scored = director.GetSnapshot();
                Require(scored.EndReason == PitchEndReason.RunScored && string.Join(",", touches) == "First,Second,Third,Home",
                    "Round trip scores in order");
                report.AppendLine($"director: RunScored at {scored.ElapsedSeconds:F2}s (time limit 30s copy), " +
                    $"batted ball {director.GetBattedBallSnapshot().Call}");
                report.AppendLine("PASS director: hand-off, hold, advance/return, timeout, reset, scoring.");
            }
            finally
            {
                director.RunnerBaseReached -= onBase;
                field.AssignConfig(original);
                batter.Initialize(original, field.PitchTargetPosition);
                UnityEngine.Object.DestroyImmediate(longPlay);
            }
        }

        private static void Hit(PlayDirector director, int swingTick)
        {
            Reset(director);
            director.RequestThrowPitch();
            for (int tick = 0; tick < 200; tick++)
            {
                if (tick == swingTick) director.RequestSwing(new SwingCommand(0f, 15f));
                Step(director);
                if (director.State == PlayState.BattedBallInFlight) return;
                if (director.State == PlayState.Ended) break;
            }
            throw new Exception("Runner verification failed: reference swing did not make contact.");
        }

        private static void TickUntil(RunnerController runner, ref float time, float dt, RunnerPhase phase)
        {
            for (int i = 0; i < 5000 && runner.Phase != phase; i++) { runner.Tick(time, dt); time += dt; }
            Require(runner.Phase == phase, "Runner reached " + phase);
        }

        private static void Tick(RunnerController runner, ref float time, float dt, int count)
        {
            for (int i = 0; i < count; i++) { runner.Tick(time, dt); time += dt; }
        }

        private static void StepUntil(PlayDirector director, Func<bool> condition)
        {
            for (int i = 0; i < 3000 && !condition(); i++) Step(director);
            Require(condition(), "Director condition reached");
        }

        private static bool AnyVisible(Component root)
        {
            foreach (Renderer part in root.GetComponentsInChildren<Renderer>(false))
                if (part.enabled) return true;
            return false;
        }

        private static string Joined(List<string> values) => string.Join(",", values);
        private static void Reset(PlayDirector director) { director.RequestResetPlay(); Step(director); }
        private static void Step(PlayDirector director)
        {
            director.SendMessage("FixedUpdate");
            Physics.Simulate(Time.fixedDeltaTime);
        }
        private static void Require(bool condition, string label)
        {
            if (!condition) throw new Exception("Runner verification failed: " + label);
        }
    }
}
