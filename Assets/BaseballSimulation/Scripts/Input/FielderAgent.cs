using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 3단계 수비수 Agent(docs/fielding-agents.md). 단순 수비는 중견수만 Behavior(BaseballFielder)를 쓰고
    /// 역할은 관측의 원-핫으로 구분한다. <see cref="TrainingEnvController"/>가 타구가 살아 있는 동안만 결정을 요청하고
    /// 수비 그룹(SimpleMultiAgentGroup) 보상을 준다. 이동·송구는 PlayDirector 명령으로만 전달한다.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BehaviorParameters), typeof(FielderController))]
    public sealed class FielderAgent : Agent
    {
        /// <summary>수비 역할 수(투수·포수·내야 4·외야 3). 역할 원-핫과 동료 위치 관측의 크기를 정한다.</summary>
        public const int RoleCount = 9;
        public const int ObservationSize = 77;
        public const int ContinuousActionCount = 2;
        public const int ThrowTargetCount = 5;
        public const int SimplifiedThrowTargetCount = 3;
        /// <summary>
        /// 이동 행동 배율. 학습기 출력은 성분 /3 뒤 단위 원으로 제한된 값이라, 그대로 쓰면 최고 속력에 정책 평균 3이 필요하다.
        /// 3을 곱해 출력 크기 1/3(원래 정책 값 1) 이상이면 최고 속력이 되게 한다. 크기 1 제한은 FielderController가 한다.
        /// </summary>
        public const float MoveActionGain = 3f;
        private const float FieldScale = 60f;
        private const float BallSpeedScale = 40f;

        [SerializeField] private PlayDirector director;
        [SerializeField] private int fielderIndex;

        private FielderController body;

        public int FielderIndex => fielderIndex;
        public PlayDirector Director => director;
        public int ThrowActionCount => director != null && director.SimplifiedFielding ? SimplifiedThrowTargetCount : ThrowTargetCount;

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
                brain.ActionSpec.NumDiscreteActions != 1 || brain.ActionSpec.BranchSizes[0] != ThrowActionCount ||
                (director.SimplifiedFielding && (body.Role != FielderRole.CenterField || body.MovementLocked)))
            {
                Debug.LogError($"[FielderAgent] 관측 {ObservationSize}, 연속 행동 2, 이산 분기 [{ThrowActionCount}] 및 단순 수비의 중견수 연결을 확인하세요.", this);
                enabled = false;
            }
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            Vector3 self = body != null ? body.Position : transform.position;
            // 위치는 자기 경기장의 홈 기준이다. 경기장을 옮기거나 여러 개 복제해도 같은 상황은 같은 관측이 된다.
            Vector3 home = director != null ? director.FieldLayout.HomePosition : Vector3.zero;
            for (int role = 0; role < RoleCount; role++) sensor.AddObservation(body != null && (int)body.Role == role ? 1f : 0f);
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

            FielderRole selfRole = body != null ? body.Role : GetComponent<FielderController>().Role;
            for (int role = 0; role < RoleCount; role++)
            {
                if (role == (int)selfRole) continue;
                int index = director != null ? director.FindFielder((FielderRole)role) : -1;
                Vector3 mate = index >= 0 ? director.GetFielder(index).Position : self;
                AddRelative(sensor, mate, self, false);
            }
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (director == null) return;
            if (director.SimplifiedFielding && director.CanThrow(fielderIndex))
            {
                director.RequestFielderMove(fielderIndex, Vector2.zero);
                int choice = Mathf.Clamp(actions.DiscreteActions[0], 0, SimplifiedThrowTargetCount - 1);
                director.RequestFielderThrow(fielderIndex, (ThrowTarget)(choice + 1));
                return;
            }
            var continuous = actions.ContinuousActions;
            // 성분별로 자르면 큰 행동이 (±1, ±1)로 몰려 원래 방향을 잃는다. 명령 경계에서 벡터 크기만 제한한다.
            director.RequestFielderMove(fielderIndex, new Vector2(continuous[0], continuous[1]) * MoveActionGain);
            int target = actions.DiscreteActions[0];
            if (!director.SimplifiedFielding && target > 0 && target < ThrowTargetCount && director.BallHolder == fielderIndex)
                director.RequestFielderThrow(fielderIndex, (ThrowTarget)target);
        }

        /// <summary>
        /// 송구할 수 없는 결정(공을 쥐지 않은 수비수)에서는 송구 선택지(1~4)를 막는다. 그래야 송구 분기가
        /// 실제로 송구할 수 있는 상태에서만 학습되고, 공을 쥐지 않은 수비수의 무의미한 선택이 정책에 섞이지 않는다.
        /// </summary>
        public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
        {
            if (director != null && director.SimplifiedFielding) return; // 세 베이스 모두 허용하며, 공이 없을 때는 선택을 실행하지 않는다.
            bool canThrow = director != null && director.CanThrow(fielderIndex);
            for (int target = 1; target < ThrowTargetCount; target++) actionMask.SetActionEnabled(0, target, canThrow);
        }

        /// <summary>학습기·모델이 없을 때 이동 0. 단순 수비의 포구 후 선택 0은 1루 송구다.</summary>
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
    }
}
