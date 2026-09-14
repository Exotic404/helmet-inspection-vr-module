using System;
using System.Linq;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace HelmetInspection.Editor
{
    public static class ChildSafeInteractionInstaller
    {
        const string A1PrefabPath = "Assets/HelmetInspection/Prefabs/A1_ReferenceHelmet.prefab";
        const string A2PrefabPath = "Assets/HelmetInspection/Prefabs/A2_InspectionHelmet.prefab";

        [MenuItem("Helmet Inspection/Install Child-Safe Tracking and Item Physics")]
        public static void Install()
        {
            ConfigureHelmetPrefab(A1PrefabPath);
            ConfigureHelmetPrefab(A2PrefabPath);

            var scene = EditorSceneManager.OpenScene(HelmetProjectStartupRepair.TrainingScenePath, OpenSceneMode.Single);
            var origins = UnityEngine.Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include);
            if (origins.Length != 1 || origins[0].Camera == null || origins[0].CameraFloorOffsetObject == null)
                throw new InvalidOperationException($"Expected exactly one complete XR Origin, found {origins.Length}.");

            var origin = origins[0];
            var translationLock = origin.GetComponent<XRPhysicalTranslationLock>();
            if (translationLock == null)
                translationLock = origin.gameObject.AddComponent<XRPhysicalTranslationLock>();
            translationLock.SetEditorReferences(origin, new Vector3(0f, 1.68f, 0f));
            EditorUtility.SetDirty(translationLock);

            var helmetRecoveries = UnityEngine.Object.FindObjectsByType<HelmetOutOfBoundsRecovery>(FindObjectsInactive.Include);
            if (helmetRecoveries.Length != 2)
                throw new InvalidOperationException($"Expected exactly two helmet recovery components, found {helmetRecoveries.Length}.");
            foreach (var recovery in helmetRecoveries)
            {
                ConfigureHelmetGrab(recovery.GetComponent<XRGrabInteractable>());
                recovery.SetEditorSafetyBounds(new Vector3(-2.25f, 0.08f, -2.25f),
                    new Vector3(2.25f, 2.75f, 2.25f), 0.35f);
                EditorUtility.SetDirty(recovery);
                EditorUtility.SetDirty(recovery.GetComponent<XRGrabInteractable>());
                EditorUtility.SetDirty(recovery.GetComponent<Rigidbody>());
            }

            var scanners = UnityEngine.Object.FindObjectsByType<InspectionScanner>(FindObjectsInactive.Include);
            if (scanners.Length != 1)
                throw new InvalidOperationException($"Expected exactly one inspection scanner, found {scanners.Length}.");
            var scanner = scanners[0];
            var scannerSocket = UnityEngine.Object.FindObjectsByType<XRSocketInteractor>(FindObjectsInactive.Include)
                .SingleOrDefault(item => item.name == "Offhand Scanner Auto-Attach Socket");
            if (scannerSocket == null)
                throw new InvalidOperationException("The scanner home socket was not found.");
            scanner.SetEditorHome(scannerSocket.attachTransform != null
                ? scannerSocket.attachTransform
                : scannerSocket.transform);
            EditorUtility.SetDirty(scanner);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, HelmetProjectStartupRepair.TrainingScenePath))
                throw new InvalidOperationException("Failed to save the child-safe interaction repair.");
            AssetDatabase.SaveAssets();
            Debug.Log("[ChildSafeRepair] Locked physical head translation, enabled colliding helmet grabs, " +
                      "bounded helmet travel, and connected full item reset.");
        }

        static void ConfigureHelmetPrefab(string path)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                ConfigureHelmetGrab(root.GetComponent<XRGrabInteractable>());
                var recovery = root.GetComponent<HelmetOutOfBoundsRecovery>();
                if (recovery == null)
                    throw new InvalidOperationException($"Helmet prefab is missing recovery: {path}");
                recovery.SetEditorSafetyBounds(new Vector3(-2.25f, 0.08f, -2.25f),
                    new Vector3(2.25f, 2.75f, 2.25f), 0.35f);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static void ConfigureHelmetGrab(XRGrabInteractable grab)
        {
            if (grab == null)
                throw new InvalidOperationException("A helmet is missing XRGrabInteractable.");
            var body = grab.GetComponent<Rigidbody>();
            var solidCollider = grab.GetComponents<Collider>().FirstOrDefault(item => !item.isTrigger);
            if (body == null || solidCollider == null)
                throw new InvalidOperationException($"{grab.name} needs a Rigidbody and solid collider.");

            body.isKinematic = false;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;
            grab.throwOnDetach = false;
            grab.velocityDamping = 1f;
            grab.velocityScale = 1f;
            grab.limitLinearVelocity = true;
            grab.limitAngularVelocity = true;
            grab.maxLinearVelocityDelta = 3.5f;
            grab.maxAngularVelocityDelta = 12f;
        }
    }
}
