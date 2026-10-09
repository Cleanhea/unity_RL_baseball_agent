using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BaseballSimulation
{
    /// <summary>
    /// 한 타석을 한 에피소드로 제어하는 타자 Agent. 공은 포수 시점 카메라로만 보고,
    /// CollectObservations의 16값 벡터에는 타자 자신의 상태와 볼카운트·아웃·주자 상황만 넣는다.
    /// 투구마다 <see cref="BatterRewardTracker"/>의 보상을 더하고, 타석이 끝나면 <see cref="TrainingEnvController"/>가
    /// 결과 보상(<see cref="PlayOutcomeRewards.BatterPlateAppearance"/>)과 함께 에피소드를 닫는다.
    /// 관측·행동 계약은 모든 학습 단계에서 같아 이전 단계 모델을 이어 쓸 수 있다.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BehaviorParameters))]
    public sealed class BatterAgent : Agent
    {
        public const int ObservationSize = 16;
        public const int ContinuousActionCount = 7;
        public const int DiscreteActionCount = 1;
        public const string BallTag = "Ball";
        public const int ImageWidth = 192;
        public const int ImageHeight = 192;
        public const int ImageStacks = 12;
        public const float DecisionIntervalSeconds = 0.01f;
        public const string SensorBallLayerName = "BatterSensorBall";
        public const float TrainingContactBonus = 1.5f;
        public const float TrainingEarliestSwingSeconds = 0.30f;
        public const float TrainingHitQualityFloor = 0.6f;
        public const float TrainingHitSpeedFloorKmh = 120f;
        public const float TrainingHitLaunchMinimum = 5f;
        public const float TrainingHitLaunchMaximum = 35f;

        [SerializeField] private PlayDirector director;

        private BatterRewardTracker rewards;
        private bool plateAppearanceOpen;
        private bool pitchActive;
        private bool setupSubmitted;
        private bool swingRequested;
        private float pitchAddedReward;
        private float plateAppearanceAddedReward;
        private bool actedThisPlateAppearance;
        private float lastPlateAppearanceAddedReward;
        private int completedEpisodes;
        private int completedPitches;
        private BatterRewardSnapshot lastPitchReward;
        private float lastPitchAddedReward;
        private float lastOutcomeReward;
        private float lastCompletedCumulativeReward;
        private int trainingLesson = -1;
        private bool trainingContactPaid;
        private float trainingContactBonus;
        private bool plateAppearanceQualifiedHit;
        private bool plateAppearanceFairContact;
        private int preparationPhase = -1;
        private int skillPhase = -1;
        private bool preparedBatting;

        /// <summary>끝난 타석(에피소드) 수.</summary>
        public int CompletedEpisodes => completedEpisodes;
        /// <summary>보상이 확정된 투구 수.</summary>
        public int CompletedPitches => completedPitches;
        /// <summary>마지막으로 확정된 투구의 보상 성분.</summary>
        public BatterRewardSnapshot LastPitchReward => lastPitchReward;
        /// <summary>마지막 투구에서 Agent에 더한 보상 합(기본 계산기 + 1단계 훈련 접촉 보상).</summary>
        public float LastPitchAddedReward => lastPitchAddedReward;
        public float LastOutcomeReward => lastOutcomeReward;
        /// <summary>마지막 타석에서 이 Agent가 한 번이라도 결정했는지. 결정이 없던 타석은 에피소드로 닫지 않는다.</summary>
        public bool LastPlateAppearanceHadDecisions { get; private set; }
        /// <summary>마지막 타석에서 투구 보상으로 더한 합(결과 보상 제외).</summary>
        public float LastPlateAppearanceAddedReward => lastPlateAppearanceAddedReward;
        /// <summary>마지막으로 끝난 타석 에피소드의 누적 보상(투구 보상 + 결과 보상).</summary>
        public float LastCompletedCumulativeReward => lastCompletedCumulativeReward;
        public PlayDirector Director => director;
        /// <summary>이번 투구의 보상이 아직 확정되지 않았으면 true다.</summary>
        public bool PitchActive => pitchActive;
        public bool PlateAppearanceOpen => plateAppearanceOpen;
        /// <summary>이번 투구의 투구 전 자세 결정을 이미 냈으면 true다.</summary>
        public bool SetupSubmitted => setupSubmitted;
        /// <summary>-1은 기존 전체 조작. 0 접촉, 1 정타, 2 제한 조작, 3 전체 조작 커리큘럼.</summary>
        public int TrainingLesson => trainingLesson;
        public float LastPitchTrainingContactBonus { get; private set; }
        public float LastPitchTrainingQualityReward { get; private set; }
        public bool LastPlateAppearanceQualifiedHit { get; private set; }
        public bool LastPlateAppearanceFairContact { get; private set; }
        public int PreparationPhase => preparationPhase;
        public int SkillPhase => skillPhase;
        public bool PreparedBatting => preparedBatting;
        public bool QualityRewardEnabled => trainingLesson >= 1 || preparedBatting;
        public float TrainingControlScale => skillPhase >= 0 ? 0.5f * Mathf.Max(BatterSkills.HeightScale(skillPhase),
            BatterSkills.AngleScale(skillPhase), BatterSkills.PositionScale(skillPhase))
            : preparationPhase >= 0 ? BatterPreparation.ControlScale(preparationPhase)
            : trainingLesson == 0 || trainingLesson == 1 ? 0f : trainingLesson == 2 ? 0.5f : 1f;
        public float EarliestSwingSeconds => preparationPhase >= 0 ? BatterPreparation.SwingGate(preparationPhase)
            : trainingLesson >= 0 && trainingLesson < 3 ? TrainingEarliestSwingSeconds : 0f;

        // Latched by the controller at a new PA, never changed halfway through a pitch.
        public void ConfigurePreparation(int phase, bool prepared)
        {
            skillPhase = -1;
            preparationPhase = Mathf.Clamp(phase, -1, BatterPreparation.PhaseCount - 1);
            preparedBatting = prepared || preparationPhase >= 0;
        }

        public void ConfigureTrainingLesson(int value) => trainingLesson = Mathf.Clamp(value, -1, 3);
        public void ConfigureSkills(int phase) => skillPhase = preparationPhase == 0
            ? Mathf.Clamp(phase, -1, BatterSkills.PhaseCount - 1) : -1;

        public void AssignDirector(PlayDirector value) => director = value;

        public override void Initialize()
        {
            base.Initialize();
            if (director == null || director.EnvironmentConfig == null)
            {
                Debug.LogError("[BatterAgent] PlayDirector와 설정 에셋 참조가 필요하다.", this);
                enabled = false;
                return;
            }

            var behavior = GetComponent<BehaviorParameters>().BrainParameters;
            if (behavior.VectorObservationSize != ObservationSize ||
                behavior.ActionSpec.NumContinuousActions != ContinuousActionCount ||
                behavior.ActionSpec.NumDiscreteActions != DiscreteActionCount ||
                behavior.ActionSpec.BranchSizes[0] != 2)
            {
                Debug.LogError($"[BatterAgent] Behavior Parameters는 관측 {ObservationSize}, 연속 행동 7, 이산 분기 [2]여야 한다.", this);
                enabled = false;
                return;
            }

            CameraSensorComponent eye = GetComponentInChildren<CameraSensorComponent>();
            if (eye == null || eye.Camera == null || eye.Width != ImageWidth || eye.Height != ImageHeight ||
                !eye.Grayscale || eye.ObservationStacks != ImageStacks ||
                GetComponentInChildren<RayPerceptionSensorComponent3D>() != null)
            {
                Debug.LogError($"[BatterAgent] 포수 시점 카메라 센서({ImageWidth}x{ImageHeight}, grayscale, stack {ImageStacks})가 필요하다. 학습 씬 센서를 갱신하세요.", this);
                enabled = false;
                return;
            }

            ConfigureBallOnlyCamera(eye.Camera, director.transform.root.GetComponentInChildren<BallController>(true));
            rewards = new BatterRewardTracker(director);
            rewards.RewardAdded += OnRewardAdded;
            rewards.EpisodeCompleted += OnPitchCompleted;
        }

        /// <summary>관측 카메라만 검은 배경에 공을 렌더한다. 공의 크기·재질·물리와 일반 카메라는 유지한다.</summary>
        public static void ConfigureBallOnlyCamera(Camera camera, BallController ball)
        {
            int layer = LayerMask.NameToLayer(SensorBallLayerName);
            if (camera == null || ball == null || layer < 0)
                throw new System.InvalidOperationException("Batter ball-only camera requires its ball and the BatterSensorBall layer. Update the training scene sensors first.");
            foreach (Renderer renderer in ball.GetComponentsInChildren<Renderer>(true))
                renderer.gameObject.layer = layer;
            camera.cullingMask = 1 << layer;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.useOcclusionCulling = false;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            UniversalAdditionalCameraData cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData != null)
            {
                cameraData.renderPostProcessing = false;
                cameraData.renderShadows = false;
            }
        }

        /// <summary>컨트롤러가 PlayReset 직후(매 투구) 호출한다. 타석이 닫혀 있으면 새 타석 에피소드를 연다.</summary>
        public void BeginPitch()
        {
            if (rewards == null) return;
            if (!plateAppearanceOpen)
            {
                plateAppearanceAddedReward = 0f;
                actedThisPlateAppearance = false;
                plateAppearanceQualifiedHit = false;
                plateAppearanceFairContact = false;
            }
            plateAppearanceOpen = true;
            rewards.BeginEpisode();
            pitchActive = true;
            pitchAddedReward = 0f;
            setupSubmitted = false;
            swingRequested = false;
            trainingContactPaid = false;
            trainingContactBonus = 0f;
        }

        /// <summary>타석이 끝나면 컨트롤러가 결과 보상을 주고 에피소드를 닫는다.</summary>
        public void EndPlateAppearance(float outcomeReward)
        {
            if (!plateAppearanceOpen) return;
            lastOutcomeReward = outcomeReward;
            lastPlateAppearanceAddedReward = plateAppearanceAddedReward;
            LastPlateAppearanceHadDecisions = actedThisPlateAppearance;
            LastPlateAppearanceQualifiedHit = plateAppearanceQualifiedHit;
            LastPlateAppearanceFairContact = plateAppearanceFairContact;
            plateAppearanceOpen = pitchActive = false;
            // 결정 없이 끝난 타석(검증용 시나리오 타구)은 에피소드가 아니다. ML-Agents는 결정 없이 연속으로 부른 EndEpisode를 무시하고 누적 보상을 이어 가므로 닫지 않는다.
            if (!actedThisPlateAppearance) return;
            AddReward(outcomeReward);
            lastCompletedCumulativeReward = GetCumulativeReward();
            completedEpisodes++;
            EndEpisode();
        }

        /// <summary>플레이가 중단되면 컨트롤러가 호출한다. 타석 에피소드를 중단으로 닫는다.</summary>
        public void AbortPlay()
        {
            if (!plateAppearanceOpen) return;
            plateAppearanceOpen = pitchActive = false;
            if (actedThisPlateAppearance) EpisodeInterrupted();
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            PitchSnapshot pitch = director != null ? director.GetSnapshot() : default;
            BattingEvaluation batting = director != null ? director.GetBattingEvaluation() : default;
            BaseballEnvironmentConfig config = director != null ? director.EnvironmentConfig : null;
            SituationSnapshot count = director != null ? director.GetSituation() : default;
            Vector2 stance = batting.Setup.StanceOffset;
            Vector3 grip = batting.Setup.GripOffset;
            Vector3 limits = config != null ? config.GripOffsetLimits : Vector3.one;
            float stanceLimit = config != null ? config.StanceOffsetLimit : 1f;

            // 공 위치·속도는 넣지 않는다. 공은 포수 시점 카메라로만 관측한다.
            sensor.AddObservation(pitchActive && pitch.State == PlayState.Ready && !setupSubmitted ? 1f : 0f);
            sensor.AddObservation(pitchActive && pitch.State == PlayState.PitchInFlight ? 1f : 0f);
            sensor.AddObservation(Mathf.Clamp01(pitch.ElapsedSeconds / 2f));
            sensor.AddObservation(Normalize(stance.x, stanceLimit));
            sensor.AddObservation(Normalize(stance.y, stanceLimit));
            sensor.AddObservation(Normalize(grip.x, limits.x));
            sensor.AddObservation(Normalize(grip.y, limits.y));
            sensor.AddObservation(Normalize(grip.z, limits.z));
            sensor.AddObservation(batting.HasSwung ? 1f : 0f);
            sensor.AddObservation(batting.HasContact ? 1f : 0f);
            sensor.AddObservation(Mathf.Clamp01(count.Balls / 3f));
            sensor.AddObservation(Mathf.Clamp01(count.Strikes / 2f));
            sensor.AddObservation(Mathf.Clamp01(count.Outs / 2f));
            sensor.AddObservation(count.OnFirst ? 1f : 0f);
            sensor.AddObservation(count.OnSecond ? 1f : 0f);
            sensor.AddObservation(count.OnThird ? 1f : 0f);
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            actedThisPlateAppearance = true;
            if (!pitchActive || director == null) return;
            var continuous = actions.ContinuousActions;
            if (!setupSubmitted)
            {
                if (director.State != PlayState.Ready) return;
                BaseballEnvironmentConfig config = director.EnvironmentConfig;
                Vector3 gripLimit = config.GripOffsetLimits;
                float scale = TrainingControlScale;
                if (skillPhase >= 0) scale = 0.5f * BatterSkills.PositionScale(skillPhase);
                float centerHeight = director.StrikeZoneCenter.y - director.FieldLayout.PitchTargetPosition.y;
                director.RequestBatterSetup(new BatterSetupCommand(
                    // Preserve the seven-action model contract; body-position actions 0/1 are ignored.
                    Vector2.zero,
                    new Vector3(Clamped(continuous[2]) * gripLimit.x * scale,
                        skillPhase >= 0 ? BatterSkills.GripHeight(Clamped(continuous[3]), gripLimit.y, centerHeight, skillPhase)
                            : preparedBatting ? BatterPreparation.GripHeight(Clamped(continuous[3]), gripLimit.y, centerHeight, scale)
                            : Clamped(continuous[3]) * gripLimit.y * scale + Mathf.Clamp(centerHeight, -gripLimit.y, gripLimit.y) * (1f - scale),
                        Clamped(continuous[4]) * gripLimit.z * scale)));
                setupSubmitted = true;
                return;
            }

            if (director.State != PlayState.PitchInFlight || swingRequested || director.HasSwung ||
                actions.DiscreteActions[0] != 1 || !SwingAllowed()) return;
            Vector2 reference = director.EnvironmentConfig.ReferenceSwingAngles;
            float angleScale = TrainingControlScale;
            if (skillPhase >= 0) angleScale = 0.5f * BatterSkills.AngleScale(skillPhase);
            director.RequestSwing(new SwingCommand(Mathf.Lerp(reference.x, 45f * Clamped(continuous[5]), angleScale),
                Mathf.Lerp(reference.y, 10f + 40f * Clamped(continuous[6]), angleScale)));
            swingRequested = true;
        }

        private bool SwingAllowed() => director.GetSnapshot().ElapsedSeconds >= EarliestSwingSeconds;

        public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
        {
            bool allowed = pitchActive && director != null && director.State == PlayState.PitchInFlight &&
                !swingRequested && !director.HasSwung && SwingAllowed();
            actionMask.SetActionEnabled(0, 1, allowed);
        }

        /// <summary>
        /// 학습기·모델이 없을 때의 중립 행동이다. 기준 자세로 서서 스윙하지 않는다.
        /// 기본 구현은 매 결정마다 경고를 남기므로 재정의만 해 둔다.
        /// </summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            actionsOut.Clear();
        }

        private void OnRewardAdded(float amount)
        {
            if (!pitchActive) return;
            AddReward(amount);
            pitchAddedReward += amount;
            plateAppearanceAddedReward += amount;
            // 실제 접촉 사건에서만 추가한다. 스윙 시도·좋은 타이밍·거리만으로 접촉 보상을 만들지 않는다.
            if (trainingLesson == 0 && !trainingContactPaid && rewards.GetSnapshot().Contact > 0f)
            {
                trainingContactPaid = true;
                trainingContactBonus = TrainingContactBonus;
                AddReward(trainingContactBonus);
                pitchAddedReward += trainingContactBonus;
                plateAppearanceAddedReward += trainingContactBonus;
            }
        }

        private void OnPitchCompleted(BatterRewardSnapshot snapshot)
        {
            if (!pitchActive) return;
            BattedBallSnapshot hit = director.GetBattedBallSnapshot();
            bool fairContact = snapshot.Contact > 0f && director.HasContact && IsFairTrainingHit(hit);
            float adjustment = 0f;
            // Contact is a temporary warm-up reward. Foul/invalid contact cannot farm it.
            if (trainingContactBonus > 0f && !fairContact)
            {
                adjustment -= trainingContactBonus;
                trainingContactBonus = 0f;
            }
            LastPitchTrainingQualityReward = 0f;
            if (QualityRewardEnabled && fairContact)
            {
                LastPitchTrainingQualityReward = TrainingFairHitReward(director.ContactQuality, hit, preparedBatting) - snapshot.Total;
                adjustment += LastPitchTrainingQualityReward;
            }
            if (adjustment != 0f)
            {
                AddReward(adjustment);
                pitchAddedReward += adjustment;
                plateAppearanceAddedReward += adjustment;
            }
            plateAppearanceQualifiedHit |= fairContact && IsQualifiedTrainingHit(director.ContactQuality, hit);
            plateAppearanceFairContact |= fairContact;
            pitchActive = false;
            lastPitchReward = snapshot;
            lastPitchAddedReward = pitchAddedReward;
            LastPitchTrainingContactBonus = trainingContactBonus;
            completedPitches++;
        }

        private static bool IsFairTrainingHit(BattedBallSnapshot hit) => hit.Call == BattedBallCall.Fair ||
            hit.Call == BattedBallCall.HomeRun || hit.Call == BattedBallCall.GroundRuleDouble;

        public static bool IsQualifiedTrainingHit(float quality, BattedBallSnapshot hit) => IsFairTrainingHit(hit) &&
            (hit.Call == BattedBallCall.HomeRun || (quality >= TrainingHitQualityFloor &&
                hit.ExitSpeed * 3.6f >= TrainingHitSpeedFloorKmh && hit.LaunchAngleDegrees >= TrainingHitLaunchMinimum &&
                hit.LaunchAngleDegrees <= TrainingHitLaunchMaximum));

        /// <summary>Total fair-hit reward, reconciled once at pitch completion. Prepared batting strengthens progress toward a qualified hit.</summary>
        public static float TrainingFairHitReward(float quality, BattedBallSnapshot hit, bool progressive = false)
        {
            if (!IsFairTrainingHit(hit) || !BaseballEnvironmentConfig.IsFinite(quality) ||
                !BaseballEnvironmentConfig.IsFinite(hit.ExitSpeed) || !BaseballEnvironmentConfig.IsFinite(hit.LaunchAngleDegrees)) return 0f;
            float speedKmh = hit.ExitSpeed * 3.6f;
            float q = Mathf.Clamp01(quality);
            if (hit.Call == BattedBallCall.HomeRun)
                return BatterRewardTracker.ContactReward + BatterRewardTracker.HomeRunReward + BatterRewardTracker.SpeedBonus(hit.ExitSpeed);
            if (IsQualifiedTrainingHit(q, hit))
                return 2f + 0.5f * q + 0.5f * Mathf.Clamp01(speedKmh / 180f);
            float angle = hit.LaunchAngleDegrees;
            float launchScore = Mathf.Clamp01(angle / TrainingHitLaunchMinimum) *
                Mathf.Clamp01((50f - angle) / (50f - TrainingHitLaunchMaximum));
            float qualityProgress = Mathf.Clamp01(q / TrainingHitQualityFloor);
            float speedProgress = Mathf.Clamp01(speedKmh / TrainingHitSpeedFloorKmh);
            // Only actual fair contact reaches this branch. Quality is timing precision
            // multiplied by sweet-spot precision; favor improving it over merely touching.
            // Keep incomplete hits below every qualified hit and preserve the final gate.
            return progressive
                ? (0.25f + 1.25f * (0.75f * qualityProgress + 0.25f * speedProgress)) * launchScore
                : 0.5f * (0.5f * qualityProgress + 0.5f * speedProgress) * launchScore;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (rewards == null) return;
            rewards.RewardAdded -= OnRewardAdded;
            rewards.EpisodeCompleted -= OnPitchCompleted;
            rewards.Dispose();
            rewards = null;
        }

        private static float Clamped(float value) => float.IsNaN(value) || float.IsInfinity(value)
            ? 0f : Mathf.Clamp(value, -1f, 1f);

        private static float Normalize(float value, float limit) => limit > 0f
            ? Mathf.Clamp(value / limit, -1f, 1f) : 0f;
    }
}
