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
    /// 고정 상대 평가 타석(docs/training-curriculum.md "고정 상대 평가")을 일시정지한 Play Mode에서 확인한다. 2·3단계 씬용이다.
    /// 1) 기준 스크립트 투수의 구종 비율·범위·존 비율 2) 기준 투수 타석: 투수 Agent 무결정·무보상, 지표 분리
    /// 3) 고정 타자 타석: 타자 정책의 모델 추론 전환·복원, 모델 스윙, 지표 분리.
    /// </summary>
    public static class BenchmarkVerification
    {
        private const int Samples = 20000;

        [MenuItem("Tools/Baseball Simulation/Training/Verify Benchmark Plate Appearances (Paused Play Mode)", false, 42)]
        public static void RunMenu() => Debug.Log(Run());

        public static string Run()
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
                throw new InvalidOperationException("Enter Play Mode and pause first.");
            TrainingEnvController controller = BatterAgentVerification.FindPrimaryController();
            if (controller == null || controller.Stage < TrainingStage.BatterPitcher)
                throw new InvalidOperationException("Open a stage 2 or 3 training scene first.");
            TrainingEnvController[] controllers = UnityEngine.Object.FindObjectsByType<TrainingEnvController>(FindObjectsSortMode.None);
            var probabilities = controllers.Select(c => (c.ScriptedPitcherBenchmarkProbability, c.FrozenBatterBenchmarkProbability)).ToArray();
            controller.SetRandomSituationProbability(0f);
            var report = new StringBuilder();
            report.AppendLine(VerifyDistribution(controller));

            var behaviors = new List<BehaviorParameters>();
            var previous = new List<BehaviorType>();
            SimulationMode previousPhysics = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                BatterAgentVerification.DisableBenchmarks(controller);
                foreach (Agent agent in UnityEngine.Object.FindObjectsByType<Agent>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    behaviors.Add(agent.GetComponent<BehaviorParameters>());
                previous.AddRange(behaviors.Select(b => b.BehaviorType));
                foreach (BehaviorParameters behavior in behaviors) behavior.BehaviorType = BehaviorType.HeuristicOnly;
                report.AppendLine(VerifyScriptedPitcher(controller));
                report.AppendLine(VerifyFrozenBatter(controller));
                return report.ToString();
            }
            finally
            {
                for (int i = 0; i < behaviors.Count; i++) behaviors[i].BehaviorType = previous[i];
                for (int i = 0; i < controllers.Length; i++) controllers[i].SetBenchmarkProbabilities(probabilities[i].Item1, probabilities[i].Item2);
                Physics.simulationMode = previousPhysics;
            }
        }

        /// <summary>기준 스크립트 투수 표본: 구종 비율 ±1.5%p, 구속은 구종 범위 안, 위치는 플레이트·존 ± 여유 안, 존 통과 비율 42~58%.</summary>
        private static string VerifyDistribution(TrainingEnvController controller)
        {
            BaseballEnvironmentConfig config = controller.Director.EnvironmentConfig;
            Vector2 center = controller.Director.StrikeZoneCenter;
            Vector2 half = BenchmarkPitcher.LocationHalfRange(config);
            float halfWidth = half.x, halfHeight = half.y;
            Require(Mathf.Abs(BenchmarkPitcher.Mix.Sum(m => m.weight) - 1f) < 1e-4f, "pitch mix weights sum to 1");
            var random = new System.Random(12345);
            var counts = new Dictionary<PitchType, int>();
            int inZone = 0;
            for (int i = 0; i < Samples; i++)
            {
                PitchCommand pitch = BenchmarkPitcher.Next(random, config, center);
                counts[pitch.Type] = counts.TryGetValue(pitch.Type, out int count) ? count + 1 : 1;
                PitchTypeProfile profile = config.GetPitchProfile(pitch.Type);
                Require(pitch.Speed >= profile.MinSpeed - 1e-4f && pitch.Speed <= profile.MaxSpeed + 1e-4f, $"{pitch.Type} speed inside its profile range");
                Require(Mathf.Abs(pitch.PlateLocation.x) <= halfWidth + 1e-5f && Mathf.Abs(pitch.PlateLocation.y - center.y) <= halfHeight + 1e-5f,
                    "location inside the benchmark location range");
                if (StrikeZone.Intersects(new Vector3(pitch.PlateLocation.x, StrikeZone.PlateDepth, pitch.PlateLocation.y),
                        config.BallRadius, config.StrikeZoneBottom, config.StrikeZoneTop))
                    inZone++;
            }
            var mix = new StringBuilder();
            foreach (var (type, weight) in BenchmarkPitcher.Mix)
            {
                float share = counts.TryGetValue(type, out int count) ? count / (float)Samples : 0f;
                Require(Mathf.Abs(share - weight) < 0.015f, $"{type} share {share:F3} within 1.5 points of {weight:F2}");
                mix.Append($"{type} {share:P1}; ");
            }
            float zoneRate = inZone / (float)Samples;
            Require(zoneRate > 0.42f && zoneRate < 0.58f, $"zone rate {zoneRate:P1} between 42% and 58%");
            return $"PASS benchmark pitcher distribution ({Samples} samples): {mix}zone rate {zoneRate:P1}.";
        }

        /// <summary>다음 타석을 기준 스크립트 투수 타석으로 만들고 끝까지 진행한다(타자는 중립 휴리스틱으로 스윙하지 않는다).</summary>
        private static string VerifyScriptedPitcher(TrainingEnvController controller)
        {
            PlayDirector director = controller.Director;
            PitcherAgent pitcher = controller.Pitcher;
            BatterAgent batter = controller.Batter;
            TrainingStats stats = controller.Stats;
            const string group = TrainingStats.BenchmarkBatterGroup;
            controller.SetBenchmarkProbabilities(1f, 0f);
            StepUntil(controller, () => controller.Opponent == PlateAppearanceOpponent.ScriptedPitcher, "a scripted-pitcher plate appearance began");
            controller.SetBenchmarkProbabilities(0f, 0f);

            int pitcherEpisodes = pitcher.CompletedEpisodes, pitcherPitches = pitcher.CompletedPitches, batterEpisodes = batter.CompletedEpisodes;
            int livePitches = stats.CountOf("Pitch Type/Four Seam"), livePlateAppearances = stats.CountOf("Plate Appearance/Pitches");
            int matchups = stats.CountOf("Matchup/Draw"), zoneCount = stats.CountOf(group + "/Zone Rate");
            int benchmarkPlateAppearances = stats.CountOf(group + "/Strikeout");
            var thrown = new List<PitchCommand>();
            void OnCalled(PitchCall call)
            {
                if (director.TryGetLastPitch(out PitchCommand pitch)) thrown.Add(pitch);
            }
            // 평가 타석이 끝난 같은 단계에서 다음 타석이 시작되므로, 투수 상태는 평가 타석이 진행되는 동안에만 본다.
            bool pitcherActive = false, pitcherRewarded = false;
            director.PitchCalled += OnCalled;
            try
            {
                StepUntil(controller, () =>
                {
                    if (controller.Opponent != PlateAppearanceOpponent.ScriptedPitcher) return true;
                    pitcherActive |= pitcher.PlateAppearanceOpen || pitcher.PitchActive;
                    pitcherRewarded |= Mathf.Abs(pitcher.GetCumulativeReward()) > 1e-6f;
                    return false;
                }, "the scripted-pitcher plate appearance ended");
            }
            finally
            {
                director.PitchCalled -= OnCalled;
            }

            Require(controller.LastPlateAppearanceOpponent == PlateAppearanceOpponent.ScriptedPitcher, "the finished plate appearance was a benchmark one");
            Require(controller.AbortedPlays == 0, "no aborted plays");
            Require(thrown.Count >= 3, $"at least three benchmark pitches thrown (got {thrown.Count})");
            Require(!pitcherActive && pitcher.CompletedPitches == pitcherPitches && pitcher.CompletedEpisodes == pitcherEpisodes,
                "the pitcher agent neither pitched nor opened an episode during the benchmark plate appearance");
            Require(!pitcherRewarded, "no reward leaked to the idle pitcher agent");
            Require(batter.CompletedEpisodes == batterEpisodes + 1, "the batter's plate appearance was one episode");
            Require(controller.LastPlateAppearanceResult != PlateAppearanceResult.InPlay, "a batter that never swings strikes out or walks");
            Require(stats.CountOf(group + "/Strikeout") == benchmarkPlateAppearances + 1 && stats.CountOf(group + "/Zone Rate") == zoneCount + thrown.Count,
                "benchmark stats recorded once per pitch and plate appearance");
            Require(stats.CountOf("Pitch Type/Four Seam") == livePitches && stats.CountOf("Plate Appearance/Pitches") == livePlateAppearances &&
                stats.CountOf("Matchup/Draw") == matchups, "live-matchup stats untouched");
            string pitches = string.Join(", ", thrown.Select(p => $"{p.Type} {p.Speed * 3.6f:F0}km/h ({p.PlateLocation.x:+0.00;-0.00}, {p.PlateLocation.y:F2})"));
            return $"PASS scripted-pitcher plate appearance: {controller.LastPlateAppearanceResult} in {thrown.Count} pitches [{pitches}], " +
                "pitcher agent idle with zero reward, stats only under Benchmark Batter.";
        }

        /// <summary>다음 타석을 고정 타자 타석으로 만들고 끝까지 진행한다. 중립 휴리스틱은 스윙하지 않으므로 스윙이 있으면 모델이 친 것이다.</summary>
        private static string VerifyFrozenBatter(TrainingEnvController controller)
        {
            PlayDirector director = controller.Director;
            PitcherAgent pitcher = controller.Pitcher;
            BatterAgent batter = controller.Batter;
            BehaviorParameters behavior = batter.GetComponent<BehaviorParameters>();
            TrainingStats stats = controller.Stats;
            const string group = TrainingStats.BenchmarkPitcherGroup;
            Require(controller.BenchmarkBatterModel != null, "the benchmark batter model is assigned");
            BehaviorType typeBefore = behavior.BehaviorType;
            var modelBefore = behavior.Model;
            controller.SetBenchmarkProbabilities(0f, 1f);
            StepUntil(controller, () => controller.Opponent == PlateAppearanceOpponent.FrozenBatter, "a frozen-batter plate appearance began");
            controller.SetBenchmarkProbabilities(0f, 0f);
            Require(behavior.BehaviorType == BehaviorType.InferenceOnly && behavior.Model == controller.BenchmarkBatterModel,
                "the batter switched to inference with the benchmark model");

            int pitcherEpisodes = pitcher.CompletedEpisodes, batterEpisodes = batter.CompletedEpisodes;
            int livePitches = stats.CountOf("Pitch Type/Four Seam"), matchups = stats.CountOf("Matchup/Draw") + stats.CountOf("Matchup/Batter Win");
            int benchmarkPlateAppearances = stats.CountOf(group + "/Strikeout");
            int swings = 0;
            bool wasSwung = false;
            StepUntil(controller, () =>
            {
                if (director.HasSwung && !wasSwung) swings++;
                wasSwung = director.HasSwung;
                return controller.Opponent != PlateAppearanceOpponent.FrozenBatter;
            }, "the frozen-batter plate appearance ended");

            Require(controller.LastPlateAppearanceOpponent == PlateAppearanceOpponent.FrozenBatter, "the finished plate appearance was a benchmark one");
            Require(controller.AbortedPlays == 0, "no aborted plays");
            Require(swings > 0, "the benchmark model swung (the neutral heuristic never swings)");
            Require(behavior.BehaviorType == typeBefore && behavior.Model == modelBefore, "the batter's behavior type and model were restored");
            Require(pitcher.CompletedEpisodes == pitcherEpisodes + 1, "the pitcher agent pitched the whole plate appearance as one episode");
            Require(batter.CompletedEpisodes == batterEpisodes + 1, "the inference batter closed its plate appearance");
            Require(stats.CountOf(group + "/Strikeout") == benchmarkPlateAppearances + 1, "benchmark plate appearance recorded under Benchmark Pitcher");
            Require(stats.CountOf("Pitch Type/Four Seam") == livePitches &&
                stats.CountOf("Matchup/Draw") + stats.CountOf("Matchup/Batter Win") == matchups, "live-matchup stats untouched");
            stats.TryGetLast(group + "/Batter Outcome", out float outcome);
            return $"PASS frozen-batter plate appearance: {controller.LastPlateAppearanceResult}, {swings} model swing(s), batter outcome {outcome:+0.00;-0.00}, " +
                $"pitcher episode closed, batter restored to {behavior.BehaviorType}, stats only under Benchmark Pitcher.";
        }

        private static void StepUntil(TrainingEnvController controller, Func<bool> done, string label)
        {
            for (int i = 0; i < 6000; i++)
            {
                if (done()) return;
                BatterAgentVerification.Step(controller.Director);
            }
            throw new Exception("Benchmark verification failed: timed out waiting until " + label);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception("Benchmark verification failed: " + message);
        }
    }
}
