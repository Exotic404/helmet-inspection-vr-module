using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.XR.CoreUtils;
using Unity.XR.CoreUtils.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HelmetInspection.Editor
{
    /// <summary>
    /// Keeps the editor workflow and XR validation settings reproducible. This is deliberately
    /// editor-only: none of this helper code or the desktop simulator is included in the Quest APK.
    /// </summary>
    [InitializeOnLoad]
    public static class HelmetProjectStartupRepair
    {
        internal const string TrainingScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        const string InteractionLayerSettingsPath = "Assets/XRI/Settings/Resources/InteractionLayerSettings.asset";
        const string SimulatorSettingsPath = "Assets/XRI/Settings/Resources/XRDeviceSimulatorSettings.asset";
        const string SimulatorPrefabPath = "Assets/Samples/XR Interaction Toolkit/3.6.0/XR Interaction Simulator/XR Interaction Simulator.prefab";
        const string SessionRepairKey = "HelmetInspection.StartupRepair.3";

        static readonly string[] OpenXrDefines =
        {
            "USE_INPUT_SYSTEM_POSE_CONTROL",
            "USE_STICK_CONTROL_THUMBSTICKS"
        };

        static HelmetProjectStartupRepair()
        {
            if (!Application.isBatchMode)
            {
                EditorApplication.delayCall += RepairOnceAfterEditorStarts;
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            }
        }

        [MenuItem("Helmet Inspection/Repair Project and Open Training Scene", priority = 1)]
        public static void RepairAndOpenTrainingScene()
        {
            ApplyProjectSettings();
            OpenTrainingScene(preserveTemporaryRecoveryScene: true);
        }

        [MenuItem("Helmet Inspection/Repair Player Camera Height", priority = 2)]
        public static void RepairPlayerCameraHeight()
        {
            var scene = EditorSceneManager.OpenScene(TrainingScenePath, OpenSceneMode.Single);
            var origins = UnityEngine.Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include);
            if (origins.Length != 1)
                throw new InvalidOperationException($"Expected exactly one XR Origin, found {origins.Length}.");

            var origin = origins[0];
            var camera = origin.Camera;
            if (camera == null || origin.CameraFloorOffsetObject == null)
                throw new InvalidOperationException("XR Origin is missing its camera or Camera Offset object.");

            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
            origin.CameraYOffset = 1.68f;
            var offset = origin.CameraFloorOffsetObject.transform.localPosition;
            offset.y = 1.68f;
            origin.CameraFloorOffsetObject.transform.localPosition = offset;
            camera.transform.localPosition = Vector3.zero;

            EditorUtility.SetDirty(origin);
            EditorUtility.SetDirty(camera.transform);
            EditorUtility.SetDirty(origin.CameraFloorOffsetObject.transform);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Failed to save repaired scene {TrainingScenePath}.");

            Debug.Log($"[HelmetStartup] Repaired XR camera: world eye height {camera.transform.position.y:F2} m, ceiling 3.00 m.");
        }

        /// <summary>Batch-safe entry point used by automated verification.</summary>
        public static void RepairProject()
        {
            ApplyProjectSettings();
            Debug.Log("[HelmetStartup] XR validation settings repaired successfully.");
        }

        /// <summary>Fails batch execution if any currently registered Unity XR rule is unresolved.</summary>
        public static void ValidateRegisteredXrRules()
        {
            var failures = new HashSet<BuildValidationRule>();
            var validationMethod = typeof(BuildValidator).GetMethod(
                "GetCurrentValidationIssues", BindingFlags.Static | BindingFlags.NonPublic);
            if (validationMethod == null)
                throw new MissingMethodException("Unity XR BuildValidator.GetCurrentValidationIssues was not found.");

            validationMethod.Invoke(null, new object[] { failures, BuildTargetGroup.Android });
            Directory.CreateDirectory("Builds");
            var report = failures.Count == 0
                ? "PASS: Unity XR Project Validation has 0 unresolved Android issues."
                : string.Join(Environment.NewLine, failures.Select(issue => "FAIL: " + issue.GetDisplayString()));
            File.WriteAllText("Builds/UnityXrValidationReport.txt", report + Environment.NewLine);

            if (failures.Count > 0)
                throw new InvalidOperationException($"Unity XR Project Validation has {failures.Count} unresolved issue(s):\n{report}");

            Debug.Log("[HelmetStartup] Unity XR Project Validation passed with 0 unresolved Android issues.");
        }

        internal static void ApplyProjectSettings()
        {
            PlayerSettings.runInBackground = true;

            // Match the official OpenXR validation fixes. Applying these to every target keeps the
            // editor and Android assemblies on the same modern input control types.
            AddDefines(NamedBuildTarget.Android);
            AddDefines(NamedBuildTarget.Standalone);
            AddDefines(NamedBuildTarget.WindowsStoreApps);

            SetSerializedArrayValue(InteractionLayerSettingsPath, "m_LayerNames", 31, "Teleport");
            ConfigureEditorOnlySimulator();
            SetPlayModeStartScene();

            AssetDatabase.SaveAssets();
        }

        static void RepairOnceAfterEditorStarts()
        {
            if (SessionState.GetBool(SessionRepairKey, false))
                return;

            SessionState.SetBool(SessionRepairKey, true);
            ApplyProjectSettings();

            var scene = EditorSceneManager.GetActiveScene();
            var hasNoUsefulScene = scene.IsValid() && scene.rootCount == 0 && string.IsNullOrEmpty(scene.path);
            var isTemporaryRecovery = scene.IsValid() &&
                                      !string.IsNullOrEmpty(scene.path) &&
                                      (scene.path.StartsWith("Temp/", StringComparison.OrdinalIgnoreCase) ||
                                       scene.path.EndsWith(".backup", StringComparison.OrdinalIgnoreCase));

            if (hasNoUsefulScene || isTemporaryRecovery)
                OpenTrainingScene(preserveTemporaryRecoveryScene: isTemporaryRecovery);
        }

        static void AddDefines(NamedBuildTarget target)
        {
            var defines = new HashSet<string>(
                PlayerSettings.GetScriptingDefineSymbols(target)
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim()),
                StringComparer.Ordinal);

            foreach (var define in OpenXrDefines)
                defines.Add(define);

            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", defines.OrderBy(value => value, StringComparer.Ordinal)));
        }

        static void SetSerializedArrayValue(string assetPath, string propertyName, int index, string value)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
                throw new InvalidOperationException($"Required settings asset is missing: {assetPath}");

            var serialized = new SerializedObject(asset);
            var property = serialized.FindProperty(propertyName);
            if (property == null || !property.isArray || property.arraySize <= index)
                throw new InvalidOperationException($"{assetPath} does not contain {propertyName}[{index}].");

            property.GetArrayElementAtIndex(index).stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        static void ConfigureEditorOnlySimulator()
        {
            var settings = AssetDatabase.LoadMainAssetAtPath(SimulatorSettingsPath);
            if (settings == null)
                return;

            // Do not leave a Resources asset referencing the simulator prefab. Even with XRI's
            // "Editor Only" toggle, that reference causes the debug UI/textures to enter the APK.
            // The editor callback below instantiates the prefab without creating a player dependency.
            var serialized = new SerializedObject(settings);
            serialized.FindProperty("m_AutomaticallyInstantiateSimulatorPrefab").boolValue = false;
            serialized.FindProperty("m_AutomaticallyInstantiateInEditorOnly").boolValue = true;
            serialized.FindProperty("m_UseClassic").boolValue = false;
            serialized.FindProperty("m_SimulatorPrefab").objectReferenceValue = null;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode || GameObject.Find("XR Interaction Simulator") != null)
                return;

            var simulatorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SimulatorPrefabPath);
            if (simulatorPrefab == null)
            {
                Debug.LogWarning($"[HelmetStartup] Editor simulator prefab is missing: {SimulatorPrefabPath}");
                return;
            }

            var simulator = UnityEngine.Object.Instantiate(simulatorPrefab);
            simulator.name = "XR Interaction Simulator";
            UnityEngine.Object.DontDestroyOnLoad(simulator);
            Debug.Log("[HelmetStartup] Started the editor-only XR Interaction Simulator.");
        }

        static void SetPlayModeStartScene()
        {
            var trainingScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(TrainingScenePath);
            if (trainingScene != null)
                EditorSceneManager.playModeStartScene = trainingScene;
        }

        static void OpenTrainingScene(bool preserveTemporaryRecoveryScene)
        {
            if (!File.Exists(TrainingScenePath))
            {
                Debug.LogError($"[HelmetStartup] Training scene is missing: {TrainingScenePath}");
                return;
            }

            var activeScene = EditorSceneManager.GetActiveScene();
            if (activeScene.path == TrainingScenePath)
                return;

            if (preserveTemporaryRecoveryScene && activeScene.IsValid() && activeScene.rootCount > 0)
            {
                const string recoveryDirectory = "Assets/HelmetInspection/Scenes/Recovery";
                if (!AssetDatabase.IsValidFolder(recoveryDirectory))
                    AssetDatabase.CreateFolder("Assets/HelmetInspection/Scenes", "Recovery");

                var recoveryPath = $"{recoveryDirectory}/Recovered_{DateTime.Now:yyyyMMdd_HHmmss}.unity";
                if (EditorSceneManager.SaveScene(activeScene, recoveryPath, true))
                    Debug.Log($"[HelmetStartup] Preserved Unity's temporary recovery scene at {recoveryPath}.");
            }

            EditorSceneManager.OpenScene(TrainingScenePath, OpenSceneMode.Single);
            Debug.Log($"[HelmetStartup] Opened the real training scene: {TrainingScenePath}");
        }
    }
}
