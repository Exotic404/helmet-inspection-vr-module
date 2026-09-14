using System;
using System.Collections.Generic;
using System.Globalization;
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

namespace HelmetInspection.Editor
{
    /// <summary>
    /// A re-runnable, editor-only visual dressing pass. It deliberately does not add
    /// runtime behaviours or alter any pre-existing collider, hotspot, UI text, or
    /// non-light transform.
    /// </summary>
    public static class EnvironmentArtPassInstaller
    {
        public const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        public const string ArtRootName = "VISUAL ART PASS - Warm Metrology Lab";
        const string Root = "Assets/HelmetInspection";
        const string GeneratedRoot = Root + "/EnvironmentArt/Generated";
        const string MaterialRoot = GeneratedRoot + "/Materials";
        const string TextureRoot = GeneratedRoot + "/Textures";
        const string MeshRoot = GeneratedRoot + "/Meshes";
        const string HelmetMaterialPath = Root + "/Materials/M_Helmet_Triplanar.mat";
        const string HelmetTexturePath = Root + "/Textures/T_HelmetMicrotexture.asset";
        const string GreenMarkerPath = Root + "/Materials/M_GreenEmission.mat";
        const string ReflectionPath = TextureRoot + "/C_LabChromeReflection.asset";
        const string ReportPath = Root + "/Documentation/EnvironmentArtPassReport.txt";

        static int s_AddedTriangles;

        readonly struct BoxSpec
        {
            public readonly Vector3 center;
            public readonly Vector3 size;
            public readonly Quaternion rotation;

            public BoxSpec(Vector3 position, Vector3 dimensions)
                : this(position, dimensions, Quaternion.identity) { }

            public BoxSpec(Vector3 position, Vector3 dimensions, Quaternion orientation)
            {
                center = position;
                size = dimensions;
                rotation = orientation;
            }
        }

        readonly struct CylinderSpec
        {
            public readonly Vector3 center;
            public readonly float radius;
            public readonly float depth;
            public readonly Quaternion rotation;

            public CylinderSpec(Vector3 position, float cylinderRadius, float cylinderDepth,
                Quaternion orientation)
            {
                center = position;
                radius = cylinderRadius;
                depth = cylinderDepth;
                rotation = orientation;
            }
        }

        sealed class ArtMaterials
        {
            public Material upperWall;
            public Material wainscot;
            public Material accentWall;
            public Material trim;
            public Material concrete;
            public Material ceiling;
            public Material paintedMetal;
            public Material brushedSteel;
            public Material worktop;
            public Material wood;
            public Material rubberCyan;
            public Material rubberAmber;
            public Material lane;
            public Material monitor;
            public Material dashboardCyan;
            public Material dashboardAmber;
            public Material diffuserWarm;
            public Material diffuserCool;
            public Material paper;
        }

        [MenuItem("Helmet Inspection/Legacy Generators/Environment Art Pass/Install Visual Pass")]
        public static void Install()
        {
            if (!LegacyGenerationGuard.TryBegin("Environment Art Pass", out var guard))
                return;
            using var legacyScope = guard;
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var environment = GameObject.Find("ENVIRONMENT - QA Metrology Lab");
            if (environment == null)
                throw new InvalidOperationException("The authored environment root is missing.");

            var previousRoot = GameObject.Find(ArtRootName);
            if (previousRoot != null)
                UnityEngine.Object.DestroyImmediate(previousRoot);

            var protectedState = ProtectedSceneState.Capture(scene);
            EnsureFolders();
            s_AddedTriangles = 0;
            var materials = CreateArtMaterials();
            RestyleExistingVisuals(materials);
            ConfigureChromeHelmets();

            var artRoot = new GameObject(ArtRootName);
            artRoot.transform.SetParent(environment.transform, false);
            BuildRoomShell(artRoot.transform, materials);
            BuildLightingFixtures(artRoot.transform, materials);
            BuildFurnitureAndProps(artRoot.transform, materials);
            BuildInspectionDressing(artRoot.transform, materials);
            ConfigureExistingLights();
            CreateChromeReflectionProbe(artRoot.transform);

            protectedState.AssertPreserved(scene);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Failed to save the environment art pass scene.");
            AssetDatabase.SaveAssets();
            ForceReimportGeneratedAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            WriteReport(scene, protectedState.ColliderCount);
            Debug.Log($"[EnvironmentArtPass] Installed visual-only lab dressing: {s_AddedTriangles} added triangles, " +
                      $"{UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include).Length} renderers, " +
                      $"{UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include).Length} total lights. " +
                      "Protected transforms, colliders, UI text, and DefectSet_A2 remained byte-for-byte stable.");
        }

        static void EnsureFolders()
        {
            EnsureFolder(Root, "EnvironmentArt");
            EnsureFolder(Root + "/EnvironmentArt", "Generated");
            EnsureFolder(GeneratedRoot, "Materials");
            EnsureFolder(GeneratedRoot, "Textures");
            EnsureFolder(GeneratedRoot, "Meshes");
            if (!AssetDatabase.IsValidFolder(Root + "/Documentation"))
                EnsureFolder(Root, "Documentation");
        }

        static void EnsureFolder(string parent, string child)
        {
            var path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, child);
        }

