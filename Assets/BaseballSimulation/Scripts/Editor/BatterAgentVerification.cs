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
            CameraSensorComponent eye = agent.GetComponentInChildren<CameraSensorComponent>();
            Require(controller.enabled && agent.enabled, "controller and Agent passed their initialization checks");
            Require(agent.GetComponent<DecisionRequester>() == null, "decisions come from the controller, not a Decision Requester");
            Require(behavior.BrainParameters.VectorObservationSize == BatterAgent.ObservationSize,
                $"vector observation size {BatterAgent.ObservationSize}");
            Require(eye != null && eye.Camera != null, "catcher camera sensor assigned");
            Require(agent.GetComponentInChildren<RayPerceptionSensorComponent3D>() == null, "legacy rays removed");
            Require(eye.Width == BatterAgent.ImageWidth && eye.Height == BatterAgent.ImageHeight &&
                eye.Grayscale && eye.ObservationStacks == BatterAgent.ImageStacks, "visual observation contract");
            Require(Mathf.Abs(Time.fixedDeltaTime - BatterAgent.DecisionIntervalSeconds) < 1e-6f,
                "camera and batter decisions use a 10 ms fixed step");
            Require(!eye.Camera.transform.IsChildOf(agent.transform), "catcher view is independent of batter stance");
            Require(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null,
                "visual verification requires graphics (omit -nographics)");
            Vector3 cameraPosition = eye.Camera.transform.position;
            Quaternion cameraRotation = eye.Camera.transform.rotation;

            SimulationMode previousPhysics = Physics.simulationMode;
            BehaviorType previousBehavior = behavior.BehaviorType;
            Vector2 previousSpread = controller.ScriptedLocationSpread;
            controller.SetRandomSituationProbability(0f);
            var report = new StringBuilder();
            report.AppendLine(VerifyBallOnlyCamera(agent));
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
                        if (SeesBall(eye, director)) seenSteps++;
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
                Require(seenSteps > 0, "camera rendered the pitched ball");
                report.AppendLine($"PASS neutral plate appearance: 3 pitches, no swing, strikeout, episode reward {agent.LastCompletedCumulativeReward:F3}.");
                report.AppendLine($"PASS catcher eye: {eye.Width}x{eye.Height} grayscale, stacks {eye.ObservationStacks}; " +
                    $"ball seen in {seenSteps}/{flightSteps} in-flight decisions.");

                // 2) 행동 경로: 자세를 존 중앙 높이에 맞추고 도착 시각에 맞춰 스윙 행동을 넣으면 맞고 인플레이로 타석이 끝난다.
                FieldLayout field = director.FieldLayout;
                BaseballEnvironmentConfig config = director.EnvironmentConfig;
                float centerPhase = config.SwingStartDegrees / (config.SwingStartDegrees - config.SwingEndDegrees);
                int before = agent.CompletedEpisodes;
                int abortedBefore = controller.AbortedPlays;
                bool setupOverridden = false, injected = false, contact = false;
                var swing = new ActionBuffers(new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0.125f }, new[] { 1 });
                for (int i = 0; i < 2000 && agent.CompletedEpisodes < before + 1 && controller.AbortedPlays == abortedBefore; i++)
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
                bool interrupted = controller.AbortedPlays == abortedBefore + 1;
                Require(injected && contact && (interrupted
                    ? controller.Stage == TrainingStage.FullTeam && controller.LastEndReason == PitchEndReason.Timeout &&
                        !controller.LastPlaySummary.Resolved && controller.LastBatterOutcomeReward == 0f && agent.CompletedEpisodes == before
                    : controller.LastPlateAppearanceResult == PlateAppearanceResult.InPlay && agent.CompletedEpisodes == before + 1),
                    "physical swing completes an in-play PA or interrupts an unresolved fielding timeout without an outcome");
                Require(agent.LastPitchReward.Contact > 0f && (interrupted ||
                    Mathf.Abs(agent.LastCompletedCumulativeReward - (agent.LastPlateAppearanceAddedReward + agent.LastOutcomeReward)) < 1e-4f),
                    "actual contact reward is retained; completed PA total includes its outcome once");
                Require(Vector3.Distance(cameraPosition, eye.Camera.transform.position) < 1e-5f &&
                    Quaternion.Angle(cameraRotation, eye.Camera.transform.rotation) < 1e-4f,
                    "catcher camera stayed fixed through stance, swing and resets");
                report.AppendLine(interrupted ? "PASS swing action: physical contact; unresolved fielding timeout interrupted, no completed PA or outcome reward."
                    : $"PASS swing action: in play, pitch reward {agent.LastPlateAppearanceAddedReward:F3}, outcome {agent.LastOutcomeReward:F3}, episode {agent.LastCompletedCumulativeReward:F3}.");
                return report.ToString();
            }
            finally
            {
                behavior.BehaviorType = previousBehavior;
                controller.SetScriptedLocationSpread(previousSpread);
                Physics.simulationMode = previousPhysics;
            }
        }

        /// <summary>실제 센서 렌더에서 공 외 픽셀·가림을 검사한다. 검증 후 공과 일반 화면을 복원한다.</summary>
        public static string VerifyBallOnlyCamera(BatterAgent agent, string outputDirectory = null)
        {
            CameraSensorComponent eye = agent.GetComponentInChildren<CameraSensorComponent>();
            Camera camera = eye.Camera;
            BallController ball = agent.Director.transform.root.GetComponentInChildren<BallController>();
            Renderer[] renderers = ball.GetComponentsInChildren<Renderer>();
            int layer = LayerMask.NameToLayer(BatterAgent.SensorBallLayerName);
            Require(layer >= 8 && camera.cullingMask == (1 << layer), "sensor includes only the ball layer");
            Require(camera.clearFlags == CameraClearFlags.SolidColor && camera.backgroundColor == Color.black &&
                !camera.useOcclusionCulling, "black background without baked occlusion");
            var data = camera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            Require(data != null && !data.renderPostProcessing && !data.renderShadows, "sensor has no post processing or scene shadows");
            foreach (Renderer renderer in agent.Director.transform.root.GetComponentsInChildren<Renderer>(true))
                Require(renderer.gameObject.layer != layer || renderer.GetComponentInParent<BallController>() == ball,
                    "only this arena's ball uses the sensor layer");
            Camera main = Camera.main;
            Require(main != null && main != camera && (main.cullingMask & (1 << layer)) != 0 &&
                (main.cullingMask & 1) != 0, "main camera still renders the ball and field");
            for (int i = 0; i < 32; i++)
                Require(Physics.GetIgnoreLayerCollision(layer, i) == Physics.GetIgnoreLayerCollision(0, i),
                    "ball layer preserves the previous collision matrix");

            Vector3 previousPosition = ball.transform.position;
            Rigidbody body = ball.GetComponent<Rigidbody>();
            Vector3 previousBodyPosition = body.position;
            bool[] previousEnabled = new bool[renderers.Length];
            for (int i = 0; i < renderers.Length; i++) previousEnabled[i] = renderers[i].enabled;
            var frame = new Texture2D(eye.Width, eye.Height, TextureFormat.RGB24, false);
            GameObject blocker = null;
            try
            {
                Vector3 point = camera.transform.position + camera.transform.forward * 3f;
                body.position = point;
                ball.transform.position = point;
                CameraSensor.ObservationToTexture(camera, frame, eye.Width, eye.Height);
                Color32[] onlyBall = frame.GetPixels32();
                int ballPixels = 0;
                foreach (Color32 pixel in onlyBall) if (pixel.r + pixel.g + pixel.b > 3) ballPixels++;
                Require(ballPixels > 0 && ballPixels < eye.Width * eye.Height / 100, "physical ball visible without a rendered background");
                if (outputDirectory != null)
                {
                    System.IO.Directory.CreateDirectory(outputDirectory);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(outputDirectory, "ball-only.png"), frame.EncodeToPNG());
                }

                blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                blocker.name = "BallOnlyCameraVerificationBlocker";
                UnityEngine.Object.DestroyImmediate(blocker.GetComponent<Collider>());
                blocker.transform.position = camera.transform.position + camera.transform.forward * 2f;
                blocker.transform.localScale = Vector3.one * 0.5f;
                CameraSensor.ObservationToTexture(camera, frame, eye.Width, eye.Height);
                Color32[] blocked = frame.GetPixels32();
                for (int i = 0; i < onlyBall.Length; i++)
                    Require(onlyBall[i].Equals(blocked[i]), "excluded geometry cannot appear or hide the ball");
                for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = false;
                CameraSensor.ObservationToTexture(camera, frame, eye.Width, eye.Height);
                foreach (Color32 pixel in frame.GetPixels32())
                    Require(pixel.r + pixel.g + pixel.b <= 3, "no ball produces an entirely black sensor image");
                if (outputDirectory != null)
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(outputDirectory, "no-ball.png"), frame.EncodeToPNG());
                return $"PASS ball-only sensor: {ballPixels} ball pixels; blank background; foreground excluded; main camera and collision matrix retained.";
            }
            finally
            {
                body.position = previousBodyPosition;
                ball.transform.position = previousPosition;
                for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = previousEnabled[i];
                if (blocker != null) UnityEngine.Object.DestroyImmediate(blocker);
                UnityEngine.Object.DestroyImmediate(frame);
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

        internal static bool SeesBall(CameraSensorComponent eye, PlayDirector director)
        {
            // 실제 센서와 같은 렌더 경로로 공 유무를 비교한다. 프러스텀 안이어도 몸에 가리면 실패한다.
            BallController ball = director.transform.root.GetComponentInChildren<BallController>();
            if (ball == null) throw new Exception("Ball not found in this arena");
            Vector3 viewport = eye.Camera.WorldToViewportPoint(ball.transform.position);
            if (viewport.z <= eye.Camera.nearClipPlane || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                return false;
            Renderer[] renderers = ball.GetComponentsInChildren<Renderer>();
            bool[] enabled = new bool[renderers.Length];
            for (int i = 0; i < renderers.Length; i++) enabled[i] = renderers[i].enabled;
            var frame = new Texture2D(eye.Width, eye.Height, TextureFormat.RGB24, false);
            try
            {
                CameraSensor.ObservationToTexture(eye.Camera, frame, eye.Width, eye.Height);
                Color32[] withBall = frame.GetPixels32();
                for (int i = 0; i < renderers.Length; i++)
                {
                    renderers[i].enabled = false;
                }
                CameraSensor.ObservationToTexture(eye.Camera, frame, eye.Width, eye.Height);
                Color32[] withoutBall = frame.GetPixels32();
                // 공의 예상 화면 위치 근처만 비교해 지면 그림자를 공 픽셀로 오인하지 않는다.
                float radius = director.EnvironmentConfig.BallRadius;
                int pixelRadius = Mathf.Max(2, Mathf.CeilToInt(radius * eye.Height /
                    (viewport.z * Mathf.Tan(0.5f * eye.Camera.fieldOfView * Mathf.Deg2Rad))) + 1);
                int centerX = Mathf.RoundToInt(viewport.x * (eye.Width - 1));
                int centerY = Mathf.RoundToInt(viewport.y * (eye.Height - 1));
                for (int y = Mathf.Max(0, centerY - pixelRadius); y <= Mathf.Min(eye.Height - 1, centerY + pixelRadius); y++)
                for (int x = Mathf.Max(0, centerX - pixelRadius); x <= Mathf.Min(eye.Width - 1, centerX + pixelRadius); x++)
                {
                    int i = y * eye.Width + x;
                    int delta = Math.Abs(withBall[i].r - withoutBall[i].r) + Math.Abs(withBall[i].g - withoutBall[i].g) +
                        Math.Abs(withBall[i].b - withoutBall[i].b);
                    if (delta > 24) return true;
                }
                return false;
            }
            finally
            {
                for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = enabled[i];
                UnityEngine.Object.DestroyImmediate(frame);
            }
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
