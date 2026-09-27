using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    /// <summary>
    /// Pitch solver, air drag/lift carry, fair/foul/home-run calls and swing power options, run on the real
    /// Rigidbody in a paused Play Mode session. Config copies are used for option checks; assets are never edited.
    /// </summary>
    public static class BattedBallVerification
    {
        private const float Mph = 0.44704f;
        private const float Feet = 0.3048f;

        [MenuItem("Tools/Baseball Simulation/Verify Batted Ball Physics (Paused Play Mode)")]
        public static void RunMenu() => Debug.Log(Run());

        public static string Run()
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
                throw new InvalidOperationException("Enter Play Mode and pause first.");
            var director = UnityEngine.Object.FindFirstObjectByType<PlayDirector>();
            var field = UnityEngine.Object.FindFirstObjectByType<FieldLayout>();
            var ball = UnityEngine.Object.FindFirstObjectByType<BallController>();
            var batter = UnityEngine.Object.FindFirstObjectByType<BatterController>();
            if (director == null || field == null || ball == null || batter == null)
                throw new InvalidOperationException("Missing director, field, ball or batter.");
            var report = new StringBuilder();
            SimulationMode previousMode = Physics.simulationMode;
            bool manual = director.ManualInputEnabled;
            try
            {
                director.SetManualInputEnabled(false);
                Physics.simulationMode = SimulationMode.Script;
                VerifyPitch(report, director, ball);
                VerifyCarry(report, director, ball, field);
                VerifyCalls(report, director, ball, field);
                VerifyDirectorHitAndPower(report, director, field, batter);
                return report.ToString();
            }
            finally
            {
                Reset(director);
                Physics.simulationMode = previousMode;
                director.SetManualInputEnabled(manual);
            }
        }

        private static void VerifyPitch(StringBuilder report, PlayDirector director, BallController ball)
        {
            Reset(director);
            director.RequestThrowPitch();
            float plateSpeed = 0f, arrival = 0f;
            for (int tick = 0; tick < 500 && director.State != PlayState.Ended; tick++)
            {
                bool before = director.GetSnapshot().HasCenterPassError;
                Step(director);
                PitchSnapshot snapshot = director.GetSnapshot();
                if (!before && snapshot.HasCenterPassError) { plateSpeed = ball.Velocity.magnitude; arrival = snapshot.ElapsedSeconds; }
            }
            PitchSnapshot end = director.GetSnapshot();
            Require(end.HasCenterPassError && end.CenterPassError < 0.02f, $"Pitch passes zone centre (error {end.CenterPassError:F4} m)");
            Require(plateSpeed < 35f && plateSpeed > 28f, $"Drag slows pitch (plate speed {plateSpeed:F1} m/s)");
            report.AppendLine($"pitch: centre error {end.CenterPassError * 1000f:F1} mm, plate speed {plateSpeed:F1} m/s " +
                $"(release 36), plane reached by tick at {arrival:F2}s");
        }

        private static void VerifyCarry(StringBuilder report, PlayDirector director, BallController ball, FieldLayout field)
        {
            // Launched from well behind home toward centre so every carry lands inside the fence ring.
            var cases = new[] { (90f, 28f, 330f), (100f, 28f, 385f), (110f, 28f, 435f), (100f, 15f, 290f), (100f, 35f, 380f) };
            foreach (var (mph, launch, referenceFeet) in cases)
            {
                Vector3 start = field.HomePosition + new Vector3(0f, 1f, -95f);
                FlyAndJudge(director, ball, field, start, Direction(0f, launch) * (mph * Mph), 20f, stopAtFirstTouch: true, out _);
                float carryFeet = Vector3.Distance(Flat(start), Flat(ball.FirstTouchPoint)) / Feet;
                Require(ball.HasFirstTouch && Mathf.Abs(carryFeet - referenceFeet) <= 25f,
                    $"Carry {mph} mph @ {launch} deg = {carryFeet:F0} ft (reference ~{referenceFeet} ft)");
                report.AppendLine($"carry: {mph} mph @ {launch} deg -> {carryFeet:F0} ft (reference ~{referenceFeet} ft)");
            }
        }

        private static void VerifyCalls(StringBuilder report, PlayDirector director, BallController ball, FieldLayout field)
        {
            bool fence = field.transform.Find("OutfieldFence") != null;
            Vector3 plate = field.HomePosition + new Vector3(0f, 1f, 0.5f);
            Expect(report, director, ball, field, "home run to centre", plate, Direction(0f, 28f) * 48f, BattedBallCall.HomeRun);
            Expect(report, director, ball, field, "home-run distance, foul side", plate, Direction(60f, 28f) * 48f, BattedBallCall.Foul);
            Expect(report, director, ball, field, "fair fly lands past bases", plate, Direction(10f, 30f) * 35f, BattedBallCall.Fair);
            Expect(report, director, ball, field, "foul fly lands past first", plate, Direction(55f, 30f) * 35f, BattedBallCall.Foul);
            Expect(report, director, ball, field, "bunt settles fair", field.HomePosition + new Vector3(0f, 0.1f, 1f),
                Direction(10f, 0f) * 6f, BattedBallCall.Fair);
            Expect(report, director, ball, field, "roller settles foul before first", field.HomePosition + new Vector3(1f, 0.1f, 1.2f),
                Direction(60f, 0f) * 10f, BattedBallCall.Foul);
            Expect(report, director, ball, field, "roller passes first in fair", field.HomePosition + new Vector3(0.5f, 0.1f, 1f),
                Direction(44f, 0f) * 25f, BattedBallCall.Fair);
            if (!fence)
            {
                report.AppendLine("SKIP wall/ground-rule double: no OutfieldFence. Run Tools > Baseball Simulation > Add Outfield Fence To Current Scene.");
                return;
            }
            // Low line drive reaches the wall on the fly, rebounds and stays a live fair ball.
            Expect(report, director, ball, field, "line drive off the wall", plate, Direction(0f, 18f) * 48f, BattedBallCall.Fair,
                extraSeconds: 3f);
            Require(ball.FirstTouchWasWall, "Line drive touched the wall first");
            // Spinless ball landing hard on the warning track bounces over the wall (restitution 0.4).
            Expect(report, director, ball, field, "bounce over the wall", field.HomePosition + new Vector3(0f, 0.5f, 97f),
                new Vector3(0f, -22f, 20f), BattedBallCall.GroundRuleDouble, extraSeconds: 2f, rpmOverride: 0f);
        }

        private static void Expect(StringBuilder report, PlayDirector director, BallController ball, FieldLayout field,
            string label, Vector3 start, Vector3 velocity, BattedBallCall expected, float extraSeconds = 0f, float? rpmOverride = null)
        {
            BattedBallJudge judge = FlyAndJudge(director, ball, field, start, velocity, 20f, false, out float decidedAt,
                extraSeconds, rpmOverride);
            Require(judge.Call == expected, $"{label}: expected {expected}, got {judge.Call}");
            report.AppendLine($"call: {label} -> {judge.Call} at {decidedAt:F2}s" +
                (judge.HasFirstTouch ? $", first touch {Flat(judge.FirstTouchPoint - field.HomePosition).magnitude:F1} m" : string.Empty));
        }

        private static BattedBallJudge FlyAndJudge(PlayDirector director, BallController ball, FieldLayout field, Vector3 start,
            Vector3 velocity, float seconds, bool stopAtFirstTouch, out float decidedAt, float extraSeconds = 0f,
            float? rpmOverride = null)
        {
            Reset(director);
            BaseballEnvironmentConfig config = field.Config;
            // Same launch-angle spin rule as the Director uses at bat contact, unless a case needs a constructed state.
            float launch = Mathf.Asin(Mathf.Clamp(velocity.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            float rpm = rpmOverride ?? Mathf.Clamp(launch * config.BattedBackspinRpmPerDegree,
                -config.MaxBattedSpinRpm, config.MaxBattedSpinRpm);
            ball.ResetTo(start, Quaternion.identity);
            ball.Launch(velocity, BallController.BackspinVector(velocity, rpm));
            var judge = new BattedBallJudge(field);
            judge.Begin(start, 0f);
            float dt = Time.fixedDeltaTime, time = 0f, stopAt = seconds;
            decidedAt = -1f;
            while (time < stopAt)
            {
                ball.ApplyFlightForces();
                Physics.Simulate(dt);
                time += dt;
                judge.Step(ball, time, dt);
                if (stopAtFirstTouch && ball.HasFirstTouch) break;
                if (judge.Call != BattedBallCall.None && (decidedAt < 0f || judge.IsFinal))
                {
                    decidedAt = time;
                    if (judge.IsFinal) break;
                    stopAt = Mathf.Min(seconds, time + extraSeconds);
                }
            }
            return judge;
        }

        private static void VerifyDirectorHitAndPower(StringBuilder report, PlayDirector director, FieldLayout field,
            BatterController batter)
        {
            int tick = BattingEvaluationVerification.FindBestSwingTick(director);
            BaseballEnvironmentConfig original = field.Config;
            BaseballEnvironmentConfig copy = UnityEngine.Object.Instantiate(original);
            int calls = 0;
            Action<BattedBallCall> onCall = _ => calls++;
            director.BattedBallCalled += onCall;
            try
            {
                var serialized = new SerializedObject(copy);
                serialized.FindProperty("swingPowerMultiplierRange").vector2Value = new Vector2(1.5f, 1.5f);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                field.AssignConfig(copy);
                batter.Initialize(copy, field.PitchTargetPosition);
                PitchEndReason firstEnd = Hit(director, tick, out BattedBallSnapshot first);
                PitchEndReason secondEnd = Hit(director, tick, out BattedBallSnapshot second);
                Require(first.ExitSpeed > 0f && first.ExitVelocity == second.ExitVelocity && batter.SwingPowerMultiplier == 1.5f,
                    "Fixed power gives identical exit velocity");
                Require(first.Call != BattedBallCall.None && calls >= 2, "Director publishes a batted-ball call");
                Require(Mathf.Abs(first.LaunchAngleDegrees - 15f) < 6f, "Reference swing plane gives ~15 deg launch");
                report.AppendLine($"director: best swing tick {tick}, fixed x1.5 -> {first.ExitSpeed:F1} m/s @ " +
                    $"{first.LaunchAngleDegrees:F1} deg, spray {first.SprayAngleDegrees:F1} deg, {first.BackspinRpm:F0} rpm, " +
                    $"call {first.Call}, distance {first.Distance:F1} m, end {firstEnd}/{secondEnd}");

                // Same seed and swing order reproduce the random multipliers.
                serialized.FindProperty("swingPowerMultiplierRange").vector2Value = new Vector2(1.2f, 2f);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                batter.Initialize(copy, field.PitchTargetPosition);
                Hit(director, tick, out BattedBallSnapshot seededA);
                float multiplierA = batter.SwingPowerMultiplier;
                batter.Initialize(copy, field.PitchTargetPosition);
                Hit(director, tick, out BattedBallSnapshot seededB);
                Require(seededA.ExitVelocity == seededB.ExitVelocity && multiplierA >= 1.2f && multiplierA <= 2f,
                    "Seeded power draw is reproducible");
                report.AppendLine($"power: seed {copy.RandomSeed} first multiplier {multiplierA:F3} reproduced");
                report.AppendLine("PASS batted ball: pitch solver, carry, calls, director hit, fixed/seeded power.");
            }
            finally
            {
                director.BattedBallCalled -= onCall;
                field.AssignConfig(original);
                batter.Initialize(original, field.PitchTargetPosition);
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        private static PitchEndReason Hit(PlayDirector director, int swingTick, out BattedBallSnapshot hit)
        {
            Reset(director);
            director.RequestThrowPitch();
            for (int tick = 0; tick < 1500 && director.State != PlayState.Ended; tick++)
            {
                if (tick == swingTick) director.RequestSwing(new SwingCommand(0f, 15f));
                Step(director);
            }
            hit = director.GetBattedBallSnapshot();
            Require(director.State == PlayState.Ended && director.HasContact, "Reference swing hits and the play ends");
            return director.GetSnapshot().EndReason;
        }

        private static Vector3 Direction(float sprayDegrees, float launchDegrees) =>
            Quaternion.Euler(-launchDegrees, sprayDegrees, 0f) * Vector3.forward;

        private static Vector3 Flat(Vector3 value) => new Vector3(value.x, 0f, value.z);
        private static void Reset(PlayDirector director) { director.RequestResetPlay(); Step(director); }
        private static void Step(PlayDirector director)
        {
            director.SendMessage("FixedUpdate");
            Physics.Simulate(Time.fixedDeltaTime);
        }
        private static void Require(bool condition, string label)
        {
            if (!condition) throw new Exception("Batted ball verification failed: " + label);
        }
    }
}
