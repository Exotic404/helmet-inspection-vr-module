using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort;

namespace HelmetInspection.Editor
{
    /// <summary>Retains the tested movement speed but removes peripheral dimming.</summary>
    public static class ComfortPresentationInstaller
    {
        public const float MoveSpeed = 1f;
        const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";

        public static void Apply()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Apply outside Play Mode.");
            var scene = SceneManager.GetSceneByPath(ScenePath);
            if (!scene.isLoaded) throw new InvalidOperationException("Load the training scene first.");
            var move = InScene<ComfortContinuousMoveProvider>().Single();
            move.moveSpeed = MoveSpeed;
            Record(move);
            foreach (var vignette in InScene<TunnelingVignetteController>())
            {
                // Keep the prefab recoverable; neither driver nor mesh may run/render.
                vignette.enabled = false;
                vignette.gameObject.SetActive(false);
                Record(vignette);
                Record(vignette.gameObject);
            }
            foreach (var legacy in InScene<LocomotionComfortVignette>())
            {
                legacy.enabled = false;
                Record(legacy);
            }
            foreach (var volume in InScene<Volume>())
                if (volume.sharedProfile != null && volume.sharedProfile.TryGet<Vignette>(out var vignette))
                {
                    vignette.active = false;
                    vignette.intensity.value = 0f;
                    EditorUtility.SetDirty(vignette);
                    AssetDatabase.SaveAssetIfDirty(vignette);
                }
            VerifyConfiguration();
            Debug.Log("[ComfortPresentation] Removed stereo and post-process dimming; movement, camera projection and lighting retained.");
        }

        public static void VerifyConfiguration()
        {
            Require(Mathf.Approximately(InScene<ComfortContinuousMoveProvider>().Single().moveSpeed, MoveSpeed),
                "Keep the existing 1 m/s movement speed.");
            Require(InScene<TunnelingVignetteController>().All(item => !item.enabled && !item.gameObject.activeSelf),
                "All stereo vignette objects and their drivers must be disabled.");
            Require(InScene<LocomotionComfortVignette>().All(item => !item.enabled), "Legacy dimming drivers must be disabled.");
            foreach (var volume in InScene<Volume>())
                if (volume.sharedProfile != null && volume.sharedProfile.TryGet<Vignette>(out var vignette))
                    Require(!vignette.active && Mathf.Approximately(vignette.intensity.value, 0f),
                        "No post-process edge darkening may remain in the volume profiles.");
            Debug.Log("[ComfortPresentation] PASS: peripheral dimming completely disabled; movement speed retained.");
        }

        static T[] InScene<T>() where T : Component => SceneManager.GetSceneByPath(ScenePath).GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<T>(true)).ToArray();

        static void Record(UnityEngine.Object item)
        {
            EditorUtility.SetDirty(item);
            if (PrefabUtility.IsPartOfPrefabInstance(item)) PrefabUtility.RecordPrefabInstancePropertyModifications(item);
        }

        static void Require(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException("[ComfortPresentation] " + message);
        }
    }
}
