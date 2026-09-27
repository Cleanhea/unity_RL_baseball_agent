using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    public static class RunnerSceneSetup
    {
        [MenuItem("Tools/Baseball Simulation/Add Batter-Runner To Current Scene")]
        public static void AddRunner()
        {
            var director = Object.FindFirstObjectByType<PlayDirector>();
            var ball = Object.FindFirstObjectByType<BallController>();
            var batter = Object.FindFirstObjectByType<BatterController>();
            if (director == null || ball == null || batter == null)
                throw new System.InvalidOperationException("Open BaseballPlayground with a batter first.");
            var serialized = new SerializedObject(director);
            if (serialized.FindProperty("runner").objectReferenceValue != null) return;
            var root = new GameObject("BatterRunner");
            Undo.RegisterCreatedObjectUndo(root, "Add batter-runner");
            root.transform.SetParent(ball.transform.parent, false);
            root.transform.position = batter.transform.position;
            // Same shapes and materials as the batter so the hand-off at contact reads as one player.
            Material uniform = AssetDatabase.LoadAssetAtPath<Material>("Assets/BaseballSimulation/Materials/PitchingMachineAccent.mat");
            Material head = AssetDatabase.LoadAssetAtPath<Material>("Assets/BaseballSimulation/Materials/FieldBaseMarker.mat");
            var visual = new GameObject("Visual");
            visual.transform.SetParent(root.transform, false);
            BatterSceneSetup.Part("Body", visual.transform, PrimitiveType.Capsule, new Vector3(0f, 0.9f, 0f), new Vector3(0.45f, 0.65f, 0.45f), uniform);
            BatterSceneSetup.Part("Head", visual.transform, PrimitiveType.Sphere, new Vector3(0f, 1.65f, 0f), Vector3.one * 0.3f, head);
            visual.SetActive(false);
            var runner = root.AddComponent<RunnerController>();
            runner.AssignVisual(visual);
            serialized.FindProperty("runner").objectReferenceValue = runner;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(runner);
            EditorSceneManager.MarkSceneDirty(root.scene);
        }
    }
}
