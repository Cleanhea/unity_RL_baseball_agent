using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    /// <summary>
    /// Strike-zone geometry and ball/strike calls on the real Rigidbody in a paused Play Mode session.
    /// Random-location checks use a config copy; assets are never edited.
    /// </summary>
    public static class PitchCallVerification
    {
        [MenuItem("Tools/Baseball Simulation/Verify Strike And Ball Calls (Paused Play Mode)")]
        public static void RunMenu() => Debug.Log(Run());

        public static string Run()
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
                throw new InvalidOperationException("Enter Play Mode and pause first.");
            var director = UnityEngine.Object.FindFirstObjectByType<PlayDirector>();
            var field = UnityEngine.Object.FindFirstObjectByType<FieldLayout>();
            var ball = UnityEngine.Object.FindFirstObjectByType<BallController>();
            if (director == null || field == null || ball == null) throw new InvalidOperationException("Missing director, field or ball.");
            var report = new StringBuilder();
            SimulationMode previousMode = Physics.simulationMode;
            bool manual = director.ManualInputEnabled;
            try
            {
                director.SetManualInputEnabled(false);
                Physics.simulationMode = SimulationMode.Script;
                VerifyGeometry(report, field.Config);
                VerifyCalls(report, director, ball, field.Config);
                VerifyRandomLocations(report, director, field);
                return report.ToString();
            }
            finally
            {
                Reset(director);
                Physics.simulationMode = previousMode;
                director.SetManualInputEnabled(manual);
            }
        }

        private static void VerifyGeometry(StringBuilder report, BaseballEnvironmentConfig config)
        {
            float r = config.BallRadius, bottom = config.StrikeZoneBottom, top = config.StrikeZoneTop;
            float side = StrikeZone.PlateWidth * 0.5f, middle = 0.5f * (bottom + top);
            bool In(float x, float depth, float height) => StrikeZone.Intersects(new Vector3(x, depth, height), r, bottom, top);
            Require(In(0f, 0.2f, middle), "Centre of zone");
            Require(In(side + r - 0.001f, 0.3f, middle) && !In(side + r + 0.001f, 0.3f, middle), "Ball grazing the plate edge");
            Require(In(0f, 0.3f, top + r - 0.001f) && !In(0f, 0.3f, top + r + 0.001f), "Ball grazing the top");
            Require(In(0f, 0.3f, bottom - r + 0.001f) && !In(0f, 0.3f, bottom - r - 0.001f), "Ball grazing the bottom");
            Require(!In(side, 0.05f, middle), "Pentagon back corners are cut (square plate would call this a strike)");
            Require(In(0f, -r + 0.001f, middle) && !In(0f, -r - 0.001f, middle), "Ball touching the back tip");
            report.AppendLine("PASS geometry: zone centre, edge/top/bottom grazes by ball radius, pentagon back corners, back tip.");
        }

        private static void VerifyCalls(StringBuilder report, PlayDirector director, BallController ball, BaseballEnvironmentConfig config)
        {
            int best = BattingEvaluationVerification.FindBestSwingTick(director);
            int events = 0;
            Action<PitchCall> onCall = _ => events++;
            director.PitchCalled += onCall;
            try
            {
                float middle = 0.5f * (config.StrikeZoneBottom + config.StrikeZoneTop);
                float side = StrikeZone.PlateWidth * 0.5f;
                PitchCallSnapshot centre = Pitch(director, new Vector2(0f, middle), -1);
                Require(centre.Call == PitchCall.CalledStrike && centre.InZone && centre.HasPlateLocation &&
                    Vector2.Distance(centre.PlateLocation, centre.AimLocation) < 0.05f, "Taken pitch at zone centre is a called strike");
                Expect(report, director, "graze outside edge", new Vector2(side + 0.03f, middle), -1, PitchCall.CalledStrike);
                Expect(report, director, "just off the edge", new Vector2(side + 0.05f, middle), -1, PitchCall.Ball);
                Expect(report, director, "well outside", new Vector2(0.4f, middle), -1, PitchCall.Ball);
                Expect(report, director, "high", new Vector2(0f, config.StrikeZoneTop + 0.10f), -1, PitchCall.Ball);
                Expect(report, director, "low", new Vector2(0f, config.StrikeZoneBottom - 0.10f), -1, PitchCall.Ball);
                PitchCallSnapshot dirt = Expect(report, director, "in the dirt", new Vector2(0f, 0f), -1, PitchCall.Ball);
                Require(ball.HasTouchedGround, "Dirt pitch touched the ground");
                PitchCallSnapshot whiff = Expect(report, director, "swing at a ball outside", new Vector2(0.6f, middle), best,
                    PitchCall.SwingingStrike);
                Require(whiff.SwingOffered && !director.HasContact, "Whiff counts as an offered swing without contact");
                PitchCallSnapshot late = Expect(report, director, "swing after the ball passed", new Vector2(0.6f, middle), best + 10,
                    PitchCall.Ball);
                Require(director.HasSwung && !late.SwingOffered, "Late swing is not an attempt");
                PitchCallSnapshot machine = Pitch(director, null, -1);
                Require(machine.Call == PitchCall.CalledStrike, "Default machine pitch (PitchTarget) is inside the rule zone");
                PitchCallSnapshot hit = Pitch(director, null, best);
                BattedBallCall batted = director.GetBattedBallSnapshot().Call;
                Require(director.HasContact && hit.Call == (batted == BattedBallCall.Foul ? PitchCall.Foul : PitchCall.InPlay),
                    "Contact is called Foul or InPlay from the batted-ball judgement");
                Require(events == 11, $"One call event per pitch (got {events} for 11 pitches)");
                Reset(director);
                Require(director.GetPitchCall().Call == PitchCall.None && !director.GetPitchCall().HasPlateLocation, "Reset clears the call");
                report.AppendLine($"calls: centre plate location ({centre.PlateLocation.x:F3}, {centre.PlateLocation.y:F3}) m, " +
                    $"machine pitch {machine.Call} at height {machine.PlateLocation.y:F3} m, hit {hit.Call} ({batted}), dirt pitch touched ground");
                report.AppendLine("PASS calls: called strike/ball edges, dirt, swinging strike, late swing, contact, one event per pitch, reset.");
            }
            finally
            {
                director.PitchCalled -= onCall;
            }
        }

        private static void VerifyRandomLocations(StringBuilder report, PlayDirector director, FieldLayout field)
        {
            BaseballEnvironmentConfig original = field.Config;
            BaseballEnvironmentConfig copy = UnityEngine.Object.Instantiate(original);
            try
            {
                var serialized = new SerializedObject(copy);
                serialized.FindProperty("pitchLocationMode").enumValueIndex = (int)PitchLocationMode.RandomAroundZone;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                field.AssignConfig(copy);
                director.RequestResetPlay(777);
                Step(director);
                int pitches = 300, strikes = 0;
                Vector2 first = default;
                for (int i = 0; i < pitches; i++)
                {
                    PitchCallSnapshot call = Pitch(director, null, -1);
                    if (i == 0) first = call.AimLocation;
                    if (call.Call == PitchCall.CalledStrike) strikes++;
                    Require(call.Call == PitchCall.CalledStrike || call.Call == PitchCall.Ball, "Taken pitches are strikes or balls");
                }
                float rate = strikes / (float)pitches;
                Require(rate > 0.35f && rate < 0.58f, $"Zone rate near MLB level (got {rate:P1})");
                director.RequestResetPlay(777);
                Step(director);
                Vector2 again = Pitch(director, null, -1).AimLocation;
                Require(again == first, "Same seed reproduces the first random location");
                report.AppendLine($"random: {pitches} taken pitches, called strike rate {rate:P1}, seed 777 first aim " +
                    $"({first.x:F3}, {first.y:F3}) reproduced");
                report.AppendLine("PASS random locations: zone rate and seeded reproducibility.");
            }
            finally
            {
                field.AssignConfig(original);
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        private static PitchCallSnapshot Expect(StringBuilder report, PlayDirector director, string label, Vector2 location,
            int swingTick, PitchCall expected)
        {
            PitchCallSnapshot call = Pitch(director, location, swingTick);
            Require(call.Call == expected, $"{label}: expected {expected}, got {call.Call}");
            report.AppendLine($"call: {label} aim ({location.x:F3}, {location.y:F3}) -> plate " +
                $"({call.PlateLocation.x:F3}, {call.PlateLocation.y:F3}) {call.Call}, zone {call.InZone}");
            return call;
        }

        private static PitchCallSnapshot Pitch(PlayDirector director, Vector2? location, int swingTick)
        {
            Reset(director);
            if (location.HasValue) director.RequestThrowPitch(location.Value);
            else director.RequestThrowPitch();
            for (int tick = 0; tick < 1500 && director.State != PlayState.Ended; tick++)
            {
                if (tick == swingTick) director.RequestSwing(new SwingCommand(0f, 15f));
                Step(director);
            }
            Require(director.State == PlayState.Ended, "Pitch ends");
            return director.GetPitchCall();
        }

        private static void Reset(PlayDirector director) { director.RequestResetPlay(); Step(director); }
        private static void Step(PlayDirector director)
        {
            director.SendMessage("FixedUpdate");
            Physics.Simulate(Time.fixedDeltaTime);
        }
        private static void Require(bool condition, string label)
        {
            if (!condition) throw new Exception("Pitch call verification failed: " + label);
        }
    }
}
