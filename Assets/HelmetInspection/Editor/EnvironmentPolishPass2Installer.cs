using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace HelmetInspection.Editor
{
    /// <summary>
    /// Second visual-only environment pass. Gameplay geometry remains authored by the
    /// original scene; every new mesh here is batched decoration without a collider.
    /// The one intentional transform correction is the untracked controller fallback
    /// pose, which keeps controller/scanner visuals out of the HMD near clip plane.
    /// </summary>
    public static class EnvironmentPolishPass2Installer
    {
        const string Root = "Assets/HelmetInspection";
        const string GeneratedRoot = Root + "/EnvironmentArt/Generated/Pass2";
        const string MaterialRoot = GeneratedRoot + "/Materials";
        const string TextureRoot = GeneratedRoot + "/Textures";
        const string MeshRoot = GeneratedRoot + "/Meshes";
        const string ReflectionPath = TextureRoot + "/C_RoomCapturedChromeReflection.asset";
        const string PassRootName = "ENVIRONMENT POLISH PASS 2 - Completed";

        static int s_AddedTriangles;

        sealed class Materials
        {
            public Material darkMetal;
            public Material brushedMetal;
            public Material screenProfile;
            public Material screenStatus;
            public Material cyan;
            public Material green;
            public Material warmWhite;
            public Material utilityBlue;
            public Material shelfLiner;
        }

        [MenuItem("Helmet Inspection/Legacy Generators/Environment Polish Pass 2/Install Pass 2")]
        public static void Install()
        {
            if (!LegacyGenerationGuard.TryBegin("Environment Polish Pass 2", out var guard))
                return;
            using var legacyScope = guard;
            // Rebuild the first-pass root first so this method is deterministic and
            // safe to run again after scene edits.
            EnvironmentArtPassInstaller.Install();
            var scene = EditorSceneManager.OpenScene(EnvironmentArtPassInstaller.ScenePath, OpenSceneMode.Single);
            var artRoot = GameObject.Find(EnvironmentArtPassInstaller.ArtRootName);
            if (artRoot == null)
                throw new InvalidOperationException("The environment art root is missing after the base pass.");

            EnsureFolders();
            s_AddedTriangles = 0;
            FixUntrackedControllerAndScannerFallbacks();
            RemoveFirstPassCenterDashboard();

            var oldPassRoot = Find(PassRootName);
            if (oldPassRoot != null)
                UnityEngine.Object.DestroyImmediate(oldPassRoot);
            var passRoot = new GameObject(PassRootName);
            passRoot.transform.SetParent(artRoot.transform, false);

            var materials = CreateMaterials();
            PolishHelmetFinish();
            BuildTwinDiagnosticScreens(passRoot.transform, materials);
            BuildPhysicalControlPanel(passRoot.transform, materials);
            BuildLinearCeilingFixture(passRoot.transform, materials);
            BuildFinishedUtilityRack(passRoot.transform, materials);
            CaptureRoomReflection(artRoot.transform);

            Require(passRoot.GetComponentsInChildren<Collider>(true).Length == 0,
                "Pass 2 visual dressing unexpectedly contains a collider.");
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, EnvironmentArtPassInstaller.ScenePath))
                throw new InvalidOperationException("Failed to save environment polish pass 2.");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"[EnvironmentPolishPass2] Installed: {s_AddedTriangles} decorative triangles, " +
                      "clean controller/scanner fallback, two functional-looking screens, room-captured " +
                      "chrome reflections, physical controls, linear ceiling fixture, and finished rack.");
        }

        static void EnsureFolders()
        {
            EnsureFolder(Root + "/EnvironmentArt/Generated", "Pass2");
            EnsureFolder(GeneratedRoot, "Materials");
            EnsureFolder(GeneratedRoot, "Textures");
            EnsureFolder(GeneratedRoot, "Meshes");
        }

        static void EnsureFolder(string parent, string child)
        {
            var path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, child);
        }

        static void FixUntrackedControllerAndScannerFallbacks()
        {
            var origin = UnityEngine.Object.FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
            var camera = origin != null ? origin.Camera : null;
            var left = origin != null ? FindChild(origin.transform, "Left Controller") : null;
            var right = origin != null ? FindChild(origin.transform, "Right Controller") : null;
            var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            if (origin == null || camera == null || left == null || right == null || scanner == null || scanner.HomeMount == null)
                throw new InvalidOperationException("The complete XR rig/scanner home setup was not found.");

            // These are only the saved values used before a tracking sample arrives.
            // Tracked Pose Driver overwrites both controller transforms on Quest.
            left.localPosition = new Vector3(-0.24f, -0.20f, 0.38f);
            right.localPosition = new Vector3(0.24f, -0.20f, 0.38f);
            scanner.transform.SetPositionAndRotation(scanner.HomeMount.position, scanner.HomeMount.rotation);
            scanner.transform.localScale = Vector3.one;
            Physics.SyncTransforms();
            EditorUtility.SetDirty(left);
            EditorUtility.SetDirty(right);
            EditorUtility.SetDirty(scanner.transform);

            Require(Vector3.Distance(camera.transform.position, left.position) > 0.35f &&
                    Vector3.Distance(camera.transform.position, right.position) > 0.35f &&
                    Vector3.Distance(camera.transform.position, scanner.transform.position) > 0.35f,
                "Corrected fallback controls still intersect the HMD near-clip region.");
        }

        static void RemoveFirstPassCenterDashboard()
        {
            foreach (var name in new[]
                     {
                         "Visible Center QA Dashboard Screen",
                         "Visible Center QA Dashboard Bezel and Mount",
                         "Visible QA Dashboard Cyan Readout",
                         "Visible QA Dashboard Amber Readout"
                     })
            {
                var item = Find(name);
                if (item != null)
                    UnityEngine.Object.DestroyImmediate(item);
            }

            // This legacy decorative pane was the flat cyan rectangle visible behind
            // the unfinished displays. Keep the authored object and transform intact,
            // but retire only its obsolete renderer now that the real screens exist.
            var legacyPane = Find("Observation Window")?.GetComponent<Renderer>();
            if (legacyPane != null)
            {
                legacyPane.enabled = false;
                EditorUtility.SetDirty(legacyPane);
            }
        }

        static Materials CreateMaterials()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                throw new InvalidOperationException("The required URP Lit shader is unavailable.");

            var profile = CreateDisplayTexture("T_HelmetShellProfile", true);
            var status = CreateDisplayTexture("T_QAStatusDashboard", false);
            return new Materials
            {
                darkMetal = CreateMaterial("M_P2DarkMetal", lit, new Color(0.055f, 0.065f, 0.068f), 0.72f, 0.36f),
                brushedMetal = CreateMaterial("M_P2BrushedMetal", lit, new Color(0.55f, 0.58f, 0.59f), 0.93f, 0.63f),
                screenProfile = CreateMaterial("M_P2HelmetProfileScreen", lit, Color.white, 0f, 0.18f,
                    profile, Color.white * 1.35f),
                screenStatus = CreateMaterial("M_P2QAStatusScreen", lit, Color.white, 0f, 0.18f,
                    status, Color.white * 1.35f),
                cyan = CreateMaterial("M_P2CyanEmission", lit, new Color(0.035f, 0.44f, 0.58f), 0.12f, 0.62f,
                    null, new Color(0.05f, 0.72f, 0.92f) * 2.2f),
                green = CreateMaterial("M_P2GreenStatus", lit, new Color(0.025f, 0.55f, 0.16f), 0.08f, 0.58f,
                    null, new Color(0.04f, 1f, 0.23f) * 2.8f),
                warmWhite = CreateMaterial("M_P2LinearDiffuser", lit, new Color(0.72f, 0.67f, 0.56f), 0f, 0.48f,
                    null, new Color(1f, 0.80f, 0.56f) * 1.25f),
                utilityBlue = CreateMaterial("M_P2UtilityBinBlue", lit, new Color(0.075f, 0.20f, 0.25f), 0.08f, 0.28f),
                shelfLiner = CreateMaterial("M_P2ShelfLiner", lit, new Color(0.12f, 0.105f, 0.085f), 0f, 0.18f)
            };
        }

        static Texture2D CreateDisplayTexture(string name, bool schematic)
        {
            const int width = 512;
            const int height = 256;
            var path = TextureRoot + "/" + name + ".asset";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var isNew = texture == null;
            if (isNew)
                texture = new Texture2D(width, height, TextureFormat.RGBA32, true, false);
            else if (!texture.Reinitialize(width, height, TextureFormat.RGBA32, true))
                throw new InvalidOperationException("Could not resize " + path);
            texture.name = name;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.anisoLevel = 2;

            var pixels = new Color[width * height];
            var cyan = new Color(0.08f, 0.76f, 0.90f, 1f);
            var cyanDim = new Color(0.022f, 0.20f, 0.25f, 1f);
            var green = new Color(0.08f, 0.95f, 0.31f, 1f);
            var amber = new Color(0.97f, 0.43f, 0.08f, 1f);
            for (var y = 0; y < height; ++y)
            for (var x = 0; x < width; ++x)
            {
                var edgeX = Mathf.Abs(x / (float)(width - 1) - 0.5f) * 2f;
                var edgeY = Mathf.Abs(y / (float)(height - 1) - 0.5f) * 2f;
                var vignette = Mathf.Clamp01(1f - 0.40f * Mathf.Max(edgeX, edgeY));
                var scanline = y % 4 == 0 ? 0.78f : 1f;
                var color = new Color(0.007f, 0.025f, 0.032f, 1f) * (vignette * scanline);
                color.a = 1f;
                if (x % 32 == 0 || y % 32 == 0)
                    color = Color.Lerp(color, cyanDim, 0.52f);

                if (schematic)
                {
                    var nx = (x - 250f) / 145f;
                    var ny = (y - 118f) / 78f;
                    var ellipse = Mathf.Abs(nx * nx + ny * ny - 1f);
                    if (ellipse < 0.045f && y >= 93)
                        color = cyan;
                    if (x >= 85 && x <= 414 && Mathf.Abs(y - 91) <= 2)
                        color = cyan;
                    if (x >= 122 && x <= 386 && Mathf.Abs(y - (66 + (x - 122) * 0.035f)) <= 2)
                        color = cyan * 0.82f;
                    if ((Mathf.Abs(x - 72) <= 1 || Mathf.Abs(x - 438) <= 1) && y >= 52 && y <= 205)
                        color = amber;
                    if (y == 52 && x >= 69 && x <= 441 || y == 205 && x >= 69 && x <= 441)
                        color = amber;
                    if ((x - 250) * (x - 250) + (y - 138) * (y - 138) < 28)
                        color = green;
                }
                else
                {
                    var wave = 77 + Mathf.RoundToInt(Mathf.Sin(x * 0.035f) * 19f + Mathf.Sin(x * 0.091f) * 8f);
                    if (x >= 26 && x <= 330 && Mathf.Abs(y - wave) <= 2)
                        color = cyan;
                    if (x >= 362 && x <= 472)
                    {
                        var column = (x - 362) / 23;
                        var top = 45 + column * 19;
                        if ((x - 362) % 23 < 14 && y >= 36 && y <= top)
                            color = column == 4 ? green : (column % 2 == 0 ? cyan : amber);
                    }
                    if (x >= 27 && x <= 475 && y >= 166 && y <= 178)
                        color = x < 342 ? green * 0.74f : amber * 0.74f;
                    if ((x - 438) * (x - 438) + (y - 215) * (y - 215) <= 64)
                        color = green;
                }
                pixels[y * width + x] = color;
            }

            texture.SetPixels(pixels);
            texture.Apply(true, false);
            texture.imageContentsHash = Hash128.Compute($"EnvironmentPass2:{name}:{width}x{height}:v2");
            if (isNew)
                AssetDatabase.CreateAsset(texture, path);
            else
                EditorUtility.SetDirty(texture);
            return texture;
        }

        static Material CreateMaterial(string name, Shader shader, Color color, float metallic, float smoothness,
            Texture texture = null, Color? emission = null)
        {
            var path = MaterialRoot + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var isNew = material == null;
            if (isNew)
                material = new Material(shader);
            else
                material.shader = shader;
            material.name = name;
            material.enableInstancing = true;
            Set(material, "_BaseColor", color);
            Set(material, "_Color", color);
            Set(material, "_Metallic", metallic);
            Set(material, "_Smoothness", smoothness);
            Set(material, "_BaseMap", texture);
            if (emission.HasValue)
            {
                material.EnableKeyword("_EMISSION");
                Set(material, "_EmissionColor", emission.Value);
                Set(material, "_EmissionMap", texture);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else
            {
                material.DisableKeyword("_EMISSION");
                Set(material, "_EmissionColor", Color.black);
                Set(material, "_EmissionMap", null);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            if (isNew)
                AssetDatabase.CreateAsset(material, path);
            else
                EditorUtility.SetDirty(material);
            return material;
        }

        static void PolishHelmetFinish()
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Textures/T_HelmetMicrotexture.asset");
            var material = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Materials/M_Helmet_Triplanar.mat");
            if (texture == null || material == null)
                throw new InvalidOperationException("The shared helmet finish assets are missing.");
            var pixels = new Color[texture.width * texture.height];
            for (var y = 0; y < texture.height; ++y)
            for (var x = 0; x < texture.width; ++x)
            {
                var polish = Mathf.Sin(x * 0.51f + y * 0.13f) * 0.004f;
                var value = 0.925f + polish;
                pixels[y * texture.width + x] = new Color(value * 0.985f, value * 0.993f, value, 1f);
            }
            texture.SetPixels(pixels);
            texture.Apply(true, false);
            Set(material, "_Grid_Texture", texture);
            Set(material, "_SlopeColor", new Color(0.91f, 0.925f, 0.94f, 1f));
            Set(material, "_Slope", 1f);
            EditorUtility.SetDirty(texture);
            EditorUtility.SetDirty(material);
            foreach (var helmetName in new[] { "A1 - REFERENCE HELMET", "A2 - DEFECTIVE SCAN" })
            {
                var helmet = GameObject.Find(helmetName);
                if (helmet == null)
                    throw new InvalidOperationException("Missing helmet " + helmetName);
                foreach (var renderer in helmet.GetComponentsInChildren<MeshRenderer>(true))
                {
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                    EditorUtility.SetDirty(renderer);
                }
            }
        }

        static void BuildTwinDiagnosticScreens(Transform parent, Materials m)
        {
            var centers = new[] { new Vector3(0.22f, 2.30f, 2.305f), new Vector3(0.22f, 1.94f, 2.305f) };
            CreateBatch("Center Screen - Helmet Shell Profile", parent, PrimitiveType.Cube,
                new[] { Matrix4x4.TRS(centers[0], Quaternion.identity, new Vector3(0.72f, 0.27f, 0.018f)) }, m.screenProfile);
            CreateBatch("Center Screen - QA Status Dashboard", parent, PrimitiveType.Cube,
                new[] { Matrix4x4.TRS(centers[1], Quaternion.identity, new Vector3(0.72f, 0.27f, 0.018f)) }, m.screenStatus);

            var bezel = new List<Matrix4x4>();
            foreach (var center in centers)
            {
                bezel.Add(Matrix4x4.TRS(center + new Vector3(0f, 0.154f, -0.012f), Quaternion.identity, new Vector3(0.79f, 0.035f, 0.042f)));
                bezel.Add(Matrix4x4.TRS(center + new Vector3(0f, -0.154f, -0.012f), Quaternion.identity, new Vector3(0.79f, 0.035f, 0.042f)));
                bezel.Add(Matrix4x4.TRS(center + new Vector3(-0.378f, 0f, -0.012f), Quaternion.identity, new Vector3(0.035f, 0.31f, 0.042f)));
                bezel.Add(Matrix4x4.TRS(center + new Vector3(0.378f, 0f, -0.012f), Quaternion.identity, new Vector3(0.035f, 0.31f, 0.042f)));
            }
            CreateBatch("Twin Diagnostic Screen Brushed Bezels", parent, PrimitiveType.Cube, bezel, m.brushedMetal);
            CreateBatch("Twin Screen Green Status LEDs", parent, PrimitiveType.Cylinder, centers.Select(center =>
                Matrix4x4.TRS(center + new Vector3(0.33f, -0.117f, -0.035f), Quaternion.Euler(90f, 0f, 0f),
                    new Vector3(0.026f, 0.008f, 0.026f))), m.green);

            CreateLabel(parent, "Profile Screen Header", "HELMET SHELL / PROFILE 08", centers[0] + new Vector3(0f, 0.105f, -0.030f),
                Quaternion.identity, 0.020f, new Color(0.44f, 0.94f, 1f), 17f);
            CreateLabel(parent, "Profile Dimension Labels", "245 mm     SHELL 2.8 mm     +/- 0.20 mm", centers[0] + new Vector3(0f, -0.105f, -0.030f),
                Quaternion.identity, 0.015f, new Color(1f, 0.66f, 0.22f), 22f);
            CreateLabel(parent, "Status Screen Header", "QA STATUS / A2 INSPECTION READY", centers[1] + new Vector3(0f, 0.105f, -0.030f),
                Quaternion.identity, 0.020f, new Color(0.44f, 0.94f, 1f), 17f);
            CreateLabel(parent, "Status Screen Footer", "SENSORS ONLINE     TOLERANCE LOCKED", centers[1] + new Vector3(0f, -0.105f, -0.030f),
                Quaternion.identity, 0.015f, new Color(0.26f, 1f, 0.48f), 22f);
        }

        static void BuildPhysicalControlPanel(Transform parent, Materials m)
        {
            var start = UnityEngine.Object.FindFirstObjectByType<TrainingStartButton>(FindObjectsInactive.Include);
            var reset = UnityEngine.Object.FindFirstObjectByType<TrainingResetButton>(FindObjectsInactive.Include);
            if (start == null || reset == null)
                throw new InvalidOperationException("START/RESET controls are missing.");
            var startCenter = FindChild(start.transform, "Raised 3D Button Cap - 18mm")?.GetComponent<Renderer>()?.bounds.center;
            var resetCenter = FindChild(reset.transform, "Raised 3D Button Cap - 18mm")?.GetComponent<Renderer>()?.bounds.center;
            if (!startCenter.HasValue || !resetCenter.HasValue)
                throw new InvalidOperationException("The animated physical button caps are missing.");

            foreach (var bezel in new[] { start.transform, reset.transform }
                         .Select(item => FindChild(item, "Rounded Recessed Bezel")?.GetComponent<Renderer>())
                         .Where(item => item != null))
            {
                bezel.sharedMaterial = m.brushedMetal;
                EditorUtility.SetDirty(bezel);
            }

            var middle = (startCenter.Value + resetCenter.Value) * 0.5f;
            CreateBatch("START RESET Recessed Brushed Control Plate", parent, PrimitiveType.Cube, new[]
            {
                Matrix4x4.TRS(new Vector3(2.385f, middle.y, middle.z), Quaternion.identity,
                    new Vector3(0.035f, 0.43f, 1.06f))
            }, m.darkMetal);
            CreateBatch("START RESET Engraved Border", parent, PrimitiveType.Cube, new[]
            {
                Matrix4x4.TRS(new Vector3(2.348f, middle.y + 0.202f, middle.z), Quaternion.identity, new Vector3(0.018f, 0.018f, 1.02f)),
                Matrix4x4.TRS(new Vector3(2.348f, middle.y - 0.202f, middle.z), Quaternion.identity, new Vector3(0.018f, 0.018f, 1.02f)),
                Matrix4x4.TRS(new Vector3(2.348f, middle.y, middle.z - 0.501f), Quaternion.identity, new Vector3(0.018f, 0.39f, 0.018f)),
                Matrix4x4.TRS(new Vector3(2.348f, middle.y, middle.z + 0.501f), Quaternion.identity, new Vector3(0.018f, 0.39f, 0.018f))
            }, m.brushedMetal);
            var screwMatrices = new List<Matrix4x4>();
            foreach (var y in new[] { middle.y - 0.17f, middle.y + 0.17f })
            foreach (var z in new[] { middle.z - 0.47f, middle.z + 0.47f })
                screwMatrices.Add(Matrix4x4.TRS(new Vector3(2.333f, y, z), Quaternion.Euler(0f, 0f, 90f),
                    new Vector3(0.032f, 0.008f, 0.032f)));
            CreateBatch("START RESET Four Retaining Screws", parent, PrimitiveType.Cylinder, screwMatrices, m.brushedMetal);
            CreateBatch("START RESET Status LEDs", parent, PrimitiveType.Cylinder, new[]
            {
                Matrix4x4.TRS(startCenter.Value + new Vector3(-0.045f, 0.168f, 0.0f), Quaternion.Euler(0f, 0f, 90f), new Vector3(0.032f, 0.008f, 0.032f))
            }, m.green);
            CreateBatch("RESET Cool Blue Status LED", parent, PrimitiveType.Cylinder, new[]
            {
                Matrix4x4.TRS(resetCenter.Value + new Vector3(-0.045f, 0.168f, 0.0f), Quaternion.Euler(0f, 0f, 90f), new Vector3(0.032f, 0.008f, 0.032f))
            }, m.cyan);
            CreateLabel(parent, "START External Readable Label", "START", startCenter.Value + new Vector3(-0.060f, 0.105f, 0f),
                Quaternion.Euler(0f, 90f, 0f), 0.034f, new Color(0.33f, 1f, 0.48f), 6f);
            CreateLabel(parent, "RESET External Readable Label", "RESET", resetCenter.Value + new Vector3(-0.060f, 0.105f, 0f),
                Quaternion.Euler(0f, 90f, 0f), 0.034f, new Color(0.35f, 0.82f, 1f), 6f);
        }

        static void BuildLinearCeilingFixture(Transform parent, Materials m)
        {
            CreateBatch("Recessed Linear Ceiling Fixture Housing", parent, PrimitiveType.Cube, new[]
            {
                Matrix4x4.TRS(new Vector3(0f, 2.915f, -0.58f), Quaternion.identity, new Vector3(2.24f, 0.10f, 0.34f)),
                Matrix4x4.TRS(new Vector3(-1.10f, 2.865f, -0.58f), Quaternion.identity, new Vector3(0.06f, 0.055f, 0.34f)),
                Matrix4x4.TRS(new Vector3(1.10f, 2.865f, -0.58f), Quaternion.identity, new Vector3(0.06f, 0.055f, 0.34f))
            }, m.darkMetal);
            CreateBatch("Linear Fixture Emissive Diffuser", parent, PrimitiveType.Cube, new[]
            {
                Matrix4x4.TRS(new Vector3(0f, 2.854f, -0.58f), Quaternion.identity, new Vector3(2.06f, 0.018f, 0.20f))
            }, m.warmWhite);
            CreateBatch("Linear Fixture Diffuser Retaining Ribs", parent, PrimitiveType.Cube,
                new[] { -0.69f, 0f, 0.69f }.Select(x => Matrix4x4.TRS(new Vector3(x, 2.842f, -0.58f),
                    Quaternion.identity, new Vector3(0.022f, 0.018f, 0.22f))), m.brushedMetal);

            ConfigureFixtureLight("Panel Wash Left", new Vector3(-0.68f, 2.84f, -0.58f), new Vector3(-1.25f, 1.20f, 0.35f));
            ConfigureFixtureLight("Panel Wash Right", new Vector3(0.68f, 2.84f, -0.58f), new Vector3(1.25f, 1.20f, 0.35f));
        }

        static void ConfigureFixtureLight(string name, Vector3 position, Vector3 target)
        {
            var light = Find(name)?.GetComponent<Light>();
            if (light == null)
                throw new InvalidOperationException("Existing light missing: " + name);
            light.transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position));
            light.type = LightType.Spot;
            light.color = new Color(1f, 0.88f, 0.72f);
            light.intensity = 0.90f;
            light.range = 4.2f;
            light.spotAngle = 105f;
            light.innerSpotAngle = 75f;
            light.shadows = LightShadows.None;
            EditorUtility.SetDirty(light);
            EditorUtility.SetDirty(light.transform);
        }

        static void BuildFinishedUtilityRack(Transform parent, Materials m)
        {
            CreateBatch("Finished Upper Right Utility Rack Frame", parent, PrimitiveType.Cube, new[]
            {
                Matrix4x4.TRS(new Vector3(2.36f, 1.91f, 1.01f), Quaternion.identity, new Vector3(0.08f, 1.18f, 0.07f)),
                Matrix4x4.TRS(new Vector3(2.36f, 1.91f, 1.70f), Quaternion.identity, new Vector3(0.08f, 1.18f, 0.07f)),
                Matrix4x4.TRS(new Vector3(2.375f, 1.91f, 1.355f), Quaternion.identity, new Vector3(0.035f, 1.18f, 0.76f))
            }, m.darkMetal);
            CreateBatch("Finished Utility Rack Solid Shelves", parent, PrimitiveType.Cube,
                new[] { 1.40f, 1.78f, 2.16f, 2.48f }.Select(y => Matrix4x4.TRS(new Vector3(2.21f, y, 1.355f),
                    Quaternion.identity, new Vector3(0.34f, 0.045f, 0.78f))), m.brushedMetal);
            CreateBatch("Utility Rack QA Bins and Folded Props", parent, PrimitiveType.Cube, new[]
            {
                Matrix4x4.TRS(new Vector3(2.15f, 1.50f, 1.16f), Quaternion.identity, new Vector3(0.22f, 0.16f, 0.27f)),
                Matrix4x4.TRS(new Vector3(2.15f, 1.50f, 1.53f), Quaternion.identity, new Vector3(0.22f, 0.16f, 0.29f)),
                Matrix4x4.TRS(new Vector3(2.15f, 1.87f, 1.26f), Quaternion.Euler(5f, 0f, 0f), new Vector3(0.24f, 0.13f, 0.32f)),
                Matrix4x4.TRS(new Vector3(2.15f, 2.25f, 1.47f), Quaternion.identity, new Vector3(0.20f, 0.13f, 0.35f))
            }, m.utilityBlue);
            CreateBatch("Utility Rack Non Slip Shelf Liners", parent, PrimitiveType.Cube,
                new[] { 1.425f, 1.805f, 2.185f }.Select(y => Matrix4x4.TRS(new Vector3(2.03f, y, 1.355f),
                    Quaternion.identity, new Vector3(0.012f, 0.012f, 0.69f))), m.shelfLiner);
            CreateLabel(parent, "Utility Rack Identification", "QA TOOLS / CLEAN", new Vector3(2.015f, 2.43f, 1.355f),
                Quaternion.Euler(0f, 90f, 0f), 0.020f, new Color(0.65f, 0.94f, 1f), 14f);
        }

        static void CaptureRoomReflection(Transform artRoot)
        {
            const int size = 128;
            var oldProbe = artRoot.GetComponentsInChildren<ReflectionProbe>(true).SingleOrDefault();
            if (oldProbe == null)
                throw new InvalidOperationException("The base chrome reflection probe is missing.");
            var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(ReflectionPath);
            if (cubemap != null && cubemap.width != size)
            {
                AssetDatabase.DeleteAsset(ReflectionPath);
                cubemap = null;
            }
            var isNew = cubemap == null;
            if (isNew)
            {
                cubemap = new Cubemap(size, TextureFormat.RGBA32, true) { name = "Room Captured Lab Chrome Reflection" };
                AssetDatabase.CreateAsset(cubemap, ReflectionPath);
            }
            cubemap.name = "Room Captured Lab Chrome Reflection";
            cubemap.wrapMode = TextureWrapMode.Clamp;
            cubemap.filterMode = FilterMode.Trilinear;

            var cameraObject = new GameObject("Temporary Room Reflection Capture Camera");
            SceneManager.MoveGameObjectToScene(cameraObject, artRoot.gameObject.scene);
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, 1.28f, 0.58f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.018f, 0.022f, 0.024f, 1f);
            camera.nearClipPlane = 0.04f;
            camera.farClipPlane = 12f;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.cullingMask = ~0;

            var hidden = new List<Renderer>();
            foreach (var helmetName in new[] { "A1 - REFERENCE HELMET", "A2 - DEFECTIVE SCAN" })
            {
                var helmet = GameObject.Find(helmetName);
                if (helmet != null)
                    hidden.AddRange(helmet.GetComponentsInChildren<Renderer>(true));
            }
            var origin = UnityEngine.Object.FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
            if (origin != null)
                hidden.AddRange(origin.GetComponentsInChildren<Renderer>(true));
            var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            if (scanner != null)
                hidden.AddRange(scanner.GetComponentsInChildren<Renderer>(true));
            hidden = hidden.Distinct().ToList();
            var states = hidden.Select(item => item.enabled).ToArray();
            for (var i = 0; i < hidden.Count; ++i)
                hidden[i].enabled = false;

            try
            {
                if (!camera.RenderToCubemap(cubemap))
                    throw new InvalidOperationException("Unity failed to render the room into the chrome cubemap.");
                cubemap.imageContentsHash = Hash128.Compute("EnvironmentPass2:RoomCapture:128:v2");
                EditorUtility.SetDirty(cubemap);
            }
            finally
            {
                for (var i = 0; i < hidden.Count; ++i)
                    if (hidden[i] != null)
                        hidden[i].enabled = states[i];
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }

            oldProbe.mode = ReflectionProbeMode.Custom;
            oldProbe.customBakedTexture = cubemap;
            oldProbe.boxProjection = true;
            oldProbe.size = new Vector3(4.55f, 2.75f, 4.55f);
            oldProbe.center = new Vector3(0f, 0.10f, 0f);
            oldProbe.blendDistance = 0.35f;
            oldProbe.intensity = 1.18f;
            oldProbe.importance = 3;
            oldProbe.resolution = size;
            oldProbe.gameObject.name = "ROOM-CAPTURED 128px Chrome Reflection Probe";
            EditorUtility.SetDirty(oldProbe);
            EditorUtility.SetDirty(oldProbe.gameObject);
        }

        static GameObject CreateBatch(string name, Transform parent, PrimitiveType primitive,
            IEnumerable<Matrix4x4> transforms, Material material)
        {
            var matrices = transforms.ToArray();
            if (matrices.Length == 0)
                throw new InvalidOperationException("No geometry supplied for " + name);
            var temporary = GameObject.CreatePrimitive(primitive);
            var source = temporary.GetComponent<MeshFilter>().sharedMesh;
            var combines = matrices.Select(matrix => new CombineInstance { mesh = source, transform = matrix }).ToArray();
            var path = MeshRoot + "/" + Sanitize(name) + ".asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            var isNew = mesh == null;
            if (isNew)
                mesh = new Mesh();
            else
                mesh.Clear();
            mesh.name = name + " Mesh";
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.CombineMeshes(combines, true, true, false);
            mesh.RecalculateBounds();
            UnityEngine.Object.DestroyImmediate(temporary);
            if (isNew)
                AssetDatabase.CreateAsset(mesh, path);
            else
                EditorUtility.SetDirty(mesh);
            s_AddedTriangles += mesh.triangles.Length / 3;

            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = root.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            GameObjectUtility.SetStaticEditorFlags(root, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ReflectionProbeStatic);
            return root;
        }

        static void CreateLabel(Transform parent, string name, string text, Vector3 position,
            Quaternion rotation, float scale, Color color, float width)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(position, rotation);
            root.transform.localScale = Vector3.one * scale;
            var label = root.AddComponent<TextMeshPro>();
            label.text = text;
            label.fontSize = 3.0f;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.color = color;
            label.outlineWidth = 0.11f;
            label.outlineColor = new Color32(0, 8, 12, 235);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.rectTransform.sizeDelta = new Vector2(width, 1.5f);
        }

        static Transform FindChild(Transform parent, string name) =>
            parent.GetComponentsInChildren<Transform>(true).FirstOrDefault(item => item.name == name);

        static GameObject Find(string name) =>
            UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include)
                .FirstOrDefault(item => item.name == name)?.gameObject;

        static string Sanitize(string value) => new string(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) || character == ' ' ? '_' : character).ToArray());

        static void Set(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        static void Set(Material material, string property, Color value)
        {
            if (material.HasProperty(property)) material.SetColor(property, value);
        }

        static void Set(Material material, string property, Texture value)
        {
            if (material.HasProperty(property)) material.SetTexture(property, value);
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        [MenuItem("Helmet Inspection/Legacy Generators/Environment Polish Pass 2/Render Saved HMD View")]
        public static void RenderSavedHmdView()
        {
            EditorSceneManager.OpenScene(EnvironmentArtPassInstaller.ScenePath, OpenSceneMode.Single);
            var origin = UnityEngine.Object.FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
            var camera = origin != null ? origin.Camera : null;
            if (camera == null)
                throw new InvalidOperationException("Saved HMD camera is missing.");

            var target = new RenderTexture(1600, 1000, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
                name = "Pass 2 Saved HMD Review"
            };
            target.Create();
            var texture = new Texture2D(1600, 1000, TextureFormat.RGBA32, false);
            var priorActive = RenderTexture.active;
            var priorTarget = camera.targetTexture;
            var priorStereo = camera.stereoTargetEye;
            var priorMsaa = camera.allowMSAA;
            try
            {
                camera.stereoTargetEye = StereoTargetEyeMask.None;
                camera.allowMSAA = false;
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0);
                texture.Apply(false, false);
                var output = Root + "/Documentation/EnvironmentPolishPass2SavedHmd.png";
                File.WriteAllBytes(output, texture.EncodeToPNG());
                AssetDatabase.ImportAsset(output, ImportAssetOptions.ForceSynchronousImport);
                Debug.Log("[EnvironmentPolishPass2] Rendered saved HMD view to " + output);
            }
            finally
            {
                RenderTexture.active = priorActive;
                camera.targetTexture = priorTarget;
                camera.stereoTargetEye = priorStereo;
                camera.allowMSAA = priorMsaa;
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [MenuItem("Helmet Inspection/Legacy Generators/Environment Polish Pass 2/Render Control Panel View")]
        public static void RenderControlPanelView()
        {
            var scene = EditorSceneManager.OpenScene(EnvironmentArtPassInstaller.ScenePath, OpenSceneMode.Single);
            var cameraObject = new GameObject("Temporary Pass 2 Control Review Camera");
            SceneManager.MoveGameObjectToScene(cameraObject, scene);
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(1.05f, 1.52f, -1.35f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(2.31f, 1.08f, -0.47f) - camera.transform.position);
            camera.fieldOfView = 55f;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 10f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.018f, 0.022f, 0.024f, 1f);
            camera.allowHDR = true;
            camera.allowMSAA = false;

            var hidden = new List<Renderer>();
            var origin = UnityEngine.Object.FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
            if (origin != null)
                hidden.AddRange(origin.GetComponentsInChildren<Renderer>(true));
            var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            if (scanner != null)
                hidden.AddRange(scanner.GetComponentsInChildren<Renderer>(true));
            hidden = hidden.Distinct().ToList();
            var states = hidden.Select(item => item.enabled).ToArray();
            foreach (var renderer in hidden)
                renderer.enabled = false;

            var target = new RenderTexture(1200, 900, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            target.Create();
            var texture = new Texture2D(1200, 900, TextureFormat.RGBA32, false);
            var priorActive = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, 1200, 900), 0, 0);
                texture.Apply(false, false);
                var output = Root + "/Documentation/EnvironmentPolishPass2Controls.png";
                File.WriteAllBytes(output, texture.EncodeToPNG());
                AssetDatabase.ImportAsset(output, ImportAssetOptions.ForceSynchronousImport);
                Debug.Log("[EnvironmentPolishPass2] Rendered control panel view to " + output);
            }
            finally
            {
                RenderTexture.active = priorActive;
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                for (var i = 0; i < hidden.Count; ++i)
                    if (hidden[i] != null)
                        hidden[i].enabled = states[i];
            }
        }
    }

    public static class EnvironmentPolishPass2Verifier
    {
        const string Root = "Assets/HelmetInspection";

        [MenuItem("Helmet Inspection/Legacy Generators/Environment Polish Pass 2/Verify Pass 2 and Gameplay")]
        public static void VerifyAll()
        {
            EnvironmentArtPassVerifier.VerifyAll();
            EditorSceneManager.OpenScene(EnvironmentArtPassInstaller.ScenePath, OpenSceneMode.Single);
            var artRoot = GameObject.Find(EnvironmentArtPassInstaller.ArtRootName);
            var passRoot = GameObject.Find("ENVIRONMENT POLISH PASS 2 - Completed");
            Require(artRoot != null && passRoot != null && passRoot.transform.IsChildOf(artRoot.transform),
                "The pass 2 visual hierarchy is missing.");
            Require(passRoot.GetComponentsInChildren<Collider>(true).Length == 0,
                "Pass 2 added a collider.");
            Require(passRoot.GetComponentsInChildren<MonoBehaviour>(true).All(item => item is TMP_Text),
                "Pass 2 added runtime behavior.");

            var profile = GameObject.Find("Center Screen - Helmet Shell Profile")?.GetComponent<Renderer>();
            var status = GameObject.Find("Center Screen - QA Status Dashboard")?.GetComponent<Renderer>();
            Require(profile?.sharedMaterial?.mainTexture != null && status?.sharedMaterial?.mainTexture != null &&
                    profile.sharedMaterial.mainTexture != status.sharedMaterial.mainTexture,
                "The two diagnostic screens do not have distinct authored textures.");
            foreach (var required in new[]
                     {
                         "Twin Screen Green Status LEDs", "START RESET Recessed Brushed Control Plate",
                         "START RESET Four Retaining Screws", "Recessed Linear Ceiling Fixture Housing",
                         "Linear Fixture Emissive Diffuser", "Finished Upper Right Utility Rack Frame",
                         "Finished Utility Rack Solid Shelves"
                     })
                Require(GameObject.Find(required) != null, "Required pass 2 detail is missing: " + required);

            var origin = UnityEngine.Object.FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
            var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            var camera = origin != null ? origin.Camera : null;
            var left = origin != null ? FindChild(origin.transform, "Left Controller") : null;
            var right = origin != null ? FindChild(origin.transform, "Right Controller") : null;
            Require(camera != null && left != null && right != null && scanner != null && scanner.HomeMount != null,
                "XR fallback verification setup is incomplete.");
            Require(Vector3.Distance(camera.transform.position, left.position) > 0.35f &&
                    Vector3.Distance(camera.transform.position, right.position) > 0.35f &&
                    Vector3.Distance(camera.transform.position, scanner.transform.position) > 0.35f,
                "A controller/scanner visual still clips the saved HMD pose.");
            Require(Vector3.Distance(scanner.transform.position, scanner.HomeMount.position) < 0.0001f &&
                    Quaternion.Angle(scanner.transform.rotation, scanner.HomeMount.rotation) < 0.01f &&
                    Vector3.Distance(scanner.transform.localScale, Vector3.one) < 0.0001f,
                "Scanner no longer starts exactly at its valid home socket.");
            var socket = scanner.HomeMount.GetComponent<XRSocketInteractor>();
            Require(socket != null && socket.startingSelectedInteractable == scanner.GetComponent<XRGrabInteractable>(),
                "Scanner auto-attach/hand-swap starting selection was changed.");

            var graph = File.ReadAllText(Root + "/Shaders/TriplanarHelmet.shadergraph");
            Require(graph.Contains("0.9900000095367432") && graph.Contains("0.949999988079071"),
                "Helmet mirror metallic/smoothness constants are wrong.");
            var probe = artRoot.GetComponentsInChildren<ReflectionProbe>(true).SingleOrDefault();
            Require(probe != null && probe.mode == ReflectionProbeMode.Custom &&
                    probe.customBakedTexture is Cubemap cube && cube.width == 128 &&
                    AssetDatabase.GetAssetPath(cube).EndsWith("C_RoomCapturedChromeReflection.asset", StringComparison.Ordinal),
                "The 128 px room-captured chrome reflection is missing.");

            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include).Length;
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include).Length;
            Require(renderers <= 150, $"Renderer budget exceeded ({renderers}/150).");
            Require(lights <= 6, $"Light budget exceeded ({lights}/6).");
            Debug.Log("[EnvironmentPolishPass2Verification] PASS: clean default HMD view, room-captured mirror " +
                      "helmets, twin screens, physical START/RESET controls, finished ceiling/rack, and all " +
                      "existing 10-defect Quest gameplay validations passed.");
        }

        static Transform FindChild(Transform parent, string name) =>
            parent.GetComponentsInChildren<Transform>(true).FirstOrDefault(item => item.name == name);

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
