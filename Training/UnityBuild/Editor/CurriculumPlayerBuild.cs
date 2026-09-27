using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>배치 Editor에서 각 학습 씬만 포함하는 Windows 실행 파일을 만든다.</summary>
public static class CurriculumPlayerBuild
{
    public static void Build()
    {
        string output = null;
        foreach (string arg in Environment.GetCommandLineArgs())
            if (arg.StartsWith("--curriculum-build-dir=", StringComparison.Ordinal))
                output = arg.Substring("--curriculum-build-dir=".Length);

        if (string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("--curriculum-build-dir=<absolute path> is required");

        int firstStage = 1;
        foreach (string arg in Environment.GetCommandLineArgs())
            if (arg.StartsWith("--curriculum-first-stage=", StringComparison.Ordinal))
                firstStage = int.Parse(arg.Substring("--curriculum-first-stage=".Length));

        string[] names = { "Stage1_Batter", "Stage2_BatterPitcher", "Stage3_FullTeam" };
        if (firstStage < 1 || firstStage > names.Length)
            throw new ArgumentOutOfRangeException(nameof(firstStage));

        for (int stage = firstStage; stage <= names.Length; stage++)
        {
            string name = names[stage - 1];
            string scene = $"Assets/BaseballSimulation/Scenes/Training/{name}.unity";
            if (!File.Exists(scene)) throw new FileNotFoundException("Training scene is missing", scene);
            EditorSceneManager.OpenScene(scene);
            int missingScripts = 0;
            foreach (GameObject root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                    missingScripts += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject);
            if (missingScripts != 0)
                throw new Exception($"{name} has {missingScripts} missing scene scripts; refusing to build");
            // Re-serialize against the clone's imported MonoScript database before building the player.
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            if (!EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene()))
                throw new Exception($"Could not save imported {name} scene before build");
            string exe = Path.Combine(output, name, name + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe));
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = exe,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception($"{name} build failed: {report.summary.result} ({report.summary.totalErrors} errors)");
            Debug.Log($"[CurriculumPlayerBuild] Built {name}: {exe}");
        }
    }
}
