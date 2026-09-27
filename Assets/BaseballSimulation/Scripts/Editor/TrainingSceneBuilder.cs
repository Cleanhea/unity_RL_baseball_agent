using System;
using System.IO;
using Unity.InferenceEngine;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BaseballSimulation.Editor
{
    /// <summary>
    /// 학습 단계별 씬을 만든다(docs/training-curriculum.md). 수동 조작용 BaseballPlayground를 틀로 복사한 뒤
    /// 단계에 필요한 Agent와 TrainingEnvController를 더한다. 틀 씬에 남은 Agent는 먼저 지워 수동 조작 씬으로 되돌린다.
    /// 같은 단계를 다시 만들면 씬 에셋 GUID를 유지한 채 내용을 새로 쓴다.
    /// </summary>
    public static class TrainingSceneBuilder
    {
        public const string PlaygroundPath = "Assets/BaseballSimulation/Scenes/BaseballPlayground.unity";
        public const string TrainingFolder = "Assets/BaseballSimulation/Scenes/Training";

        public static string ScenePath(TrainingStage stage)
        {
            switch (stage)
            {
                case TrainingStage.Batter: return TrainingFolder + "/Stage1_Batter.unity";
                case TrainingStage.BatterPitcher: return TrainingFolder + "/Stage2_BatterPitcher.unity";
                default: return TrainingFolder + "/Stage3_FullTeam.unity";
            }
        }

        public const string DefenseMaterialPath = "Assets/BaseballSimulation/Materials/DefenseUniform.mat";
        /// <summary>2단계부터 고정 상대 평가 타석에서 쓰는 고정 타자 모델(1단계 학습 결과를 복사한 것).</summary>
        public const string BenchmarkBatterModelPath = "Assets/BaseballSimulation/Models/BenchmarkBatter_Stage1.onnx";

        private const string ArenaCountPrefKey = "BaseballSimulation.TrainingArenaCount";
        /// <summary>복제한 경기장 사이 X 간격(m). 펜스 반경 110 m와 관중석보다 넉넉해 공·레이가 다른 경기장에 닿지 않는다.</summary>
        public const float ArenaSpacing = 400f;
        private const string ArenaMenu = "Tools/Baseball Simulation/Training/Arena Count/";

        /// <summary>단계 씬을 만들 때 복제할 경기장 수(1~16, 기본 4). 메뉴 선택은 이 PC의 EditorPrefs에 저장한다.</summary>
        public static int ArenaCount
        {
            get => Mathf.Clamp(EditorPrefs.GetInt(ArenaCountPrefKey, 4), 1, 16);
            set => EditorPrefs.SetInt(ArenaCountPrefKey, Mathf.Clamp(value, 1, 16));
        }

        [MenuItem(ArenaMenu + "1", false, 30)] private static void ArenaCount1() => ArenaCount = 1;
        [MenuItem(ArenaMenu + "4", false, 31)] private static void ArenaCount4() => ArenaCount = 4;
        [MenuItem(ArenaMenu + "8", false, 32)] private static void ArenaCount8() => ArenaCount = 8;
        [MenuItem(ArenaMenu + "1", true)] private static bool ArenaCount1Check() { Menu.SetChecked(ArenaMenu + "1", ArenaCount == 1); return true; }
        [MenuItem(ArenaMenu + "4", true)] private static bool ArenaCount4Check() { Menu.SetChecked(ArenaMenu + "4", ArenaCount == 4); return true; }
        [MenuItem(ArenaMenu + "8", true)] private static bool ArenaCount8Check() { Menu.SetChecked(ArenaMenu + "8", ArenaCount == 8); return true; }

        [MenuItem("Tools/Baseball Simulation/Training/Build Stage 1 Scene (Batter)", false, 20)]
        public static void BuildStage1Menu() => BuildWithPrompt(TrainingStage.Batter);

        [MenuItem("Tools/Baseball Simulation/Training/Build Stage 2 Scene (Batter + Pitcher)", false, 21)]
        public static void BuildStage2Menu() => BuildWithPrompt(TrainingStage.BatterPitcher);

        [MenuItem("Tools/Baseball Simulation/Training/Build Stage 3 Scene (Full Team)", false, 22)]
        public static void BuildStage3Menu() => BuildWithPrompt(TrainingStage.FullTeam);

        /// <summary>3단계 수비수 5명의 시작 위치(지면, 홈 = 원점, +Z 중견수, +X 1루). 2루수·포수가 없어 유격수가 가운데로 치우친다.</summary>
        public static readonly (FielderRole role, string name, Vector3 spot)[] FielderLayout =
        {
            (FielderRole.FirstBase, "Fielder_1B", new Vector3(16f, 0f, 23f)),
            (FielderRole.Shortstop, "Fielder_SS", new Vector3(-8f, 0f, 33f)),
            (FielderRole.ThirdBase, "Fielder_3B", new Vector3(-17f, 0f, 22.5f)),
            (FielderRole.LeftCenter, "Fielder_LCF", new Vector3(-24f, 0f, 70f)),
            (FielderRole.RightCenter, "Fielder_RCF", new Vector3(24f, 0f, 70f)),
        };

        private static void BuildWithPrompt(TrainingStage stage)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string path = ScenePath(stage);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null && !EditorUtility.DisplayDialog(
                    "학습 씬 다시 만들기", $"{path}를 BaseballPlayground에서 경기장 {ArenaCount}개로 다시 만든다. 이 씬에 직접 바꾼 내용은 사라진다.",
                    "다시 만들기", "취소"))
                return;
            Build(stage);
        }

        /// <summary>단계 씬을 메뉴에서 고른 경기장 수로 만든다.</summary>
        public static Scene Build(TrainingStage stage) => Build(stage, ArenaCount);

        /// <summary>
        /// 단계 씬을 만들고 저장한 뒤 연 상태로 둔다. 배치 모드에서도 호출한다. 경기장 하나를 완성한 뒤
        /// <paramref name="arenaCount"/>개가 되도록 X 방향으로 <see cref="ArenaSpacing"/>씩 복제한다.
        /// </summary>
        public static Scene Build(TrainingStage stage, int arenaCount)
        {
            CleanPlayground();
            if (!AssetDatabase.IsValidFolder(TrainingFolder))
                AssetDatabase.CreateFolder("Assets/BaseballSimulation/Scenes", "Training");

            string path = ScenePath(stage);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
            {
                if (!AssetDatabase.CopyAsset(PlaygroundPath, path))
                    throw new InvalidOperationException($"Could not copy {PlaygroundPath} to {path}.");
            }
            else
            {
                File.Copy(FullPath(PlaygroundPath), FullPath(path), true);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }

            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            BatterAgentSceneSetup.AddToCurrentScene();
            PlayDirector director = UnityEngine.Object.FindAnyObjectByType<PlayDirector>();
            BatterAgent batter = UnityEngine.Object.FindAnyObjectByType<BatterAgent>();
            PitcherAgent pitcher = null;
            if (stage >= TrainingStage.BatterPitcher)
            {
                pitcher = AddPitcher(director);
                GameObject machine = GameObject.Find("PitchingMachine");
                if (machine != null) machine.SetActive(false);
            }
            RunnerAgent[] runners = null;
            FielderAgent[] fielders = null;
            if (stage >= TrainingStage.FullTeam)
            {
                fielders = AddFielders(director);
                runners = AddRunners(director);
            }
            AddController(stage, director, batter, pitcher, runners, fielders);
            DuplicateArenas(director.transform.root.gameObject, arenaCount);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Could not save {path}.");
            Debug.Log($"[TrainingSceneBuilder] {stage} 학습 씬을 경기장 {Mathf.Max(1, arenaCount)}개로 만들었다: {path}");
            return scene;
        }

        /// <summary>
        /// 완성한 경기장 루트를 복제해 경기장 수를 맞춘다. 복제본 안의 참조(Director·Agent·컨트롤러)는 Unity가 복제본끼리 다시 잇는다.
        /// 회전 없이 X로만 옮긴다(규칙 계산이 필드 축을 가정한다). 경기장 번호로 난수 시드를 나누고,
        /// 겹쳐 보이는 디버그 화면과 수동 입력은 0번 경기장에만 남긴다.
        /// </summary>
        private static void DuplicateArenas(GameObject root, int arenaCount)
        {
            for (int i = 1; i < arenaCount; i++)
            {
                GameObject copy = UnityEngine.Object.Instantiate(root, root.transform.position + Vector3.right * (ArenaSpacing * i),
                    root.transform.rotation);
                copy.name = $"{root.name} (Arena {i})";
                copy.GetComponentInChildren<TrainingEnvController>(true).SetArenaIndex(i);
                foreach (DebugPresenter presenter in copy.GetComponentsInChildren<DebugPresenter>(true)) presenter.enabled = false;
                foreach (ManualPlayController manual in copy.GetComponentsInChildren<ManualPlayController>(true)) manual.enabled = false;
            }
        }

        /// <summary>BaseballPlayground에서 학습 Agent를 지워 수동 조작 씬으로 되돌린다. 바뀐 것이 있을 때만 저장한다.</summary>
        public static void CleanPlayground()
        {
            Scene playground = EditorSceneManager.OpenScene(PlaygroundPath, OpenSceneMode.Single);
            bool changed = BatterAgentSceneSetup.RemoveFromCurrentScene();
            foreach (TrainingEnvController controller in UnityEngine.Object.FindObjectsByType<TrainingEnvController>(FindObjectsInactive.Include))
            {
                UnityEngine.Object.DestroyImmediate(controller.gameObject);
                changed = true;
            }
            if (changed && !EditorSceneManager.SaveScene(playground))
                throw new InvalidOperationException($"Could not save {PlaygroundPath}.");
        }

        private static TrainingEnvController AddController(TrainingStage stage, PlayDirector director, BatterAgent batter,
            PitcherAgent pitcher, RunnerAgent[] runners, FielderAgent[] fielders)
        {
            var controllerObject = new GameObject("TrainingEnvController");
            controllerObject.transform.SetParent(director.transform.parent, false);
            TrainingEnvController controller = controllerObject.AddComponent<TrainingEnvController>();
            controller.Assign(stage, director, batter, pitcher, runners, fielders);
            if (stage >= TrainingStage.BatterPitcher)
            {
                ModelAsset benchmark = AssetDatabase.LoadAssetAtPath<ModelAsset>(BenchmarkBatterModelPath);
                if (benchmark == null)
                    Debug.LogWarning($"[TrainingSceneBuilder] 고정 타자 모델이 없어 투수 고정 상대 평가를 쓰지 않는다: {BenchmarkBatterModelPath}");
                controller.AssignBenchmarkBatterModel(benchmark);
            }
            EditorUtility.SetDirty(controller);
            return controller;
        }

        /// <summary>
        /// 수비수 5명을 시작 위치에 세우고 PlayDirector 수비수 목록에 같은 순서로 연결한다.
        /// 다섯 명이 같은 Behavior(BaseballFielder, 팀 1)를 쓰고 역할은 관측으로 구분한다. 부품에 Collider가 없다.
        /// </summary>
        private static FielderAgent[] AddFielders(PlayDirector director)
        {
            BallController ball = UnityEngine.Object.FindAnyObjectByType<BallController>();
            Material uniform = DefenseMaterial();
            Material skin = AssetDatabase.LoadAssetAtPath<Material>("Assets/BaseballSimulation/Materials/FieldBaseMarker.mat");
            var bodies = new FielderController[FielderLayout.Length];
            var agents = new FielderAgent[FielderLayout.Length];
            for (int i = 0; i < FielderLayout.Length; i++)
            {
                var (role, name, spot) = FielderLayout[i];
                var root = new GameObject(name);
                root.transform.SetParent(ball.transform.parent, false);
                root.transform.SetPositionAndRotation(spot, Quaternion.LookRotation(new Vector3(-spot.x, 0f, -spot.z).normalized));
                BatterSceneSetup.Part("Body", root.transform, PrimitiveType.Capsule, new Vector3(0f, 0.9f, 0f), new Vector3(0.45f, 0.65f, 0.45f), uniform);
                BatterSceneSetup.Part("Head", root.transform, PrimitiveType.Sphere, new Vector3(0f, 1.65f, 0f), Vector3.one * 0.3f, skin);
                BatterSceneSetup.Part("Glove", root.transform, PrimitiveType.Sphere, new Vector3(0.3f, FielderController.GloveHeight, 0.25f), Vector3.one * 0.18f, skin);
                FielderController body = root.AddComponent<FielderController>();
                body.Configure(role, spot);
                bodies[i] = body;
                FielderAgent agent = root.AddComponent<FielderAgent>();
                agent.Assign(director, i);
                agent.MaxStep = 0;
                ConfigureBehavior(root.GetComponent<BehaviorParameters>(), "BaseballFielder", 1, FielderAgent.ObservationSize,
                    new ActionSpec(FielderAgent.ContinuousActionCount, new[] { FielderAgent.ThrowTargetCount }));
                EditorUtility.SetDirty(body);
                EditorUtility.SetDirty(agent);
                agents[i] = agent;
            }
            director.AssignFielders(bodies);
            EditorUtility.SetDirty(director);
            return agents;
        }

        /// <summary>
        /// 기존 타자주자(슬롯 0)에 더해 1·2·3루에서 타석을 시작하는 누상 주자 3명(슬롯 1~3)을 만들고 PlayDirector에 연결한다.
        /// 네 명 모두 주루 판단 Agent(BaseballRunner, 팀 0)를 붙인다. 모양은 타자주자와 같다.
        /// </summary>
        private static RunnerAgent[] AddRunners(PlayDirector director)
        {
            var serialized = new SerializedObject(director);
            var batterRunner = serialized.FindProperty("runner").objectReferenceValue as RunnerController;
            if (batterRunner == null) throw new InvalidOperationException("The playground needs the batter-runner (Add Runner menu).");
            Material uniform = AssetDatabase.LoadAssetAtPath<Material>("Assets/BaseballSimulation/Materials/PitchingMachineAccent.mat");
            Material head = AssetDatabase.LoadAssetAtPath<Material>("Assets/BaseballSimulation/Materials/FieldBaseMarker.mat");
            FieldLayout field = UnityEngine.Object.FindAnyObjectByType<FieldLayout>();
            var baseRunners = new RunnerController[3];
            var bodies = new RunnerController[4];
            bodies[0] = batterRunner;
            for (int i = 0; i < 3; i++)
            {
                var root = new GameObject($"BaseRunner_{i + 1}B");
                root.transform.SetParent(batterRunner.transform.parent, false);
                root.transform.position = field.GetBasePosition((BaseId)(i + 1));
                var visual = new GameObject("Visual");
                visual.transform.SetParent(root.transform, false);
                BatterSceneSetup.Part("Body", visual.transform, PrimitiveType.Capsule, new Vector3(0f, 0.9f, 0f), new Vector3(0.45f, 0.65f, 0.45f), uniform);
                BatterSceneSetup.Part("Head", visual.transform, PrimitiveType.Sphere, new Vector3(0f, 1.65f, 0f), Vector3.one * 0.3f, head);
                visual.SetActive(false);
                RunnerController body = root.AddComponent<RunnerController>();
                body.AssignVisual(visual);
                EditorUtility.SetDirty(body);
                baseRunners[i] = body;
                bodies[i + 1] = body;
            }
            director.AssignBaseRunners(baseRunners);
            EditorUtility.SetDirty(director);

            var agents = new RunnerAgent[4];
            for (int slot = 0; slot < 4; slot++)
            {
                RunnerAgent agent = bodies[slot].gameObject.AddComponent<RunnerAgent>();
                agent.Assign(director, slot);
                agent.MaxStep = 0;
                ConfigureBehavior(bodies[slot].GetComponent<BehaviorParameters>(), "BaseballRunner", 0, RunnerAgent.ObservationSize,
                    ActionSpec.MakeDiscrete(RunnerAgent.DecisionCount));
                EditorUtility.SetDirty(agent);
                agents[slot] = agent;
            }
            return agents;
        }

        private static void ConfigureBehavior(BehaviorParameters behavior, string name, int team, int observations, ActionSpec actions)
        {
            behavior.BehaviorName = name;
            behavior.BehaviorType = BehaviorType.Default;
            behavior.TeamId = team;
            behavior.UseChildSensors = true;
            behavior.BrainParameters.VectorObservationSize = observations;
            behavior.BrainParameters.NumStackedVectorObservations = 1;
            behavior.BrainParameters.ActionSpec = actions;
            EditorUtility.SetDirty(behavior);
        }

        /// <summary>
        /// 투수판 위에 기본 도형 우투수를 세운다. 발사 지점(PitchOrigin)이 오른손 위치가 되도록 몸을 1루 쪽으로 조금 비킨다.
        /// 부품에는 Collider가 없어 공·레이를 막지 않는다. 공격(타자)은 팀 0, 수비(투수)는 팀 1이다.
        /// </summary>
        private static PitcherAgent AddPitcher(PlayDirector director)
        {
            FieldLayout field = UnityEngine.Object.FindAnyObjectByType<FieldLayout>();
            BallController ball = UnityEngine.Object.FindAnyObjectByType<BallController>();
            Material uniform = DefenseMaterial();
            Material skin = AssetDatabase.LoadAssetAtPath<Material>("Assets/BaseballSimulation/Materials/FieldBaseMarker.mat");

            var root = new GameObject("Pitcher");
            root.transform.SetParent(ball.transform.parent, false);
            Vector3 origin = field.PitchOriginPosition;
            Vector3 toHome = field.HomePosition - field.PitcherPlatePosition;
            toHome.y = 0f;
            root.transform.SetPositionAndRotation(new Vector3(origin.x + 0.35f, field.PitcherPlatePosition.y, origin.z + 0.25f),
                Quaternion.LookRotation(toHome.normalized, Vector3.up));
            BatterSceneSetup.Part("Body", root.transform, PrimitiveType.Capsule, new Vector3(0f, 0.9f, 0f), new Vector3(0.45f, 0.65f, 0.45f), uniform);
            BatterSceneSetup.Part("Head", root.transform, PrimitiveType.Sphere, new Vector3(0f, 1.65f, 0f), Vector3.one * 0.3f, skin);
            Transform arm = BatterSceneSetup.Part("ThrowingArm", root.transform, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.1f, 0.25f, 0.1f), uniform);
            Vector3 shoulder = root.transform.TransformPoint(new Vector3(0.25f, 1.45f, 0f));
            arm.SetPositionAndRotation(0.5f * (shoulder + origin), Quaternion.FromToRotation(Vector3.up, origin - shoulder));
            arm.localScale = new Vector3(0.1f, 0.5f * Vector3.Distance(shoulder, origin), 0.1f);

            PitcherAgent agent = root.AddComponent<PitcherAgent>();
            agent.AssignDirector(director);
            agent.MaxStep = 0;
            BehaviorParameters behavior = root.GetComponent<BehaviorParameters>();
            behavior.BehaviorName = "BaseballPitcher";
            behavior.BehaviorType = BehaviorType.Default;
            behavior.TeamId = 1;
            behavior.UseChildSensors = true;
            behavior.BrainParameters.VectorObservationSize = PitcherAgent.ObservationSize;
            behavior.BrainParameters.NumStackedVectorObservations = 1;
            behavior.BrainParameters.ActionSpec = new ActionSpec(PitcherAgent.ContinuousActionCount,
                new[] { PitcherAgent.PitchTypeCount, PitcherAgent.LocationCells, PitcherAgent.LocationCells });
            EditorUtility.SetDirty(agent);
            EditorUtility.SetDirty(behavior);
            return agent;
        }

        /// <summary>수비(투수·수비수) 유니폼 재질. 없으면 공격 유니폼 재질을 복사해 파란색으로 만든다.</summary>
        internal static Material DefenseMaterial()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(DefenseMaterialPath);
            if (material != null) return material;
            Material template = AssetDatabase.LoadAssetAtPath<Material>("Assets/BaseballSimulation/Materials/PitchingMachineAccent.mat");
            material = new Material(template) { name = "DefenseUniform", color = new Color(0.16f, 0.30f, 0.72f) };
            AssetDatabase.CreateAsset(material, DefenseMaterialPath);
            return material;
        }

        private static string FullPath(string assetPath) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? string.Empty, assetPath);
    }
}