        static void ForceReimportGeneratedAssets()
        {
            // These assets are authored procedurally. A forced import keeps Unity's
            // artifact cache synchronized after an in-place texture/material update;
            // otherwise a prior native preview can temporarily display the content
            // of another generated texture even though the serialized GUID is right.
            var options = ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport;
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { GeneratedRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!AssetDatabase.IsValidFolder(path))
                    AssetDatabase.ImportAsset(path, options);
            }
        }

        static ArtMaterials CreateArtMaterials()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                throw new InvalidOperationException("URP Lit shader is unavailable.");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlit == null)
                throw new InvalidOperationException("URP Unlit shader is unavailable.");

            var wallTexture = CreateSurfaceTexture("T_WarmPaint", 128, (x, y) =>
            {
                var grain = Mathf.Sin(x * 0.81f + y * 1.73f) * 0.012f +
                            Mathf.Sin(x * 2.17f - y * 0.43f) * 0.006f;
                return new Color(0.58f + grain, 0.47f + grain, 0.34f + grain, 1f);
            });
            var panelTexture = CreateSurfaceTexture("T_PaintedPanel", 128, (x, y) =>
            {
                var brushed = Mathf.Sin(y * 2.35f + Mathf.Sin(x * 0.12f)) * 0.018f;
                return new Color(0.28f + brushed, 0.25f + brushed, 0.21f + brushed, 1f);
            });
            var concreteTexture = CreateSurfaceTexture("T_MicroCement", 128, (x, y) =>
            {
                var noise = Mathf.Sin(x * 1.91f + y * 2.73f) * 0.020f +
                            Mathf.Sin(x * 0.27f - y * 1.13f) * 0.014f;
                return new Color(0.48f + noise, 0.465f + noise, 0.43f + noise, 1f);
            });
            var ceilingTexture = CreateSurfaceTexture("T_AcousticPanel", 128, (x, y) =>
            {
                var pore = ((x * 37 + y * 73) % 101 < 4) ? -0.12f : 0f;
                return new Color(0.84f + pore, 0.81f + pore, 0.74f + pore, 1f);
            });
            var steelTexture = CreateSurfaceTexture("T_BrushedSteel", 128, (x, y) =>
            {
                var line = Mathf.Sin(x * 3.7f) * 0.035f + Mathf.Sin(x * 0.71f) * 0.018f;
                return new Color(0.58f + line, 0.60f + line, 0.61f + line, 1f);
            });
            var woodTexture = CreateSurfaceTexture("T_WarmWood", 128, (x, y) =>
            {
                var grain = Mathf.Sin(x * 0.23f + Mathf.Sin(y * 0.12f) * 2f) * 0.055f;
                return new Color(0.43f + grain, 0.20f + grain * 0.45f, 0.075f + grain * 0.18f, 1f);
            });
            var rubberTexture = CreateSurfaceTexture("T_RubberDimple", 128, (x, y) =>
            {
                var dx = (x % 12) - 6f;
                var dy = (y % 12) - 6f;
                var dimple = dx * dx + dy * dy < 3.3f ? -0.10f : 0f;
                var value = 0.72f + dimple;
                return new Color(value, value, value, 1f);
            });
            CreateDashboardTexture();

            return new ArtMaterials
            {
                upperWall = CreateLitMaterial("M_UpperWallWarm", lit, Color.white, 0f, 0.24f,
                    wallTexture, new Vector2(5f, 3f)),
                wainscot = CreateLitMaterial("M_WainscotPanel", lit, Color.white, 0.06f, 0.34f,
                    panelTexture, new Vector2(6f, 2f)),
                accentWall = CreateLitMaterial("M_AccentWallCharcoal", lit,
                    new Color(0.020f, 0.018f, 0.016f, 1f), 0.18f, 0.36f),
                trim = CreateLitMaterial("M_WarmBronzeTrim", lit,
                    new Color(0.42f, 0.23f, 0.09f, 1f), 0.72f, 0.52f),
                concrete = CreateLitMaterial("M_PolishedMicroCement", lit, Color.white, 0.06f, 0.37f,
                    concreteTexture, new Vector2(7f, 7f)),
                ceiling = CreateLitMaterial("M_AcousticCeiling", lit, Color.white, 0f, 0.18f,
                    ceilingTexture, new Vector2(2f, 2f)),
                paintedMetal = CreateLitMaterial("M_PaintedLabMetal", lit,
                    new Color(0.20f, 0.19f, 0.17f, 1f), 0.48f, 0.39f),
                brushedSteel = CreateLitMaterial("M_ArtBrushedSteel", lit, Color.white, 0.88f, 0.54f,
                    steelTexture, new Vector2(7f, 1f)),
                worktop = CreateLitMaterial("M_DenseCompositeWorktop", lit,
                    new Color(0.115f, 0.105f, 0.09f, 1f), 0.12f, 0.43f),
                wood = CreateLitMaterial("M_WarmWood", lit, Color.white, 0f, 0.36f,
                    woodTexture, new Vector2(5f, 2f)),
                rubberCyan = CreateLitMaterial("M_ReferenceRubberMat", lit,
                    new Color(0.035f, 0.20f, 0.23f, 1f), 0f, 0.26f, rubberTexture, new Vector2(8f, 8f)),
                rubberAmber = CreateLitMaterial("M_InspectionRubberMat", lit,
                    new Color(0.25f, 0.105f, 0.025f, 1f), 0f, 0.26f, rubberTexture, new Vector2(8f, 8f)),
                lane = CreateLitMaterial("M_InsetWayfinding", lit,
                    new Color(0.055f, 0.35f, 0.39f, 1f), 0.12f, 0.42f),
                monitor = CreateLitMaterial("M_QADashboard", unlit,
                    new Color(0.008f, 0.020f, 0.027f, 1f), 0f, 0f),
                dashboardCyan = CreateLitMaterial("M_QADashboardCyan", unlit,
                    new Color(0.08f, 0.68f, 0.82f, 1f), 0f, 0f),
                dashboardAmber = CreateLitMaterial("M_QADashboardAmber", unlit,
                    new Color(0.95f, 0.35f, 0.055f, 1f), 0f, 0f),
                diffuserWarm = CreateLitMaterial("M_WarmLuminaireDiffuser", lit,
                    new Color(0.58f, 0.42f, 0.24f, 1f), 0f, 0.55f, null, Vector2.one,
                    new Color(1f, 0.55f, 0.20f, 1f) * 0.55f),
                diffuserCool = CreateLitMaterial("M_CoolLuminaireDiffuser", lit,
                    new Color(0.30f, 0.52f, 0.60f, 1f), 0f, 0.55f, null, Vector2.one,
                    new Color(0.15f, 0.55f, 0.75f, 1f) * 0.50f),
                paper = CreateLitMaterial("M_QADocumentPaper", lit,
                    new Color(0.76f, 0.70f, 0.59f, 1f), 0f, 0.16f)
            };
        }

        static Texture2D CreateSurfaceTexture(string name, int size, Func<int, int, Color> sample)
        {
            var path = TextureRoot + "/" + name + ".asset";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var isNew = texture == null;
            if (isNew)
                texture = new Texture2D(size, size, TextureFormat.RGBA32, true, false);
            else if (!texture.Reinitialize(size, size, TextureFormat.RGBA32, true))
                throw new InvalidOperationException($"Could not resize generated texture '{path}'.");
            texture.name = name;
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Bilinear;
            texture.anisoLevel = 2;
            var pixels = new Color[size * size];
            for (var y = 0; y < size; ++y)
            for (var x = 0; x < size; ++x)
                pixels[y * size + x] = sample(x, y);
            texture.SetPixels(pixels);
            texture.Apply(true, false);
            // Native Texture2D .asset files otherwise serialize a zero content hash.
            // Unity 6 can then reuse the wrong cached GPU image across generated
            // textures (for example the dashboard on the painted wall).
            texture.imageContentsHash = Hash128.Compute($"EnvironmentArt:{name}:{size}:v4");
            if (isNew)
                AssetDatabase.CreateAsset(texture, path);
            else
                EditorUtility.SetDirty(texture);
            return texture;
        }

        static Texture2D CreateDashboardTexture()
        {
            const int width = 512;
            const int height = 256;
            var path = TextureRoot + "/T_QADashboard.asset";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var isNew = texture == null;
            if (isNew)
                texture = new Texture2D(width, height, TextureFormat.RGBA32, true, false);
            else if (!texture.Reinitialize(width, height, TextureFormat.RGBA32, true))
                throw new InvalidOperationException($"Could not resize generated texture '{path}'.");
            texture.name = "T_QADashboard";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.anisoLevel = 2;
            var pixels = new Color[width * height];
            var background = new Color(0.012f, 0.028f, 0.036f, 1f);
            var grid = new Color(0.035f, 0.14f, 0.17f, 1f);
            var cyan = new Color(0.10f, 0.75f, 0.90f, 1f);
            var amber = new Color(1f, 0.46f, 0.10f, 1f);
            for (var y = 0; y < height; ++y)
            for (var x = 0; x < width; ++x)
            {
                var color = background;
                if (x % 32 <= 1 || y % 32 <= 1)
                    color = grid;
                var graphY = 78 + Mathf.RoundToInt(Mathf.Sin(x * 0.041f) * 23f + Mathf.Sin(x * 0.013f) * 16f);
                if (x >= 26 && x < 330 && Mathf.Abs(y - graphY) <= 2)
                    color = cyan;
                if (x >= 358 && x <= 470 && y >= 35 && y <= 54 + ((x - 358) % 25) * 5)
                    color = (x / 16) % 2 == 0 ? amber : cyan;
                if (y >= 210 && y <= 226 && x >= 24 && x <= 245)
                    color = cyan * 0.72f;
                if (y >= 210 && y <= 226 && x >= 265 && x <= 475)
                    color = amber * 0.72f;
                if ((x >= 22 && x <= 28 || x >= 484 && x <= 490) && y >= 18 && y <= 238)
                    color = cyan * 0.55f;
                pixels[y * width + x] = color;
            }
            texture.SetPixels(pixels);
            texture.Apply(true, false);
            texture.imageContentsHash = Hash128.Compute($"EnvironmentArt:T_QADashboard:{width}x{height}:v4");
            if (isNew)
                AssetDatabase.CreateAsset(texture, path);
            else
                EditorUtility.SetDirty(texture);
            return texture;
        }

        static Material CreateLitMaterial(string name, Shader shader, Color color, float metallic,
            float smoothness, Texture texture = null, Vector2? textureScale = null, Color? emission = null)
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
            SetIfPresent(material, "_BaseColor", color);
            SetIfPresent(material, "_Color", color);
            SetIfPresent(material, "_Metallic", metallic);
            SetIfPresent(material, "_Smoothness", smoothness);
            SetIfPresent(material, "_BaseMap", texture);
            if (material.HasProperty("_BaseMap"))
                material.SetTextureScale("_BaseMap", textureScale ?? Vector2.one);
            if (emission.HasValue)
            {
                material.EnableKeyword("_EMISSION");
                SetIfPresent(material, "_EmissionColor", emission.Value);
                SetIfPresent(material, "_EmissionMap", texture);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            }
            else
            {
                material.DisableKeyword("_EMISSION");
                SetIfPresent(material, "_EmissionColor", Color.black);
                SetIfPresent(material, "_EmissionMap", null);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            if (isNew)
                AssetDatabase.CreateAsset(material, path);
            else
                EditorUtility.SetDirty(material);
            return material;
        }

        static void RestyleExistingVisuals(ArtMaterials m)
        {
            SetMaterial("Floor", m.concrete);
            foreach (var wall in new[] { "Back Wall", "Front Wall", "Left Wall", "Right Wall" })
                SetMaterial(wall, m.upperWall);
            SetMaterial("Ceiling", m.ceiling);
            SetMaterial("Back Wall Navy Band", m.accentWall);
            SetMaterial("Floor Inlay Left", m.lane);
            SetMaterial("Floor Inlay Right", m.lane);

            SetMaterial("Safety Cabinet", m.paintedMetal);
            var legacyDoor = Find("Cabinet Door")?.GetComponent<Renderer>();
            if (legacyDoor != null)
                legacyDoor.enabled = false;
            SetMaterialIfPresent("Instrument Cart", m.wood);
            SetMaterialIfPresent("Instrument Cart Shelf", m.wood);
            SetMaterialIfPresent("Wall Monitor Housing", m.paintedMetal);
            SetMaterialIfPresent("Wall Monitor Screen", m.monitor);
            SetMaterial("Ventilation Header", m.brushedSteel);
            SetMaterial("Observation Window", m.diffuserCool);
            SetMaterialIfPresent("Observation Window Header", m.trim);

            SetMaterial("Table Top", m.worktop);
            SetMaterial("Front Fascia", m.paintedMetal);
            SetMaterialAll("Table Leg", m.paintedMetal);
            SetMaterial("Reference Zone Inlay", m.rubberCyan);
            SetMaterial("Inspection Zone Inlay", m.rubberAmber);
            SetMaterialAll("Stand Base", m.paintedMetal);
            SetMaterialAllIfPresent("Cart Wheel", AssetDatabase.LoadAssetAtPath<Material>(Root + "/Materials/M_BlackRubber.mat"));
        }

        static void ConfigureChromeHelmets()
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(HelmetTexturePath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(HelmetMaterialPath);
            if (texture == null || material == null)
                throw new InvalidOperationException("The triplanar helmet material or microtexture is missing.");

            var colors = new Color[texture.width * texture.height];
            for (var y = 0; y < texture.height; ++y)
            for (var x = 0; x < texture.width; ++x)
            {
                var polish = Mathf.Sin(x * 0.73f + y * 0.17f) * 0.018f +
                             Mathf.Sin((x + y) * 2.31f) * 0.006f;
                var value = 0.84f + polish;
                colors[y * texture.width + x] = new Color(value * 0.97f, value * 0.988f, value, 1f);
            }
            texture.name = "T_HelmetMicrotexture";
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Bilinear;
            texture.SetPixels(colors);
            texture.Apply(true, false);
            SetIfPresent(material, "_Grid_Texture", texture);
            SetIfPresent(material, "_SlopeColor", new Color(0.80f, 0.83f, 0.87f, 1f));
            SetIfPresent(material, "_Slope", 1f);
            material.enableInstancing = true;
            EditorUtility.SetDirty(texture);
            EditorUtility.SetDirty(material);

            var found = AssetDatabase.LoadAssetAtPath<Material>(GreenMarkerPath);
            if (found != null)
            {
                SetIfPresent(found, "_BaseColor", new Color(0.08f, 1f, 0.30f, 1f));
                SetIfPresent(found, "_Color", new Color(0.08f, 1f, 0.30f, 1f));
                found.EnableKeyword("_EMISSION");
                SetIfPresent(found, "_EmissionColor", new Color(0.06f, 1f, 0.25f, 1f) * 3.2f);
                EditorUtility.SetDirty(found);
            }
        }

        static void BuildRoomShell(Transform parent, ArtMaterials m)
        {
            var lowerPanels = new List<BoxSpec>
            {
                new(new Vector3(0f, 0.55f, 2.420f), new Vector3(4.94f, 1.10f, 0.025f)),
                new(new Vector3(0f, 0.55f, -2.420f), new Vector3(4.94f, 1.10f, 0.025f)),
                new(new Vector3(-2.420f, 0.55f, 0f), new Vector3(0.025f, 1.10f, 4.94f)),
                new(new Vector3(2.420f, 0.55f, 0f), new Vector3(0.025f, 1.10f, 4.94f))
            };
            CreateBoxBatch("Lower Wainscot Panel Field", parent, lowerPanels, m.wainscot);

            var trim = new List<BoxSpec>
            {
                new(new Vector3(0f, 0.065f, 2.395f), new Vector3(4.90f, 0.13f, 0.045f)),
                new(new Vector3(0f, 0.065f, -2.395f), new Vector3(4.90f, 0.13f, 0.045f)),
                new(new Vector3(-2.395f, 0.065f, 0f), new Vector3(0.045f, 0.13f, 4.90f)),
                new(new Vector3(2.395f, 0.065f, 0f), new Vector3(0.045f, 0.13f, 4.90f)),
                new(new Vector3(0f, 1.115f, 2.400f), new Vector3(4.90f, 0.055f, 0.040f)),
                new(new Vector3(0f, 1.115f, -2.400f), new Vector3(4.90f, 0.055f, 0.040f)),
                new(new Vector3(-2.400f, 1.115f, 0f), new Vector3(0.040f, 0.055f, 4.90f)),
                new(new Vector3(2.400f, 1.115f, 0f), new Vector3(0.040f, 0.055f, 4.90f))
            };
            for (var value = -2f; value <= 2.01f; value += 0.8f)
            {
                trim.Add(new BoxSpec(new Vector3(value, 0.58f, 2.395f), new Vector3(0.025f, 0.96f, 0.038f)));
                trim.Add(new BoxSpec(new Vector3(value, 0.58f, -2.395f), new Vector3(0.025f, 0.96f, 0.038f)));
                trim.Add(new BoxSpec(new Vector3(-2.395f, 0.58f, value), new Vector3(0.038f, 0.96f, 0.025f)));
                trim.Add(new BoxSpec(new Vector3(2.395f, 0.58f, value), new Vector3(0.038f, 0.96f, 0.025f)));
            }
            CreateBoxBatch("Baseboards Wainscot Caps and Battens", parent, trim, m.trim);

            CreateBoxBatch("Dark Charcoal Table Accent Wall", parent, new[]
            {
                new BoxSpec(new Vector3(0f, 1.53f, 2.407f), new Vector3(2.52f, 2.68f, 0.026f))
            }, m.accentWall);
            var accentSeams = new List<BoxSpec>();
            for (var x = -0.84f; x <= 0.85f; x += 0.42f)
                accentSeams.Add(new BoxSpec(new Vector3(x, 1.53f, 2.388f), new Vector3(0.010f, 2.58f, 0.012f)));
            accentSeams.Add(new BoxSpec(new Vector3(0f, 2.80f, 2.388f), new Vector3(2.45f, 0.012f, 0.012f)));
            CreateBoxBatch("Accent Wall Panel Lines", parent, accentSeams, m.trim);

            var floorSeams = new List<BoxSpec>();
            foreach (var x in new[] { -1.25f, 0f, 1.25f })
                floorSeams.Add(new BoxSpec(new Vector3(x, 0.0025f, 0f), new Vector3(0.012f, 0.005f, 4.86f)));
            foreach (var z in new[] { -1.25f, 0f, 1.25f })
                floorSeams.Add(new BoxSpec(new Vector3(0f, 0.0025f, z), new Vector3(4.86f, 0.005f, 0.012f)));
            foreach (var x in new[] { -1.795f, -1.705f, 1.705f, 1.795f })
                floorSeams.Add(new BoxSpec(new Vector3(x, 0.005f, 0f), new Vector3(0.018f, 0.009f, 4.50f)));
            CreateBoxBatch("Microcement Expansion Joints and Lane Edges", parent, floorSeams, m.trim);

            var ceilingPanels = new List<BoxSpec>();
            foreach (var x in new[] { -1.84f, -0.61f, 0.61f, 1.84f })
            foreach (var z in new[] { -1.84f, -0.61f, 0.61f, 1.84f })
                ceilingPanels.Add(new BoxSpec(new Vector3(x, 2.955f, z), new Vector3(1.16f, 0.018f, 1.16f)));
            CreateBoxBatch("Acoustic Ceiling Tile Array", parent, ceilingPanels, m.ceiling);
        }

        static void BuildLightingFixtures(Transform parent, ArtMaterials m)
        {
            var rotations = Quaternion.identity;
            var fixtureCenters = new[]
            {
                new Vector3(-0.55f, 2.925f, 0.42f),
                new Vector3(0.55f, 2.925f, 0.42f),
                new Vector3(-1.45f, 2.925f, -0.55f),
                new Vector3(1.45f, 2.925f, -0.55f)
            };
            CreateCylinderBatch("Recessed Luminaire Housings", parent,
                fixtureCenters.Select(center => new CylinderSpec(center, 0.115f, 0.045f, rotations)), m.paintedMetal);
            CreateCylinderBatch("Cool Fixture Diffusers", parent, new[]
            {
                new CylinderSpec(new Vector3(-0.55f, 2.900f, 0.42f), 0.073f, 0.008f, rotations),
                new CylinderSpec(new Vector3(1.45f, 2.900f, -0.55f), 0.073f, 0.008f, rotations)
            }, m.dashboardCyan);
            CreateCylinderBatch("Warm Fixture Diffusers", parent, new[]
            {
                new CylinderSpec(new Vector3(0.55f, 2.900f, 0.42f), 0.073f, 0.008f, rotations),
                new CylinderSpec(new Vector3(-1.45f, 2.900f, -0.55f), 0.073f, 0.008f, rotations)
            }, m.dashboardAmber);
        }

        static void BuildFurnitureAndProps(Transform parent, ArtMaterials m)
        {
            var cabinetDoors = new[]
            {
                new BoxSpec(new Vector3(-2.300f, 0.88f, 1.392f), new Vector3(0.222f, 1.34f, 0.025f)),
                new BoxSpec(new Vector3(-2.060f, 0.88f, 1.392f), new Vector3(0.222f, 1.34f, 0.025f)),
                new BoxSpec(new Vector3(-2.18f, 0.105f, 1.375f), new Vector3(0.42f, 0.12f, 0.035f)),
                new BoxSpec(new Vector3(-2.18f, 1.62f, 1.375f), new Vector3(0.43f, 0.055f, 0.035f))
            };
            CreateBoxBatch("Cabinet Twin Doors and Kick Plate", parent, cabinetDoors, m.paintedMetal);
            CreateBoxBatch("Cabinet Handles and Identification Rail", parent, new[]
            {
                new BoxSpec(new Vector3(-2.245f, 0.92f, 1.368f), new Vector3(0.020f, 0.31f, 0.028f)),
                new BoxSpec(new Vector3(-2.115f, 0.92f, 1.368f), new Vector3(0.020f, 0.31f, 0.028f)),
                new BoxSpec(new Vector3(-2.18f, 1.47f, 1.365f), new Vector3(0.30f, 0.09f, 0.025f))
            }, m.brushedSteel);

            var cartFrame = new List<BoxSpec>();
            foreach (var x in new[] { 1.85f, 2.31f })
            foreach (var z in new[] { 1.07f, 1.63f })
                cartFrame.Add(new BoxSpec(new Vector3(x, 0.47f, z), new Vector3(0.045f, 0.70f, 0.045f)));
            cartFrame.Add(new BoxSpec(new Vector3(2.08f, 0.62f, 1.64f), new Vector3(0.50f, 0.045f, 0.045f)));
            cartFrame.Add(new BoxSpec(new Vector3(2.34f, 0.72f, 1.35f), new Vector3(0.045f, 0.22f, 0.62f)));
            CreateBoxBatch("Instrument Cart Steel Frame", parent, cartFrame, m.brushedSteel);

            CreateBoxBatch("QA Tablet and Folder Stack", parent, new[]
            {
                new BoxSpec(new Vector3(2.02f, 0.895f, 1.34f), new Vector3(0.27f, 0.025f, 0.19f), Quaternion.Euler(0f, -8f, 0f)),
                new BoxSpec(new Vector3(2.23f, 0.900f, 1.50f), new Vector3(0.19f, 0.018f, 0.13f), Quaternion.Euler(0f, 7f, 0f)),
                new BoxSpec(new Vector3(2.23f, 0.920f, 1.50f), new Vector3(0.19f, 0.018f, 0.13f), Quaternion.Euler(0f, 4f, 0f)),
                new BoxSpec(new Vector3(2.23f, 0.940f, 1.50f), new Vector3(0.19f, 0.018f, 0.13f))
            }, m.paper);
            CreateBoxBatch("Decorative Vernier Calipers", parent, new[]
            {
                new BoxSpec(new Vector3(1.92f, 0.925f, 1.53f), new Vector3(0.025f, 0.016f, 0.27f), Quaternion.Euler(0f, 22f, 0f)),
                new BoxSpec(new Vector3(1.89f, 0.935f, 1.43f), new Vector3(0.11f, 0.022f, 0.025f), Quaternion.Euler(0f, 22f, 0f)),
                new BoxSpec(new Vector3(1.96f, 0.935f, 1.63f), new Vector3(0.09f, 0.022f, 0.025f), Quaternion.Euler(0f, 22f, 0f))
            }, m.brushedSteel);

            CreateBoxBatch("Workbench Edge Profile and Cross Braces", parent, new[]
            {
                new BoxSpec(new Vector3(0f, 0.745f, 0.205f), new Vector3(1.64f, 0.075f, 0.055f)),
                new BoxSpec(new Vector3(0f, 0.745f, 0.995f), new Vector3(1.64f, 0.075f, 0.055f)),
                new BoxSpec(new Vector3(-0.795f, 0.745f, 0.60f), new Vector3(0.055f, 0.075f, 0.78f)),
                new BoxSpec(new Vector3(0.795f, 0.745f, 0.60f), new Vector3(0.055f, 0.075f, 0.78f)),
                new BoxSpec(new Vector3(0f, 0.23f, 0.92f), new Vector3(1.36f, 0.055f, 0.055f)),
                new BoxSpec(new Vector3(0f, 0.23f, 0.28f), new Vector3(1.36f, 0.055f, 0.055f)),
                new BoxSpec(new Vector3(-0.66f, 0.23f, 0.60f), new Vector3(0.055f, 0.055f, 0.67f)),
                new BoxSpec(new Vector3(0.66f, 0.23f, 0.60f), new Vector3(0.055f, 0.055f, 0.67f))
            }, m.brushedSteel);

            CreateBoxBatch("Wall Monitor Flush Mount and Bezel Detail", parent, new[]
            {
                new BoxSpec(new Vector3(1.75f, 2.05f, 2.425f), new Vector3(0.24f, 0.18f, 0.08f)),
                new BoxSpec(new Vector3(1.75f, 2.05f, 2.350f), new Vector3(0.84f, 0.035f, 0.025f)),
                new BoxSpec(new Vector3(1.75f, 1.80f, 2.350f), new Vector3(0.84f, 0.035f, 0.025f)),
                new BoxSpec(new Vector3(1.35f, 2.05f, 2.350f), new Vector3(0.035f, 0.52f, 0.025f)),
                new BoxSpec(new Vector3(2.15f, 2.05f, 2.350f), new Vector3(0.035f, 0.52f, 0.025f))
            }, m.trim);

            CreateBoxBatch("Visible Center QA Dashboard Screen", parent, new[]
            {
                new BoxSpec(new Vector3(0.22f, 2.08f, 2.305f), new Vector3(0.68f, 0.36f, 0.018f))
            }, m.monitor);
            CreateBoxBatch("Visible Center QA Dashboard Bezel and Mount", parent, new[]
            {
                new BoxSpec(new Vector3(0.22f, 2.275f, 2.285f), new Vector3(0.75f, 0.035f, 0.035f)),
                new BoxSpec(new Vector3(0.22f, 1.885f, 2.285f), new Vector3(0.75f, 0.035f, 0.035f)),
                new BoxSpec(new Vector3(-0.137f, 2.08f, 2.285f), new Vector3(0.035f, 0.39f, 0.035f)),
                new BoxSpec(new Vector3(0.577f, 2.08f, 2.285f), new Vector3(0.035f, 0.39f, 0.035f)),
                new BoxSpec(new Vector3(0.22f, 2.34f, 2.390f), new Vector3(0.18f, 0.10f, 0.08f))
            }, m.trim);
            CreateBoxBatch("Visible QA Dashboard Cyan Readout", parent, new[]
            {
                new BoxSpec(new Vector3(0.08f, 2.205f, 2.265f), new Vector3(0.27f, 0.012f, 0.004f)),
                new BoxSpec(new Vector3(-0.055f, 2.165f, 2.265f), new Vector3(0.075f, 0.012f, 0.004f), Quaternion.Euler(0f, 0f, 12f)),
                new BoxSpec(new Vector3(0.015f, 2.145f, 2.265f), new Vector3(0.075f, 0.012f, 0.004f), Quaternion.Euler(0f, 0f, -20f)),
                new BoxSpec(new Vector3(0.085f, 2.158f, 2.265f), new Vector3(0.075f, 0.012f, 0.004f), Quaternion.Euler(0f, 0f, 28f)),
                new BoxSpec(new Vector3(0.155f, 2.135f, 2.265f), new Vector3(0.075f, 0.012f, 0.004f), Quaternion.Euler(0f, 0f, -18f)),
                new BoxSpec(new Vector3(0.225f, 2.150f, 2.265f), new Vector3(0.075f, 0.012f, 0.004f), Quaternion.Euler(0f, 0f, 22f)),
                new BoxSpec(new Vector3(0.39f, 2.095f, 2.265f), new Vector3(0.032f, 0.10f, 0.004f)),
                new BoxSpec(new Vector3(0.47f, 2.125f, 2.265f), new Vector3(0.032f, 0.16f, 0.004f)),
                new BoxSpec(new Vector3(0.10f, 1.965f, 2.265f), new Vector3(0.30f, 0.015f, 0.004f))
            }, m.diffuserCool);
            CreateBoxBatch("Visible QA Dashboard Amber Readout", parent, new[]
            {
                new BoxSpec(new Vector3(0.35f, 2.060f, 2.263f), new Vector3(0.032f, 0.030f, 0.004f)),
                new BoxSpec(new Vector3(0.43f, 2.035f, 2.263f), new Vector3(0.032f, 0.080f, 0.004f)),
                new BoxSpec(new Vector3(0.51f, 2.075f, 2.263f), new Vector3(0.032f, 0.14f, 0.004f)),
                new BoxSpec(new Vector3(0.42f, 1.965f, 2.263f), new Vector3(0.18f, 0.015f, 0.004f))
            }, m.diffuserWarm);

            CreateBoxBatch("Observation Lightwell Frame and Mullions", parent, new[]
            {
                new BoxSpec(new Vector3(-1f, 1.68f, 2.350f), new Vector3(1.26f, 0.045f, 0.035f)),
                new BoxSpec(new Vector3(-1.59f, 1.68f, 2.350f), new Vector3(0.045f, 0.70f, 0.035f)),
                new BoxSpec(new Vector3(-0.41f, 1.68f, 2.350f), new Vector3(0.045f, 0.70f, 0.035f)),
                new BoxSpec(new Vector3(-1f, 1.37f, 2.350f), new Vector3(1.22f, 0.045f, 0.035f)),
                new BoxSpec(new Vector3(-1f, 1.99f, 2.350f), new Vector3(1.22f, 0.045f, 0.035f))
            }, m.trim);
        }

        static void BuildInspectionDressing(Transform parent, ArtMaterials m)
        {
            var matBorders = new List<BoxSpec>();
            foreach (var center in new[] { -0.45f, 0.45f })
            {
                matBorders.Add(new BoxSpec(new Vector3(center, 0.866f, 0.345f), new Vector3(0.55f, 0.014f, 0.018f)));
                matBorders.Add(new BoxSpec(new Vector3(center, 0.866f, 0.855f), new Vector3(0.55f, 0.014f, 0.018f)));
                matBorders.Add(new BoxSpec(new Vector3(center - 0.266f, 0.866f, 0.60f), new Vector3(0.018f, 0.014f, 0.51f)));
                matBorders.Add(new BoxSpec(new Vector3(center + 0.266f, 0.866f, 0.60f), new Vector3(0.018f, 0.014f, 0.51f)));
            }
            CreateBoxBatch("Inspection Mat Stitched Edge Profiles", parent, matBorders, m.brushedSteel);
            CreateTabletopLabel(parent, "Reference Mat Printed Label", "A1  /  REFERENCE",
                new Vector3(-0.45f, 0.877f, 0.79f), new Color(0.35f, 0.93f, 1f, 1f));
            CreateTabletopLabel(parent, "Inspection Mat Printed Label", "A2  /  INSPECT",
                new Vector3(0.45f, 0.877f, 0.79f), new Color(1f, 0.58f, 0.18f, 1f));

            var panelFrames = new List<BoxSpec>();
            foreach (var x in new[] { -1.30f, 1.29f })
            {
                panelFrames.Add(new BoxSpec(new Vector3(x, 2.02f, 2.378f), new Vector3(1.10f, 0.045f, 0.035f)));
                panelFrames.Add(new BoxSpec(new Vector3(x, 1.08f, 2.378f), new Vector3(1.10f, 0.045f, 0.035f)));
                panelFrames.Add(new BoxSpec(new Vector3(x - 0.527f, 1.55f, 2.378f), new Vector3(0.045f, 0.94f, 0.035f)));
                panelFrames.Add(new BoxSpec(new Vector3(x + 0.527f, 1.55f, 2.378f), new Vector3(0.045f, 0.94f, 0.035f)));
                panelFrames.Add(new BoxSpec(new Vector3(x, 1.55f, 2.415f), new Vector3(1.10f, 0.98f, 0.030f)));
            }
            CreateBoxBatch("Wall Signage Floating Frames and Backplates", parent, panelFrames, m.trim);
        }

        static void CreateTabletopLabel(Transform parent, string name, string text, Vector3 position, Color color)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.position = position;
            root.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            root.transform.localScale = Vector3.one * 0.037f;
            var label = root.AddComponent<TextMeshPro>();
            label.text = text;
            label.fontSize = 1.8f;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.color = color;
            label.outlineWidth = 0.13f;
            label.outlineColor = new Color32(0, 10, 14, 230);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.rectTransform.sizeDelta = new Vector2(9.5f, 1.4f);
        }

        static void ConfigureExistingLights()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.48f, 0.43f, 0.35f, 1f);
            RenderSettings.ambientEquatorColor = new Color(0.30f, 0.27f, 0.22f, 1f);
            RenderSettings.ambientGroundColor = new Color(0.09f, 0.075f, 0.060f, 1f);
            RenderSettings.ambientIntensity = 0.92f;
            RenderSettings.subtractiveShadowColor = new Color(0.16f, 0.145f, 0.125f, 1f);

            var directional = Find("Directional - Soft Lab Fill")?.GetComponent<Light>();
            if (directional != null)
            {
                directional.color = new Color(1f, 0.95f, 0.86f);
                directional.intensity = 0.76f;
                directional.shadows = LightShadows.Soft;
                directional.shadowStrength = 0.62f;
            }

            ConfigureSpot("A1 Inspection Spot", new Vector3(-0.55f, 2.89f, 0.42f),
                new Vector3(-0.45f, 0.94f, 0.60f), new Color(1f, 0.96f, 0.90f), 1.45f, 34f, 24f, true);
            ConfigureSpot("A2 Inspection Spot", new Vector3(0.55f, 2.89f, 0.42f),
                new Vector3(0.45f, 0.94f, 0.60f), new Color(1f, 0.82f, 0.60f), 1.45f, 34f, 24f, true);
            ConfigureSpot("Panel Wash Left", new Vector3(-1.45f, 2.89f, -0.55f),
                new Vector3(-2.35f, 1.35f, 0.35f), new Color(1f, 0.95f, 0.86f), 1.05f, 110f, 80f, false);
            ConfigureSpot("Panel Wash Right", new Vector3(1.45f, 2.89f, -0.55f),
                new Vector3(2.35f, 1.35f, 0.35f), new Color(1f, 0.96f, 0.90f), 1.00f, 110f, 80f, false);
        }

        static void ConfigureSpot(string name, Vector3 position, Vector3 target, Color color,
            float intensity, float outerAngle, float innerAngle, bool shadows)
        {
            var root = Find(name);
            var light = root != null ? root.GetComponent<Light>() : null;
            if (root == null || light == null)
                throw new InvalidOperationException($"Existing light '{name}' is missing.");
            root.transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position));
            light.type = LightType.Spot;
            light.color = color;
            light.intensity = intensity;
            light.range = 3.8f;
            light.spotAngle = outerAngle;
            light.innerSpotAngle = innerAngle;
            light.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            light.shadowStrength = shadows ? 0.58f : 0f;
        }

        static void CreateChromeReflectionProbe(Transform parent)
        {
            const int size = 64;
            var cubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(ReflectionPath);
            var isNew = cubemap == null;
            if (isNew)
                cubemap = new Cubemap(size, TextureFormat.RGBA32, true);
            else if (cubemap.width != size)
                throw new InvalidOperationException($"Generated reflection cubemap must remain {size}px: {ReflectionPath}");
            cubemap.name = "Baked Warm Lab Chrome Reflection";
            cubemap.filterMode = FilterMode.Trilinear;
            cubemap.wrapMode = TextureWrapMode.Clamp;
            var faceColors = new Dictionary<CubemapFace, Color>
            {
                [CubemapFace.PositiveX] = new Color(0.42f, 0.34f, 0.25f, 1f),
                [CubemapFace.NegativeX] = new Color(0.22f, 0.39f, 0.46f, 1f),
                [CubemapFace.PositiveY] = new Color(0.72f, 0.69f, 0.60f, 1f),
                [CubemapFace.NegativeY] = new Color(0.16f, 0.15f, 0.14f, 1f),
                [CubemapFace.PositiveZ] = new Color(0.055f, 0.085f, 0.10f, 1f),
                [CubemapFace.NegativeZ] = new Color(0.55f, 0.48f, 0.36f, 1f)
            };
            foreach (var pair in faceColors)
            {
                var pixels = new Color[size * size];
                for (var y = 0; y < size; ++y)
                for (var x = 0; x < size; ++x)
                {
                    var u = x / (float)(size - 1);
                    var v = y / (float)(size - 1);
                    var fixture = Mathf.Exp(-Mathf.Pow((u - 0.50f) * 7f, 2f) - Mathf.Pow((v - 0.72f) * 12f, 2f));
                    pixels[y * size + x] = Color.Lerp(pair.Value, Color.white, fixture * 0.62f);
                }
                cubemap.SetPixels(pixels, pair.Key);
            }
            cubemap.Apply(true, false);
            if (isNew)
                AssetDatabase.CreateAsset(cubemap, ReflectionPath);
            else
                EditorUtility.SetDirty(cubemap);

            var root = new GameObject("Baked Table Chrome Reflection Probe");
            root.transform.SetParent(parent, false);
            root.transform.position = new Vector3(0f, 1.35f, 0.55f);
            var probe = root.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Custom;
            probe.customBakedTexture = cubemap;
            probe.boxProjection = true;
            probe.size = new Vector3(3.6f, 2.5f, 3.3f);
            probe.center = new Vector3(0f, 0.20f, 0f);
            probe.blendDistance = 0.45f;
            probe.intensity = 1.10f;
            probe.importance = 2;
            probe.resolution = size;
        }

        static GameObject CreateBoxBatch(string name, Transform parent, IEnumerable<BoxSpec> specs,
            Material material)
        {
            var values = specs.ToArray();
            return CreatePrimitiveBatch(name, parent, PrimitiveType.Cube,
                values.Select(value => Matrix4x4.TRS(value.center, value.rotation, value.size)).ToArray(), material);
        }

        static GameObject CreateCylinderBatch(string name, Transform parent, IEnumerable<CylinderSpec> specs,
            Material material)
        {
            var values = specs.ToArray();
            return CreatePrimitiveBatch(name, parent, PrimitiveType.Cylinder,
                values.Select(value => Matrix4x4.TRS(value.center, value.rotation,
                    new Vector3(value.radius * 2f, value.depth * 0.5f, value.radius * 2f))).ToArray(), material);
        }

        static GameObject CreatePrimitiveBatch(string name, Transform parent, PrimitiveType primitiveType,
            Matrix4x4[] matrices, Material material)
        {
            if (matrices.Length == 0)
                throw new InvalidOperationException($"Visual batch '{name}' has no geometry.");
            var temporary = GameObject.CreatePrimitive(primitiveType);
            var source = temporary.GetComponent<MeshFilter>().sharedMesh;
            var combines = matrices.Select(matrix => new CombineInstance { mesh = source, transform = matrix }).ToArray();
            var meshPath = MeshRoot + "/" + Sanitize(name) + ".asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
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
                AssetDatabase.CreateAsset(mesh, meshPath);
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
            GameObjectUtility.SetStaticEditorFlags(root, StaticEditorFlags.BatchingStatic |
                                                        StaticEditorFlags.ReflectionProbeStatic);
            return root;
        }

        static void SetMaterial(string objectName, Material material)
        {
            var target = Find(objectName);
            var renderer = target != null ? target.GetComponent<Renderer>() : null;
            if (renderer == null || material == null)
                throw new InvalidOperationException($"Could not restyle '{objectName}'.");
            renderer.sharedMaterial = material;
            EditorUtility.SetDirty(renderer);
        }

        static void SetMaterialAll(string objectName, Material material)
        {
            var targets = FindAll(objectName).ToArray();
            if (targets.Length == 0 || material == null)
                throw new InvalidOperationException($"Could not restyle any '{objectName}' objects.");
            foreach (var target in targets)
            {
                var renderer = target.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = material;
                    EditorUtility.SetDirty(renderer);
                }
            }
        }

        static void SetMaterialIfPresent(string objectName, Material material)
        {
            var renderer = Find(objectName)?.GetComponent<Renderer>();
            if (renderer == null || material == null)
                return;
            renderer.sharedMaterial = material;
            EditorUtility.SetDirty(renderer);
        }

        static void SetMaterialAllIfPresent(string objectName, Material material)
        {
            if (material == null)
                return;
            foreach (var target in FindAll(objectName))
            {
                var renderer = target.GetComponent<Renderer>();
                if (renderer == null)
                    continue;
                renderer.sharedMaterial = material;
                EditorUtility.SetDirty(renderer);
            }
        }

        static GameObject Find(string name) => FindAll(name).FirstOrDefault();

        static IEnumerable<GameObject> FindAll(string name) =>
            UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include)
                .Where(item => item.name == name).Select(item => item.gameObject);

        static string Sanitize(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Select(character => invalid.Contains(character) || character == ' ' ? '_' : character).ToArray());
        }

        static void DeleteAsset(string path)
        {
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                AssetDatabase.DeleteAsset(path);
        }

        static void SetIfPresent(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        static void SetIfPresent(Material material, string property, Color value)
        {
            if (material.HasProperty(property)) material.SetColor(property, value);
        }

        static void SetIfPresent(Material material, string property, Texture value)
        {
            if (material.HasProperty(property)) material.SetTexture(property, value);
        }

        static void WriteReport(Scene scene, int colliderCount)
        {
            var report = new StringBuilder();
            report.AppendLine("HELMET INSPECTION LAB - ENVIRONMENT ART PASS");
            report.AppendLine($"Scene: {scene.path}");
            report.AppendLine($"Added batched decorative triangles: {s_AddedTriangles}");
            report.AppendLine($"Total renderers: {UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include).Length} / 150");
            report.AppendLine($"Total lights including disabled scanner light: {UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include).Length} / 6");
            report.AppendLine($"Pre-existing colliders preserved: {colliderCount}; art-pass colliders: 0");
            report.AppendLine("Helmet shader target: triplanar chrome, Metallic 0.99, Smoothness 0.95");
            report.AppendLine("Reflection: one custom baked probe; Pass 2 replaces the base 64px map with a 128px finished-room capture");
            report.AppendLine("Protected gameplay transforms, collider values, UI text, and DefectSet_A2 hash: PASS");
            File.WriteAllText(ReportPath, report.ToString());
            AssetDatabase.ImportAsset(ReportPath, ImportAssetOptions.ForceSynchronousImport);
        }

        sealed class ProtectedSceneState
        {
            readonly Dictionary<Transform, string> m_Transforms;
            readonly Dictionary<Collider, string> m_Colliders;
            readonly Dictionary<TMP_Text, string> m_Text;
            readonly string m_DefectSetHash;

            public int ColliderCount => m_Colliders.Count;

            ProtectedSceneState(Dictionary<Transform, string> transforms,
                Dictionary<Collider, string> colliders, Dictionary<TMP_Text, string> text,
                string defectSetHash)
            {
                m_Transforms = transforms;
                m_Colliders = colliders;
                m_Text = text;
                m_DefectSetHash = defectSetHash;
            }

            public static ProtectedSceneState Capture(Scene scene)
            {
                var transforms = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                    .Where(item => item.GetComponent<Light>() == null)
                    .ToDictionary(item => item, TransformSignature);
                var colliders = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Collider>(true))
                    .ToDictionary(item => item, ColliderSignature);
                var text = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<TMP_Text>(true))
                    .ToDictionary(item => item, item => item.text ?? string.Empty);
                return new ProtectedSceneState(transforms, colliders, text,
                    HashFile(Root + "/Data/DefectSet_A2.asset"));
            }

            public void AssertPreserved(Scene scene)
            {
                foreach (var pair in m_Transforms)
                    if (pair.Key == null || TransformSignature(pair.Key) != pair.Value)
                        throw new InvalidOperationException("A protected pre-existing transform changed during the art pass.");

                var currentColliders = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Collider>(true)).ToArray();
                if (currentColliders.Length != m_Colliders.Count)
                    throw new InvalidOperationException("The art pass added or removed a gameplay collider.");
                foreach (var pair in m_Colliders)
                    if (pair.Key == null || ColliderSignature(pair.Key) != pair.Value)
                        throw new InvalidOperationException("A pre-existing collider changed during the art pass.");

                foreach (var pair in m_Text)
                    if (pair.Key == null || (pair.Key.text ?? string.Empty) != pair.Value)
                        throw new InvalidOperationException("Existing UI or signage text changed during the art pass.");

                if (HashFile(Root + "/Data/DefectSet_A2.asset") != m_DefectSetHash)
                    throw new InvalidOperationException("DefectSet_A2 changed during the visual pass.");
            }

            static string TransformSignature(Transform value) =>
                $"{HierarchyPath(value)}|{Vector(value.localPosition)}|{QuaternionValue(value.localRotation)}|{Vector(value.localScale)}";

            static string ColliderSignature(Collider value)
            {
                var detail = value switch
                {
                    BoxCollider box => $"box:{Vector(box.center)}:{Vector(box.size)}",
                    SphereCollider sphere => $"sphere:{Vector(sphere.center)}:{Float(sphere.radius)}",
                    CapsuleCollider capsule => $"capsule:{Vector(capsule.center)}:{Float(capsule.radius)}:{Float(capsule.height)}:{capsule.direction}",
                    MeshCollider mesh => $"mesh:{AssetDatabase.GetAssetPath(mesh.sharedMesh)}:{mesh.sharedMesh?.name}:{mesh.convex}",
                    _ => value.GetType().Name
                };
                return $"{HierarchyPath(value.transform)}|{value.enabled}|{value.isTrigger}|{detail}";
            }

            static string HierarchyPath(Transform value)
            {
                var names = new Stack<string>();
                while (value != null)
                {
                    names.Push(value.name);
                    value = value.parent;
                }
                return string.Join("/", names);
            }

            static string Vector(Vector3 value) => $"{Float(value.x)},{Float(value.y)},{Float(value.z)}";
            static string QuaternionValue(Quaternion value) =>
                $"{Float(value.x)},{Float(value.y)},{Float(value.z)},{Float(value.w)}";
            static string Float(float value) => value.ToString("R", CultureInfo.InvariantCulture);

            static string HashFile(string assetPath)
            {
                using var sha = SHA256.Create();
                var bytes = File.ReadAllBytes(Path.GetFullPath(assetPath));
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
            }
        }

        [MenuItem("Helmet Inspection/Legacy Generators/Environment Art Pass/Render Review Image")]
        public static void RenderReviewImage()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (GameObject.Find(ArtRootName) == null)
                throw new InvalidOperationException("Install the environment art pass before rendering it.");

            var cameraObject = new GameObject("Temporary Environment Art Review Camera");
            SceneManager.MoveGameObjectToScene(cameraObject, scene);
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, 1.58f, -2.22f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 1.32f, 0.68f) - camera.transform.position);
            camera.fieldOfView = 63f;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 20f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.030f, 0.032f, 1f);
            camera.allowHDR = true;
            // Camera.Render in Unity 6 batch mode can bind a one-sample URP color
            // attachment even when the target requests MSAA, producing a blank image.
            // The review capture is an editor diagnostic, so use deterministic 1x AA.
            camera.allowMSAA = false;

            var hiddenRenderers = new List<Renderer>();
            var rig = GameObject.Find("XR Origin - Room Scale Quest 3");
            if (rig != null)
                hiddenRenderers.AddRange(rig.GetComponentsInChildren<Renderer>(true));
            var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            if (scanner != null)
                hiddenRenderers.AddRange(scanner.GetComponentsInChildren<Renderer>(true));
            var states = hiddenRenderers.Select(item => item.enabled).ToArray();
            foreach (var renderer in hiddenRenderers)
                renderer.enabled = false;

            var target = new RenderTexture(1600, 1000, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
                name = "Environment Art Pass Review"
            };
            target.Create();
            var previous = RenderTexture.active;
            // Match the ARGB render target with a four-channel readback. Some Windows
            // batch-mode graphics drivers swizzle RGB24 readbacks, making warm beige
            // materials appear cyan in the review image even though the scene is correct.
            var texture = new Texture2D(1600, 1000, TextureFormat.RGBA32, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0);
                texture.Apply(false, false);
                var output = Root + "/Documentation/EnvironmentArtPassPreview.png";
                File.WriteAllBytes(output, texture.EncodeToPNG());
                AssetDatabase.ImportAsset(output, ImportAssetOptions.ForceSynchronousImport);
                Debug.Log($"[EnvironmentArtPass] Rendered review image to {output}.");
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                for (var i = 0; i < hiddenRenderers.Count; ++i)
                    if (hiddenRenderers[i] != null)
                        hiddenRenderers[i].enabled = states[i];
            }
        }
    }
}
