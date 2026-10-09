using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// 3단계 주자 Agent(docs/fielding-agents.md). 슬롯 0은 타자주자, 1~3은 1·2·3루에서 타석을 시작하는 누상 주자다.
    /// 네 명이 같은 Behavior(BaseballRunner)를 쓰고, 달리기는 환경이 하며 이 Agent는 진루/귀루 판단만
    /// <see cref="PlayDirector.RequestRunnerDecision(int, RunnerDecision)"/>으로 전달한다. 타구 후 살아 있는 동안
    /// <see cref="TrainingEnvController"/>가 결정을 요청하고, 플레이가 끝나면 주자 그룹 보상을 준다.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BehaviorParameters), typeof(RunnerController))]
    public sealed class RunnerAgent : Agent
    {
        public const int ObservationSize = 65;
        public const int DecisionCount = 3;
        public const int SlotCount = 4;
        private const float FieldScale = 60f;

        [SerializeField] private PlayDirector director;
        [Tooltip("0 타자주자, 1~3 누상 주자 슬롯(PlayDirector 주자 슬롯과 같은 번호).")]
        [SerializeField] private int runnerSlot;

        public PlayDirector Director => director;
        public int RunnerSlot => runnerSlot;

        /// <summary>타구 후 이 슬롯의 주자가 베이스 경로 위에 살아 있으면 true다.</summary>
        public bool IsRunning => director != null && director.State == PlayState.BattedBallInFlight &&
            director.GetRunnerSnapshot(runnerSlot).IsLive;

        public void Assign(PlayDirector value, int slot)
        {
            director = value;
            runnerSlot = slot;
        }

        public override void Initialize()
        {
            base.Initialize();
            if (director == null || director.EnvironmentConfig == null || runnerSlot < 0 || runnerSlot >= SlotCount)
            {
                Debug.LogError("[RunnerAgent] PlayDirector 참조와 0~3 주자 슬롯이 필요하다.", this);
                enabled = false;
                return;
            }
            var brain = GetComponent<BehaviorParameters>().BrainParameters;
            if (brain.VectorObservationSize != ObservationSize || brain.ActionSpec.NumContinuousActions != 0 ||
                brain.ActionSpec.NumDiscreteActions != 1 || brain.ActionSpec.BranchSizes[0] != DecisionCount)
            {
                Debug.LogError($"[RunnerAgent] Behavior Parameters는 관측 {ObservationSize}, 연속 행동 0, 이산 분기 [3]이어야 한다.", this);
                enabled = false;
            }
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            RunnerSnapshot runner = director != null ? director.GetRunnerSnapshot(runnerSlot) : default;
            for (int s = 0; s < SlotCount; s++) sensor.AddObservation(s == runnerSlot ? 1f : 0f);
            sensor.AddObservation(runner.Phase == RunnerPhase.Advancing ? 1f : 0f);
            sensor.AddObservation(runner.Phase == RunnerPhase.Returning ? 1f : 0f);
            sensor.AddObservation(runner.Phase == RunnerPhase.Holding ? 1f : 0f);
            for (int b = 0; b < 4; b++) sensor.AddObservation((int)runner.LastTouchedBase == b ? 1f : 0f);
            for (int b = 0; b < 4; b++) sensor.AddObservation((int)runner.NextBase == b ? 1f : 0f);
            for (int b = 0; b < 4; b++) sensor.AddObservation((int)runner.TargetBase == b ? 1f : 0f);
            sensor.AddObservation(director != null && director.IsRunnerForced(runnerSlot) ? 1f : 0f);
            sensor.AddObservation(director != null && director.RunnerNeedsRetouch(runnerSlot) ? 1f : 0f);
            Vector3 self = runner.Position;
            // 위치는 자기 경기장의 홈 기준이다. 경기장을 옮기거나 여러 개 복제해도 같은 상황은 같은 관측이 된다.
            Vector3 home = director != null ? director.FieldLayout.HomePosition : Vector3.zero;
            sensor.AddObservation(Mathf.Clamp((self.x - home.x) / FieldScale, -2f, 2f));
            sensor.AddObservation(Mathf.Clamp((self.z - home.z) / FieldScale, -2f, 2f));
            FieldLayout field = director != null ? director.FieldLayout : null;
            float toNext = field != null && runner.IsLive ? Vector3.Distance(self, field.GetBasePosition(runner.NextBase)) : 0f;
            sensor.AddObservation(Mathf.Clamp01(toNext / 30f));

            PitchSnapshot pitch = director != null ? director.GetSnapshot() : default;
            int holder = director != null ? director.BallHolder : -1;
            sensor.AddObservation(Mathf.Clamp((pitch.BallPosition.x - home.x) / FieldScale, -2f, 2f));
            sensor.AddObservation(Mathf.Clamp((pitch.BallPosition.y - home.y) / 20f, -1f, 2f));
            sensor.AddObservation(Mathf.Clamp((pitch.BallPosition.z - home.z) / FieldScale, -2f, 2f));
            Vector3 ballVelocity = holder >= 0 ? Vector3.zero : pitch.BallVelocity;
            sensor.AddObservation(Mathf.Clamp(ballVelocity.x / 40f, -1.5f, 1.5f));
            sensor.AddObservation(Mathf.Clamp(ballVelocity.y / 40f, -1.5f, 1.5f));
            sensor.AddObservation(Mathf.Clamp(ballVelocity.z / 40f, -1.5f, 1.5f));
            sensor.AddObservation(holder >= 0 ? 1f : 0f);
            Vector3 holderOffset = holder >= 0 ? director.GetFielder(holder).Position - self : Vector3.zero;
            sensor.AddObservation(Mathf.Clamp(holderOffset.x / FieldScale, -2f, 2f));
            sensor.AddObservation(Mathf.Clamp(holderOffset.z / FieldScale, -2f, 2f));
            bool grounded = director != null && (director.BattedBallFielded || director.GetBattedBallSnapshot().HasFirstTouch);
            sensor.AddObservation(grounded ? 1f : 0f);
            sensor.AddObservation(director != null ? Mathf.Clamp01(director.GetSituation().Outs / 2f) : 0f);

            for (int s = 0; s < SlotCount; s++)
            {
                if (s == runnerSlot) continue;
                RunnerSnapshot other = director != null ? director.GetRunnerSnapshot(s) : default;
                Vector3 offset = other.IsLive ? other.Position - self : Vector3.zero;
                sensor.AddObservation(other.IsLive ? 1f : 0f);
                sensor.AddObservation(Mathf.Clamp(offset.x / FieldScale, -2f, 2f));
                sensor.AddObservation(Mathf.Clamp(offset.z / FieldScale, -2f, 2f));
                sensor.AddObservation(other.IsLive ? (int)other.NextBase / 3f : 0f);
            }

            for (int i = 0; i < FielderAgent.RoleCount; i++)
            {
                int index = director != null ? director.FindFielder((FielderRole)i) : -1;
                Vector3 offset = index >= 0 ? director.GetFielder(index).Position - self : Vector3.zero;
                sensor.AddObservation(Mathf.Clamp(offset.x / FieldScale, -2f, 2f));
                sensor.AddObservation(Mathf.Clamp(offset.z / FieldScale, -2f, 2f));
            }
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (director == null || !IsRunning) return;
            switch (actions.DiscreteActions[0])
            {
                case 1: director.RequestRunnerDecision(runnerSlot, RunnerDecision.Advance); break;
                case 2: director.RequestRunnerDecision(runnerSlot, RunnerDecision.Return); break;
            }
        }

        /// <summary>학습기·모델이 없을 때의 중립 행동: 판단하지 않는다(포스된 베이스까지만 자동으로 달리고 멈춘다).</summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            actionsOut.Clear();
        }
    }
}
