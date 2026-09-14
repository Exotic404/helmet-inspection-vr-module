using System;
using System.Linq;
using Unity.XR.CoreUtils;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HelmetInspection.Editor
{
    public static class ChildSafeInteractionVerifier
    {
        public static void Verify()
        {
            var scene = EditorSceneManager.OpenScene(HelmetProjectStartupRepair.TrainingScenePath, OpenSceneMode.Single);
            VerifyHeadTranslationLock();
            VerifyItemResetsAndSolidColliders();

            // Reload without saving so the deliberately displaced verification poses
            // can never become authored scene data.
            EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single);
            Debug.Log("[ChildSafeVerification] PASS: head translation lock, virtual-origin movement, " +
                      "solid helmet overlap detection, and all item home resets.");
        }

        static void VerifyHeadTranslationLock()
        {
            var origin = UnityEngine.Object.FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
            var translationLock = origin != null ? origin.GetComponent<XRPhysicalTranslationLock>() : null;
            if (origin == null || translationLock == null || origin.Camera == null || origin.CameraFloorOffsetObject == null)
                throw new InvalidOperationException("The complete XR translation-lock rig was not found.");

            var camera = origin.Camera.transform;
            var offset = origin.CameraFloorOffsetObject.transform;
            var testRotation = Quaternion.Euler(18f, 57f, 0f);
            camera.SetLocalPositionAndRotation(new Vector3(0.82f, 1.94f, -0.67f), testRotation);
            translationLock.ApplyNow();

            var lockedHead = offset.parent.InverseTransformPoint(camera.position);
            if (Vector3.Distance(lockedHead, translationLock.AnchoredHeadLocalPosition) > 0.0001f)
                throw new InvalidOperationException($"Synthetic HMD translation was not removed: {lockedHead}.");
            if (Quaternion.Angle(camera.localRotation, testRotation) > 0.01f)
                throw new InvalidOperationException("The physical translation lock changed tracked head rotation.");

            var priorWorldHead = camera.position;
            var virtualMove = new Vector3(0.45f, 0f, 0.30f);
            origin.transform.position += virtualMove;
            translationLock.ApplyNow();
            if (Vector3.Distance(camera.position, priorWorldHead + virtualMove) > 0.0001f)
                throw new InvalidOperationException("The translation lock blocked virtual XR Origin locomotion.");
        }

        static void VerifyItemResetsAndSolidColliders()
        {
            var helmets = UnityEngine.Object.FindObjectsByType<HelmetOutOfBoundsRecovery>(FindObjectsInactive.Include);
            if (helmets.Length != 2)
                throw new InvalidOperationException($"Expected two helmets, found {helmets.Length}.");

            var colliders = helmets.Select(item => item.GetComponents<Collider>().Single(collider => !collider.isTrigger)).ToArray();
            var commonPosition = new Vector3(0f, 1.5f, 0f);
            for (var i = 0; i < helmets.Length; ++i)
                helmets[i].transform.SetPositionAndRotation(commonPosition, Quaternion.identity);
            Physics.SyncTransforms();
            if (!Physics.ComputePenetration(colliders[0], colliders[0].transform.position, colliders[0].transform.rotation,
                    colliders[1], colliders[1].transform.position, colliders[1].transform.rotation,
                    out _, out var overlapDistance) || overlapDistance <= 0f)
                throw new InvalidOperationException("Helmet solid colliders do not detect mutual overlap.");

            foreach (var helmet in helmets)
            {
                helmet.ResetToHomeNow();
                if (helmet.Home == null || Vector3.Distance(helmet.transform.position, helmet.Home.position) > 0.0001f ||
                    Quaternion.Angle(helmet.transform.rotation, helmet.Home.rotation) > 0.01f)
                    throw new InvalidOperationException($"{helmet.name} did not reset to its authored home.");
            }

            var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            if (scanner == null || scanner.HomeMount == null)
                throw new InvalidOperationException("The scanner reset home is missing.");
            scanner.transform.SetPositionAndRotation(new Vector3(1.4f, 2.2f, -1.1f), Quaternion.Euler(25f, 70f, 15f));
            scanner.ResetPlacement();
            if (Vector3.Distance(scanner.transform.position, scanner.HomeMount.position) > 0.0001f ||
                Quaternion.Angle(scanner.transform.rotation, scanner.HomeMount.rotation) > 0.01f)
                throw new InvalidOperationException("The scanner did not reset to its authored mount.");
        }
    }
}
