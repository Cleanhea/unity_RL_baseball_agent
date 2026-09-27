using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    public static class FenceSceneSetup
    {
        private const string FenceName = "OutfieldFence";
        private const int SegmentCount = 96;

        /// <summary>
        /// 경기 경계 반경에 설정 높이의 펜스를 둘러 세운다. 공이 튕겨 나오는 실제 Collider이며,
        /// 홈런·인정 2루타 판정(<see cref="BattedBallJudge"/>)과 같은 반경·두께를 쓴다.
        /// </summary>
        [MenuItem("Tools/Baseball Simulation/Add Outfield Fence To Current Scene")]
        public static void AddFence()
        {
            var field = Object.FindFirstObjectByType<FieldLayout>();
            if (field == null || field.Config == null)
                throw new System.InvalidOperationException("Open BaseballPlayground first.");
            if (field.transform.Find(FenceName) != null) return;
            BaseballEnvironmentConfig config = field.Config;
            var root = new GameObject(FenceName);
            Undo.RegisterCreatedObjectUndo(root, "Add outfield fence");
            root.transform.SetParent(field.transform, false);
            Material material = AssetDatabase.LoadAssetAtPath<Material>("Assets/BaseballSimulation/Materials/StandWall.mat");
            float thickness = BaseballEnvironmentConfig.FenceThickness;
            float centerRadius = config.PlayBoundaryHorizontalRadius + thickness * 0.5f;
            // Slight overlap closes the gaps between flat segments on the circle.
            float chord = 2f * (config.PlayBoundaryHorizontalRadius + thickness) * Mathf.Sin(Mathf.PI / SegmentCount) + 0.05f;
            Vector3 home = field.HomePosition;
            for (int i = 0; i < SegmentCount; i++)
            {
                Quaternion rotation = Quaternion.Euler(0f, 360f / SegmentCount * i, 0f);
                GameObject segment = GameObject.CreatePrimitive(PrimitiveType.Cube);
                segment.name = $"Segment_{i:D2}";
                segment.transform.SetParent(root.transform, false);
                segment.transform.SetPositionAndRotation(
                    home + rotation * Vector3.forward * centerRadius + Vector3.up * (config.FenceHeight * 0.5f), rotation);
                segment.transform.localScale = new Vector3(chord, config.FenceHeight, thickness);
                segment.GetComponent<Renderer>().sharedMaterial = material;
            }
            EditorSceneManager.MarkSceneDirty(root.scene);
        }
    }
}
