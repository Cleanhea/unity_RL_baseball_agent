using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    public static class BatterSceneSetup
    {
        [MenuItem("Tools/Baseball Simulation/Add Batter To Current Scene")]
        public static void AddBatter()
        {
            var director = Object.FindFirstObjectByType<PlayDirector>();
            var field = Object.FindFirstObjectByType<FieldLayout>();
            var ball = Object.FindFirstObjectByType<BallController>();
            if (director == null || field == null || ball == null)
                throw new System.InvalidOperationException("Open BaseballPlayground first.");
            var serialized = new SerializedObject(director);
            if (serialized.FindProperty("batter").objectReferenceValue != null) return;
            var root = new GameObject("Batter");
            Undo.RegisterCreatedObjectUndo(root, "Add batter");
            root.transform.SetParent(ball.transform.parent, false);
            Vector3 target = field.PitchTargetPosition;
            root.transform.position = new Vector3(target.x - 1.1f, field.Config.HomePosition.y, target.z);
            Material uniform = AssetDatabase.LoadAssetAtPath<Material>("Assets/BaseballSimulation/Materials/PitchingMachineAccent.mat");
            Material batMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/BaseballSimulation/Materials/FieldBaseMarker.mat");
            Part("Body", root.transform, PrimitiveType.Capsule, new Vector3(0f, 0.9f, 0f), new Vector3(0.45f, 0.65f, 0.45f), uniform);
            Part("Head", root.transform, PrimitiveType.Sphere, new Vector3(0f, 1.65f, 0f), Vector3.one * 0.3f, batMaterial);
            Part("FrontFoot", root.transform, PrimitiveType.Cube, new Vector3(0f, 0.1f, 0.25f), new Vector3(0.3f, 0.2f, 0.22f), uniform);
            Part("BackFoot", root.transform, PrimitiveType.Cube, new Vector3(0f, 0.1f, -0.25f), new Vector3(0.3f, 0.2f, 0.22f), uniform);
            Transform bat = Part("Bat", root.transform, PrimitiveType.Cylinder, Vector3.zero, Vector3.one, batMaterial);
            var batter = root.AddComponent<BatterController>();
            batter.AssignBat(bat);
            batter.Initialize(field.Config, target);
            serialized.FindProperty("batter").objectReferenceValue = batter;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(batter);
            EditorSceneManager.MarkSceneDirty(root.scene);
        }

        internal static Transform Part(string name, Transform parent, PrimitiveType type,
            Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(type);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.GetComponent<Renderer>().sharedMaterial = material;
            return part.transform;
        }
    }
}
