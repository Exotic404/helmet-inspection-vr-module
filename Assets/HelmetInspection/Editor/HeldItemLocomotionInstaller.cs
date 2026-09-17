using System;
using System.Linq;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;

namespace HelmetInspection.Editor
{
    /// <summary>
    /// Separates inspection props from the player's collision capsule without
    /// losing hand detection, trigger layers, or room collisions.
    /// </summary>
    public static class HeldItemLocomotionInstaller
    {
        public const string PropLayerName = "InspectionProp";

        [MenuItem("Helmet Inspection/Repair Held Item Locomotion")]
        public static void Install()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var layer = EnsurePropLayer();
            ConfigurePrefab("Assets/HelmetInspection/Prefabs/A1_ReferenceHelmet.prefab", layer);
            ConfigurePrefab("Assets/HelmetInspection/Prefabs/A2_InspectionHelmet.prefab", layer);

            var scene = EditorSceneManager.OpenScene(HelmetProjectStartupRepair.TrainingScenePath);
            var origin = UnityEngine.Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include).Single();
            var character = origin.Origin.GetComponent<CharacterController>();
            if (character == null)
                throw new InvalidOperationException("The training origin needs its collision controller.");

            // Unlike per-grab IgnoreCollision state, a serialized exclusion remains
            // effective when recentering recreates the native character controller.
            character.excludeLayers |= 1 << layer;
            EditorUtility.SetDirty(character);
            PrefabUtility.RecordPrefabInstancePropertyModifications(character);

            foreach (var helmet in UnityEngine.Object.FindObjectsByType<HelmetOutOfBoundsRecovery>(
                         FindObjectsInactive.Include))
                ConfigureSolidColliders(helmet.gameObject, layer);
            foreach (var scanner in UnityEngine.Object.FindObjectsByType<InspectionScanner>(
                         FindObjectsInactive.Include))
                ConfigureSolidColliders(scanner.gameObject, layer);

            ConfigureHandDetection(origin, layer);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException("Could not save the locomotion collision repair.");
            AssetDatabase.SaveAssets();
            Debug.Log("[HeldItemLocomotion] Installed persistent player/prop separation; " +
                      "room, helmet-to-helmet, and interaction trigger collisions retained.");
        }

        static void ConfigureHandDetection(XROrigin origin, int layer)
        {
            var propMask = 1 << layer;
            // Physics detection masks are independent of XRI interaction layers.
            // Include the new prop layer in every hand query as well as the body exclusion.
            foreach (var direct in origin.GetComponentsInChildren<XRDirectInteractor>(true))
            {
                direct.physicsLayerMask |= propMask;
                SaveOverride(direct);
            }
            foreach (var near in origin.GetComponentsInChildren<SphereInteractionCaster>(true))
            {
                near.physicsLayerMask |= propMask;
                SaveOverride(near);
            }
            foreach (var far in origin.GetComponentsInChildren<CurveInteractionCaster>(true))
            {
                far.raycastMask |= propMask;
                SaveOverride(far);
            }
            foreach (var ray in origin.GetComponentsInChildren<XRRayInteractor>(true))
            {
                ray.raycastMask |= propMask;
                SaveOverride(ray);
            }
        }

        static void SaveOverride(UnityEngine.Object component)
        {
            EditorUtility.SetDirty(component);
            if (PrefabUtility.IsPartOfPrefabInstance(component))
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }

        static int EnsurePropLayer()
        {
            var existing = LayerMask.NameToLayer(PropLayerName);
            if (existing >= 0)
                return existing;
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = settings.FindProperty("layers");
            for (var index = 8; index < layers.arraySize; ++index)
            {
                var layer = layers.GetArrayElementAtIndex(index);
                if (!string.IsNullOrEmpty(layer.stringValue))
                    continue;
                layer.stringValue = PropLayerName;
                settings.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                return index;
            }
            throw new InvalidOperationException("No free physics layer for inspection props.");
        }

        static void ConfigurePrefab(string path, int layer)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                ConfigureSolidColliders(root, layer);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        static void ConfigureSolidColliders(GameObject root, int layer)
        {
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider.isTrigger)
                    continue;
                collider.gameObject.layer = layer;
                EditorUtility.SetDirty(collider.gameObject);
                if (PrefabUtility.IsPartOfPrefabInstance(collider.gameObject))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(collider.gameObject);
            }
        }
    }
}
