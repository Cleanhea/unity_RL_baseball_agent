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
            RunnerSnapshot runner = playDirector.GetRunnerSnapshot();
            string runnerText = runner.Phase == RunnerPhase.Inactive
                ? "대기"
                : $"{runner.Phase}  마지막 {runner.LastTouchedBase} → 다음 {runner.NextBase} (목표 {runner.TargetBase})";
            BattedBallSnapshot hit = playDirector.GetBattedBallSnapshot();
            string hitText = hit.ExitSpeed <= 0f
                ? "-"
                : $"{(hit.Call == BattedBallCall.None ? "판정 대기" : hit.Call.ToString())}  " +
                  $"{hit.ExitSpeed:F1} m/s ({hit.ExitSpeed * 3.6f:F0} km/h)  발사각 {hit.LaunchAngleDegrees:F0}°  " +
                  $"방향 {hit.SprayAngleDegrees:F0}°  회전 {hit.BackspinRpm:F0} rpm" +
                  $"\n  최고 높이 {hit.ApexHeight:F1} m" +
                  (hit.HasFirstTouch ? $"  비거리 {hit.Distance:F1} m ({hit.Distance / 0.3048f:F0} ft)  체공 {hit.HangTime:F2} s" : string.Empty);

            PitchCallSnapshot pitch = playDirector.GetPitchCall();
            string pitchText = $"{(pitch.Call == PitchCall.None ? "판정 전" : pitch.Call.ToString())}  " +
                $"목표 ({pitch.AimLocation.x:+0.00;-0.00} m, {pitch.AimLocation.y:F2} m)" +
                (pitch.HasPlateLocation ? $"  통과 ({pitch.PlateLocation.x:+0.00;-0.00} m, {pitch.PlateLocation.y:F2} m)" : string.Empty) +
                $"  존 {(pitch.InZone ? "안" : "밖")}";

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

            SituationSnapshot count = playDirector.GetSituation();
            string pitchInfo = playDirector.TryGetLastPitch(out PitchCommand lastPitch)
                ? $"  {lastPitch.Type} {lastPitch.Speed * 3.6f:F0} km/h" : string.Empty;
            string situationText = $"B{count.Balls} S{count.Strikes} O{count.Outs}  주자 " +
                $"{(count.OnFirst ? "1" : "-")}{(count.OnSecond ? "2" : "-")}{(count.OnThird ? "3" : "-")}  " +
                $"반 이닝 득점 {count.RunsThisHalfInning}" +
                (count.PlateAppearanceOver ? $"  타석 종료: {count.Result}" : string.Empty) + pitchInfo;

            string rejectionLine = string.IsNullOrEmpty(snapshot.LastRejectionReason)
                ? string.Empty
                : $"\n거부 사유: {snapshot.LastRejectionReason}";

            string text =
                "[타자 평가]\n" +
                $"상황: {situationText}\n" +
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
                $"\n투구: {pitchText}" +
                $"\n타구: {hitText}" +
                $"\n주자: {runnerText}" +
                "\n타구 후: F 진루 / B 귀루" +
                $"\n입력: {(playDirector.ManualInputEnabled ? "수동" : "스크립트")}" +
                rejectionLine;

            var rect = new Rect(16f, 16f, 640f, 560f);
            GUI.DrawTexture(rect, backgroundTexture);
            GUI.Label(rect, text, labelStyle);
            DrawZonePanel(pitch);
        }

        /// <summary>
        /// 포수 시점의 존 패널. 1루 쪽(+x)이 오른쪽이다. 사각형은 플레이트 폭 × 존 높이이고,
        /// 점은 플레이트 앞 모서리를 지난 공(실제 지름)이다.
        /// </summary>
        private void DrawZonePanel(PitchCallSnapshot pitch)
        {
            const float scale = 130f;   // px per metre
            var panel = new Rect(Screen.width - 236f, 16f, 220f, 270f);
            GUI.DrawTexture(panel, backgroundTexture);
            float ground = panel.yMax - 44f;
            float centreX = panel.center.x;
            Rect ToScreen(float x, float height, float halfSize) =>
                new Rect(centreX + x * scale - halfSize, ground - height * scale - halfSize, halfSize * 2f, halfSize * 2f);

            Color previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.35f);
            GUI.DrawTexture(new Rect(panel.x + 8f, ground, panel.width - 16f, 1f), Texture2D.whiteTexture);
            if (pitch.ZoneTop > pitch.ZoneBottom)
            {
                float left = centreX - StrikeZone.PlateWidth * 0.5f * scale;
                float width = StrikeZone.PlateWidth * scale;
                float top = ground - pitch.ZoneTop * scale;
                float height = (pitch.ZoneTop - pitch.ZoneBottom) * scale;
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(left, top, width, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(left, top + height - 2f, width, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(left, top, 2f, height), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(left + width - 2f, top, 2f, height), Texture2D.whiteTexture);
            }
            if (pitch.Call != PitchCall.None || pitch.HasPlateLocation)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                GUI.DrawTexture(ToScreen(pitch.AimLocation.x, pitch.AimLocation.y, 2f), Texture2D.whiteTexture);
            }
            if (pitch.HasPlateLocation)
            {
                GUI.color = pitch.Call switch
                {
                    PitchCall.Ball => new Color(0.3f, 0.9f, 0.4f),
                    PitchCall.CalledStrike or PitchCall.SwingingStrike => new Color(1f, 0.3f, 0.3f),
                    PitchCall.Foul => new Color(1f, 0.85f, 0.2f),
                    PitchCall.InPlay => new Color(0.3f, 0.8f, 1f),
                    _ => Color.white,
                };
                GUI.DrawTexture(ToScreen(pitch.PlateLocation.x, pitch.PlateLocation.y, 0.037f * scale), Texture2D.whiteTexture);
            }
            GUI.color = previous;
            GUI.Label(new Rect(panel.x, panel.yMax - 42f, panel.width, 40f),
                pitch.Call == PitchCall.None ? "존 (포수 시점)" : $"{pitch.Call}  존 {(pitch.InZone ? "안" : "밖")}", labelStyle);
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
