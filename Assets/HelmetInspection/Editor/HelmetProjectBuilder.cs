using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.CompositionLayers;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace HelmetInspection.Editor
{
    public static partial class HelmetProjectBuilder
    {
        const string Root = "Assets/HelmetInspection";
        const string ScenePath = Root + "/Scenes/HelmetDefectInspection.unity";
        const string A1Source = Root + "/Models/Source/A1_helmet.glb";
        const string A2Source = Root + "/Models/Source/A2_defective_scan.glb";
        const string A1MeshPath = Root + "/Models/Generated/A1_helmet.asset";
        const string A2MeshPath = Root + "/Models/Generated/A2_defective_scan.asset";
        const string A1PrefabPath = Root + "/Prefabs/A1_ReferenceHelmet.prefab";
        const string A2PrefabPath = Root + "/Prefabs/A2_InspectionHelmet.prefab";
        const string DefectSetPath = Root + "/Data/DefectSet_A2.asset";
        const string ShaderGraphPath = Root + "/Shaders/TriplanarHelmet.shadergraph";
        const string XrRigPrefabPath = "Assets/Samples/XR Interaction Toolkit/3.6.0/Starter Assets/Prefabs/XR Origin (XR Rig).prefab";
        const string DirectInteractorPrefabPath = "Assets/Samples/XR Interaction Toolkit/3.6.0/Starter Assets/Prefabs/Interactors/Direct Interactor.prefab";
        const string A1Hash = "D5F2D42979D4ED9895BA35A3161F9DFAC30ABC672E5E7FC9994E74AF5F7FD3C5";
        const string A2Hash = "53341635C4017E4CA4FBE2C42D5B29CAF7636CF623D66BA55748D9EFFDF7B0D8";

        static readonly Color Navy = new Color(0.035f, 0.075f, 0.13f, 1f);
        static readonly Color NavyLight = new Color(0.07f, 0.14f, 0.22f, 1f);
        static readonly Color WarmWhite = new Color(0.83f, 0.80f, 0.72f, 1f);
        static readonly Color Cyan = new Color(0.08f, 0.76f, 0.94f, 1f);
        static readonly Color Amber = new Color(1f, 0.55f, 0.12f, 1f);
        static readonly Color Green = new Color(0.15f, 0.85f, 0.42f, 1f);

        [MenuItem("Helmet Inspection/Legacy Generators/Build Complete Module")]
        public static void BuildAll()
        {
            if (!LegacyGenerationGuard.TryBegin("Build Complete Module", out var guard))
                return;
            using var legacyScope = guard;
            try
            {
                Debug.Log("[HelmetBuilder] Starting reproducible Module 1 build.");
                EnsureFolders();
                EnsureTmpResources();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                ConfigureRenderPipeline();
                ConfigureQuestPlatform();

                var a1 = GlbMeshImporter.ImportSingleMesh(A1Source, A1MeshPath, A1Hash);
                var a2 = GlbMeshImporter.ImportSingleMesh(A2Source, A2MeshPath, A2Hash);
                var defectSet = DefectAuthoringTool.AnalyzeCurateAndSave(a1, a2, DefectSetPath);
                var materials = CreateMaterials();
                CreateHelmetPrefab("A1 Reference Helmet", a1, materials.helmet, A1PrefabPath);
                CreateHelmetPrefab("A2 Defective Helmet", a2, materials.helmet, A2PrefabPath);
                // Material/prefab creation triggers native asset imports that can replace the
                // in-memory ScriptableObject handle. Reload the persisted data before scene use.
                AssetDatabase.ImportAsset(DefectSetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                defectSet = AssetDatabase.LoadAssetAtPath<DefectSet>(DefectSetPath);
                BuildScene(defectSet, materials);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                Debug.Log("[HelmetBuilder] Module 1 generation completed successfully.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                throw;
            }
        }

        static void EnsureFolders()
        {
            var folders = new[]
            {
                Root, Root + "/Data", Root + "/Documentation", Root + "/Fonts", Root + "/Materials", Root + "/Meshes",
                Root + "/Models", Root + "/Models/Generated", Root + "/Prefabs", Root + "/Scenes",
                Root + "/Settings", Root + "/Shaders", Root + "/Textures", "Assets/XR", "Assets/XR/Settings"
            };
            foreach (var folder in folders)
                EnsureFolder(folder);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            var name = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                EnsureFolder(parent);
                AssetDatabase.CreateFolder(parent, name);
            }
        }

        static void EnsureTmpResources()
        {
            const string fontPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(fontPath) == null)
                throw new InvalidOperationException(
                    "The checked-in TextMesh Pro essential resources are incomplete; LiberationSans SDF is missing.");
        }

        static void ConfigureRenderPipeline()
        {
            var rendererPath = Root + "/Settings/HelmetLabRenderer.asset";
            var pipelinePath = Root + "/Settings/HelmetLabURP.asset";
            DeleteAssetIfPresent(pipelinePath);
            DeleteAssetIfPresent(rendererPath);

            var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            renderer.name = "Helmet Lab Mobile Renderer";
            AssetDatabase.CreateAsset(renderer, rendererPath);
            var pipeline = UniversalRenderPipelineAsset.Create(renderer);
            pipeline.name = "Helmet Lab Quest URP";
            pipeline.supportsHDR = false;
            pipeline.supportsCameraDepthTexture = false;
            pipeline.supportsCameraOpaqueTexture = false;
            pipeline.renderScale = 1f;
            pipeline.msaaSampleCount = 4;
            pipeline.shadowDistance = 12f;
            AssetDatabase.CreateAsset(pipeline, pipelinePath);
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            QualitySettings.antiAliasing = 4;
            QualitySettings.shadowDistance = 12f;
            QualitySettings.shadowCascades = 2;
            QualitySettings.vSyncCount = 0;
            EditorUtility.SetDirty(renderer);
            EditorUtility.SetDirty(pipeline);
        }

        static void ConfigureQuestPlatform()
        {
            HelmetProjectStartupRepair.ApplyProjectSettings();
            PlayerSettings.companyName = "ADFL Training";
            PlayerSettings.productName = "Helmet Defect Inspection - Module 1";
            PlayerSettings.bundleVersion = "1.0.0";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "org.adfl.training.helmetinspection.module1");
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android, Il2CppCompilerConfiguration.Release);
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Medium);
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.gpuSkinning = false;
            PlayerSettings.MTRendering = true;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
            EditorUserBuildSettings.buildAppBundle = false;

            ConfigureXrManagement();
            ConfigureOpenXrFeatures();
        }

        static void ConfigureXrManagement()
        {
            const string settingsPath = "Assets/XR/Settings/XRGeneralSettingsPerBuildTarget.asset";
            var perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(settingsPath);
            if (perTarget == null)
            {
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                perTarget.name = "XR General Settings Per Build Target";
                AssetDatabase.CreateAsset(perTarget, settingsPath);
            }
            if (!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.Android))
                perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
            if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
                perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);

            var general = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
            general.InitManagerOnStart = true;
            var manager = perTarget.ManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            manager.automaticLoading = true;
            manager.automaticRunning = true;
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            if (!XRPackageMetadataStore.AssignLoader(manager, typeof(OpenXRLoader).FullName, BuildTargetGroup.Android))
                throw new InvalidOperationException("OpenXR loader could not be assigned for Android.");
            EditorUtility.SetDirty(perTarget);
            EditorUtility.SetDirty(general);
            EditorUtility.SetDirty(manager);
            AssetDatabase.SaveAssets();
        }

        static void ConfigureOpenXrFeatures()
        {
            // OpenXRPackageSettings is intentionally internal; calling its public static factory by
            // reflection is the supported editor-equivalent of opening the OpenXR settings panel once.
            var packageSettingsType = Type.GetType("UnityEditor.XR.OpenXR.OpenXRPackageSettings, Unity.XR.OpenXR.Editor");
            var factory = packageSettingsType?.GetMethod("GetOrCreateInstance", BindingFlags.Public | BindingFlags.Static);
            factory?.Invoke(null, null);
            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (settings == null)
                throw new InvalidOperationException("Android OpenXR settings were not created.");
            settings.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
            settings.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.None;
            settings.latencyOptimization = OpenXRSettings.LatencyOptimization.PrioritizeInputPolling;
            EnableFeature<MetaQuestFeature>(settings);
            EnableFeature<OpenXRCompositionLayersFeature>(settings);
            EnableFeature<OculusTouchControllerProfile>(settings);
            EnableFeature<MetaQuestTouchPlusControllerProfile>(settings);
            // Meta's buffer-discard optimization is Vulkan-only. This project deliberately
            // targets OpenGLES3 for the widest Quest 3 compatibility, so keep it disabled.
            var metaFeature = settings.GetFeature<MetaQuestFeature>();
            var discardField = typeof(MetaQuestFeature).GetField("m_optimizeBufferDiscards", BindingFlags.Instance | BindingFlags.NonPublic);
            discardField?.SetValue(metaFeature, false);
            EditorUtility.SetDirty(metaFeature);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        static void EnableFeature<T>(OpenXRSettings settings) where T : UnityEngine.XR.OpenXR.Features.OpenXRFeature
        {
            var feature = settings.GetFeature<T>();
            if (feature == null)
                throw new InvalidOperationException($"Required OpenXR feature {typeof(T).Name} is unavailable.");
            feature.enabled = true;
            EditorUtility.SetDirty(feature);
        }

        static (Material helmet, Material floor, Material wall, Material metal, Material darkMetal,
            Material glass, Material cyan, Material amber, Material green, Material white, Material black) CreateMaterials()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
                throw new InvalidOperationException("URP Lit shader is unavailable.");

            var microTexture = CreateMicroTexture();
            var graphShader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderGraphPath);
            if (graphShader == null)
                throw new InvalidOperationException($"Triplanar helmet Shader Graph failed to import at {ShaderGraphPath}.");
            var helmet = CreateMaterial(Root + "/Materials/M_Helmet_Triplanar.mat", graphShader, Navy);
            SetIfPresent(helmet, "_Grid_Texture", microTexture);
            SetIfPresent(helmet, "_SlopeColor", NavyLight);
            SetIfPresent(helmet, "_Slope", 1f);
            SetIfPresent(helmet, "_EnableSlopeWarning", 0f);

            var floor = CreateMaterial(Root + "/Materials/M_Floor.mat", lit, new Color(0.16f, 0.18f, 0.19f, 1f), 0.08f, 0.42f);
            var wall = CreateMaterial(Root + "/Materials/M_Wall.mat", lit, new Color(0.64f, 0.63f, 0.58f, 1f), 0f, 0.28f);
            var metal = CreateMaterial(Root + "/Materials/M_BrushedMetal.mat", lit, new Color(0.34f, 0.37f, 0.38f, 1f), 0.72f, 0.48f);
            var darkMetal = CreateMaterial(Root + "/Materials/M_DarkMetal.mat", lit, new Color(0.055f, 0.068f, 0.074f, 1f), 0.8f, 0.4f);
            var glass = CreateMaterial(Root + "/Materials/M_Glass.mat", lit, new Color(0.11f, 0.25f, 0.29f, 0.36f), 0.15f, 0.78f, true);
            var cyan = CreateMaterial(Root + "/Materials/M_CyanEmission.mat", lit, Cyan, 0.1f, 0.55f, false, Cyan * 3f);
            var amber = CreateMaterial(Root + "/Materials/M_AmberEmission.mat", lit, Amber, 0.1f, 0.5f, false, Amber * 2.2f);
            var green = CreateMaterial(Root + "/Materials/M_GreenEmission.mat", lit, Green, 0.1f, 0.5f, false, Green * 2.4f);
            var white = CreateMaterial(Root + "/Materials/M_WarmWhite.mat", lit, WarmWhite, 0f, 0.32f);
            var black = CreateMaterial(Root + "/Materials/M_BlackRubber.mat", lit, new Color(0.018f, 0.022f, 0.025f, 1f), 0f, 0.24f);
            var scannerAlbedo = CreateScannerDetailTexture(false);
            var scannerNormal = CreateScannerDetailTexture(true);
            var scanner = CreateMaterial(Root + "/Materials/M_ScannerPolymer.mat", lit,
                new Color(0.055f, 0.065f, 0.07f, 1f), 0.05f, 0.34f);
            SetIfPresent(scanner, "_BaseMap", scannerAlbedo);
            SetIfPresent(scanner, "_BumpMap", scannerNormal);
            SetIfPresent(scanner, "_BumpScale", 0.45f);
            scanner.EnableKeyword("_NORMALMAP");
            EditorUtility.SetDirty(scanner);
            return (helmet, floor, wall, metal, darkMetal, glass, cyan, amber, green, white, black);
        }

        static Texture2D CreateScannerDetailTexture(bool normal)
        {
            var path = Root + (normal ? "/Textures/T_ScannerPanelNormal.asset" : "/Textures/T_ScannerPanelAlbedo.asset");
            DeleteAssetIfPresent(path);
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, normal)
            {
                name = normal ? "Scanner panel-line normal detail" : "Scanner charcoal albedo detail",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 2
            };
            var pixels = new Color[size * size];
            for (var y = 0; y < size; ++y)
            for (var x = 0; x < size; ++x)
            {
                var seam = Mathf.Abs(y - 18) <= 1 || Mathf.Abs(y - 47) <= 1;
                if (normal)
                {
                    var nx = seam ? (y < 32 ? -0.16f : 0.16f) : Mathf.Sin(x * 0.7f + y * 0.2f) * 0.018f;
                    var ny = seam ? 0.10f : 0f;
                    var nz = Mathf.Sqrt(Mathf.Max(0f, 1f - nx * nx - ny * ny));
                    pixels[y * size + x] = new Color(nx * 0.5f + 0.5f, ny * 0.5f + 0.5f, nz * 0.5f + 0.5f, 1f);
                }
                else
                {
                    var grain = Mathf.Sin(x * 1.73f + y * 0.91f) * 0.009f;
                    var value = 0.065f + grain - (seam ? 0.027f : 0f);
                    pixels[y * size + x] = new Color(value * 0.86f, value * 0.94f, value, 1f);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(true, false);
            AssetDatabase.CreateAsset(texture, path);
            return texture;
        }

        static Texture2D CreateMicroTexture()
        {
            const string path = Root + "/Textures/T_HelmetMicrotexture.asset";
            DeleteAssetIfPresent(path);
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "Helmet UV-independent triplanar microtexture",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 2
            };
            var colors = new Color[size * size];
            var random = new System.Random(71031);
            for (var y = 0; y < size; ++y)
            for (var x = 0; x < size; ++x)
            {
                var grain = ((float)random.NextDouble() - 0.5f) * 0.045f;
                var diagonal = ((x + y) % 9 == 0 ? 0.018f : 0f);
                colors[y * size + x] = new Color(
                    Mathf.Clamp01(Navy.r + grain + diagonal),
                    Mathf.Clamp01(Navy.g + grain + diagonal),
                    Mathf.Clamp01(Navy.b + grain + diagonal), 1f);
            }
            texture.SetPixels(colors);
            texture.Apply(true, false);
            AssetDatabase.CreateAsset(texture, path);
            return texture;
        }

        static Material CreateMaterial(string path, Shader shader, Color color, float metallic = 0f,
            float smoothness = 0.4f, bool transparent = false, Color? emission = null)
        {
            DeleteAssetIfPresent(path);
            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            SetIfPresent(material, "_BaseColor", color);
            SetIfPresent(material, "_Color", color);
            SetIfPresent(material, "_Metallic", metallic);
            SetIfPresent(material, "_Smoothness", smoothness);
            if (emission.HasValue)
            {
                material.EnableKeyword("_EMISSION");
                SetIfPresent(material, "_EmissionColor", emission.Value);
            }
            if (transparent)
            {
                SetIfPresent(material, "_Surface", 1f);
                SetIfPresent(material, "_Blend", 0f);
                SetIfPresent(material, "_ZWrite", 0f);
                material.renderQueue = (int)RenderQueue.Transparent;
                material.SetOverrideTag("RenderType", "Transparent");
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            AssetDatabase.CreateAsset(material, path);
            return material;
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

        static void CreateHelmetPrefab(string name, Mesh mesh, Material material, string path)
        {
            DeleteAssetIfPresent(path);
            var root = new GameObject(name);
            var visual = new GameObject("Shell Scan Mesh");
            visual.transform.SetParent(root.transform, false);
            visual.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = visual.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;

            var collider = root.AddComponent<BoxCollider>();
            collider.center = mesh.bounds.center;
            collider.size = mesh.bounds.size + Vector3.one * 0.006f;
            var body = root.AddComponent<Rigidbody>();
            body.mass = 1.2f;
            body.useGravity = true;
            body.linearDamping = 0.18f;
            body.angularDamping = 0.35f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var attach = new GameObject("Grab Attach").transform;
            attach.SetParent(root.transform, false);
            attach.localPosition = mesh.bounds.center;
            var grab = root.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;
            grab.throwOnDetach = false;
            grab.useDynamicAttach = true;
            grab.attachTransform = attach;
            grab.attachEaseInTime = 0.08f;
            grab.velocityDamping = 1f;
            grab.velocityScale = 1f;
            grab.limitLinearVelocity = true;
            grab.limitAngularVelocity = true;
            grab.maxLinearVelocityDelta = 3.5f;
            grab.maxAngularVelocityDelta = 12f;
            root.AddComponent<HelmetOutOfBoundsRecovery>();
            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
        }

        static void DeleteAssetIfPresent(string path)
        {
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                AssetDatabase.DeleteAsset(path);
        }
    }
}
