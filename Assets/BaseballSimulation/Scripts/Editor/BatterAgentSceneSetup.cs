using System;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BaseballSimulation.Editor
{
    public static class BatterAgentSceneSetup
    {
        public const string EyeName = "CatcherEye";
        public const string CameraName = "BatterCatcherCamera";

        /// <summary>
        /// Director가 참조하는 타자 루트에 Agent를 붙인다. 자식 센서(UseChildSensors)가 타자 몸을 따라 수집되게 한다.
        /// 이전 배치(별도 BatterAgent 오브젝트)가 있으면 지우고 타자 루트로 옮긴다. 관측 계약은 매번 다시 맞추고,
        /// 기존 BallEye 레이를 포수 시점 카메라로 전환한다. 결정은 TrainingEnvController가 요청하므로
        /// Decision Requester는 두지 않고, 남아 있으면 지운다.
        /// </summary>
        [MenuItem("Tools/Baseball Simulation/Add Batter ML-Agent To Current Scene")]
        public static void AddToCurrentScene()
        {
            PlayDirector director = UnityEngine.Object.FindAnyObjectByType<PlayDirector>();
            FieldLayout field = UnityEngine.Object.FindAnyObjectByType<FieldLayout>();
            BallController ball = UnityEngine.Object.FindAnyObjectByType<BallController>();
            if (director == null || director.EnvironmentConfig == null || field == null || ball == null)
                throw new InvalidOperationException("Open BaseballPlayground with a configured PlayDirector first.");
            var batter = new SerializedObject(director).FindProperty("batter").objectReferenceValue as BatterController;
            if (batter == null)
                throw new InvalidOperationException("Add the batter to the scene first.");

            GameObject root = batter.gameObject;
            BatterAgent agent = root.GetComponent<BatterAgent>();
            if (agent == null)
            {
                BatterAgent existing = UnityEngine.Object.FindAnyObjectByType<BatterAgent>();
                if (existing != null)
                {
                    GameObject previous = existing.gameObject;
                    // Transform + BatterAgent + BehaviorParameters (+ DecisionRequester)만 가진 전용 오브젝트일 때만 지운다.
                    if (previous.transform.childCount > 0 || previous.GetComponents<Component>().Length > 4)
                        throw new InvalidOperationException(
                            $"'{previous.name}' holds other components or children. Move the BatterAgent manually.");
                    Undo.DestroyObjectImmediate(previous);
                }
                agent = Undo.AddComponent<BatterAgent>(root);
                agent.AssignDirector(director);
                agent.MaxStep = 0; // Director pitch call or batted-ball call owns the end of this episode.
            }

            BehaviorParameters behavior = root.GetComponent<BehaviorParameters>();
            Undo.RecordObject(behavior, "Configure batter ML-Agent");
            behavior.BehaviorName = "BaseballBatter";
            behavior.BehaviorType = BehaviorType.Default;
            behavior.UseChildSensors = true;
            behavior.BrainParameters.VectorObservationSize = BatterAgent.ObservationSize;
            behavior.BrainParameters.NumStackedVectorObservations = 1;
            behavior.BrainParameters.ActionSpec = new ActionSpec(BatterAgent.ContinuousActionCount, new[] { 2 });
            if (root.GetComponentInChildren<RayPerceptionSensorComponent3D>(true) != null)
                behavior.Model = null; // 레이 정책은 카메라 관측과 호환되지 않는다.

            DecisionRequester requester = root.GetComponent<DecisionRequester>();
            if (requester != null) Undo.DestroyObjectImmediate(requester);

            EnsureTag(BatterAgent.BallTag);
            if (!ball.CompareTag(BatterAgent.BallTag))
            {
                Undo.RecordObject(ball.gameObject, "Tag ball");
                ball.gameObject.tag = BatterAgent.BallTag;
                EditorUtility.SetDirty(ball.gameObject);
            }
            ConfigureEye(root.transform, field);

            EditorUtility.SetDirty(agent);
            EditorUtility.SetDirty(behavior);
            EditorSceneManager.MarkSceneDirty(root.scene);
        }

        /// <summary>
        /// 현재 씬에서 타자 Agent·Behavior Parameters·Decision Requester와 BallEye를 지워 수동 조작 씬으로 되돌린다.
        /// 공 태그는 남긴다. 지운 것이 있으면 true다.
        /// </summary>
        public static bool RemoveFromCurrentScene()
        {
            bool removed = false;
            foreach (BatterAgent agent in UnityEngine.Object.FindObjectsByType<BatterAgent>(FindObjectsInactive.Include))
            {
                GameObject root = agent.gameObject;
                foreach (RayPerceptionSensorComponent3D eye in root.GetComponentsInChildren<RayPerceptionSensorComponent3D>(true))
                {
                    if (eye.gameObject.name == "BallEye" && eye.gameObject != root && eye.GetComponents<Component>().Length == 2)
                        Undo.DestroyObjectImmediate(eye.gameObject);
                    else Undo.DestroyObjectImmediate(eye);
                }
                foreach (CameraSensorComponent eye in root.GetComponentsInChildren<CameraSensorComponent>(true))
                {
                    if (eye.Camera != null && eye.Camera.name == CameraName)
                        Undo.DestroyObjectImmediate(eye.Camera.gameObject);
                    if (eye.gameObject.name == EyeName && eye.gameObject != root && eye.GetComponents<Component>().Length == 2)
                        Undo.DestroyObjectImmediate(eye.gameObject);
                    else Undo.DestroyObjectImmediate(eye);
                }
                // Decision Requester가 Agent에, Agent가 Behavior Parameters에 의존하므로 이 순서로 지운다.
                DecisionRequester requester = root.GetComponent<DecisionRequester>();
                if (requester != null) Undo.DestroyObjectImmediate(requester);
                Undo.DestroyObjectImmediate(agent);
                BehaviorParameters behavior = root.GetComponent<BehaviorParameters>();
                if (behavior != null) Undo.DestroyObjectImmediate(behavior);
                EditorSceneManager.MarkSceneDirty(root.scene);
                removed = true;
            }
            return removed;
        }

        /// <summary>
        /// 센서는 Agent의 자식, 카메라는 경기장의 Field 자식이다. 자세·스윙·주루와 관계없이 포수 시점을 유지한다.
        /// </summary>
        internal static void ConfigureEye(Transform root, FieldLayout field)
        {
            EnsureSensorBallLayer();
            foreach (RayPerceptionSensorComponent3D ray in root.GetComponentsInChildren<RayPerceptionSensorComponent3D>(true))
            {
                if (ray.gameObject.name == "BallEye" && ray.GetComponents<Component>().Length == 2)
                    Undo.DestroyObjectImmediate(ray.gameObject);
                else Undo.DestroyObjectImmediate(ray);
            }
            CameraSensorComponent sensor = root.GetComponentInChildren<CameraSensorComponent>(true);
            if (sensor != null)
            {
                bool changed = sensor.Width != BatterAgent.ImageWidth || sensor.Height != BatterAgent.ImageHeight ||
                    !sensor.Grayscale || sensor.ObservationStacks != BatterAgent.ImageStacks;
                if (changed)
                {
                    Undo.RecordObject(sensor, "Update batter visual observation contract");
                    sensor.Width = BatterAgent.ImageWidth;
                    sensor.Height = BatterAgent.ImageHeight;
                    sensor.Grayscale = true;
                    sensor.ObservationStacks = BatterAgent.ImageStacks;
                    EditorUtility.SetDirty(sensor);
                    BehaviorParameters behavior = root.GetComponent<BehaviorParameters>();
                    Undo.RecordObject(behavior, "Clear incompatible batter camera model");
                    behavior.Model = null;
                    EditorUtility.SetDirty(behavior);
                }
                ConfigureBallOnlyView(sensor.Camera, field);
                return; // 카메라 위치와 FOV 등 Inspector에서 조정한 값은 보존한다.
            }
            var eye = new GameObject(EyeName);
            Undo.RegisterCreatedObjectUndo(eye, "Add batter catcher eye");
            eye.transform.SetParent(root, false);
            var cameraObject = new GameObject(CameraName);
            Undo.RegisterCreatedObjectUndo(cameraObject, "Add catcher observation camera");
            cameraObject.transform.SetParent(field.transform, false);
            // 3단계 포수 몸(z=-1.6)의 앞에서 본다. 몸이 렌즈를 가리지 않는 낮은 포수 시점이다.
            Vector3 position = field.HomePosition + new Vector3(0f, 0.85f, -1.2f);
            float zoneCenter = 0.5f * (field.Config.StrikeZoneBottom + field.Config.StrikeZoneTop);
            Vector3 target = new Vector3(field.HomePosition.x, field.HomePosition.y + zoneCenter, field.PitchTargetPosition.z);
            cameraObject.transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position, Vector3.up));
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false; // CameraSensor가 결정할 때만 오프스크린으로 렌더한다.
            camera.fieldOfView = 50f;
            camera.aspect = (float)BatterAgent.ImageWidth / BatterAgent.ImageHeight;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 180f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderPostProcessing = false;
            ConfigureBallOnlyView(camera, field);
            sensor = eye.AddComponent<CameraSensorComponent>();
            sensor.SensorName = EyeName;
            sensor.Camera = camera;
            sensor.Width = BatterAgent.ImageWidth;
            sensor.Height = BatterAgent.ImageHeight;
            sensor.Grayscale = true;
            sensor.ObservationStacks = BatterAgent.ImageStacks;
            sensor.CompressionType = SensorCompressionType.PNG;
            sensor.RuntimeCameraEnable = false;
            EditorUtility.SetDirty(sensor);
        }

        private static void ConfigureBallOnlyView(Camera camera, FieldLayout field)
        {
            BallController ball = field.transform.root.GetComponentInChildren<BallController>(true);
            if (camera == null || ball == null)
                throw new InvalidOperationException("The batter sensor needs its arena camera and ball.");
            Undo.RecordObject(camera, "Render only the ball in the batter sensor");
            foreach (Renderer renderer in ball.GetComponentsInChildren<Renderer>(true))
                Undo.RecordObject(renderer.gameObject, "Assign ball observation layer");
            UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (data != null) Undo.RecordObject(data, "Disable batter sensor post processing");
            BatterAgent.ConfigureBallOnlyCamera(camera, ball);
            EditorUtility.SetDirty(camera);
            if (data != null) EditorUtility.SetDirty(data);
            foreach (Renderer renderer in ball.GetComponentsInChildren<Renderer>(true))
                EditorUtility.SetDirty(renderer.gameObject);
            EditorSceneManager.MarkSceneDirty(field.gameObject.scene);
        }

        private static void EnsureSensorBallLayer()
        {
            if (LayerMask.NameToLayer(BatterAgent.SensorBallLayerName) >= 0) return;
            var manager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty layers = manager.FindProperty("layers");
            for (int i = 8; i < layers.arraySize; i++)
            {
                SerializedProperty layer = layers.GetArrayElementAtIndex(i);
                if (!string.IsNullOrEmpty(layer.stringValue)) continue;
                layer.stringValue = BatterAgent.SensorBallLayerName;
                manager.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                return;
            }
            throw new InvalidOperationException("No free user layer for BatterSensorBall. Existing layers were preserved.");
        }

        private static void EnsureTag(string tag)
        {
            var manager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty tags = manager.FindProperty("tags");
            for (int i = 0; i < tags.arraySize; i++)
                if (tags.GetArrayElementAtIndex(i).stringValue == tag) return;
            tags.InsertArrayElementAtIndex(tags.arraySize);
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
            manager.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }
    }
}
