using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace HelmetInspection.Editor
{
    /// <summary>Acceptance checks for the Blender studio and scanner, followed by
    /// existing gameplay regressions. Verification never saves the training scene.</summary>
    public static class ArtRefreshVerifier
    {
        const string ArtRoot = "Assets/HelmetInspection/ArtRefresh";
        const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        const string DefectPath = "Assets/HelmetInspection/Data/DefectSet_A2.asset";
        const string DefectHash = "64097A790CBF329B3BA06FCAB541AE54B588D191948FD717E0E0E43433173AFA";
        const string ReportPath = "Builds/ArtRefreshValidation.txt";

        [MenuItem("Helmet Inspection/Metrology Studio/Verify Metrology Studio and Gameplay")]
        public static void Verify()
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Run studio verification outside Play Mode.");
            var report = new List<string>();
            var result = "FAIL";
            var sceneHash = Hash(ScenePath);
            try
            {
                Run(HelmetProjectValidator.ValidateProject, "Core Quest, source helmet, authored defect, and interaction validation");
                Run(ChildSafeInteractionVerifier.Verify, "Head translation, physical helmet collisions, and placement resets");
                Run(ExteriorHoleAccessibilityVerifier.Verify, "All six exterior hole targets and back-side rejection");
                Run(ScannerReliabilityVerifier.Verify, "All-ten scanner competition, rotated/scaled poses, close range, input routing, and gates");
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                VerifyVisuals(report);
                Check(Hash(ScenePath) == sceneHash, "Scene file remains byte-identical after verification", report);
                Check(Hash(DefectPath) == DefectHash, "Measured defect data retains its original SHA-256: " + DefectHash, report);
                report.Add("NOTE  Physical Quest comfort, headset frame rate, and real controller handling still require a headset test.");
                result = "PASS";
                Debug.Log("[ArtRefreshValidation] PASS: Blender studio/scanner, saved reflections, metallic red helmets, " +
                          "and all existing gameplay regression suites. See " + ReportPath);
            }
            catch (Exception error)
            {
                report.Add("FAIL  " + error.Message);
                throw;
            }
            finally
            {
                // The scanner and child-safe verifiers deliberately move objects in
                // memory. Reloading discards those poses even after a failed check.
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Directory.CreateDirectory("Builds");
                var text = new StringBuilder("METROLOGY STUDIO / ART AND GAMEPLAY VALIDATION\n");
                text.AppendLine("Generated: " + DateTimeOffset.Now.ToString("O"));
                text.AppendLine("Unity: " + Application.unityVersion);
                text.AppendLine("Result: " + result);
                text.AppendLine();
                foreach (var line in report)
                    text.AppendLine(line);
                File.WriteAllText(ReportPath, text.ToString());
            }

            void Run(Action action, string title)
            {
                action();
                report.Add("PASS  " + title);
            }
        }

        static void VerifyVisuals(List<string> report)
        {
            var scene = SceneManager.GetActiveScene();
            var objects = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Select(item => item.gameObject).ToArray();
            var missingScripts = objects.Sum(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount);
            Check(missingScripts == 0, $"Missing scripts: {missingScripts}", report);
            var art = objects.SingleOrDefault(item => item.name == ArtRefreshInstaller.ArtName);
            Check(art != null && art.activeInHierarchy && art.transform.parent != null &&
                  art.transform.parent.name == "ENVIRONMENT - QA Metrology Lab",
                "Active Metrology Studio art is grouped under the existing environment", report);
            Check(art.GetComponentsInChildren<Collider>(true).Length == 0 &&
                  art.GetComponentsInChildren<MonoBehaviour>(true).All(item => item is TMP_Text),
                "New room art contains no gameplay colliders or behavior scripts", report);
            var scanner = Object.FindFirstObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            Check(scanner != null, "Existing scanner interaction component is present", report);
            var scannerVisual = scanner.transform.Find("VISUAL - Precision Inspector");
            Check(scannerVisual != null && scannerVisual.gameObject.activeInHierarchy,
                "New Blender scanner is parented beneath the existing scanner", report);
            Check(scannerVisual.GetComponentsInChildren<Collider>(true).Length == 0 &&
                  scannerVisual.GetComponentsInChildren<MonoBehaviour>(true).All(item => item is TMP_Text),
                "New scanner art contains no replacement colliders or gameplay scripts", report);

            var environmentTriangles = CountTriangles(art.transform);
            var scannerTriangles = CountTriangles(scannerVisual);
            Check(environmentTriangles > 0 && environmentTriangles <= 25000,
                $"Environment mesh triangles: {environmentTriangles:N0} / 25,000", report);
            Check(scannerTriangles > 0 && scannerTriangles <= 6000,
                $"Scanner mesh triangles: {scannerTriangles:N0} / 6,000", report);
            Check(art.GetComponentsInChildren<MeshFilter>(true).Any(item => item.sharedMesh != null &&
                      AssetDatabase.GetAssetPath(item.sharedMesh).StartsWith(ArtRoot + "/Generated/Meshes/MetrologyLab_")) &&
                  scannerVisual.GetComponentsInChildren<MeshFilter>(true).Any(item => item.sharedMesh != null &&
                      AssetDatabase.GetAssetPath(item.sharedMesh).StartsWith(ArtRoot + "/Generated/Meshes/InspectorScanner_")),
                "Both visual hierarchies use imported Blender meshes", report);

            var renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include);
            var activeRenderers = renderers.Count(item => item.enabled && item.gameObject.activeInHierarchy);
            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
            var activeLights = lights.Count(item => item.enabled && item.gameObject.activeInHierarchy);
            var colliders = Object.FindObjectsByType<Collider>(FindObjectsInactive.Include);
            Check(activeRenderers <= 150, $"Active enabled renderers: {activeRenderers} / 150", report);
            Check(lights.Length <= 6 && activeLights <= 6,
                $"Lights: {activeLights} active, {lights.Length} total / 6", report);
            Check(colliders.Length == 41, $"Gameplay collider count: {colliders.Length} / 41", report);
            Check(renderers.Where(item => item.enabled && item.gameObject.activeInHierarchy)
                    .All(item => item.sharedMaterials.All(material => material != null && material.shader != null)),
                "Active renderers have materials and shaders", report);

            var scannerData = new SerializedObject(scanner);
            var emitter = scannerData.FindProperty("emitterRenderer").objectReferenceValue as Renderer;
            var origin = scannerData.FindProperty("beamOrigin").objectReferenceValue as Transform;
            Check(emitter != null && emitter.enabled && emitter.gameObject.activeInHierarchy &&
                  emitter.transform.IsChildOf(scannerVisual) && emitter.sharedMaterial != null &&
                  emitter.sharedMaterial.name.IndexOf("OpticalTeal", StringComparison.OrdinalIgnoreCase) >= 0,
                "Live scanner proximity feedback targets the active OpticalTeal lens", report);
            Check(origin != null, "Authored beam origin remains assigned", report);
            var lens = scannerVisual.TransformPoint(new Vector3(0f, 0f, 0.085f));
            var lensError = Vector3.Distance(lens, origin.position);
            var forwardError = Vector3.Angle(scannerVisual.forward, origin.forward);
            Check(lensError <= 0.001f && forwardError <= 1f,
                $"Scanner optical alignment: {lensError * 1000f:F3} mm position, {forwardError:F3} degrees forward", report);
            VerifyScannerCollider(scanner, scannerVisual, report);
            VerifyEmission(report);
            VerifyHelmetMaterial(report);
            VerifyReflection(art.transform, report);
        }

        static void VerifyScannerCollider(InspectionScanner scanner, Transform visual, List<string> report)
        {
            var collider = scanner.GetComponent<BoxCollider>();
            Check(collider != null && collider.enabled && !collider.isTrigger,
                "Scanner retains its existing solid root BoxCollider", report);
            var minimum = collider.center - collider.size * 0.5f;
            var maximum = collider.center + collider.size * 0.5f;
            var imported = visual.GetComponentsInChildren<MeshFilter>(true).Where(filter => filter.sharedMesh != null &&
                AssetDatabase.GetAssetPath(filter.sharedMesh).StartsWith(ArtRoot + "/Generated/Meshes/InspectorScanner_")).ToArray();
            Check(imported.Length > 0, "Native Blender scanner meshes exist for collider-fit verification", report);
            var overflow = 0f;
            foreach (var filter in imported)
            {
                var bounds = filter.sharedMesh.bounds;
                var toCollider = collider.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                for (var x = 0; x < 2; ++x)
                for (var y = 0; y < 2; ++y)
                for (var z = 0; z < 2; ++z)
                {
                    var corner = toCollider.MultiplyPoint3x4(new Vector3(
                        x == 0 ? bounds.min.x : bounds.max.x,
                        y == 0 ? bounds.min.y : bounds.max.y,
                        z == 0 ? bounds.min.z : bounds.max.z));
                    overflow = Mathf.Max(overflow, minimum.x - corner.x, corner.x - maximum.x,
                        minimum.y - corner.y, corner.y - maximum.y, minimum.z - corner.z, corner.z - maximum.z);
                }
            }
            Check(overflow <= 0.0005f,
                $"Existing scanner collider encloses imported grip/body/lens bounds: {overflow * 1000f:F3} mm overflow / 0.5 mm tolerance", report);
        }

        static void VerifyEmission(List<string> report)
        {
            foreach (var name in new[] { "SchematicDisplay", "SensorDisplay", "StatusIndicator",
                         "InspectorScanner_Scanner_OpticalTeal", "MetrologyLab_Lab_WarmDiffuser", "StartEmerald", "ResetBlue" })
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(ArtRoot + "/Generated/Materials/" + name + ".mat");
                Check(material != null && material.IsKeywordEnabled("_EMISSION") &&
                      (material.globalIlluminationFlags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) == 0,
                    name + " persists _EMISSION without EmissiveIsBlack stripping", report);
            }
        }

        static void VerifyHelmetMaterial(List<string> report)
        {
            const string materialPath = "Assets/HelmetInspection/Materials/M_Helmet_Triplanar.mat";
            const string texturePath = ArtRoot + "/Generated/Textures/MetallicRedLacquer.png";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            Check(material != null && texture != null && material.HasProperty("_Grid_Texture") &&
                  material.GetTexture("_Grid_Texture") == texture,
                "Shared helmet shader uses MetallicRedLacquer.png in its active triplanar texture input", report);
            var helmets = Object.FindObjectsByType<HelmetOutOfBoundsRecovery>(FindObjectsInactive.Include);
            Check(helmets.Length == 2 && helmets.All(helmet => helmet.GetComponentsInChildren<MeshRenderer>(true)
                    .Any(renderer => renderer.enabled && renderer.sharedMaterial == material)),
                "Both original helmet meshes share the new red material", report);
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Check(decoded.LoadImage(File.ReadAllBytes(texturePath)), "Red lacquer PNG decodes correctly", report);
                var pixels = decoded.GetPixels();
                var red = pixels.Average(pixel => pixel.r);
                var green = pixels.Average(pixel => pixel.g);
                var blue = pixels.Average(pixel => pixel.b);
                Check(red > 0.2f && red > green * 3f && red > blue * 3f,
                    $"Saved lacquer color is red: mean RGB ({red:F3}, {green:F3}, {blue:F3})", report);
            }
            finally
            {
                Object.DestroyImmediate(decoded);
            }
        }

        static void VerifyReflection(Transform art, List<string> report)
        {
            const string path = ArtRoot + "/Generated/Textures/StudioReflection.asset";
            var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
            Check(cube != null && AssetDatabase.Contains(cube) && cube.width >= 128 && cube.isReadable,
                "Studio reflection is a saved, readable room cubemap at 128 px or better", report);
            var probes = art.GetComponentsInChildren<ReflectionProbe>(true);
            Check(probes.Length == 1 && probes[0].mode == ReflectionProbeMode.Custom &&
                  probes[0].customBakedTexture == cube && probes[0].boxProjection &&
                  RenderSettings.customReflection == cube,
                "One box-projected room probe and ambient reflections use the saved studio cubemap", report);
            var faceMeans = new List<float>();
            for (var face = 0; face < 6; ++face)
            {
                var pixels = cube.GetPixels((CubemapFace)face, 0);
                var luminance = pixels.Select(color => color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f).ToArray();
                var mean = luminance.Average();
                var variance = luminance.Average(value => (value - mean) * (value - mean));
                var span = luminance.Max() - luminance.Min();
                var poisonPixels = pixels.Count(color =>
                {
                    var bytes = (Color32)color;
                    return bytes.r == 0xCD && bytes.g == 0xCD && bytes.b == 0xCD;
                });
                Check(pixels.Length == cube.width * cube.height && variance > 0.000001f && span > 0.01f &&
                      poisonPixels < pixels.Length * 0.99f,
                    $"Saved reflection {(CubemapFace)face}: luminance variance {variance:F6}, range {span:F3}; no uniform 0xCD capture", report);
                faceMeans.Add(mean);
            }
            Check(faceMeans.Max() - faceMeans.Min() > 0.005f,
                "Room reflection faces contain distinct captured directions", report);
        }

        static long CountTriangles(Transform root)
        {
            long total = 0;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null)
                    continue;
                for (var submesh = 0; submesh < mesh.subMeshCount; ++submesh)
                    if (mesh.GetTopology(submesh) == MeshTopology.Triangles)
                        total += mesh.GetIndexCount(submesh) / 3;
            }
            return total;
        }

        static void Check(bool condition, string message, List<string> report)
        {
            if (!condition)
                throw new InvalidOperationException(message);
            report.Add("PASS  " + message);
        }

        static string Hash(string path)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty);
        }
    }
}
