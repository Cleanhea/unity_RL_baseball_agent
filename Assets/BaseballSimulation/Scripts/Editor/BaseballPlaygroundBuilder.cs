using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace BaseballSimulation.EditorTools
{
    /// <summary>
    /// 구현 계획 단계 1(야구장과 기본 배치)과 피칭머신(직구 중앙 통과 투구)의
    /// 씬·머티리얼·설정 에셋을 Unity Editor API로 만든다.
    ///
    /// 씬 YAML을 직접 편집하지 않고 Editor가 생성하도록 해서 참조와 GUID를 보호한다
    /// (AGENTS.md "Unity 에셋 안전"). 같은 메뉴를 다시 실행하면 씬을 규격대로 다시 만든다.
    /// 설정 에셋과 머티리얼은 이미 있으면 값을 덮어쓰지 않고 재사용한다.
    /// </summary>
    public static class BaseballPlaygroundBuilder
    {
        private const string RootFolder = "Assets/BaseballSimulation";
        private const string ScenesFolder = RootFolder + "/Scenes";
        private const string MaterialsFolder = RootFolder + "/Materials";
        private const string ConfigFolder = RootFolder + "/Config";

        private const string ScenePath = ScenesFolder + "/BaseballPlayground.unity";
        private const string ConfigPath = ConfigFolder + "/DefaultBaseballEnvironment.asset";
        private const string VolumeProfilePath = "Assets/Settings/SampleSceneProfile.asset";

        // 지면 위 장식 판의 높이 층. 아래로 갈수록 위에 겹친다.
        // 최상단이 0.044 m라 공 반지름(0.037 m)과 비슷한 수준으로 얇다.
        private const float FairTerritoryY = 0.005f;
        private const float FoulTerritoryY = 0.010f;
        private const float InfieldDirtY = 0.016f;
        private const float InfieldGrassY = 0.021f;
        private const float InfieldCircleY = 0.026f;
        private const float FoulLineY = 0.032f;
        private const float BaseVisualY = 0.038f;
        private const float SlabThickness = 0.01f;
        private const float BaseVisualThickness = 0.012f;

        private const float FoulLineWidth = 0.15f;
        private const float BaseVisualSize = 0.38f;
        private const float HomeVisualSize = 0.43f;
        private const float PitcherPlateWidth = 0.61f;
        private const float PitcherPlateDepth = 0.15f;

        // 내야 흙 위에 얹는 안쪽 잔디가 남기는 베이스 패스 폭.
        private const float BasePathWidth = 3.2f;
        private const float PitcherMoundRadius = 2.74f;
        private const float HomeCircleRadius = 3.96f;

        private const int BoundarySegmentCount = 32;
        private const float BoundaryCurbHeight = 0.5f;
        private const float BoundaryCurbThickness = 0.4f;

        // 관중석. 경기 경계 밖에만 두므로 플레이 판정과 겹치지 않는다.
        private const float StandGapFromBoundary = 4f;
        private const int StandTierCount = 4;
        private const float StandTierDepth = 8f;
        private const float StandTierRise = 3f;

        // 피칭머신 외형. 표시 전용이라 모든 부품에서 Collider를 제거해 공 발사를 방해하지 않는다.
        private const float MachineBodyLength = 0.65f;
        private const float MachineBodyRadius = 0.20f;
        private const float MachineMuzzleLength = 0.10f;
        private const float MachineMuzzleRadius = 0.24f;
        private const float MachineStandWidth = 0.5f;

        // 스트라이크존 표시(테두리 + 중앙). 실제 판정에는 쓰지 않는다.
        private const float StrikeZoneBarThickness = 0.03f;

        // 카메라: 홈 뒤쪽의 높은 3인칭 고정 시점(docs/environment-spec.md 2.3).
        private static readonly Vector3 CameraOffsetFromHome = new Vector3(0f, 12f, -20f);
        private static readonly Vector3 CameraEulerAngles = new Vector3(22f, 0f, 0f);

        [MenuItem("Tools/Baseball Simulation/Build Playground Scene", false, 0)]
        public static void BuildPlaygroundSceneMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            bool sceneExists = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null;
            if (sceneExists && !EditorUtility.DisplayDialog(
                    "BaseballPlayground 다시 만들기",
                    $"{ScenePath} 를 규격대로 다시 만든다.\n" +
                    "씬에 직접 추가한 오브젝트는 사라진다. 계속할까?",
                    "다시 만들기",
                    "취소"))
            {
                return;
            }

            Build();
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("Tools/Baseball Simulation/Validate Playground Scene", false, 1)]
        public static void ValidatePlaygroundSceneMenu()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    return;
                }

                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            Debug.Log(BuildValidationReport(scene));
        }

        /// <summary>
        /// 배치 모드(-executeMethod)에서 호출하는 진입점이다.
        /// </summary>
        public static void BuildFromCommandLine()
        {
            Build();

            Debug.Log(BuildValidationReport(SceneManager.GetActiveScene()));
            Debug.Log("BASEBALL_BUILD_RESULT: OK");
        }

        /// <summary>
        /// 폴더, 머티리얼, 설정 에셋, 씬을 만들고 저장한다.
        /// </summary>
        public static void Build()
        {
            EnsureFolders();

            BaseballEnvironmentConfig config = LoadOrCreateConfig();
            Materials materials = LoadOrCreateMaterials();

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateEnvironment(config, materials);
            CreateCamera(config);
            CreateDirectionalLight();
            CreateGlobalVolume();

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError($"[BaseballPlaygroundBuilder] 씬 저장 실패: {ScenePath}");
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BaseballPlaygroundBuilder] 씬을 만들었다: {ScenePath}");
        }

        private static void EnsureFolders()
        {
            EnsureFolder("Assets", "BaseballSimulation");
            EnsureFolder(RootFolder, "Scenes");
            EnsureFolder(RootFolder, "Materials");
            EnsureFolder(RootFolder, "Config");
            // Prefabs 폴더는 반복 생성·참조할 이유가 생긴 객체를 승격할 때 만든다
            // (docs/architecture.md 2장). 지금은 빈 폴더를 만들지 않는다.
        }

        private static void EnsureFolder(string parent, string child)
        {
            string path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }

        private static BaseballEnvironmentConfig LoadOrCreateConfig()
        {
            BaseballEnvironmentConfig config = LoadConfigWithRetries();
            if (config != null)
            {
                return config;
            }

            // 여기서 AssetDatabase.SaveAssets()/Refresh()를 바로 부르지 않는다. 씬의 어떤
            // 오브젝트도 아직 이 에셋을 참조하지 않은 시점에 그런 호출을 하면 Unity가
            // "미사용 에셋"으로 보고 언로드해, 이후 씬에 넣는 참조가 끊긴(파괴된) 네이티브
            // 오브젝트를 가리키게 된다(Transform 참조는 씬 오브젝트라 이 문제가 없다). 저장은
            // 씬 전체를 만들고 참조까지 다 연결한 뒤 Build()의 마지막 한 곳에서만 한다.
            config = ScriptableObject.CreateInstance<BaseballEnvironmentConfig>();
            AssetDatabase.CreateAsset(config, ConfigPath);
            Debug.Log($"[BaseballPlaygroundBuilder] 설정 에셋을 만들었다: {ConfigPath}");
            return config;
        }

        /// <summary>
        /// 배치 모드 시작 직후나 스크립트를 막 재컴파일한 직후에는 Unity가 이 커스텀
        /// ScriptableObject 타입을 "No script asset for X" 내부 경고와 함께 잠시 인식하지
        /// 못해 <c>AssetDatabase.LoadAssetAtPath</c>가 존재하는 에셋도 null로 돌려줄 수 있다.
        /// 단순 재시도(같은 프레임)만으로는 해소되지 않아, 실제 시간을 흘려보내며 몇 차례
        /// 다시 시도한다.
        /// </summary>
        private static BaseballEnvironmentConfig LoadConfigWithRetries()
        {
            const int maxAttempts = 20;
            const int delayMilliseconds = 250;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var config = AssetDatabase.LoadAssetAtPath<BaseballEnvironmentConfig>(ConfigPath);
                if (config != null)
                {
                    if (attempt > 0)
                    {
                        Debug.Log(
                            $"[BaseballPlaygroundBuilder] 설정 에셋을 {attempt + 1}번째 시도에서 불러왔다.");
                    }

                    return config;
                }

                System.Threading.Thread.Sleep(delayMilliseconds);
            }

            return null;
        }

        private struct Materials
        {
            public Material Ground;
            public Material FairTurf;
            public Material InfieldDirt;
            public Material FoulLine;
            public Material BaseMarker;
            public Material Boundary;
            public Material StandSeating;
            public Material StandWall;
            public Material Ball;
            public Material MachineBody;
            public Material MachineAccent;
            public Material StrikeZoneFrame;
            public Material StrikeZoneCenter;
        }

        private static Materials LoadOrCreateMaterials()
        {
            return new Materials
            {
                Ground = LoadOrCreateMaterial("FieldGround", new Color(0.16f, 0.19f, 0.16f)),
                FairTurf = LoadOrCreateMaterial("FieldFairTurf", new Color(0.20f, 0.45f, 0.22f)),
                InfieldDirt = LoadOrCreateMaterial("FieldInfieldDirt", new Color(0.55f, 0.39f, 0.25f)),
                FoulLine = LoadOrCreateMaterial("FieldFoulLine", new Color(0.94f, 0.94f, 0.94f)),
                BaseMarker = LoadOrCreateMaterial("FieldBaseMarker", new Color(0.96f, 0.96f, 0.92f)),
                Boundary = LoadOrCreateMaterial("FieldPlayBoundary", new Color(0.12f, 0.34f, 0.42f)),
                StandSeating = LoadOrCreateMaterial("StandSeating", new Color(0.20f, 0.24f, 0.32f)),
                StandWall = LoadOrCreateMaterial("StandWall", new Color(0.58f, 0.57f, 0.54f)),
                Ball = LoadOrCreateMaterial("Ball", new Color(0.92f, 0.92f, 0.86f)),
                MachineBody = LoadOrCreateMaterial("PitchingMachineBody", new Color(0.24f, 0.25f, 0.28f)),
                MachineAccent = LoadOrCreateMaterial("PitchingMachineAccent", new Color(0.78f, 0.22f, 0.14f)),
                StrikeZoneFrame = LoadOrCreateMaterial("StrikeZoneFrame", new Color(1f, 0.85f, 0.15f)),
                StrikeZoneCenter = LoadOrCreateMaterial("StrikeZoneCenter", new Color(1f, 0.15f, 0.15f)),
            };
        }

        private static Material LoadOrCreateMaterial(string name, Color baseColor)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                return existing;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError(
                    "[BaseballPlaygroundBuilder] URP Lit 셰이더를 찾지 못했다. " +
                    "URP가 설치된 프로젝트에서 실행해야 한다.");
                shader = Shader.Find("Standard");
            }

            var material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", baseColor);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", baseColor);
            }

            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", 0.05f);
            }

            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static void CreateEnvironment(BaseballEnvironmentConfig config, Materials materials)
        {
            var environmentRoot = new GameObject("BaseballEnvironment");
            environmentRoot.transform.position = Vector3.zero;

            var field = new GameObject("Field");
            field.transform.SetParent(environmentRoot.transform, false);

            Vector3 home = config.HomePosition;
            float boundaryRadius = config.PlayBoundaryHorizontalRadius;

            CreateGround(field.transform, home, boundaryRadius, materials.Ground);

            // 경기 영역 전체를 페어 색 원판으로 깔고, 페어가 아닌 세 방향을 지면 색 판으로 덮는다.
            // 결과로 남는 초록은 홈에서 90도로 열린 페어 영역을 원형 경계까지 정확히 따른다.
            CreateDisc(
                "FairTerritory", field.transform, home, boundaryRadius,
                home.y + FairTerritoryY, materials.FairTurf);

            var foulTerritory = new GameObject("FoulTerritory");
            foulTerritory.transform.SetParent(field.transform, false);
            float foulY = home.y + FoulTerritoryY;
            CreateCornerDiamond(
                "FoulWedgeFirstSide", foulTerritory.transform, home, Vector3.right,
                boundaryRadius, foulY, materials.Ground);
            CreateCornerDiamond(
                "FoulWedgeThirdSide", foulTerritory.transform, home, Vector3.left,
                boundaryRadius, foulY, materials.Ground);
            CreateCornerDiamond(
                "FoulWedgeBehindHome", foulTerritory.transform, home, Vector3.back,
                boundaryRadius, foulY, materials.Ground);

            CreateInfield(field.transform, config, materials);

            CreateFoulVisuals(field.transform, home, boundaryRadius, materials.FoulLine);

            Transform homeMarker = CreateBaseMarker(
                "Home", field.transform, home, HomeVisualSize, materials.BaseMarker);
            Transform firstMarker = CreateBaseMarker(
                "First", field.transform, config.FirstBasePosition, BaseVisualSize, materials.BaseMarker);
            Transform secondMarker = CreateBaseMarker(
                "Second", field.transform, config.SecondBasePosition, BaseVisualSize, materials.BaseMarker);
            Transform thirdMarker = CreateBaseMarker(
                "Third", field.transform, config.ThirdBasePosition, BaseVisualSize, materials.BaseMarker);

            Transform pitcherPlate = CreatePitcherPlate(
                field.transform, config.PitcherPlatePosition, materials.BaseMarker);

            Transform pitchOrigin = CreatePoint("PitchOrigin", field.transform, config.PitchOriginPosition);
            Transform pitchTarget = CreatePoint("PitchTarget", field.transform, config.PitchTargetPosition);

            // 피칭머신은 발사구가 PitchOrigin과 겹치도록 두고, 표시용 부품은 모두
            // Collider를 없애 공 발사를 방해하지 않는다(docs/environment-spec.md 2.2~2.3).
            CreatePitchingMachine(
                field.transform, config.PitchOriginPosition, config.PitchTargetPosition,
                materials.MachineBody, materials.MachineAccent);

            // 스트라이크존 표시는 PitchTarget 자식으로 둬 목표 위치와 항상 같이 움직인다.
            CreateStrikeZoneVisual(
                pitchTarget, config.PitchOriginPosition, config.PitchTargetPosition,
                config.StrikeZoneHalfWidth, config.StrikeZoneHalfHeight,
                materials.StrikeZoneFrame, materials.StrikeZoneCenter);

            CreatePlayBoundaryVisual(field.transform, home, boundaryRadius, materials.Boundary);

            // 관중석은 경기 영역 밖의 표시 전용 구조물이라 Field가 아닌 형제로 둔다.
            CreateStands(environmentRoot.transform, home, boundaryRadius, materials);

            var fieldLayout = field.AddComponent<FieldLayout>();
            // config는 SerializedObject가 아니라 직접 대입한다(FieldLayout.AssignConfig 문서 참고).
            fieldLayout.AssignConfig(config);
            var serialized = new SerializedObject(fieldLayout);
            serialized.FindProperty("home").objectReferenceValue = homeMarker;
            serialized.FindProperty("firstBase").objectReferenceValue = firstMarker;
            serialized.FindProperty("secondBase").objectReferenceValue = secondMarker;
            serialized.FindProperty("thirdBase").objectReferenceValue = thirdMarker;
            serialized.FindProperty("pitcherPlate").objectReferenceValue = pitcherPlate;
            serialized.FindProperty("pitchOrigin").objectReferenceValue = pitchOrigin;
            serialized.FindProperty("pitchTarget").objectReferenceValue = pitchTarget;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            if (fieldLayout.Config == null)
            {
                throw new System.InvalidOperationException(
                    "[BaseballPlaygroundBuilder] FieldLayout에 설정 데이터를 연결하지 못했다. 설정 에셋 참조가 " +
                    "무효했을 수 있다(LoadOrCreateConfig 참고. 특히 배치 모드에서는 스크립트를 재컴파일한 직후 " +
                    "AssetDatabase가 커스텀 ScriptableObject 타입을 잠깐 인식하지 못할 수 있다). 씬을 저장하지 " +
                    "않고 중단한다. 대화형 Editor에서 이 메뉴를 다시 실행하면 보통 해결된다.");
            }

            var actors = new GameObject("Actors");
            actors.transform.SetParent(environmentRoot.transform, false);
            BallController ball = CreateBall(actors.transform, config, config.PitchOriginPosition, materials.Ball);

            var systems = new GameObject("Systems");
            systems.transform.SetParent(environmentRoot.transform, false);
            CreateSystems(systems.transform, fieldLayout, ball);
            BaseballSimulation.Editor.BatterSceneSetup.AddBatter();
        }

        private static BallController CreateBall(
            Transform parent, BaseballEnvironmentConfig config, Vector3 position, Material material)
        {
            GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = "Ball";
            ball.transform.SetParent(parent, false);
            ball.transform.SetPositionAndRotation(position, Quaternion.identity);
            // 구형 프리미티브는 반지름 0.5(지름 1)다. 지름만큼 스케일하면 SphereCollider의
            // 유효 반지름도 함께 config.BallRadius와 일치한다.
            ball.transform.localScale = Vector3.one * (config.BallRadius * 2f);
            ball.GetComponent<MeshRenderer>().sharedMaterial = material;

            ball.AddComponent<Rigidbody>();
            var ballController = ball.AddComponent<BallController>();
            // config는 SerializedObject가 아니라 직접 대입한다(FieldLayout.AssignConfig 문서 참고).
            ballController.AssignConfig(config);

            return ballController;
        }

        private static void CreateSystems(Transform parent, FieldLayout fieldLayout, BallController ball)
        {
            var directorObject = new GameObject("PlayDirector");
            directorObject.transform.SetParent(parent, false);
            var playDirector = directorObject.AddComponent<PlayDirector>();
            var directorSerialized = new SerializedObject(playDirector);
            directorSerialized.FindProperty("fieldLayout").objectReferenceValue = fieldLayout;
            directorSerialized.FindProperty("ball").objectReferenceValue = ball;
            directorSerialized.ApplyModifiedPropertiesWithoutUndo();

            var manualControllerObject = new GameObject("ManualPlayController");
            manualControllerObject.transform.SetParent(parent, false);
            var manualController = manualControllerObject.AddComponent<ManualPlayController>();
            var manualSerialized = new SerializedObject(manualController);
            manualSerialized.FindProperty("playDirector").objectReferenceValue = playDirector;
            manualSerialized.ApplyModifiedPropertiesWithoutUndo();

            var debugPresenterObject = new GameObject("DebugPresenter");
            debugPresenterObject.transform.SetParent(parent, false);
            var debugPresenter = debugPresenterObject.AddComponent<DebugPresenter>();
            var presenterSerialized = new SerializedObject(debugPresenter);
            presenterSerialized.FindProperty("playDirector").objectReferenceValue = playDirector;
            presenterSerialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// 받침대(Cube) + 본체(Cylinder) + 발사구(Cylinder)로 피칭머신을 표현한다.
        /// 발사구 앞면이 <paramref name="origin"/>과 정확히 겹치고, 본체는 홈 반대 방향으로
        /// 뻗어 있어 공이 날아가는 방향(<paramref name="origin"/> → <paramref name="target"/>)을
        /// 가리지 않는다. 모든 부품은 Collider가 없는 표시 전용이다.
        /// </summary>
        private static void CreatePitchingMachine(
            Transform parent, Vector3 origin, Vector3 target, Material bodyMaterial, Material accentMaterial)
        {
            var machine = new GameObject("PitchingMachine");
            machine.transform.SetParent(parent, false);
            machine.transform.position = origin;

            Vector3 away = origin - target;
            away.y = 0f;
            away = away.sqrMagnitude > 0.0001f ? away.normalized : Vector3.forward;

            Quaternion barrelRotation = Quaternion.FromToRotation(Vector3.up, away);

            Vector3 bodyCenter = origin + (away * (MachineBodyLength * 0.5f));
            CreateCylinder(
                "Body", machine.transform, bodyCenter, barrelRotation,
                MachineBodyRadius, MachineBodyLength, bodyMaterial);

            // 발사구 입구는 공 발사 위치와 정확히 일치시킨다.
            CreateCylinder(
                "Muzzle", machine.transform, origin, barrelRotation,
                MachineMuzzleRadius, MachineMuzzleLength, accentMaterial);

            float standTopY = Mathf.Max(origin.y - MachineBodyRadius, 0.1f);
            Vector3 standCenter = new Vector3(bodyCenter.x, standTopY * 0.5f, bodyCenter.z);
            CreateBox(
                "Stand", machine.transform, standCenter, Quaternion.identity,
                new Vector3(MachineStandWidth, standTopY, MachineStandWidth), bodyMaterial);
        }

        /// <summary>
        /// PitchTarget을 중심으로 스트라이크존 테두리(상하좌우 4개 막대)와 중앙 표시를 만든다.
        /// 표시 전용이며 Collider가 없어 공과 충돌하지 않는다(docs/environment-spec.md 2.2).
        /// </summary>
        private static void CreateStrikeZoneVisual(
            Transform pitchTarget, Vector3 origin, Vector3 target,
            float halfWidth, float halfHeight, Material frameMaterial, Material centerMaterial)
        {
            var group = new GameObject("StrikeZoneVisual");
            group.transform.SetParent(pitchTarget, false);
            group.transform.position = target;

            Vector3 travel = target - origin;
            travel.y = 0f;
            Quaternion rotation = travel.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(travel.normalized, Vector3.up)
                : Quaternion.identity;

            float width = halfWidth * 2f;
            float height = halfHeight * 2f;
            float t = StrikeZoneBarThickness;

            CreateBox(
                "Top", group.transform, target + (rotation * new Vector3(0f, halfHeight, 0f)), rotation,
                new Vector3(width, t, t), frameMaterial);
            CreateBox(
                "Bottom", group.transform, target + (rotation * new Vector3(0f, -halfHeight, 0f)), rotation,
                new Vector3(width, t, t), frameMaterial);
            CreateBox(
                "Left", group.transform, target + (rotation * new Vector3(-halfWidth, 0f, 0f)), rotation,
                new Vector3(t, height, t), frameMaterial);
            CreateBox(
                "Right", group.transform, target + (rotation * new Vector3(halfWidth, 0f, 0f)), rotation,
                new Vector3(t, height, t), frameMaterial);
            CreateBox(
                "Center", group.transform, target, rotation,
                new Vector3(t * 3f, t * 3f, t), centerMaterial);
        }

        private static void CreateGround(
            Transform parent, Vector3 home, float boundaryRadius, Material material)
        {
            // Plane 프리미티브는 한 변이 10 units다. 경계 지름보다 넉넉하게 덮는다.
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(parent, false);
            ground.transform.position = home;
            // 파울 영역 판이 경계 밖으로 나가도 지면 위에 남도록 넉넉히 잡는다.
            float scale = boundaryRadius * 0.30f;
            ground.transform.localScale = new Vector3(scale, 1f, scale);
            ground.GetComponent<MeshRenderer>().sharedMaterial = material;
            // 지면 MeshCollider는 남긴다. 2단계 공 물리의 지면 충돌면이다.
        }

        private static void CreateInfield(
            Transform parent, BaseballEnvironmentConfig config, Materials materials)
        {
            var infield = new GameObject("Infield");
            infield.transform.SetParent(parent, false);

            Vector3 home = config.HomePosition;
            float baseDistance = Vector3.Distance(home, config.FirstBasePosition);

            // 흙 다이아몬드의 네 꼭짓점이 홈과 세 베이스에 놓인다.
            Vector3 diamondCenter = (config.FirstBasePosition + config.ThirdBasePosition) * 0.5f;
            CreateCenteredDiamond(
                "InfieldDirt", infield.transform, diamondCenter, baseDistance,
                home.y + InfieldDirtY, materials.InfieldDirt);

            // 안쪽 잔디를 얹어 베이스 패스만 흙으로 남긴다.
            float grassSide = baseDistance - (BasePathWidth * 2f);
            if (grassSide > 0f)
            {
                CreateCenteredDiamond(
                    "InfieldGrass", infield.transform, diamondCenter, grassSide,
                    home.y + InfieldGrassY, materials.FairTurf);
            }

            CreateDisc(
                "PitcherMound", infield.transform, config.PitcherPlatePosition, PitcherMoundRadius,
                home.y + InfieldCircleY, materials.InfieldDirt);
            CreateDisc(
                "HomeCircle", infield.transform, home, HomeCircleRadius,
                home.y + InfieldCircleY, materials.InfieldDirt);
        }

        /// <summary>
        /// yaw 45°로 눕힌 정사각형 판. 네 꼭짓점이 ±X, ±Z를 향하므로
        /// 두 변이 파울선과 나란하다.
        /// </summary>
        private static void CreateCenteredDiamond(
            string name, Transform parent, Vector3 center, float side, float y, Material material)
        {
            CreateBox(
                name,
                parent,
                new Vector3(center.x, y, center.z),
                Quaternion.Euler(0f, 45f, 0f),
                new Vector3(side, SlabThickness, side),
                material);
        }

        /// <summary>
        /// 한 꼭짓점을 <paramref name="corner"/>에 두고 <paramref name="outward"/> 방향
        /// (±X 또는 ±Z)으로 펼친 정사각형 판. 홈을 꼭짓점으로 하면 정확히 90도 영역을 덮는다.
        /// </summary>
        private static void CreateCornerDiamond(
            string name, Transform parent, Vector3 corner, Vector3 outward,
            float side, float y, Material material)
        {
            Vector3 center = corner + (outward * (side * Mathf.Sqrt(2f) * 0.5f));
            CreateCenteredDiamond(name, parent, center, side, y, material);
        }

        private static void CreateDisc(
            string name, Transform parent, Vector3 center, float radius, float y, Material material)
        {
            GameObject disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = name;

            Collider collider = disc.GetComponent<Collider>();
            if (collider != null)
            {
                Object.DestroyImmediate(collider);
            }

            disc.transform.SetParent(parent, false);
            disc.transform.SetPositionAndRotation(
                new Vector3(center.x, y, center.z), Quaternion.identity);
            // Cylinder 프리미티브는 지름 1, 높이 2다.
            disc.transform.localScale = new Vector3(radius * 2f, SlabThickness * 0.5f, radius * 2f);
            disc.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static void CreateFoulVisuals(
            Transform parent, Vector3 home, float length, Material material)
        {
            var group = new GameObject("FoulVisuals");
            group.transform.SetParent(parent, false);

            CreateFoulLine(group.transform, "FoulLineFirst", home, 45f, length, material);
            CreateFoulLine(group.transform, "FoulLineThird", home, -45f, length, material);
        }

        private static void CreateFoulLine(
            Transform parent, string name, Vector3 home, float yaw, float length, Material material)
        {
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 direction = rotation * Vector3.forward;
            Vector3 center = home + (direction * (length * 0.5f));
            center.y = home.y + FoulLineY;

            CreateBox(
                name, parent, center, rotation,
                new Vector3(FoulLineWidth, SlabThickness, length), material);
        }

        private static Transform CreateBaseMarker(
            string name, Transform parent, Vector3 position, float size, Material material)
        {
            Transform marker = CreatePoint(name, parent, position);
            CreateBox(
                "Visual", marker, position + new Vector3(0f, BaseVisualY, 0f), Quaternion.identity,
                new Vector3(size, BaseVisualThickness, size), material);
            return marker;
        }

        private static Transform CreatePitcherPlate(
            Transform parent, Vector3 position, Material material)
        {
            Transform marker = CreatePoint("PitcherPlate", parent, position);
            CreateBox(
                "Visual", marker, position + new Vector3(0f, BaseVisualY, 0f), Quaternion.identity,
                new Vector3(PitcherPlateWidth, BaseVisualThickness, PitcherPlateDepth), material);
            return marker;
        }

        private static void CreatePlayBoundaryVisual(
            Transform parent, Vector3 home, float radius, Material material)
        {
            var group = new GameObject("PlayBoundary");
            group.transform.SetParent(parent, false);
            group.transform.position = home;

            float chord = 2f * radius * Mathf.Sin(Mathf.PI / BoundarySegmentCount);
            for (int i = 0; i < BoundarySegmentCount; i++)
            {
                float angleDegrees = (360f / BoundarySegmentCount) * i;
                var rotation = Quaternion.Euler(0f, angleDegrees, 0f);
                Vector3 outward = rotation * Vector3.forward;
                Vector3 center = home + (outward * radius);
                center.y = home.y + (BoundaryCurbHeight * 0.5f);

                CreateBox(
                    $"BoundarySegment_{i:D2}",
                    group.transform,
                    center,
                    // 세그먼트가 원의 접선 방향을 향하도록 90도 돌린다.
                    rotation * Quaternion.Euler(0f, 90f, 0f),
                    new Vector3(BoundaryCurbThickness, BoundaryCurbHeight, chord),
                    material);
            }
        }

        /// <summary>
        /// 경기 경계 바깥을 둘러싸는 계단식 관중석을 만든다.
        ///
        /// 표시 전용이라 Collider가 없고, 안쪽 끝이 경기 경계보다 멀리 있어
        /// 공·주자·수비 판정 어디에도 관여하지 않는다. 단은 바깥으로 갈수록 높아져
        /// 안쪽에서 보면 아래 단에 가려지므로, 각 단을 지면부터 올라오는 통 블록으로 둔다.
        /// </summary>
        private static void CreateStands(
            Transform parent, Vector3 home, float boundaryRadius, Materials materials)
        {
            var stands = new GameObject("Stands");
            stands.transform.SetParent(parent, false);
            stands.transform.position = home;

            float firstTierInner = boundaryRadius + StandGapFromBoundary;

            for (int tier = 0; tier < StandTierCount; tier++)
            {
                float innerRadius = firstTierInner + (tier * StandTierDepth);
                float outerRadius = innerRadius + StandTierDepth;
                float height = (tier + 1) * StandTierRise;

                // 가장 바깥 단은 뒤를 막는 외벽 역할이라 색을 구분한다.
                Material material = tier == StandTierCount - 1
                    ? materials.StandWall
                    : materials.StandSeating;

                var tierGroup = new GameObject($"Tier_{tier:D2}");
                tierGroup.transform.SetParent(stands.transform, false);
                tierGroup.transform.position = home;

                // 이웃 조각이 벌어지지 않도록 바깥 반지름 기준 현 길이를 쓴다.
                // 안쪽에서는 조금씩 겹치지만 같은 재질이라 표시에 문제가 없다.
                float chord = 2f * outerRadius * Mathf.Sin(Mathf.PI / BoundarySegmentCount);
                float midRadius = (innerRadius + outerRadius) * 0.5f;

                for (int i = 0; i < BoundarySegmentCount; i++)
                {
                    var rotation = Quaternion.Euler(0f, (360f / BoundarySegmentCount) * i, 0f);
                    Vector3 outward = rotation * Vector3.forward;
                    Vector3 center = home + (outward * midRadius);
                    center.y = home.y + (height * 0.5f);

                    CreateBox(
                        $"Section_{i:D2}",
                        tierGroup.transform,
                        center,
                        rotation * Quaternion.Euler(0f, 90f, 0f),
                        new Vector3(StandTierDepth, height, chord),
                        material);
                }
            }
        }

        private static Transform CreatePoint(string name, Transform parent, Vector3 position)
        {
            var point = new GameObject(name);
            point.transform.SetParent(parent, false);
            point.transform.position = position;
            point.transform.rotation = Quaternion.identity;
            return point.transform;
        }

        private static GameObject CreateBox(
            string name,
            Transform parent,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            Material material)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;

            // 표시용 판은 물리에 개입하지 않는다. 지면 충돌은 Ground의 MeshCollider가 담당한다.
            Collider collider = box.GetComponent<Collider>();
            if (collider != null)
            {
                Object.DestroyImmediate(collider);
            }

            box.transform.SetParent(parent, false);
            box.transform.SetPositionAndRotation(position, rotation);
            box.transform.localScale = scale;
            box.GetComponent<MeshRenderer>().sharedMaterial = material;
            return box;
        }

        /// <summary>
        /// 반지름 <paramref name="radius"/>, 길이 <paramref name="length"/>인 Cylinder를 만든다.
        /// 기본 Cylinder 프리미티브는 반지름 0.5, 높이 2(로컬 Y축)라 스케일로 크기를 맞춘다.
        /// 표시용 부품이라 Collider는 제거한다.
        /// </summary>
        private static GameObject CreateCylinder(
            string name, Transform parent, Vector3 position, Quaternion rotation,
            float radius, float length, Material material)
        {
            GameObject cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylinder.name = name;

            Collider collider = cylinder.GetComponent<Collider>();
            if (collider != null)
            {
                Object.DestroyImmediate(collider);
            }

            cylinder.transform.SetParent(parent, false);
            cylinder.transform.SetPositionAndRotation(position, rotation);
            cylinder.transform.localScale = new Vector3(radius * 2f, length * 0.5f, radius * 2f);
            cylinder.GetComponent<MeshRenderer>().sharedMaterial = material;
            return cylinder;
        }

        private static void CreateCamera(BaseballEnvironmentConfig config)
        {
            var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = new Color(0.19215687f, 0.3019608f, 0.4745098f, 0f);
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 500f;

            cameraObject.AddComponent<AudioListener>();
            cameraObject.AddComponent<UniversalAdditionalCameraData>();

            cameraObject.transform.SetPositionAndRotation(
                config.HomePosition + CameraOffsetFromHome,
                Quaternion.Euler(CameraEulerAngles));
        }

        private static void CreateDirectionalLight()
        {
            // SampleScene의 Directional Light 구성을 출발점으로 삼는다.
            var lightObject = new GameObject("Directional Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 2f;
            light.shadows = LightShadows.Soft;
            lightObject.AddComponent<UniversalAdditionalLightData>();
            lightObject.transform.SetPositionAndRotation(
                new Vector3(0f, 3f, 0f), Quaternion.Euler(50f, -30f, 0f));
        }

        private static void CreateGlobalVolume()
        {
            var volumeObject = new GameObject("Global Volume");
            Volume volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumeProfilePath);
            if (profile == null)
            {
                Debug.LogWarning(
                    $"[BaseballPlaygroundBuilder] 볼륨 프로파일을 찾지 못했다: {VolumeProfilePath}");
            }
            else
            {
                volume.sharedProfile = profile;
            }
        }

        private static string BuildValidationReport(Scene scene)
        {
            var lines = new List<string> { $"[BaseballPlaygroundBuilder] 검증 보고: {scene.path}" };

            FieldLayout layout = FindInScene<FieldLayout>(scene);
            if (layout == null)
            {
                lines.Add("실패: 씬에서 FieldLayout을 찾지 못했다.");
                return string.Join("\n", lines);
            }

            lines.Add(layout.TryValidate(out string error)
                ? "참조 검증: 통과"
                : $"참조 검증: 실패\n{error}");

            lines.Add($"설정 에셋: {AssetDatabase.GetAssetPath(layout.Config)}");
            lines.Add($"홈 위치: {layout.HomePosition}");
            lines.Add($"1루 위치: {layout.FirstBasePosition}");
            lines.Add($"2루 위치: {layout.SecondBasePosition}");
            lines.Add($"3루 위치: {layout.ThirdBasePosition}");
            lines.Add($"투수판 위치: {layout.PitcherPlatePosition}");
            lines.Add($"투구 시작점: {layout.PitchOriginPosition}");
            lines.Add($"투구 목표점: {layout.PitchTargetPosition}");
            lines.Add($"홈-1루 거리: {layout.HomeToFirstDistance:F4} m (기대 27.4358 m)");
            lines.Add($"홈-3루 거리: {layout.HomeToThirdDistance:F4} m (기대 27.4358 m)");
            lines.Add($"홈-2루 거리: {layout.HomeToSecondDistance:F4} m");
            lines.Add($"홈-투수판 거리: {layout.HomeToPitcherPlateDistance:F4} m (기대 18.44 m)");
            lines.Add($"페어 판정 (0,0,10): {layout.IsFairGroundPoint(new Vector3(0f, 0f, 10f))} (기대 True)");
            lines.Add($"페어 판정 (11,0,10): {layout.IsFairGroundPoint(new Vector3(11f, 0f, 10f))} (기대 False)");
            lines.Add($"페어 판정 (10,0,10): {layout.IsFairGroundPoint(new Vector3(10f, 0f, 10f))} (기대 True, 파울선 위)");
            lines.Add($"페어 판정 (0,0,-5): {layout.IsFairGroundPoint(new Vector3(0f, 0f, -5f))} (기대 False)");
            lines.Add($"경계 판정 (0,1,100): {layout.IsInsidePlayBoundary(new Vector3(0f, 1f, 100f))} (기대 True)");
            lines.Add($"경계 판정 (0,1,120): {layout.IsInsidePlayBoundary(new Vector3(0f, 1f, 120f))} (기대 False)");
            lines.Add($"경계 판정 (0,61,10): {layout.IsInsidePlayBoundary(new Vector3(0f, 61f, 10f))} (기대 False)");
            lines.Add($"경계 판정 (0,-3,10): {layout.IsInsidePlayBoundary(new Vector3(0f, -3f, 10f))} (기대 False)");
            lines.Add($"다음 베이스 Home -> {layout.GetNextBase(BaseId.Home)}");
            lines.Add($"다음 베이스 Third -> {layout.GetNextBase(BaseId.Third)}");

            AppendPitchingMachineReport(lines, scene, layout);

            return string.Join("\n", lines);
        }

        /// <summary>
        /// 피칭머신 참조와 탄도 계산식을 배치 모드에서 확인할 수 있는 만큼 검증한다.
        /// Rigidbody 실제 비행은 Play Mode에서만 확인할 수 있어 여기서는 다루지 않는다
        /// (docs/verification.md 12.2에 실행하지 못한 항목으로 기록).
        /// </summary>
        private static void AppendPitchingMachineReport(List<string> lines, Scene scene, FieldLayout layout)
        {
            lines.Add(string.Empty);
            lines.Add("--- 피칭머신 (배치 모드에서 확인 가능한 범위) ---");

            PlayDirector director = FindInScene<PlayDirector>(scene);
            BallController ball = FindInScene<BallController>(scene);
            ManualPlayController manual = FindInScene<ManualPlayController>(scene);
            DebugPresenter presenter = FindInScene<DebugPresenter>(scene);

            lines.Add(director != null ? "PlayDirector: 존재" : "실패: PlayDirector를 찾지 못했다.");
            lines.Add(ball != null ? "BallController: 존재" : "실패: BallController를 찾지 못했다.");
            lines.Add(manual != null ? "ManualPlayController: 존재" : "실패: ManualPlayController를 찾지 못했다.");
            lines.Add(presenter != null ? "DebugPresenter: 존재" : "실패: DebugPresenter를 찾지 못했다.");

            BaseballEnvironmentConfig config = layout.Config;
            if (config == null)
            {
                lines.Add(
                    "설정 데이터 참조가 비어 있다(격리된 배치 검증 환경에서만 관찰된 문제로 보인다 - " +
                    "docs/verification.md 12.2 참고). 탄도 계산 자체는 FieldLayout 참조 없이 " +
                    "PitchOrigin/PitchTarget 좌표와 알려진 기본 속력으로 계속 확인한다.");
            }
            else
            {
                lines.Add(
                    $"공 반지름/질량: {config.BallRadius} m / {config.BallMass} kg " +
                    "(기대 0.037 m / 0.145 kg)");
                lines.Add($"기본 투구 속력: {config.PitchSpeed} m/s (기대 36 m/s)");
                lines.Add(
                    $"스트라이크존 절반 폭/높이: {config.StrikeZoneHalfWidth} m / {config.StrikeZoneHalfHeight} m " +
                    "(간이 검증용 가정, docs/environment-spec.md 4장)");
            }

            Vector3 origin = layout.PitchOriginPosition;
            Vector3 target = layout.PitchTargetPosition;
            float gravity = Physics.gravity.magnitude;
            float defaultSpeed = config != null ? config.PitchSpeed : 36f;

            foreach (float speed in new[] { 30f, defaultSpeed, 42f })
            {
                ReportBallisticCheck(lines, origin, target, speed, gravity);
            }
        }

        /// <summary>
        /// PlayDirector.TryComputeLaunchVelocity가 돌려준 초기 속도를 단순 운동학으로
        /// 적분해(Rigidbody 없이) 목표 평면 통과 시점의 위치를 계산하고 목표와의 거리를 보고한다.
        /// 이 적분은 이상적인 포물선 운동만 가정하므로 Unity 물리 스텝 오차, 충돌, CCD는
        /// 반영하지 않는다 — 실제 물리 확인은 Play Mode 몫이다.
        /// </summary>
        private static void ReportBallisticCheck(
            List<string> lines, Vector3 origin, Vector3 target, float speed, float gravity)
        {
            if (!PlayDirector.TryComputeLaunchVelocity(
                    origin, target, speed, gravity, out Vector3 velocity, out string rejection))
            {
                lines.Add($"속력 {speed:F1} m/s: 거부됨 - {rejection}");
                return;
            }

            Vector3 travel = target - origin;
            travel.y = 0f;
            Vector3 travelDirection = travel.normalized;
            float horizontalDistance = travel.magnitude;
            float horizontalSpeed = Vector3.Dot(new Vector3(velocity.x, 0f, velocity.z), travelDirection);
            float flightTime = horizontalDistance / horizontalSpeed;

            // 등가속도 운동학으로 목표 평면 통과 시점의 위치를 직접 계산한다(수치 적분 아님).
            Vector3 predictedPosition = origin + (velocity * flightTime)
                + (0.5f * flightTime * flightTime * Physics.gravity);

            float error = Vector3.Distance(predictedPosition, target);
            lines.Add(
                $"속력 {speed:F1} m/s: 초기 속도 크기 {velocity.magnitude:F3} m/s, " +
                $"비행 시간 {flightTime:F3} s, 이상적 운동학 기준 중앙 통과 오차 {error:F4} m " +
                "(목표 0.02 m 이내; 부동소수점 수준 오차만 있어야 정상)");
        }

        private static T FindInScene<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                var component = root.GetComponentInChildren<T>(true);
                if (component != null)
                {
                    return component;
                }
            }

            return null;
        }

    }
}

