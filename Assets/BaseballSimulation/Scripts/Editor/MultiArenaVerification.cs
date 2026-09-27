using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    /// <summary>
    /// 한 씬에 복제한 경기장들이 서로 간섭하지 않고 같은 규칙으로 병렬 진행되는지 일시정지한 Play Mode에서 확인한다.
    /// 참조가 자기 경기장 안에 있는지, 동시에 진행해도 중단·공 이탈이 없는지, 난수 시드가 경기장마다 다른지,
    /// 같은 상황이면 수비·주자 관측이 경기장과 무관하게 같은지(홈 기준 좌표)를 본다.
    /// </summary>
    public static class MultiArenaVerification
    {
        [MenuItem("Tools/Baseball Simulation/Training/Verify Multiple Arenas (Paused Play Mode)", false, 41)]
        public static void RunMenu() => Debug.Log(Run());

        public static string Run()
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
                throw new InvalidOperationException("Enter Play Mode and pause first.");
            TrainingEnvController[] arenas = UnityEngine.Object.FindObjectsByType<TrainingEnvController>(FindObjectsSortMode.None)
                .OrderBy(c => c.ArenaIndex).ToArray();
            Require(arenas.Length >= 2, $"at least two arenas in the scene (found {arenas.Length})");
            for (int i = 0; i < arenas.Length; i++) Require(arenas[i].ArenaIndex == i, "arena indices are 0..N-1 without gaps");
            var report = new StringBuilder();

            // 1) 참조가 자기 경기장 루트 안에만 있다(복제 시 참조 재연결 확인).
            foreach (TrainingEnvController arena in arenas)
            {
                Transform root = arena.transform.root;
                var owned = new List<Component> { arena.Director, arena.Batter, arena.Director.FieldLayout };
                if (arena.Pitcher != null) owned.Add(arena.Pitcher);
                owned.AddRange(arena.Runners);
                owned.AddRange(arena.Fielders);
                for (int i = 0; i < arena.Director.FielderCount; i++) owned.Add(arena.Director.GetFielder(i));
                foreach (Component component in owned)
                    Require(component != null && component.transform.IsChildOf(root), $"arena {arena.ArenaIndex}: {component?.name} belongs to its own arena");
            }
            float minGap = float.MaxValue;
            for (int i = 0; i < arenas.Length; i++)
                for (int j = i + 1; j < arenas.Length; j++)
                    minGap = Mathf.Min(minGap, Flat(arenas[i].Director.FieldLayout.HomePosition - arenas[j].Director.FieldLayout.HomePosition).magnitude);
            Require(minGap >= 300f, $"arenas are at least 300 m apart (closest {minGap:F0} m)");
            report.AppendLine($"PASS layout: {arenas.Length} arenas, references stay inside their own arena, closest arenas {minGap:F0} m apart.");
            VerifyPitchSolverAndDistance(arenas, report);

            var behaviors = new List<BehaviorParameters>();
            var previous = new List<BehaviorType>();
            SimulationMode previousPhysics = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                // 평가 타석(고정 타자 모델)이 진행 중이면 타자 정책이 바뀌어 있으므로, 끝낸 뒤에 원래 Behavior 종류를 저장한다.
                BatterAgentVerification.DisableBenchmarks(arenas[0]);
                foreach (Agent agent in UnityEngine.Object.FindObjectsByType<Agent>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    behaviors.Add(agent.GetComponent<BehaviorParameters>());
                previous.AddRange(behaviors.Select(b => b.BehaviorType));
                foreach (BehaviorParameters behavior in behaviors) behavior.BehaviorType = BehaviorType.HeuristicOnly;

                // 2) 모든 경기장을 함께 진행: 중단 없음, 공은 자기 경기장 안, 경기장마다 플레이가 이어진다.
                int[] startPlays = arenas.Select(a => a.CompletedPlays).ToArray();
                int[] startAborted = arenas.Select(a => a.AbortedPlays).ToArray();
                var firstSpeeds = new float[arenas.Length];
                float maxBallDistance = 0f;
                float maxPlateMiss = 0f;
                int measuredCrossings = 0;
                for (int step = 0; step < 800; step++)
                {
                    StepAll(arenas);
                    for (int i = 0; i < arenas.Length; i++)
                    {
                        PlayDirector director = arenas[i].Director;
                        maxBallDistance = Mathf.Max(maxBallDistance, Flat(director.GetSnapshot().BallPosition - director.FieldLayout.HomePosition).magnitude);
                        PitchCallSnapshot call = director.GetPitchCall();
                        if (call.HasPlateLocation)
                        {
                            maxPlateMiss = Mathf.Max(maxPlateMiss, Vector2.Distance(call.PlateLocation, call.AimLocation));
                            measuredCrossings++;
                        }
                        if (firstSpeeds[i] == 0f && director.TryGetLastPitch(out PitchCommand pitch)) firstSpeeds[i] = pitch.Speed;
                    }
                }
                for (int i = 0; i < arenas.Length; i++)
                {
                    Require(arenas[i].CompletedPlays - startPlays[i] >= 2, $"arena {i} kept playing ({arenas[i].CompletedPlays - startPlays[i]} pitches)");
                    Require(arenas[i].AbortedPlays == startAborted[i], $"arena {i} had no aborted plays");
                }
                Require(maxBallDistance < TrainingSceneBuilder.ArenaSpacing * 0.5f, $"every ball stayed in its own arena (max {maxBallDistance:F1} m from its home)");
                Require(measuredCrossings > 0 && maxPlateMiss < 0.03f,
                    $"actual pitches crossed within 3 cm of aim in every arena (max {maxPlateMiss:F3} m)");
                string speeds = string.Join(", ", firstSpeeds.Select(s => (s * 3.6f).ToString("F1")));
                if (arenas[0].Stage == TrainingStage.Batter)
                    Require(firstSpeeds.Distinct().Count() == arenas.Length, $"scripted pitch speeds differ per arena ({speeds} km/h)");
                report.AppendLine($"PASS parallel run: 800 steps, {string.Join("/", arenas.Select((a, i) => a.CompletedPlays - startPlays[i]))} pitches per arena, " +
                    $"0 aborted, max plate miss {maxPlateMiss * 100f:F2} cm, " +
                    $"max ball distance {maxBallDistance:F1} m from own home, first pitch speeds {speeds} km/h.");

                // 3) 3단계: 같은 상황이면 경기장과 무관하게 수비 시작 위치와 수비·주자 관측이 같다(홈 기준 좌표).
                if (arenas[0].Stage == TrainingStage.FullTeam)
                {
                    foreach (TrainingEnvController arena in arenas) arena.SetRandomSituationProbability(0f);
                    foreach (TrainingEnvController arena in arenas)
                    {
                        arena.Director.RequestNewPlateAppearance();
                        arena.Director.RequestResetPlay();
                    }
                    AdvanceAll(arenas);
                    float spotError = 0f;
                    for (int i = 1; i < arenas.Length; i++)
                        for (int f = 0; f < arenas[0].Director.FielderCount; f++)
                            spotError = Mathf.Max(spotError, Vector3.Distance(Relative(arenas[i], arenas[i].Director.GetFielder(f).Position),
                                Relative(arenas[0], arenas[0].Director.GetFielder(f).Position)));
                    Require(spotError < 1e-3f, $"fielders reset to the same spots relative to home in every arena (max {spotError:F4} m)");

                    Vector3 exit = BatterController.ComputeExitVelocity(-13.8f, -6f, 28f);
                    foreach (TrainingEnvController arena in arenas)
                    {
                        arena.Director.RequestSetSituation(true, false, false, 0);
                        arena.Director.RequestScriptedBattedBall(exit);
                    }
                    AdvanceAll(arenas);
                    foreach (TrainingEnvController arena in arenas)
                        Require(arena.Director.State == PlayState.BattedBallInFlight, $"arena {arena.ArenaIndex} started the same batted ball");
                    for (int step = 0; step < 11; step++) StepAll(arenas);
                    float obsError = 0f;
                    int compared = 0;
                    for (int i = 1; i < arenas.Length; i++)
                    {
                        for (int f = 0; f < arenas[0].Fielders.Length; f++, compared++)
                            obsError = Mathf.Max(obsError, MaxDifference(arenas[0].Fielders[f].GetObservations(), arenas[i].Fielders[f].GetObservations()));
                        for (int r = 0; r < arenas[0].Runners.Length; r++)
                        {
                            if (!arenas[0].Runners[r].IsRunning) continue;
                            obsError = Mathf.Max(obsError, MaxDifference(arenas[0].Runners[r].GetObservations(), arenas[i].Runners[r].GetObservations()));
                            compared++;
                        }
                    }
                    Require(obsError < 1e-3f, $"fielder and runner observations match across arenas (max difference {obsError:F5})");
                    report.AppendLine($"PASS relative coordinates: fielder reset spots match (max {spotError * 1000f:F2} mm), " +
                        $"{compared} fielder/runner observation vectors match across arenas during the same play (max difference {obsError:F6}).");
                }
                return report.ToString();
            }
            finally
            {
                for (int i = 0; i < behaviors.Count; i++) behaviors[i].BehaviorType = previous[i];
                Physics.simulationMode = previousPhysics;
            }
        }

        private static void StepAll(TrainingEnvController[] arenas)
        {
            Academy.Instance.EnvironmentStep();
            AdvanceAll(arenas);
        }

        private static void VerifyPitchSolverAndDistance(TrainingEnvController[] arenas, StringBuilder report)
        {
            int solved = 0;
            foreach (TrainingEnvController arena in arenas)
            {
                PlayDirector director = arena.Director;
                FieldLayout field = director.FieldLayout;
                BaseballEnvironmentConfig config = director.EnvironmentConfig;
                BallController ball = director.transform.root.GetComponentInChildren<BallController>(true);
                Require(ball != null, $"arena {arena.ArenaIndex} has a ball");
                Vector3 origin = field.PitchOriginPosition;
                Vector3 machineTarget = field.PitchTargetPosition;
                Vector3 machinePlate = StrikeZone.ToPlate(field, machineTarget);
                Vector3 toPlate = Flat(field.PitcherPlatePosition - field.HomePosition).normalized;
                Vector3 plateRight = Vector3.Cross(Vector3.up, toPlate);
                Vector2 aim = director.StrikeZoneCenter;
                Vector3 target = machineTarget + plateRight * (aim.x - machinePlate.x) + Vector3.up * (aim.y - machinePlate.z);
                foreach (PitchType type in Enum.GetValues(typeof(PitchType)))
                {
                    PitchTypeProfile profile = config.GetPitchProfile(type);
                    // Previously some speeds failed only in arenas 2 and 3 because world-coordinate integration
                    // rounded every physics step. Include the entire scripted range and all five pitch types.
                    for (int tenthKmh = 1100; tenthKmh <= 1600; tenthKmh++)
                    {
                        float kmh = tenthKmh * 0.1f;
                        bool ok = PitchPhysics.TrySolve(origin, target, kmh / 3.6f, profile, config,
                            ball.AngularDamping, Time.fixedDeltaTime, out _, out _, out _, out string reason);
                        Require(ok, $"arena {arena.ArenaIndex}: {type} {kmh:F1} km/h solves ({reason})");
                        solved++;
                    }
                }

                Vector3 firstTouch = field.HomePosition + new Vector3(30f, 0f, 40f);
                var hit = new BattedBallSnapshot(BattedBallCall.Fair, Vector3.forward, 0f, 0f,
                    true, firstTouch, 1f, field.HomePosition);
                Require(hit.FirstTouchPoint == firstTouch && Mathf.Abs(hit.Distance - 50f) < 0.001f,
                    $"arena {arena.ArenaIndex}: first touch stays in world coordinates and distance starts at home");
            }
            report.AppendLine($"PASS arena coordinates: {solved} pitch solutions across five types and 110-160 km/h, " +
                "first-touch distances use each arena's home.");
        }

        private static void AdvanceAll(TrainingEnvController[] arenas)
        {
            foreach (TrainingEnvController arena in arenas) arena.Director.SendMessage("FixedUpdate");
            Physics.Simulate(Time.fixedDeltaTime);
        }

        private static Vector3 Relative(TrainingEnvController arena, Vector3 world) => world - arena.Director.FieldLayout.HomePosition;

        private static float MaxDifference(IReadOnlyList<float> a, IReadOnlyList<float> b)
        {
            Require(a.Count == b.Count && a.Count > 0, "observation vectors were collected");
            float max = 0f;
            for (int i = 0; i < a.Count; i++) max = Mathf.Max(max, Mathf.Abs(a[i] - b[i]));
            return max;
        }

        private static Vector3 Flat(Vector3 value) => new Vector3(value.x, 0f, value.z);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception("Multi-arena verification failed: " + message);
        }
    }
}
