using System;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HelmetInspection.Editor
{
    public static class XRTrackingSpaceRecenterInstaller
    {
        [MenuItem("Helmet Inspection/Repair Quest Tracking-Space Recenter")]
        public static void Install()
        {
            var scene = EditorSceneManager.OpenScene(HelmetProjectStartupRepair.TrainingScenePath, OpenSceneMode.Single);
            var origins = UnityEngine.Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include);
            if (origins.Length != 1 || origins[0].Camera == null)
                throw new InvalidOperationException($"Expected exactly one complete XR Origin, found {origins.Length}.");

            var origin = origins[0];
            var recenter = origin.GetComponent<XRTrackingSpaceRecenter>();
            if (recenter == null)
                recenter = origin.gameObject.AddComponent<XRTrackingSpaceRecenter>();
            recenter.SetEditorReferences(origin, new Vector3(0f, 1.68f, -1.8f), Vector3.forward);

            EditorUtility.SetDirty(recenter);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, HelmetProjectStartupRepair.TrainingScenePath))
                throw new InvalidOperationException("Failed to save the tracking-space recenter repair.");
            AssetDatabase.SaveAssets();
            Debug.Log("[XRRecenter] Installed launch/resume tracking-space recovery on the Quest XR Origin.");
        }
    }
}
