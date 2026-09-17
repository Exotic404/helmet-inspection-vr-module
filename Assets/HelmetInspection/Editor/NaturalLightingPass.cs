using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace HelmetInspection.Editor
{
    /// <summary>Visual-only matte finish pass. Never regenerates art or gameplay objects.</summary>
    public static class NaturalLightingPass
    {
        const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        const string PreviewPath = "Logs/MatteFinish";

        public static void ReviewBaseline()
        {
            EditorSceneManager.OpenScene(ScenePath);
            CaptureReview("Before");
        }

        [MenuItem("Helmet Inspection/Metrology Studio/Apply Matte Natural Lighting")]
        public static void ApplyAndReview()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            var scene = EditorSceneManager.OpenScene(ScenePath);
            // Protect authored transforms, components, meshes and defect data against
            // accidental changes from what should only be a finish/lighting adjustment.
            var protectedComponents = Object.FindObjectsByType<Component>(FindObjectsInactive.Include)
                .Where(item => item is Transform || item is Collider || item is Rigidbody || item is MonoBehaviour)
                .ToDictionary(item => item, item => EditorJsonUtility.ToJson(item));
            var meshes = Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include)
                .ToDictionary(item => item, item => item.sharedMesh);
            const string dataPath = "Assets/HelmetInspection/Data/DefectSet_A2.asset";
            var dataBefore = File.ReadAllBytes(dataPath);

            var protectedMaterials = Object.FindObjectsByType<DefectHotspot>(FindObjectsInactive.Include)
                .SelectMany(item => item.GetComponentsInChildren<Renderer>(true))
                .SelectMany(item => item.sharedMaterials).Where(item => item != null).ToHashSet();
            var materials = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include)
                .Where(item => item.enabled && item.gameObject.activeInHierarchy)
                .SelectMany(item => item.sharedMaterials).Where(item => item != null && !protectedMaterials.Contains(item))
                .Where(item => AssetDatabase.GetAssetPath(item).StartsWith("Assets/HelmetInspection/", StringComparison.Ordinal) &&
                               item.shader.name == "Universal Render Pipeline/Lit")
                .Distinct().ToArray();
            foreach (var material in materials)
                ApplyFinish(material);

            // Keep diffuse illumination close to the existing room brightness. Reduce
            // specular energy and overlapping task lights instead of darkening the room.
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.52f, 0.535f, 0.55f);
            RenderSettings.ambientIntensity = 1f;
            var ambient = new SphericalHarmonicsL2();
            ambient.AddAmbientLight(new Color(0.41f, 0.425f, 0.44f));
            RenderSettings.ambientProbe = ambient;
            RenderSettings.reflectionIntensity = 0.45f;
            foreach (var probe in Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include))
            {
                probe.intensity = 0.45f;
                EditorUtility.SetDirty(probe);
            }
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
            {
                if (light.name == "Directional - Soft Lab Fill")
                {
                    light.color = new Color(1f, 0.98f, 0.95f);
                    light.intensity = 1f;
                    light.shadows = LightShadows.Soft;
                    light.shadowStrength = 0.55f;
                }
                else if (light.name == "A1 Inspection Spot" || light.name == "A2 Inspection Spot")
                {
                    light.intensity = 1.1f;
                    light.color = new Color(1f, 0.98f, 0.95f);
                    light.innerSpotAngle = 50f;
                }
                else if (light.name == "Panel Wash Left" || light.name == "Panel Wash Right")
                {
                    light.intensity = 0.8f;
                    light.color = new Color(0.97f, 0.985f, 1f);
                    light.innerSpotAngle = 50f;
                }
                else continue; // Do not touch the scanner's functional proximity light.
                EditorUtility.SetDirty(light);
            }

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/HelmetInspection/Settings/HelmetLabVolume.asset");
            if (profile.TryGet<ColorAdjustments>(out var color))
            {
                color.postExposure.Override(0f);
                color.contrast.Override(0f);
                EditorUtility.SetDirty(color);
            }
            if (profile.TryGet<Bloom>(out var bloom))
            {
                bloom.intensity.Override(0f);
                bloom.active = false;
                EditorUtility.SetDirty(bloom);
            }

            foreach (var pair in protectedComponents)
                Require(pair.Key != null && EditorJsonUtility.ToJson(pair.Key) == pair.Value,
                    "Gameplay component or authored transform changed: " + pair.Key?.name);
            foreach (var pair in meshes)
                Require(pair.Key != null && pair.Key.sharedMesh == pair.Value, "Mesh geometry changed: " + pair.Key?.name);
            Require(File.ReadAllBytes(dataPath).SequenceEqual(dataBefore), "Defect data changed during the visual pass.");
            var worktop = materials.Single(item => item.name == "MetrologyLab_Lab_WarmCeramic");
            Require(worktop.GetFloat("_Metallic") == 0f && worktop.GetFloat("_Smoothness") <= 0.2f,
                "The visible main worktop must use a matte non-metallic finish.");

            EditorSceneManager.MarkSceneDirty(scene);
            Require(EditorSceneManager.SaveScene(scene), "Could not save the matte lighting scene.");
            AssetDatabase.SaveAssets();
            Debug.Log($"[NaturalLighting] PASS: {materials.Length} material finishes updated; " +
                      $"{protectedComponents.Count} gameplay components/transforms and {meshes.Count} meshes unchanged; all defect data unchanged.");
            CaptureReview("After");
        }

        static void ApplyFinish(Material material)
        {
            var name = material.name;
            var smoothness = 0.23f;
            var metallic = 0f;
            if (name.Contains("Aluminum") || name.Contains("MachinedEdge") || name.Contains("BrushedMetal"))
            { smoothness = 0.3f; metallic = 0.65f; }
            else if (name.Contains("Copper")) { smoothness = 0.28f; metallic = 0.5f; }
            else if (name.Contains("Rubber") || name.Contains("Grip") || name.Contains("Elastomer"))
            { smoothness = 0.12f; }
            else if (name.Contains("Floor") || name.Contains("Ceiling") || name.Contains("WarmWhite"))
            { smoothness = 0.16f; }
            else if (name.Contains("WarmCeramic")) { smoothness = 0.18f; }
            else if (name.Contains("CeramicShell")) { smoothness = 0.22f; }
            else if (name.Contains("Graphite")) { smoothness = 0.2f; metallic = 0.08f; }
            else if (name.Contains("DisplayGlass") || name.Contains("OpticalTeal"))
            { smoothness = 0.35f; }
            else if (name.Contains("Display") || name.Contains("Markings"))
            { smoothness = 0.15f; }
            else if (name.Contains("Diffuser")) { smoothness = 0.12f; }

            material.SetFloat("_Smoothness", Mathf.Min(material.GetFloat("_Smoothness"), smoothness));
            material.SetFloat("_Metallic", Mathf.Min(material.GetFloat("_Metallic"), metallic));
            material.SetFloat("_ClearCoatMask", 0f);
            // Decorative light panels should be luminous, not clipped white glare.
            // Scanner optics, status screens and found-marker materials remain functional.
            if (name == "MetrologyLab_Lab_WarmDiffuser")
                material.SetColor("_EmissionColor", new Color(0.8f, 0.81f, 0.8f, 1f));
            else if (name == "MetrologyLab_Lab_InstrumentTeal")
                material.SetColor("_EmissionColor", new Color(0.06f, 0.38f, 0.3f, 1f));
            EditorUtility.SetDirty(material);
            Debug.Log($"[NaturalLighting] {name}: smoothness={material.GetFloat("_Smoothness"):F2}, metallic={material.GetFloat("_Metallic"):F2}");
        }

        static void CaptureReview(string prefix)
        {
            Directory.CreateDirectory(PreviewPath);
            var cameraObject = new GameObject("Temporary Matte Finish Review Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.nearClipPlane = 0.025f;
            camera.farClipPlane = 20f;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            var cameraData = camera.GetUniversalAdditionalCameraData();
            cameraData.allowXRRendering = false;
            var hmd = Object.FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>().Camera;
            var hmdData = hmd.GetUniversalAdditionalCameraData();
            cameraData.renderPostProcessing = hmdData.renderPostProcessing;
            cameraData.volumeLayerMask = hmdData.volumeLayerMask;
            cameraData.volumeTrigger = camera.transform;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.06f, 0.08f, 0.09f);
            try
            {
                Render("Room", new Vector3(-0.15f, 1.65f, -2.12f), new Vector3(0.05f, 1.25f, 0.8f), 70f);
                Render("Worktop", new Vector3(0.6f, 1.62f, -0.6f), new Vector3(0.1f, 1.0f, 0.62f), 58f);
                var scanner = Object.FindFirstObjectByType<InspectionScanner>();
                Render("Scanner", scanner.transform.TransformPoint(new Vector3(0.22f, 0.14f, 0.2f)),
                    scanner.transform.position, 45f);
            }
            finally { Object.DestroyImmediate(cameraObject); }

            void Render(string name, Vector3 position, Vector3 at, float fov)
            {
                camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(at - position));
                camera.fieldOfView = fov;
                camera.aspect = 1.6f;
                foreach (var label in Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include))
                    if (label.enabled) label.ForceMeshUpdate();
                Canvas.ForceUpdateCanvases();
                var image = ArtRefreshInstaller.Capture(camera, 1600, 1000);
                try { File.WriteAllBytes($"{PreviewPath}/{prefix}{name}.png", image.EncodeToPNG()); }
                finally { Object.DestroyImmediate(image); }
                Debug.Log($"[NaturalLighting] Rendered {prefix}{name}.");
            }
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("[NaturalLighting] " + message);
        }
    }
}
