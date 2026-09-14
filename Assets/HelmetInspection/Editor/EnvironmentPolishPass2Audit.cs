using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.XR.CoreUtils;

namespace HelmetInspection.Editor
{
    public static class EnvironmentPolishPass2Audit
    {
        [MenuItem("Helmet Inspection/Legacy Generators/Environment Polish Pass 2/Audit Scene")]
        public static void Audit()
        {
            EditorSceneManager.OpenScene(EnvironmentArtPassInstaller.ScenePath, OpenSceneMode.Single);
            var camera = Object.FindAnyObjectByType<Camera>(FindObjectsInactive.Include);
            var scanner = Object.FindAnyObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            Debug.Log($"[Pass2Audit] Camera {Path(camera?.transform)} pos={camera?.transform.position} " +
                      $"local={camera?.transform.localPosition} forward={camera?.transform.forward}");
            Debug.Log($"[Pass2Audit] Scanner {Path(scanner?.transform)} pos={scanner?.transform.position} " +
                      $"local={scanner?.transform.localPosition} scale={scanner?.transform.lossyScale} " +
                      $"cameraDistance={(camera != null && scanner != null ? Vector3.Distance(camera.transform.position, scanner.transform.position) : -1f):F3}");
            if (scanner != null)
            {
                Debug.Log($"[Pass2Audit] Home {Path(scanner.HomeMount)} pos={scanner.HomeMount?.position} " +
                          $"local={scanner.HomeMount?.localPosition} scale={scanner.HomeMount?.lossyScale}");
                foreach (var renderer in scanner.GetComponentsInChildren<Renderer>(true))
                    Debug.Log($"[Pass2Audit] ScannerRenderer {Path(renderer.transform)} bounds={renderer.bounds}");
            }

            var origin = Object.FindAnyObjectByType<XROrigin>(FindObjectsInactive.Include);
            if (origin != null)
                foreach (var transform in origin.GetComponentsInChildren<Transform>(true)
                             .Where(item => item.name.Contains("Hand") || item.name.Contains("Controller") || item.name.Contains("Camera")))
                    Debug.Log($"[Pass2Audit] XR {Path(transform)} pos={transform.position} local={transform.localPosition} scale={transform.lossyScale}");

            if (camera != null)
                foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include)
                             .Where(item => item.enabled && item.gameObject.activeInHierarchy &&
                                                    item.bounds.SqrDistance(camera.transform.position) < 0.18f * 0.18f)
                             .OrderBy(item => item.bounds.SqrDistance(camera.transform.position)))
                    Debug.Log($"[Pass2Audit] NearCamera {Path(renderer.transform)} bounds={renderer.bounds} " +
                              $"material={renderer.sharedMaterial?.name}");

            foreach (var button in Object.FindObjectsByType<MechanicalTrainingButtonBase>(FindObjectsInactive.Include))
            {
                var collider = button.GetComponent<BoxCollider>();
                Debug.Log($"[Pass2Audit] Button {Path(button.transform)} pos={button.transform.position} " +
                          $"colliderCenter={collider?.center} colliderSize={collider?.size}");
                foreach (var renderer in button.GetComponentsInChildren<Renderer>(true))
                    Debug.Log($"[Pass2Audit] ButtonRenderer {Path(renderer.transform)} bounds={renderer.bounds} material={renderer.sharedMaterial?.name}");
            }

            foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include)
                         .Where(item => item.name.Contains("Screen") || item.name.Contains("Monitor") ||
                                        item.name.Contains("Rack") || item.name.Contains("Shelf")))
                Debug.Log($"[Pass2Audit] Candidate {Path(renderer.transform)} bounds={renderer.bounds} material={renderer.sharedMaterial?.name}");
        }

        static string Path(Transform transform)
        {
            if (transform == null) return "<null>";
            var path = transform.name;
            for (var parent = transform.parent; parent != null; parent = parent.parent)
                path = parent.name + "/" + path;
            return path;
        }
    }
}
