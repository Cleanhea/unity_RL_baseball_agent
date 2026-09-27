using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    /// <summary>
    /// Display-only alignment of the strike-zone frame and home plate marker with the rule zone
    /// used by <see cref="StrikeZone"/>. Judging never reads these objects.
    /// </summary>
    public static class StrikeZoneSceneSetup
    {
        /// <summary>Frame drawn on the front-edge plane of the plate: plate width × configured zone height.</summary>
        public static void LayoutFrame(BaseballEnvironmentConfig config, Vector3 home, Vector3 pitcherPlate,
            out Vector3 center, out float halfWidth, out float halfHeight)
        {
            Vector3 forward = pitcherPlate - home;
            forward.y = 0f;
            forward.Normalize();
            halfWidth = StrikeZone.PlateWidth * 0.5f;
            halfHeight = 0.5f * (config.StrikeZoneTop - config.StrikeZoneBottom);
            center = home + forward * StrikeZone.PlateDepth + Vector3.up * (0.5f * (config.StrikeZoneTop + config.StrikeZoneBottom));
        }

        [MenuItem("Tools/Baseball Simulation/Align Strike Zone And Plate Visuals")]
        public static void Align()
        {
            var field = Object.FindFirstObjectByType<FieldLayout>();
            if (field == null || field.Config == null) throw new System.InvalidOperationException("Open BaseballPlayground first.");
            var serialized = new SerializedObject(field);
            var pitchTarget = serialized.FindProperty("pitchTarget").objectReferenceValue as Transform;
            var home = serialized.FindProperty("home").objectReferenceValue as Transform;
            Transform frame = pitchTarget != null ? pitchTarget.Find("StrikeZoneVisual") : null;
            if (frame == null || home == null) throw new System.InvalidOperationException("StrikeZoneVisual or Home is missing.");

            LayoutFrame(field.Config, field.HomePosition, field.PitcherPlatePosition,
                out Vector3 center, out float halfWidth, out float halfHeight);
            Vector3 travel = field.PitchTargetPosition - field.PitchOriginPosition;
            travel.y = 0f;
            Quaternion rotation = Quaternion.LookRotation(travel.normalized, Vector3.up);
            Place(frame.Find("Top"), center + rotation * new Vector3(0f, halfHeight, 0f), rotation, 0, halfWidth * 2f);
            Place(frame.Find("Bottom"), center + rotation * new Vector3(0f, -halfHeight, 0f), rotation, 0, halfWidth * 2f);
            Place(frame.Find("Left"), center + rotation * new Vector3(-halfWidth, 0f, 0f), rotation, 1, halfHeight * 2f);
            Place(frame.Find("Right"), center + rotation * new Vector3(halfWidth, 0f, 0f), rotation, 1, halfHeight * 2f);
            // The red "Center" marker stays on PitchTarget: it shows the machine's aim, not the zone centre.

            // Home is the plate's back tip (where the foul lines meet); centre the square marker over the plate.
            Transform plate = home.Find("Visual");
            if (plate != null)
            {
                Undo.RecordObject(plate, "Align plate visual");
                Vector3 forward = field.PitcherPlatePosition - field.HomePosition;
                forward.y = 0f;
                Vector3 position = field.HomePosition + forward.normalized * (StrikeZone.PlateDepth * 0.5f);
                position.y = plate.position.y;
                plate.position = position;
            }
            EditorSceneManager.MarkSceneDirty(field.gameObject.scene);
        }

        private static void Place(Transform bar, Vector3 position, Quaternion rotation, int lengthAxis, float length)
        {
            if (bar == null) return;
            Undo.RecordObject(bar, "Align strike zone visual");
            bar.SetPositionAndRotation(position, rotation);
            Vector3 scale = bar.localScale;
            scale[lengthAxis] = length;
            bar.localScale = scale;
        }
    }
}
