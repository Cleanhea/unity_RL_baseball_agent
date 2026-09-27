using UnityEngine;
using UnityEngine.InputSystem;

namespace BaseballSimulation
{
    /// <summary>
    /// 키보드 입력을 <see cref="PlayDirector"/>의 환경 명령으로 변환한다
    /// (docs/architecture.md 3.1 "ManualPlayController").
    ///
    /// 입력 수집은 일반 프레임 <see cref="Update"/>에서 하고, Rigidbody나 플레이 상태를
    /// 직접 바꾸지 않는다. 실제 발사·초기화는 PlayDirector의 FixedUpdate가 담당한다.
    /// 프로젝트의 Active Input Handling이 "Input System Package (New)"로 설정돼 있어
    /// 레거시 <c>UnityEngine.Input</c> 대신 <see cref="Keyboard"/>를 사용한다.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ManualPlayController : MonoBehaviour
    {
        [Tooltip("P/R 입력을 전달할 대상.")]
        [SerializeField]
        private PlayDirector playDirector;

        [Tooltip("실제 스윙 평면의 좌우각. 타구 방향을 직접 지정하지 않는다.")]
        [SerializeField, Range(-45f, 45f)] private float sprayDegrees;
        [Tooltip("실제 스윙 평면의 상향각(음수는 내려치는 스윙).")]
        [SerializeField, Range(-30f, 50f)] private float launchDegrees = 15f;

        private void Update()
        {
            if (playDirector == null || !playDirector.ManualInputEnabled)
            {
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            float adjustment = 45f * Time.deltaTime;
            sprayDegrees = Mathf.Clamp(sprayDegrees + (keyboard.rightArrowKey.isPressed ? adjustment : 0f)
                - (keyboard.leftArrowKey.isPressed ? adjustment : 0f), -45f, 45f);
            launchDegrees = Mathf.Clamp(launchDegrees + (keyboard.upArrowKey.isPressed ? adjustment : 0f)
                - (keyboard.downArrowKey.isPressed ? adjustment : 0f), -30f, 50f);
            playDirector.AimSprayDegrees = sprayDegrees;
            playDirector.AimLaunchDegrees = launchDegrees;
            if (playDirector.State == PlayState.Ready)
            {
                Vector2 stanceDelta = new Vector2(
                    (keyboard.dKey.wasPressedThisFrame ? 0.05f : 0f) - (keyboard.aKey.wasPressedThisFrame ? 0.05f : 0f),
                    (keyboard.wKey.wasPressedThisFrame ? 0.05f : 0f) - (keyboard.sKey.wasPressedThisFrame ? 0.05f : 0f));
                Vector3 gripDelta = new Vector3(
                    (keyboard.lKey.wasPressedThisFrame ? 0.05f : 0f) - (keyboard.jKey.wasPressedThisFrame ? 0.05f : 0f),
                    (keyboard.uKey.wasPressedThisFrame ? 0.05f : 0f) - (keyboard.oKey.wasPressedThisFrame ? 0.05f : 0f),
                    (keyboard.iKey.wasPressedThisFrame ? 0.05f : 0f) - (keyboard.kKey.wasPressedThisFrame ? 0.05f : 0f));
                if (stanceDelta != Vector2.zero || gripDelta != Vector3.zero)
                {
                    BatterSetupCommand setup = playDirector.GetBattingEvaluation().Setup;
                    playDirector.RequestBatterSetup(new BatterSetupCommand(setup.StanceOffset + stanceDelta,
                        setup.GripOffset + gripDelta));
                }
            }
            if (keyboard.spaceKey.wasPressedThisFrame)
                playDirector.RequestSwing(new SwingCommand(sprayDegrees, launchDegrees));
            if (playDirector.State == PlayState.BattedBallInFlight)
            {
                if (keyboard.fKey.wasPressedThisFrame) playDirector.RequestRunnerDecision(RunnerDecision.Advance);
                if (keyboard.bKey.wasPressedThisFrame) playDirector.RequestRunnerDecision(RunnerDecision.Return);
            }

            if (keyboard.pKey.wasPressedThisFrame)
            {
                playDirector.RequestThrowPitch();
            }

            if (keyboard.rKey.wasPressedThisFrame)
            {
                playDirector.RequestResetPlay();
            }
        }
    }
}
