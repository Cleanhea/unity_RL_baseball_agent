using System;
using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BaseballSimulation.Editor
{
    public static class BatterAgentSceneSetup
    {
        public const string EyeName = "BallEye";
        private const float DefaultEyeHeight = 1.65f;
        private const int RaysPerDirection = 25;
        private const float SphereCastRadius = 0.25f;
        private const int ObservationStacks = 3;
        private const float FanMarginDegrees = 12f;

        /// <summary>
        /// Director가 참조하는 타자 루트에 Agent를 붙인다. 자식 센서(UseChildSensors)가 타자 몸을 따라 수집되게 한다.
        /// 이전 배치(별도 BatterAgent 오브젝트)가 있으면 지우고 타자 루트로 옮긴다. 관측 계약은 매번 다시 맞추고,
        /// 공 태그와 공을 보는 레이 센서(BallEye)는 없을 때만 만든다. 결정은 TrainingEnvController가 요청하므로
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

            DecisionRequester requester = root.GetComponent<DecisionRequester>();
            if (requester != null) Undo.DestroyObjectImmediate(requester);

            EnsureTag(BatterAgent.BallTag);
            if (!ball.CompareTag(BatterAgent.BallTag))
            {
                Undo.RecordObject(ball.gameObject, "Tag ball");
                ball.gameObject.tag = BatterAgent.BallTag;
                EditorUtility.SetDirty(ball.gameObject);
            }
            if (root.GetComponentInChildren<RayPerceptionSensorComponent3D>(true) == null)
                AddEye(root.transform, field);

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
                    if (eye.gameObject.name == EyeName && eye.gameObject != root) Undo.DestroyObjectImmediate(eye.gameObject);
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
        /// 머리 높이에 레이 부채꼴을 둔다. 부채꼴 평면은 눈·발사 지점·투구 목표를 지나도록 기울여
        /// 한 평면의 레이로 발사부터 홈 통과까지 공 경로를 따라가게 한다.
        /// </summary>
        private static void AddEye(Transform root, FieldLayout field)
        {
            Transform head = root.Find("Head");
            var eye = new GameObject(EyeName);
            Undo.RegisterCreatedObjectUndo(eye, "Add batter ball eye");
            eye.transform.SetParent(root, false);
            eye.transform.localPosition = head != null ? head.localPosition : new Vector3(0f, DefaultEyeHeight, 0f);

            Vector3 origin = eye.transform.position;
            Vector3 toRelease = field.PitchOriginPosition - origin;
            Vector3 toTarget = field.PitchTargetPosition - origin;
            Vector3 normal = Vector3.Cross(toTarget, toRelease).normalized;
            if (normal.y < 0f) normal = -normal;
            Vector3 forward = (toRelease.normalized + toTarget.normalized).normalized;
            eye.transform.rotation = Quaternion.LookRotation(forward, normal);

            RayPerceptionSensorComponent3D sensor = eye.AddComponent<RayPerceptionSensorComponent3D>();
            sensor.SensorName = "BallEye";
            sensor.DetectableTags = new List<string> { BatterAgent.BallTag };
            sensor.RaysPerDirection = RaysPerDirection;
            sensor.MaxRayDegrees = Mathf.Min(90f, 0.5f * Vector3.Angle(toRelease, toTarget) + FanMarginDegrees);
            sensor.SphereCastRadius = SphereCastRadius;
            sensor.RayLength = toRelease.magnitude + 3f;
            sensor.ObservationStacks = ObservationStacks;
            sensor.StartVerticalOffset = 0f;
            sensor.EndVerticalOffset = 0f;
            EditorUtility.SetDirty(sensor);
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
