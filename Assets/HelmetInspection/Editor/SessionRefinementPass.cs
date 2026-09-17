using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort;

namespace HelmetInspection.Editor
{
    public static class SessionRefinementPass
    {
        [MenuItem("Helmet Inspection/Diagnostics/Apply Standing Height And Ten-Finding Refinement")]
        public static void Apply()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode first.");
            for (var i = 0; i < SceneManager.sceneCount; ++i)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save your open scene edits first.");
            var scene = EditorSceneManager.OpenScene(ArtRefreshInstaller.ScenePath);
            var backup = "Logs/RefinementSep17/Before_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
            Directory.CreateDirectory(backup);
            File.Copy(ArtRefreshInstaller.ScenePath, backup + "/HelmetDefectInspection.unity");
            File.Copy("Assets/HelmetInspection/Settings/HelmetLabVolume.asset", backup + "/HelmetLabVolume.asset");
            var components = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Component>(true))
                .Where(item => item != null).ToArray();
            var d09 = BulgeScanAccessibilityRepair.FindTarget();
            var protectedComponents = components.Where(item => item is not Transform && item is not TMP_Text &&
                item is not ComfortContinuousMoveProvider && item is not TunnelingVignetteController &&
                item is not LocomotionComfortVignette && item is not XRTrackingSpaceRecenter && item != d09)
                .ToDictionary(item => item, EditorJsonUtility.ToJson);
            var poses = components.OfType<Transform>().ToDictionary(item => item,
                item => (item.localPosition, item.localRotation, item.localScale, item.parent));

            ComfortPresentationInstaller.Apply();
            BulgeScanAccessibilityRepair.Apply();
            var origin = components.OfType<XROrigin>().Single();
            if (origin.GetComponent<XRPhysicalTranslationLock>() is not { enabled: false })
                throw new InvalidOperationException("Natural head tracking must remain enabled.");
            var recenter = origin.GetComponent<XRTrackingSpaceRecenter>();
            var serialized = new SerializedObject(recenter);
            serialized.FindProperty("standingEyeHeight").floatValue = 1.68f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Record(recenter);
            var session = components.OfType<TrainingSessionController>().Single();
            if (session.TargetCount != 10 || session.AvailableDefectCount != 12)
                throw new InvalidOperationException("Expected ten required findings from twelve authored defects.");
            foreach (var text in components.OfType<TMP_Text>())
            {
                if (text.name == "Intro Body")
                    text.text = Regex.Replace(text.text, @"Log (?:all \d+|any \d+ of \d+) findings", "Log any 10 of 12 findings");
                else if (text.name == "Progress") text.text = "QA FINDINGS  00 / 10";
                else continue;
                Record(text);
            }
            foreach (var pair in protectedComponents)
                if (pair.Key == null || EditorJsonUtility.ToJson(pair.Key) != pair.Value)
                    throw new InvalidOperationException("Protected component changed: " + pair.Key);
            foreach (var pair in poses)
                if ((pair.Key.localPosition, pair.Key.localRotation, pair.Key.localScale, pair.Key.parent) != pair.Value)
                    throw new InvalidOperationException("Authored placement changed: " + pair.Key.name);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Failed to save refined scene.");
            AssetDatabase.SaveAssets();
            Debug.Log($"[SessionRefinement] PASS: no peripheral dimming, 1.68 m calibrated standing eye height, " +
                $"any 10/12 findings, D09 corrected. {protectedComponents.Count} components and {poses.Count} authored poses preserved. Backup: {backup}");
        }

        static void Record(UnityEngine.Object item)
        {
            EditorUtility.SetDirty(item);
            if (PrefabUtility.IsPartOfPrefabInstance(item)) PrefabUtility.RecordPrefabInstancePropertyModifications(item);
        }
    }
}
