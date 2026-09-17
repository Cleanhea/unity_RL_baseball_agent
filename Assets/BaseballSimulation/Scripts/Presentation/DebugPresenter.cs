using UnityEngine;

namespace BaseballSimulation
{
    /// <summary>
    /// PlayDirector의 읽기 전용 스냅샷을 Game 뷰 HUD로 표시한다
    /// (docs/architecture.md 3.1 "Debug View"). 시뮬레이션 상태를 바꾸지 않는다.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DebugPresenter : MonoBehaviour
    {
        [SerializeField]
        private PlayDirector playDirector;

        private GUIStyle labelStyle;
        private Texture2D backgroundTexture;

        private void OnGUI()
        {
            if (playDirector == null)
            {
                return;
            }

            EnsureStyles();

            PitchSnapshot snapshot = playDirector.GetSnapshot();
            BattingEvaluation batting = playDirector.GetBattingEvaluation();
            string timing = batting.HasTimingReference ? $"{batting.TimingErrorSeconds * 1000f:F1} ms" : "-";
            string angle = batting.HasSwung ? $"{batting.SwingAngleErrorDegrees:F1} deg" : "-";
            string distance = batting.HasClosestDistance ? $"{batting.ClosestDistance:F3} m" : "-";

            string centerPassText = snapshot.HasCenterPassError
                ? $"{snapshot.CenterPassError:F3} m"
                : "-";

            string autoRepeatText;
            if (!snapshot.AutoRepeatEnabled)
            {
                autoRepeatText = "꺼짐";
            }
            else if (snapshot.State == PlayState.Ended)
            {
                autoRepeatText = $"켜짐 (다음 투구까지 {snapshot.AutoRepeatRemainingSeconds:F1}s)";
            }
            else
            {
                autoRepeatText = "켜짐";
            }

            string rejectionLine = string.IsNullOrEmpty(snapshot.LastRejectionReason)
                ? string.Empty
                : $"\n거부 사유: {snapshot.LastRejectionReason}";

            string text =
                "[타자 평가]\n" +
                $"상태: {snapshot.State}  (종료 사유: {snapshot.EndReason})\n" +
                $"경과 시간: {snapshot.ElapsedSeconds:F2} s\n" +
                $"공 속력: {snapshot.BallSpeed:F2} m/s\n" +
                $"중앙 통과 오차: {centerPassText}\n" +
                $"완료한 투구 수: {snapshot.CompletedPitchCount}\n" +
                $"자동 반복: {autoRepeatText}\n" +
                "조작: P = 투구, R = 초기화" +
                $"\nSwing: {playDirector.HasSwung}  Hit: {playDirector.HasContact}  Quality: {playDirector.ContactQuality:F2}" +
                $"\n스윙 좌우/상향: {playDirector.AimSprayDegrees:F0} / {playDirector.AimLaunchDegrees:F0} deg" +
                $"\n타자 위치: {batting.StanceErrorMetres:F3} m  점수 {batting.Scores.x:F2}" +
                $"\n배트 위치: {batting.GripErrorMetres:F3} m  점수 {batting.Scores.y:F2}" +
                $"\n타이밍: {timing}  점수 {batting.Scores.z:F2}" +
                $"\n스윙 각도: {angle}  점수 {batting.Scores.w:F2}" +
                $"\n공-배트 최소 거리: {distance}" +
                "\nSpace 스윙 / 방향키 스윙 평면 각도" +
                "\n투구 전: WASD 타자 / IJKL 배트 / U·O 배트 높이 (5cm)" +
                $"\n입력: {(playDirector.ManualInputEnabled ? "수동" : "스크립트")}" +
                rejectionLine;

            var rect = new Rect(16f, 16f, 590f, 420f);
            GUI.DrawTexture(rect, backgroundTexture);
            GUI.Label(rect, text, labelStyle);
        }

        private void EnsureStyles()
        {
            if (labelStyle != null)
            {
                return;
            }

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                richText = false,
                padding = new RectOffset(10, 10, 8, 8),
            };
            labelStyle.normal.textColor = Color.white;

            backgroundTexture = new Texture2D(1, 1);
            backgroundTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.55f));
            backgroundTexture.Apply();
        }

        private void OnDestroy()
        {
            if (backgroundTexture != null)
            {
                Destroy(backgroundTexture);
            }
        }
    }
}
