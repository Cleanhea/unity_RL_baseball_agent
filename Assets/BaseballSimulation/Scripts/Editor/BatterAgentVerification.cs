using System;
using System.Text;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEditor;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    /// <summary>
    /// 학습 씬의 타자 Agent를 TrainingEnvController 흐름으로 확인한다. 어느 단계 씬에서든 쓸 수 있다.
    /// Academy 단계 → Director 고정 단계 → Physics.Simulate 순서로 직접 진행한다. 에피소드는 한 타석이다.
    /// </summary>
    public static class BatterAgentVerification
    {
        [MenuItem("Tools/Baseball Simulation/Verify Batter ML-Agent (Paused Play Mode)")]
        public static void RunMenu() => Debug.Log(Run());

        public static string Run()
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
                throw new InvalidOperationException("Enter Play Mode and pause first.");
            TrainingEnvController controller = FindPrimaryController();
            BatterAgent agent = controller != null ? controller.Batter : null;
            if (agent == null || agent.Director == null)
                throw new InvalidOperationException("Open a training scene (Tools > Baseball Simulation > Training) first.");
            PlayDirector director = agent.Director;
            BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
            RayPerceptionSensorComponent3D eye = agent.GetComponentInChildren<RayPerceptionSensorComponent3D>();
            Require(controller.enabled && agent.enabled, "controller and Agent passed their initialization checks");
            Require(agent.GetComponent<DecisionRequester>() == null, "decisions come from the controller, not a Decision Requester");
            Require(behavior.BrainParameters.VectorObservationSize == BatterAgent.ObservationSize,
                $"vector observation size {BatterAgent.ObservationSize}");
            Require(eye != null && eye.RaySensor != null, "ball eye ray sensor created");
            int rays = 2 * eye.RaysPerDirection + 1;
            Require(eye.RaySensor.GetObservationSpec().Shape[0] == rays * (eye.DetectableTags.Count + 2),
                "ray observation size");

            SimulationMode previousPhysics = Physics.simulationMode;
            BehaviorType previousBehavior = behavior.BehaviorType;
            Vector2 previousSpread = controller.ScriptedLocationSpread;
            controller.SetRandomSituationProbability(0f);
            var report = new StringBuilder();
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                DisableBenchmarks(controller);
                previousBehavior = behavior.BehaviorType;
                behavior.BehaviorType = BehaviorType.HeuristicOnly;
                // 1단계 스크립트 투수도 2·3단계 중립 투수처럼 존 중앙으로만 던지게 한다. 위치 분포는 TrainingStageVerification이 확인한다.
                controller.SetScriptedLocationSpread(Vector2.zero);
                FinishCurrentPlateAppearance(controller);

                // 1) 중립 휴리스틱: 스윙 없이 존 중앙 스트라이크 3개로 삼진. 한 타석이 한 에피소드이고 보상은 3 × 미접촉 + 삼진.
                int initialEpisodes = agent.CompletedEpisodes, initialPitches = agent.CompletedPitches;
                bool swung = false;
                int flightSteps = 0, seenSteps = 0;
                for (int i = 0; i < 2000 && agent.CompletedEpisodes < initialEpisodes + 1; i++)
                {
                    bool inFlight = director.State == PlayState.PitchInFlight;
                    int pitchesBefore = agent.CompletedPitches;
                    Step(director);
                    if (inFlight)
                    {
                        flightSteps++;
                        if (SeesBall(eye)) seenSteps++;
                    }
                    if (agent.CompletedPitches > pitchesBefore)
                        Require(Mathf.Abs(agent.LastPitchAddedReward - agent.LastPitchReward.Total) < 1e-4f, "each pitch adds exactly its tracker total");
                    swung |= director.HasSwung;
                }
                Require(agent.CompletedEpisodes == initialEpisodes + 1, "one neutral plate appearance completed");
                Require(!swung, "neutral heuristic never swings");
                Require(agent.CompletedPitches - initialPitches == 3 && controller.LastPlateAppearanceResult == PlateAppearanceResult.Strikeout,
                    $"three taken center strikes are a strikeout (pitches {agent.CompletedPitches - initialPitches}, result {controller.LastPlateAppearanceResult})");
                float expected = 3f * BatterRewardTracker.MissPenalty - PlayOutcomeRewards.StrikeoutValue;
                Require(Mathf.Abs(agent.LastCompletedCumulativeReward - expected) < 1e-4f, $"strikeout episode reward {expected}");
                Require(!director.ManualInputEnabled && !director.AutoRepeatEnabled, "controller owns input and repeat control");
                Require(seenSteps > 0, "ray eye saw the pitched ball");
                report.AppendLine($"PASS neutral plate appearance: 3 pitches, no swing, strikeout, episode reward {agent.LastCompletedCumulativeReward:F3}.");
                report.AppendLine($"PASS ball eye: {rays} rays x {eye.DetectableTags.Count + 2} values, stacks {eye.ObservationStacks}; " +
                    $"ball seen in {seenSteps}/{flightSteps} in-flight decisions.");

                // 2) 행동 경로: 자세를 존 중앙 높이에 맞추고 도착 시각에 맞춰 스윙 행동을 넣으면 맞고 인플레이로 타석이 끝난다.
                FieldLayout field = director.FieldLayout;
                BaseballEnvironmentConfig config = director.EnvironmentConfig;
                float centerPhase = config.SwingStartDegrees / (config.SwingStartDegrees - config.SwingEndDegrees);
                int before = agent.CompletedEpisodes;
                bool setupOverridden = false, injected = false, contact = false;
                var swing = new ActionBuffers(new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0.125f }, new[] { 1 });
                for (int i = 0; i < 2000 && agent.CompletedEpisodes < before + 1; i++)
                {
                    Academy.Instance.EnvironmentStep();
                    if (!setupOverridden && director.State == PlayState.Ready && agent.SetupSubmitted)
                    {
                        float drop = director.StrikeZoneCenter.y - field.PitchTargetPosition.y;
                        director.RequestBatterSetup(new BatterSetupCommand(Vector2.zero, new Vector3(0f, drop, 0f)));
                        setupOverridden = true;
                    }
                    if (!injected && director.State == PlayState.PitchInFlight && director.PitchArrivalSeconds > 0f &&
                        director.GetSnapshot().ElapsedSeconds >= director.PitchArrivalSeconds - centerPhase * config.SwingDuration)
                    {
                        agent.OnActionReceived(swing);
                        injected = true;
                    }
                    Advance(director);
                    contact |= director.HasContact;
                }
                Require(injected && contact && controller.LastPlateAppearanceResult == PlateAppearanceResult.InPlay,
                    "swing action through OnActionReceived put the first pitch in play");
                Require(agent.LastPitchReward.Contact > 0f &&
                    Mathf.Abs(agent.LastCompletedCumulativeReward - (agent.LastPlateAppearanceAddedReward + agent.LastOutcomeReward)) < 1e-4f,
                    "episode reward is the pitch reward plus the plate-appearance outcome");
                report.AppendLine($"PASS swing action: in play, pitch reward {agent.LastPlateAppearanceAddedReward:F3}, outcome {agent.LastOutcomeReward:F3}, " +
                    $"episode {agent.LastCompletedCumulativeReward:F3}.");
                return report.ToString();
            }
            finally
            {
                behavior.BehaviorType = previousBehavior;
                controller.SetScriptedLocationSpread(previousSpread);
                Physics.simulationMode = previousPhysics;
            }
        }

        /// <summary>진행 중인 타석이 있으면 끝날 때까지(또는 새 타석이 시작될 때까지) 진행한다.</summary>
        internal static void FinishCurrentPlateAppearance(TrainingEnvController controller)
        {
            PlayDirector director = controller.Director;
            for (int i = 0; i < 4000; i++)
            {
                if (director.State == PlayState.Ready && controller.PlayActive && !controller.SettingSituation &&
                    !controller.Batter.SetupSubmitted && director.GetSituation().Balls == 0 && director.GetSituation().Strikes == 0)
                    return;
                Step(director);
            }
            throw new Exception("Batter ML-Agent verification failed: could not reach a fresh plate appearance");
        }

        /// <summary>
        /// 모든 경기장의 고정 상대 평가 타석을 끄고, 진행 중인 평가 타석이 있으면 끝날 때까지 진행한다(물리는 Script 모드여야 한다).
        /// 기존 검증은 Agent끼리 대결하는 타석을 전제로 하므로 Behavior 종류를 바꾸기 전에 부른다.
        /// </summary>
        internal static void DisableBenchmarks(TrainingEnvController primary)
        {
            TrainingEnvController[] controllers = UnityEngine.Object.FindObjectsByType<TrainingEnvController>(FindObjectsSortMode.None);
            foreach (TrainingEnvController controller in controllers) controller.SetBenchmarkProbabilities(0f, 0f);
            for (int i = 0; i < 4000 && System.Array.Exists(controllers, c => c.Opponent != PlateAppearanceOpponent.Live); i++)
                Step(primary.Director);
            Require(!System.Array.Exists(controllers, c => c.Opponent != PlateAppearanceOpponent.Live),
                "benchmark plate appearances in progress finished before the checks");
        }

        internal static bool SeesBall(RayPerceptionSensorComponent3D eye)
        {
            RayPerceptionOutput.RayOutput[] outputs = eye.RaySensor.RayPerceptionOutput?.RayOutputs;
            if (outputs == null) return false;
            foreach (RayPerceptionOutput.RayOutput ray in outputs)
                if (ray.HasHit && ray.HitTagIndex == 0) return true;
            return false;
        }

        /// <summary>Academy 한 단계 후 Director 고정 단계와 물리 한 단계를 진행한다.</summary>
        internal static void Step(PlayDirector director)
        {
            Academy.Instance.EnvironmentStep();
            Advance(director);
        }

        private static PlayDirector[] directors = new PlayDirector[0];

        /// <summary>
        /// 모든 경기장의 Director 고정 단계를 한 번씩 진행하고 물리 한 단계를 진행한다. 경기장이 여러 개인 씬에서도
        /// 검증 대상이 아닌 경기장이 멈추지 않고 실제 학습처럼 함께 진행한다.
        /// </summary>
        internal static void Advance(PlayDirector director)
        {
            if (System.Array.IndexOf(directors, director) < 0 || System.Array.Exists(directors, d => d == null))
                directors = UnityEngine.Object.FindObjectsByType<PlayDirector>(FindObjectsSortMode.None);
            foreach (PlayDirector each in directors) each.SendMessage("FixedUpdate");
            Physics.Simulate(Time.fixedDeltaTime);
        }

        /// <summary>검증 대상 경기장(가장 작은 경기장 번호, 보통 0번)의 컨트롤러.</summary>
        internal static TrainingEnvController FindPrimaryController()
        {
            TrainingEnvController primary = null;
            foreach (TrainingEnvController controller in UnityEngine.Object.FindObjectsByType<TrainingEnvController>(FindObjectsSortMode.None))
                if (primary == null || controller.ArenaIndex < primary.ArenaIndex) primary = controller;
            return primary;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception("Batter ML-Agent verification failed: " + message);
        }
    }
}
