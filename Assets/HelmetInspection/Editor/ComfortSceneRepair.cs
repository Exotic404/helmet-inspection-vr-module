using System;
using System.IO;
using System.Linq;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HelmetInspection.Editor
{
    public static class ComfortSceneRepair
    {
        const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";

        [MenuItem("Helmet Inspection/Diagnostics/Apply Table And Tracking Comfort Repair")]
        public static void Apply()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play Mode before applying the repair.");
            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("Save your open scene edits before applying the repair.");
            var scene = EditorSceneManager.OpenScene(ScenePath);
            var components = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Component>(true))
                .Where(component => component != null).ToArray();
            // Protect all existing serialized gameplay, physics, lights, cameras and
            // materials. Only the two comfort drivers and move speed may change.
            var protectedComponents = components.Where(component => component is not Transform &&
                component is not XRPhysicalTranslationLock && component is not ComfortContinuousMoveProvider &&
                component is not LocomotionComfortVignette &&
                component is not UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort.TunnelingVignetteController)
                .ToDictionary(component => component, EditorJsonUtility.ToJson);
            var poses = components.OfType<Transform>().ToDictionary(item => item,
                item => (item.localPosition, item.localRotation, item.localScale, item.parent));
            var backup = "Logs/ComfortSep16/SceneBefore_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + ".unity";
            Directory.CreateDirectory("Logs/ComfortSep16");
            File.Copy(ScenePath, backup, false);
            TableSurfaceRepair.Apply();
            ComfortPresentationInstaller.Apply();
            var origin = components.OfType<XROrigin>().Single();
            var positionLock = origin.GetComponent<XRPhysicalTranslationLock>();
            if (positionLock == null || origin.GetComponent<XRTrackingSpaceRecenter>() is not { enabled: true })
                throw new InvalidOperationException("Expected the existing tracking lock and handover recenter components.");
            positionLock.enabled = false;
            EditorUtility.SetDirty(positionLock);
            if (PrefabUtility.IsPartOfPrefabInstance(positionLock))
                PrefabUtility.RecordPrefabInstancePropertyModifications(positionLock);
            foreach (var pair in protectedComponents)
                if (pair.Key == null || EditorJsonUtility.ToJson(pair.Key) != pair.Value)
                    throw new InvalidOperationException("Protected component changed: " + pair.Key);
            foreach (var pair in poses)
                if ((pair.Key.localPosition, pair.Key.localRotation, pair.Key.localScale, pair.Key.parent) != pair.Value)
                    throw new InvalidOperationException("Existing object placement changed: " + pair.Key.name);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new IOException("Failed to save repaired training scene.");
            AssetDatabase.SaveAssets();
            Debug.Log($"[ComfortSceneRepair] PASS: saved repair; {protectedComponents.Count} components and {poses.Count} existing poses unchanged. " +
                $"Natural 6DoF with handover recenter enabled; camera projection untouched. Backup: {backup}");
        }
    }
}
