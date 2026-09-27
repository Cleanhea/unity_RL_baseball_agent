using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    /// <summary>Run against the real Rigidbody in a paused Play Mode session; never edits scene assets.</summary>
    public static class BattingEvaluationVerification
    {
        [MenuItem("Tools/Baseball Simulation/Verify Batting Evaluation (Paused Play Mode)")]
        public static void RunMenu() => Debug.Log(Run());

        public static string Run()
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
                throw new InvalidOperationException("Enter Play Mode and pause first.");
            var director = UnityEngine.Object.FindFirstObjectByType<PlayDirector>();
            var ball = UnityEngine.Object.FindFirstObjectByType<BallController>();
            var batter = UnityEngine.Object.FindFirstObjectByType<BatterController>();
            if (director == null || ball == null || batter == null) throw new InvalidOperationException("Missing actors.");
            var report = new StringBuilder();
            SimulationMode previousMode = Physics.simulationMode;
            bool manual = director.ManualInputEnabled;
            int evaluations = 0;
            Action<BattingEvaluation> onEvaluation = _ => evaluations++;
            director.BattingEvaluated += onEvaluation;
            try
            {
                director.SetManualInputEnabled(false);
                Physics.simulationMode = SimulationMode.Script;
                // Pitch timing depends on speed and air drag, so derive the reference swing tick instead of fixing it.
                int best = FindBestSwingTick(director);
                evaluations = 0;
                BattingEvaluation normal = RunPitch(director, default, best, new SwingCommand(0f, 15f));
                Require(normal.HasContact, "Central pitch contact");
                Require(normal.Scores.x == 1f && normal.Scores.y == 1f && normal.Scores.w > 0.99f,
                    "Independent reference scores");
                Require(normal.HasTimingReference && normal.Scores.z > 0.5f, "Timing reference");
                Add(report, "reference", normal);

                var stance = RunPitch(director, new BatterSetupCommand(new Vector2(-0.6f, 0f), Vector3.zero),
                    best, new SwingCommand(0f, 15f));
                Require(!stance.HasContact && stance.Scores.x == 0f && stance.Scores.y == 1f,
                    "Body translation affects reach and only stance diagnostic");
                Require(Mathf.Abs(stance.GripPosition.x - normal.GripPosition.x + 0.6f) < 0.001f, "Grip follows body");
                Add(report, "stance -0.6m", stance);

                var grip = RunPitch(director, new BatterSetupCommand(Vector2.zero, new Vector3(0f, 0.35f, 0f)),
                    best, new SwingCommand(0f, 15f));
                Require(!grip.HasContact && grip.Scores.x == 1f && grip.Scores.y == 0f, "Grip height changes contact");
                Add(report, "grip +0.35m", grip);

                var early = RunPitch(director, default, best - 8, new SwingCommand(0f, 15f));
                var late = RunPitch(director, default, best + 8, new SwingCommand(0f, 15f));
                Require(!early.HasContact && early.TimingErrorSeconds < -0.1f && early.Scores.z == 0f, "Early swing");
                Require(!late.HasContact && late.TimingErrorSeconds > 0.1f && late.Scores.z == 0f, "Late swing");
                Add(report, "early", early); Add(report, "late", late);

                var angle = RunPitch(director, default, best, new SwingCommand(40f, -20f));
                Require(angle.SwingAngleErrorDegrees > 40f && angle.Scores.w < normal.Scores.w,
                    "Swing plane angle is evaluated");
                Require(Vector3.Distance(angle.BatTipPosition, normal.BatTipPosition) > 0.1f, "Angle changes actual bat geometry");
                Add(report, "different angle", angle);

                var noSwing = RunPitch(director, default, -1, default);
                Require(!noSwing.HasSwung && !noSwing.HasTimingReference && !noSwing.HasClosestDistance &&
                    noSwing.Scores.z == 0f && noSwing.Scores.w == 0f, "No swing is not a perfect timing/angle result");
                Require(evaluations == 7, "Exactly one evaluation per hit or miss");

                for (int i = 0; i < 10; i++)
                {
                    var repeated = RunPitch(director, default, best, new SwingCommand(0f, 15f));
                    Require(repeated.HasContact && Mathf.Abs(repeated.TimingErrorSeconds - normal.TimingErrorSeconds) < 0.00001f,
                        "Repeatable hit and timing");
                }
                Reset(director);
                director.RequestBatterSetup(new BatterSetupCommand(new Vector2(float.NaN, 0f), Vector3.zero));
                Step(director);
                Require(!string.IsNullOrEmpty(director.GetSnapshot().LastRejectionReason) &&
                    director.GetBattingEvaluation().Setup.StanceOffset == Vector2.zero, "Reject NaN without changing pose");
                director.RequestBatterSetup(new BatterSetupCommand(new Vector2(20f, 0f), Vector3.zero));
                Step(director);
                Require(director.GetBattingEvaluation().Setup.StanceOffset == Vector2.zero, "Reject out of bounds");
                director.RequestThrowPitch(); Step(director);
                director.RequestBatterSetup(new BatterSetupCommand(new Vector2(0.1f, 0f), Vector3.zero)); Step(director);
                Require(director.GetBattingEvaluation().Setup.StanceOffset == Vector2.zero, "No teleport during pitch");
                director.RequestResetPlay();
                director.RequestSwing(new SwingCommand(0f, 15f));
                director.RequestBatterSetup(new BatterSetupCommand(new Vector2(0.1f, 0f), Vector3.zero));
                Step(director);
                Require(director.State == PlayState.Ready && !director.HasSwung && !director.HasContact &&
                    ball.Velocity.sqrMagnitude < 0.0001f && director.GetBattingEvaluation().Setup.StanceOffset == Vector2.zero,
                    "Reset cancels queued actions and restores body/bat/ball/evaluation");
                report.AppendLine($"PASS: 7 independent scenarios (best swing tick {best}), 10 repeat hits, input guards and full reset.");
                VerifyGeometry(report);
                return report.ToString();
            }
            finally
            {
                director.BattingEvaluated -= onEvaluation;
                Reset(director);
                Physics.simulationMode = previousMode;
                director.SetManualInputEnabled(manual);
            }
        }

        /// <summary>Probe swing at tick 18, then shift by the measured timing error. Needs Script physics mode.</summary>
        internal static int FindBestSwingTick(PlayDirector director)
        {
            const int probe = 18;
            BattingEvaluation result = RunPitch(director, default, probe, new SwingCommand(0f, 15f));
            if (!result.HasTimingReference) throw new Exception("Batting verification failed: probe swing has no timing reference");
            return probe - Mathf.RoundToInt(result.TimingErrorSeconds / Time.fixedDeltaTime);
        }

        private static BattingEvaluation RunPitch(PlayDirector director, BatterSetupCommand setup,
            int swingTick, SwingCommand swing)
        {
            Reset(director);
            director.RequestBatterSetup(setup);
            director.RequestThrowPitch();
            for (int tick = 0; tick < 1000; tick++)
            {
                if (tick == swingTick) director.RequestSwing(swing);
                Step(director);
                if (director.State == PlayState.Ended) return director.GetBattingEvaluation();
            }
            throw new Exception("Pitch did not end.");
        }

        private static void VerifyGeometry(StringBuilder report)
        {
            var source = UnityEngine.Object.FindFirstObjectByType<FieldLayout>().Config;
            var config = UnityEngine.Object.Instantiate(source);
            var go = new GameObject("TemporaryBattingGeometryCheck");
            try
            {
                var batter = go.AddComponent<BatterController>();
                var serialized = new SerializedObject(config);
                foreach (float arc in new[] { 45f, 80f, 110f })
                {
                    serialized.FindProperty("swingStartDegrees").floatValue = arc;
                    serialized.FindProperty("swingEndDegrees").floatValue = -arc;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    batter.Initialize(config, config.PitchTargetPosition);
                    batter.SetPitchReference(0.5f);
                    batter.BeginSwing(new SwingCommand(0f, 15f), 0.375f);
                    Require(batter.GetEvaluation().Scores.z > 0.999f, "Symmetric arc timing");
                    Vector3 velocity;
                    Require(batter.Tick(0.49f, 0.51f, config.PitchTargetPosition + Vector3.forward * 0.36f,
                        config.PitchTargetPosition - Vector3.forward * 0.36f, out velocity), "Swept contact across arc widths");
                    Require(!batter.Tick(0.51f, 0.53f, config.PitchTargetPosition, config.PitchTargetPosition,
                        out velocity), "One contact per swing");
                }
                serialized.FindProperty("swingStartDegrees").floatValue = 120f;
                serialized.FindProperty("swingEndDegrees").floatValue = -60f;
                serialized.FindProperty("referenceSwingAngles").vector2Value = new Vector2(20f, 10f);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                batter.Initialize(config, config.PitchTargetPosition);
                batter.SetPitchReference(0.5f);
                float centerAge = config.SwingDuration * 2f / 3f;
                batter.BeginSwing(new SwingCommand(20f, 10f), 0.5f - centerAge);
                Require(batter.GetEvaluation().Scores.z > 0.999f, "Asymmetric zero-angle timing");
                Require(Vector3.Distance(batter.GripPosition + batter.GetBatDirection(centerAge) *
                    config.BatLength * config.BatSweetSpotFraction, config.PitchTargetPosition) < 0.0001f,
                    "Reference angles keep sweet spot at target");
                serialized.FindProperty("swingStartDegrees").floatValue = -20f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Require(!config.TryValidate(out _), "Reject range that does not cross reference pose");
                report.AppendLine("PASS: 45/80/110 arcs, asymmetric timing, reference angle geometry, single contact.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(config);
            }
        }

        private static void Reset(PlayDirector director) { director.RequestResetPlay(); Step(director); }
        private static void Step(PlayDirector director)
        {
            director.SendMessage("FixedUpdate");
            Physics.Simulate(Time.fixedDeltaTime);
        }
        private static void Require(bool condition, string label)
        {
            if (!condition) throw new Exception("Batting verification failed: " + label);
        }
        private static void Add(StringBuilder report, string label, BattingEvaluation value)
        {
            report.AppendLine($"{label}: hit={value.HasContact}, stance={value.StanceErrorMetres:F3}m, " +
                $"grip={value.GripErrorMetres:F3}m, timing={value.TimingErrorSeconds:F4}s, " +
                $"angle={value.SwingAngleErrorDegrees:F2}deg, scores={value.Scores}, exit={value.ExitVelocity}");
        }
    }
}
