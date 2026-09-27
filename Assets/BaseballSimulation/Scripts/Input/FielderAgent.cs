using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 3단계 수비수 Agent(docs/fielding-agents.md). 다섯 명이 같은 Behavior(BaseballFielder)를 쓰고
    /// 역할은 관측의 원-핫으로 구분한다. <see cref="TrainingEnvController"/>가 타구가 살아 있는 동안만 결정을 요청하고
    /// 수비 그룹(SimpleMultiAgentGroup) 보상을 준다. 이동·송구는 PlayDirector 명령으로만 전달한다.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BehaviorParameters), typeof(FielderController))]
    public sealed class FielderAgent : Agent
    {
        public const int ObservationSize = 65;
        public const int ContinuousActionCount = 2;
        public const int ThrowTargetCount = 5;
        private const float FieldScale = 60f;
        private const float BallSpeedScale = 40f;

        [SerializeField] private PlayDirector director;
        [SerializeField] private int fielderIndex;

        private FielderController body;

        public int FielderIndex => fielderIndex;
        public PlayDirector Director => director;

        public void Assign(PlayDirector value, int index)
        {
            director = value;
            fielderIndex = index;
        }

        public override void Initialize()
        {
            base.Initialize();
            body = GetComponent<FielderController>();
            if (director == null || director.EnvironmentConfig == null || fielderIndex < 0 ||
                fielderIndex >= director.FielderCount || director.GetFielder(fielderIndex) != body)
            {
                Debug.LogError("[FielderAgent] PlayDirector 수비수 목록의 같은 인덱스에 이 수비수가 있어야 한다.", this);
                enabled = false;
                return;
            }
            var brain = GetComponent<BehaviorParameters>().BrainParameters;
            if (brain.VectorObservationSize != ObservationSize || brain.ActionSpec.NumContinuousActions != ContinuousActionCount ||
                brain.ActionSpec.NumDiscreteActions != 1 || brain.ActionSpec.BranchSizes[0] != ThrowTargetCount)
            {
                Debug.LogError($"[FielderAgent] Behavior Parameters는 관측 {ObservationSize}, 연속 행동 2, 이산 분기 [5]여야 한다.", this);
                enabled = false;
            }
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            Vector3 self = body != null ? body.Position : transform.position;
            // 위치는 자기 경기장의 홈 기준이다. 경기장을 옮기거나 여러 개 복제해도 같은 상황은 같은 관측이 된다.
            Vector3 home = director != null ? director.FieldLayout.HomePosition : Vector3.zero;
            for (int role = 0; role < 5; role++) sensor.AddObservation(body != null && (int)body.Role == role ? 1f : 0f);
            sensor.AddObservation(Mathf.Clamp((self.x - home.x) / FieldScale, -2f, 2f));
            sensor.AddObservation(Mathf.Clamp((self.z - home.z) / FieldScale, -2f, 2f));
            float speed = director != null ? director.EnvironmentConfig.FielderSpeed : 1f;
            Vector3 velocity = body != null ? body.Velocity : Vector3.zero;
            sensor.AddObservation(velocity.x / speed);
            sensor.AddObservation(velocity.z / speed);

            int holder = director != null ? director.BallHolder : -1;
            sensor.AddObservation(holder == fielderIndex ? 1f : 0f);
            sensor.AddObservation(holder >= 0 && holder != fielderIndex ? 1f : 0f);
            sensor.AddObservation(holder < 0 ? 1f : 0f);

            PitchSnapshot pitch = director != null ? director.GetSnapshot() : default;
            AddRelative(sensor, pitch.BallPosition, self, true);
            Vector3 ballVelocity = holder >= 0 ? Vector3.zero : pitch.BallVelocity;
            sensor.AddObservation(Mathf.Clamp(ballVelocity.x / BallSpeedScale, -1.5f, 1.5f));
            sensor.AddObservation(Mathf.Clamp(ballVelocity.y / BallSpeedScale, -1.5f, 1.5f));
            sensor.AddObservation(Mathf.Clamp(ballVelocity.z / BallSpeedScale, -1.5f, 1.5f));
            bool grounded = director != null && (director.BattedBallFielded || director.GetBattedBallSnapshot().HasFirstTouch);
            sensor.AddObservation(grounded ? 1f : 0f);
            sensor.AddObservation(pitch.State == PlayState.BattedBallInFlight ? 1f : 0f);

            sensor.AddObservation(director != null ? Mathf.Clamp01(director.GetSituation().Outs / 2f) : 0f);
            // 주자 슬롯 4개(타자주자, 1·2·3루 시작 주자): 살아 있음, 상대 위치, 진루·귀루 중, 포스, 리터치 필요.
            for (int slot = 0; slot < 4; slot++)
            {
                RunnerSnapshot runner = director != null ? director.GetRunnerSnapshot(slot) : default;
                Vector3 runnerOffset = runner.IsLive ? runner.Position - self : Vector3.zero;
                sensor.AddObservation(runner.IsLive ? 1f : 0f);
                sensor.AddObservation(Mathf.Clamp(runnerOffset.x / FieldScale, -2f, 2f));
                sensor.AddObservation(Mathf.Clamp(runnerOffset.z / FieldScale, -2f, 2f));
                sensor.AddObservation(runner.Phase == RunnerPhase.Advancing ? 1f : 0f);
                sensor.AddObservation(runner.Phase == RunnerPhase.Returning ? 1f : 0f);
                sensor.AddObservation(director != null && director.IsRunnerForced(slot) ? 1f : 0f);
                sensor.AddObservation(director != null && director.RunnerNeedsRetouch(slot) ? 1f : 0f);
            }

            FieldLayout field = director != null ? director.FieldLayout : null;
            foreach (BaseId baseId in new[] { BaseId.First, BaseId.Second, BaseId.Third, BaseId.Home })
                AddRelative(sensor, field != null ? field.GetBasePosition(baseId) : Vector3.zero, self, false);

            int count = director != null ? director.FielderCount : 0;
            for (int i = 0; i < 5; i++)
            {
                if (i == fielderIndex) continue;
                Vector3 mate = i < count ? director.GetFielder(i).Position : self;
                AddRelative(sensor, mate, self, false);
            }
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (director == null) return;
            var continuous = actions.ContinuousActions;
            director.RequestFielderMove(fielderIndex, new Vector2(Clamped(continuous[0]), Clamped(continuous[1])));
            int target = actions.DiscreteActions[0];
            if (target > 0 && target < ThrowTargetCount && director.BallHolder == fielderIndex)
                director.RequestFielderThrow(fielderIndex, (ThrowTarget)target);
        }

        /// <summary>학습기·모델이 없을 때의 중립 행동: 제자리에 서 있고 송구하지 않는다.</summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            actionsOut.Clear();
        }

        private static void AddRelative(VectorSensor sensor, Vector3 point, Vector3 self, bool withHeight)
        {
            Vector3 offset = point - self;
            sensor.AddObservation(Mathf.Clamp(offset.x / FieldScale, -2f, 2f));
            if (withHeight) sensor.AddObservation(Mathf.Clamp((point.y - self.y) / 20f, -1f, 2f));
            sensor.AddObservation(Mathf.Clamp(offset.z / FieldScale, -2f, 2f));
        }

        private static float Clamped(float value) => float.IsNaN(value) || float.IsInfinity(value)
            ? 0f : Mathf.Clamp(value, -1f, 1f);
    }
}
