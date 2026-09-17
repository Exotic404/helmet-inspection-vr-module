using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Gravity;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.CompositionLayers;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;
using UnityEngine.Rendering.Universal;

namespace HelmetInspection.Editor
{
    public static class HelmetProjectValidator
    {
        const string Root = "Assets/HelmetInspection";
        const string ScenePath = Root + "/Scenes/HelmetDefectInspection.unity";
        const string A1Source = Root + "/Models/Source/A1_helmet.glb";
        const string A2Source = Root + "/Models/Source/A2_defective_scan.glb";
        const string A1MeshPath = Root + "/Models/Generated/A1_helmet.asset";
        const string A2MeshPath = Root + "/Models/Generated/A2_defective_scan.asset";
        const string DefectSetPath = Root + "/Data/DefectSet_A2.asset";
        const string HelmetMaterialPath = Root + "/Materials/M_Helmet_Triplanar.mat";
        const string ShaderGraphPath = Root + "/Shaders/TriplanarHelmet.shadergraph";
        const string A1Hash = "D5F2D42979D4ED9895BA35A3161F9DFAC30ABC672E5E7FC9994E74AF5F7FD3C5";
        const string A2Hash = "53341635C4017E4CA4FBE2C42D5B29CAF7636CF623D66BA55748D9EFFDF7B0D8";

        [MenuItem("Helmet Inspection/Validate Quest Module")]
        public static void ValidateProject()
        {
            var passed = new List<string>();
            var failed = new List<string>();
            void Check(bool condition, string description)
            {
                (condition ? passed : failed).Add(description);
            }

            Check(Application.unityVersion == "6000.5.7f1", $"Unity version is exactly 6000.5.7f1 (actual {Application.unityVersion}).");
            Check(File.Exists(A1Source) && Sha256(A1Source) == A1Hash, "A1 source GLB is present and its SHA-256 matches the supplied file.");
            Check(File.Exists(A2Source) && Sha256(A2Source) == A2Hash, "A2 source GLB is present and its SHA-256 matches the supplied file.");

            var a1 = AssetDatabase.LoadAssetAtPath<Mesh>(A1MeshPath);
            var a2 = AssetDatabase.LoadAssetAtPath<Mesh>(A2MeshPath);
            ValidateHelmetMesh(a1, "A1", passed, failed);
            ValidateHelmetMesh(a2, "A2", passed, failed);

            var defects = AssetDatabase.LoadAssetAtPath<DefectSet>(DefectSetPath);
            Check(defects != null, "DefectSet_A2 exists as a ScriptableObject asset.");
            Check(defects != null && defects.Defects.Count == 10, "DefectSet_A2 contains exactly 10 authored defects.");
            Check(defects != null && defects.Defects.All(item =>
                    item.IsHole ? item.markerRadius >= 0.019f : item.deviationMillimeters >= 2f),
                "Every hole has a measured opening radius and every closed-surface defect clears the 2 mm deviation threshold.");
            Check(defects != null && defects.Defects.All(item => item.sourceClusterSize >= 2), "Every curated defect represents a measured multi-vertex surface patch.");
            Check(defects != null && DefectsMatchMeasuredSpecification(defects),
                "All defect labels, converted local coordinates, radii, and deviations match the measured specification.");

            var helmetMaterial = AssetDatabase.LoadAssetAtPath<Material>(HelmetMaterialPath);
            Check(helmetMaterial != null, "Helmet material exists.");
            Check(helmetMaterial != null && AssetDatabase.GetAssetPath(helmetMaterial.shader) == ShaderGraphPath,
                "Helmet material uses the UV-independent triplanar Shader Graph.");

            if (!File.Exists(ScenePath))
            {
                failed.Add("Training scene exists.");
            }
            else
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                ValidateScene(passed, failed);
            }

            ValidatePlayerSettings(passed, failed);
            Check(EditorBuildSettings.scenes.Length == 1 && EditorBuildSettings.scenes[0].enabled &&
                  EditorBuildSettings.scenes[0].path == ScenePath, "The training scene is the only enabled build scene.");

            WriteReport(passed, failed, defects);
            foreach (var item in passed)
                Debug.Log("[HelmetValidation] PASS: " + item);
            foreach (var item in failed)
                Debug.LogError("[HelmetValidation] FAIL: " + item);
            if (failed.Count > 0)
                throw new InvalidOperationException($"Quest module validation failed: {failed.Count} check(s). See Builds/ValidationReport.txt.");
            Debug.Log($"[HelmetValidation] All {passed.Count} checks passed.");
        }

        static void ValidateHelmetMesh(Mesh mesh, string label, ICollection<string> passed, ICollection<string> failed)
        {
            void Check(bool condition, string description) => (condition ? passed : failed).Add(description);
            Check(mesh != null, $"{label} generated mesh exists.");
            if (mesh == null)
                return;
            var maxDimension = Mathf.Max(mesh.bounds.size.x, mesh.bounds.size.y, mesh.bounds.size.z);
            Check(maxDimension >= 0.24f && maxDimension <= 0.28f,
                $"{label} is meter-scaled to helmet size (largest dimension {maxDimension:F3} m)." );
            Check(mesh.vertexCount > 7000, $"{label} preserves the supplied scan density ({mesh.vertexCount} vertices)." );
            Check(mesh.uv == null || mesh.uv.Length == 0, $"{label} remains UV-free and relies on triplanar shading." );
            Check(mesh.subMeshCount == 1, $"{label} contains one mesh/submesh." );
        }

        static void ValidateScene(ICollection<string> passed, ICollection<string> failed)
        {
            void Check(bool condition, string description) => (condition ? passed : failed).Add(description);
            var origins = UnityEngine.Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include);
            Check(origins.Length == 1, "Scene contains exactly one XR Origin.");
            if (origins.Length == 1)
            {
                Check(Vector3.Distance(origins[0].transform.position, new Vector3(0f, 0f, -1.8f)) < 0.005f,
                    "XR Origin starts at (0, 0, -1.8)." );
                Check(origins[0].RequestedTrackingOriginMode == XROrigin.TrackingOriginMode.Floor,
                    "XR Origin uses floor-level room-scale tracking." );
            }

            var cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include);
            Check(cameras.Length == 1 && cameras[0].nearClipPlane <= 0.05f && cameras[0].farClipPlane <= 40f,
                "Tracked HMD camera uses VR-safe 0.05–40 m clip planes." );
            var cameraOffset = origins.Length == 1 ? origins[0].CameraFloorOffsetObject : null;
            Check(cameras.Length == 1 && origins.Length == 1 && cameraOffset != null &&
                  Vector3.Distance(cameras[0].transform.localPosition, Vector3.zero) < 0.001f &&
                  Mathf.Abs(cameraOffset.transform.localPosition.y - 1.68f) < 0.005f &&
                  Mathf.Abs(origins[0].CameraYOffset - 1.68f) < 0.005f &&
                  cameras[0].transform.position.y < 2.0f,
                "Tracked camera uses one 1.68 m eye-height offset and starts safely below the ceiling." );
            Check(UnityEngine.Object.FindObjectsByType<XRDirectInteractor>(FindObjectsInactive.Include).Length >= 2,
                "Both hands provide direct interaction." );
            Check(UnityEngine.Object.FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include).Length >= 2,
                "Both hands provide near/far interaction." );

            var snaps = UnityEngine.Object.FindObjectsByType<SnapTurnProvider>(FindObjectsInactive.Include);
            Check(snaps.Length > 0 && snaps.All(item => item.enabled && Mathf.Approximately(item.turnAmount, 30f) && !item.enableTurnAround),
                "Snap turning is enabled at 30 degrees with turn-around disabled." );
            var moves = UnityEngine.Object.FindObjectsByType<ComfortContinuousMoveProvider>(FindObjectsInactive.Include);
            Check(moves.Length == 1 && moves[0].enabled && moves[0].gameObject.activeInHierarchy &&
                  moves[0].moveSpeed >= 1.5f && moves[0].moveSpeed <= 2f && moves[0].forwardSource == cameras.FirstOrDefault()?.transform &&
                  moves[0].rightHandMoveInput.inputSourceMode == XRInputValueReader.InputSourceMode.Unused,
                "Left-stick head-relative continuous movement is active at a comfortable 1.5-2.0 m/s, with right-stick move input unused." );
            Check(origins.Length == 1 && origins[0].GetComponent<CharacterController>() is { enabled: true },
                "XR Origin uses an enabled CharacterController for solid room collision." );
            var propLayer = LayerMask.NameToLayer(HeldItemLocomotionInstaller.PropLayerName);
            var character = origins.Length == 1 ? origins[0].Origin.GetComponent<CharacterController>() : null;
            Check(propLayer >= 0 && character != null &&
                  (character.excludeLayers.value & (1 << propLayer)) != 0 &&
                  UnityEngine.Object.FindObjectsByType<XRGrabInteractable>(FindObjectsInactive.Include)
                      .Where(item => item.GetComponent<HelmetOutOfBoundsRecovery>() != null ||
                                     item.GetComponent<InspectionScanner>() != null)
                      .All(item => item.GetComponentsInChildren<Collider>(true)
                          .Where(collider => !collider.isTrigger)
                          .All(collider => collider.gameObject.layer == propLayer)),
                "Player capsule persistently excludes solid inspection props across recentering and grabbing." );
            Check(propLayer >= 0 && origins.Length == 1 &&
                  origins[0].GetComponentsInChildren<XRDirectInteractor>(true)
                      .All(item => (item.physicsLayerMask.value & (1 << propLayer)) != 0) &&
                  origins[0].GetComponentsInChildren<SphereInteractionCaster>(true)
                      .All(item => (item.physicsLayerMask.value & (1 << propLayer)) != 0) &&
                  origins[0].GetComponentsInChildren<CurveInteractionCaster>(true)
                      .All(item => (item.raycastMask.value & (1 << propLayer)) != 0) &&
                  origins[0].GetComponentsInChildren<XRRayInteractor>(true)
                      .All(item => (item.raycastMask.value & (1 << propLayer)) != 0),
                "Direct hands and near/far rays can detect the inspection prop physics layer." );
            Check(origins.Length == 1 && origins[0].GetComponent<XRTrackingSpaceRecenter>() != null,
                "XR Origin recenters stale Quest tracking coordinates on launch and long resume." );
            Check(origins.Length == 1 && origins[0].GetComponent<XRPhysicalTranslationLock>() is { enabled: false },
                "Natural 6DoF head tracking is preserved; handover protection uses discrete recentering, not continuous camera cancellation." );
            Check(UnityEngine.Object.FindObjectsByType<GravityProvider>(FindObjectsInactive.Include)
                    .Any(item => item.enabled && item.gameObject.activeInHierarchy && item.useGravity),
                "CharacterController locomotion has active gravity and grounding." );
            Check(UnityEngine.Object.FindObjectsByType<ContinuousTurnProvider>(FindObjectsInactive.Include).All(item => !item.enabled),
                "Continuous turning is disabled." );
            Check(UnityEngine.Object.FindObjectsByType<TeleportationProvider>(FindObjectsInactive.Include).All(item => !item.enabled),
                "Teleport locomotion is disabled." );

            Check(UnityEngine.Object.FindObjectsByType<DefectHotspot>(FindObjectsInactive.Include).Length == 10,
                "Scene has exactly 10 data-driven defect hotspots." );
            Check(UnityEngine.Object.FindObjectsByType<InspectionScanner>(FindObjectsInactive.Include).Length == 1,
                "Scene has exactly one off-hand inspection scanner." );
            Check(UnityEngine.Object.FindObjectsByType<TrainingSessionController>(FindObjectsInactive.Include).Length == 1,
                "Scene has exactly one training session controller." );

            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include);
            var volumes = UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include);
            Check(lights.Length <= 6, $"Scene uses no more than 6 lights ({lights.Length})." );
            Check(renderers.Length <= 150, $"Scene uses no more than 150 renderers ({renderers.Length})." );
            Check(volumes.Length == 1 && volumes[0].isGlobal, "Scene uses exactly one global post-processing volume." );
            Check(UnityEngine.Object.FindObjectsByType<LocomotionComfortVignette>(FindObjectsInactive.Include).Length == 1 &&
                  volumes[0].sharedProfile.TryGet<Vignette>(out var vignette) && vignette.intensity.value <= 0.1f,
                "Smooth movement drives one mild, smoothly faded comfort vignette." );

            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include);
            Check(canvases.Length > 0 && canvases.All(canvas => canvas.renderMode == RenderMode.WorldSpace),
                "All training UI canvases are world-space." );
            Check(UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include).Length > 0,
                "Training UI uses TextMesh Pro." );
            Check(UnityEngine.Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsInactive.Include)
                    .All(item => item.fontSize >= 27f && item.outlineWidth > 0f),
                "Every world-space panel uses enlarged, outlined text." );

            var table = GameObject.Find("Table Top");
            Check(table != null && Vector3.Distance(table.transform.position, new Vector3(0f, 0.80f, 0.60f)) < 0.005f &&
                  Vector3.Distance(table.transform.localScale, new Vector3(1.60f, 0.10f, 0.80f)) < 0.005f,
                "Inspection table has the specified position, top height, and 1.6 x 0.8 m footprint." );
            var reference = GameObject.Find("A1 - REFERENCE HELMET");
            var inspection = GameObject.Find("A2 - DEFECTIVE SCAN");
            Check(reference != null && inspection != null &&
                  Vector3.Distance(reference.transform.position, new Vector3(-0.45f, 1.047f, 0.60f)) < 0.005f &&
                  Vector3.Distance(inspection.transform.position, new Vector3(0.45f, 1.048f, 0.60f)) < 0.005f,
                "A1 and A2 occupy the specified comparison stations." );
            ValidateHelmetPhysics(reference, "A1", Check);
            ValidateHelmetPhysics(inspection, "A2", Check);

            var roomColliderNames = new[] { "Floor", "Back Wall", "Front Wall", "Left Wall", "Right Wall", "Ceiling" };
            Check(roomColliderNames.All(name => GameObject.Find(name)?.GetComponent<BoxCollider>() != null),
                "Floor, all four walls, and ceiling use solid box colliders." );

            var hotspotObjects = UnityEngine.Object.FindObjectsByType<DefectHotspot>(FindObjectsInactive.Include);
            Check(inspection != null && hotspotObjects.All(item => item.transform.IsChildOf(inspection.transform)),
                "All 10 hotspots are parented under the movable A2 helmet root." );
            Check(hotspotObjects.All(item => item.GetComponent<SphereCollider>() is { isTrigger: true }) &&
                  hotspotObjects.All(item => item.GetComponentInChildren<MeshRenderer>(true) is { enabled: false }),
                "Unfound defect markers start hidden and use trigger spheres only." );
            var holeHotspots = hotspotObjects.Where(item => item.IsHole).ToArray();
            Check(holeHotspots.Length == 6 && holeHotspots.All(item =>
                    item.HoleExteriorOffset >= 0.025f && item.HoleScanPadding >= 0.015f &&
                    item.InspectionRadius > item.MarkerRadius),
                "All six holes have enlarged exterior acquisition targets without changing their measured radii." );
            Check(holeHotspots.All(item =>
                    Vector3.Distance(item.MarkerCenter,
                        item.MeasuredCenter + item.MarkerNormal * item.HoleExteriorOffset) < 0.0001f),
                "Every hole acquisition target follows its measured outward surface normal." );
            Check(holeHotspots.All(IsHoleTargetOutsideHelmet),
                "Every hole acquisition target is outside the A2 solid collision proxy." );

            var buttons = UnityEngine.Object.FindObjectsByType<MechanicalTrainingButtonBase>(FindObjectsInactive.Include);
            Check(buttons.Length == 2 && GameObject.Find("Shared Recessed Control Plinth") != null,
                "BEGIN and RESTART are mechanical 3D buttons mounted on one shared plinth." );
            Check(buttons.All(item => item.transform.Find("Raised 3D Button Cap - 18mm") != null),
                "Both controls have raised rounded button caps." );

            var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            var scannerBody = scanner != null ? scanner.transform.Find("Tapered Charcoal Scanner Body") : null;
            Check(scannerBody != null && scannerBody.GetComponent<MeshFilter>()?.sharedMesh != null &&
                  scannerBody.GetComponent<MeshFilter>().sharedMesh.bounds.size.z >= 0.14f &&
                  scannerBody.GetComponent<MeshFilter>().sharedMesh.bounds.size.z <= 0.16f,
                "INS-01 uses a purpose-modeled tapered 15-16 cm body." );
            Check(scanner != null && scanner.GetComponentInChildren<Light>(true) is { range: >= 0.14f and <= 0.16f },
                "INS-01 emitter includes the specified short-range proximity light." );
            Check(scanner != null && scanner.HomeMount != null,
                "RESTART can release and restore the scanner to its off-hand mount." );
            Check(scanner != null && scanner.IgnoresHelmetCollisions && scanner.HoleBeamRange >= 0.6f,
                "Scanner passes through helmet collision proxies and confirms exterior hole targets at child-friendly range." );
        }

        static bool IsHoleTargetOutsideHelmet(DefectHotspot hotspot)
        {
            var helmet = hotspot != null ? hotspot.GetComponentInParent<HelmetOutOfBoundsRecovery>(true) : null;
            if (helmet == null)
                return false;
            return helmet.GetComponentsInChildren<Collider>(true)
                .Where(item => item != null && !item.isTrigger && !item.transform.IsChildOf(hotspot.transform))
                .All(item => Vector3.SqrMagnitude(item.ClosestPoint(hotspot.MarkerCenter) - hotspot.MarkerCenter) >
                             0.00000001f);
        }

        static void ValidateHelmetPhysics(GameObject helmet, string label, Action<bool, string> check)
        {
            var body = helmet != null ? helmet.GetComponent<Rigidbody>() : null;
            var grab = helmet != null ? helmet.GetComponent<XRGrabInteractable>() : null;
            check(body != null && body.mass >= 1.1f && body.mass <= 1.3f &&
                  body.collisionDetectionMode is CollisionDetectionMode.ContinuousDynamic or CollisionDetectionMode.ContinuousSpeculative &&
                  body.linearDamping > 0f && body.angularDamping > 0f,
                $"{label} has realistic mass, damping, and continuous collision detection.");
            check(grab != null && !grab.throwOnDetach,
                $"{label} explicitly disables throw-on-release.");
            check(grab != null && grab.movementType == XRBaseInteractable.MovementType.VelocityTracking &&
                  grab.limitLinearVelocity && grab.limitAngularVelocity && grab.maxLinearVelocityDelta <= 3.5f,
                $"{label} uses bounded physics-driven grab motion so held helmets collide.");
            var recovery = helmet != null ? helmet.GetComponent<HelmetOutOfBoundsRecovery>() : null;
            check(recovery != null && recovery.Home != null &&
                  recovery.MinimumRoomBounds.x >= -2.25f && recovery.MaximumRoomBounds.x <= 2.25f &&
                  recovery.MinimumRoomBounds.z >= -2.25f && recovery.MaximumRoomBounds.z <= 2.25f,
                $"{label} cannot be carried outside the room and has an authored reset home.");
        }

        static void ValidatePlayerSettings(ICollection<string> passed, ICollection<string> failed)
        {
            void Check(bool condition, string description) => (condition ? passed : failed).Add(description);
            Check(PlayerSettings.runInBackground,
                "Run In Background is enabled for stable XR tracking when system UI takes focus." );
            var androidDefines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Android)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            Check(androidDefines.Contains("USE_INPUT_SYSTEM_POSE_CONTROL"),
                "OpenXR uses Input System XR PoseControl." );
            Check(androidDefines.Contains("USE_STICK_CONTROL_THUMBSTICKS"),
                "OpenXR thumbsticks use StickControl." );
            Check(GetInteractionLayerName(31) == "Teleport",
                "XRI interaction layer 31 is reserved for Teleport." );
            Check(PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android) == "org.adfl.training.helmetinspection.module1",
                "Android application identifier is production-style and stable." );
            Check(PlayerSettings.Android.minSdkVersion >= AndroidSdkVersions.AndroidApiLevel29, "Android minimum API level is 29 or newer." );
            Check(PlayerSettings.Android.targetArchitectures == AndroidArchitecture.ARM64, "Android target architecture is ARM64 only." );
            Check(PlayerSettings.Android.applicationEntry == AndroidApplicationEntry.GameActivity, "Android uses Unity's Quest-compatible Game Activity entry point." );
            Check(PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) == ScriptingImplementation.IL2CPP, "Android scripting backend is IL2CPP." );
            var graphics = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            Check(graphics.Length == 1 && graphics[0] == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3,
                "Android graphics API is explicitly OpenGLES3." );
            Check(GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset, "URP is assigned as the default render pipeline." );

            var perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>("Assets/XR/Settings/XRGeneralSettingsPerBuildTarget.asset");
            var manager = perTarget != null ? perTarget.ManagerSettingsForBuildTarget(BuildTargetGroup.Android) : null;
            Check(manager != null && manager.activeLoaders.Any(loader => loader is OpenXRLoader), "OpenXR Loader is assigned for Android." );
            var openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            Check(openXr != null && openXr.renderMode == OpenXRSettings.RenderMode.SinglePassInstanced,
                "OpenXR uses Single Pass Instanced rendering." );
            Check(openXr != null && openXr.latencyOptimization == OpenXRSettings.LatencyOptimization.PrioritizeInputPolling,
                "OpenXR latency optimization prioritizes input polling." );
            Check(FeatureEnabled<MetaQuestFeature>(openXr), "Meta Quest OpenXR support is enabled." );
            Check(FeatureEnabled<OpenXRCompositionLayersFeature>(openXr), "OpenXR Composition Layers support is enabled." );
            Check(FeatureEnabled<OculusTouchControllerProfile>(openXr), "Oculus Touch controller profile is enabled." );
            Check(FeatureEnabled<MetaQuestTouchPlusControllerProfile>(openXr), "Meta Quest Touch Plus controller profile is enabled." );
        }

        static string GetInteractionLayerName(int index)
        {
            const string settingsPath = "Assets/XRI/Settings/Resources/InteractionLayerSettings.asset";
            var asset = AssetDatabase.LoadMainAssetAtPath(settingsPath);
            if (asset == null)
                return string.Empty;

            var serialized = new SerializedObject(asset);
            var layerNames = serialized.FindProperty("m_LayerNames");
            return layerNames != null && layerNames.isArray && layerNames.arraySize > index
                ? layerNames.GetArrayElementAtIndex(index).stringValue
                : string.Empty;
        }

        static bool FeatureEnabled<T>(OpenXRSettings settings) where T : UnityEngine.XR.OpenXR.Features.OpenXRFeature
        {
            return settings != null && settings.GetFeature<T>() is { enabled: true };
        }

        static bool DefectsMatchMeasuredSpecification(DefectSet set)
        {
            var titles = new[]
            {
                "Large Missing-Geometry Hole (upper hole cluster, near crown)",
                "Large Missing-Geometry Hole (upper-rear hole)",
                "Missing-Geometry Hole (adjacent to H1, crown area)",
                "Missing-Geometry Hole (left side, rear)",
                "Largest Missing-Geometry Hole (front-right, most severe hole)",
                "Missing-Geometry Hole (right side, rear)",
                "Deep Structural Dent", "Localized Impact Deformation", "Material Bulge", "Surface Warping"
            };
            var positions = new[]
            {
                new Vector3(-0.01020f, 0.10027f, 0.07436f),
                new Vector3(-0.07625f, -0.01461f, 0.09828f),
                new Vector3(-0.05007f, 0.09624f, 0.06449f),
                new Vector3(-0.11158f, -0.05719f, 0.00755f),
                new Vector3(0.07823f, 0.07288f, -0.06542f),
                new Vector3(0.10907f, -0.05929f, 0.02155f),
                new Vector3(-0.10000f, 0.09000f, -0.04000f),
                new Vector3(0.00929f, 0.03474f, -0.11576f),
                new Vector3(0.08997f, 0.05702f, 0.05816f),
                new Vector3(0.02866f, 0.06716f, -0.10495f),
            };
            var radii = new[] { 0.025f, 0.026f, 0.019f, 0.024f, 0.028f, 0.022f, 0.018f, 0.016f, 0.016f, 0.013f };
            var deviations = new[] { 0f, 0f, 0f, 0f, 0f, 0f, 16.6f, 9.9f, 9.0f, 5.5f };
            if (set.Defects.Count != titles.Length)
                return false;
            for (var i = 0; i < titles.Length; ++i)
            {
                var item = set.Defects[i];
                if (item.id != $"A2-D{i + 1:00}" || item.title != titles[i] ||
                    Vector3.Distance(item.localPosition, positions[i]) > 0.00001f ||
                    Mathf.Abs(item.markerRadius - radii[i]) > 0.00001f ||
                    Mathf.Abs(item.deviationMillimeters - deviations[i]) > 0.01f ||
                    item.IsHole != (i < 6))
                    return false;
            }
            return true;
        }

        static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var algorithm = SHA256.Create();
            return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }

        static void WriteReport(IReadOnlyCollection<string> passed, IReadOnlyCollection<string> failed, DefectSet defects)
        {
            Directory.CreateDirectory("Builds");
            var report = new StringBuilder();
            report.AppendLine("HELMET DEFECT INSPECTION -- MODULE 1 VALIDATION");
            report.AppendLine($"Generated: {DateTimeOffset.Now:O}");
            report.AppendLine($"Unity: {Application.unityVersion}");
            report.AppendLine($"Result: {(failed.Count == 0 ? "PASS" : "FAIL")}");
            report.AppendLine($"Checks: {passed.Count} passed, {failed.Count} failed");
            report.AppendLine();
            foreach (var item in passed) report.AppendLine("PASS  " + item);
            foreach (var item in failed) report.AppendLine("FAIL  " + item);
            if (defects != null)
            {
                report.AppendLine();
                report.AppendLine("CURATED A2 DEFECTS");
                foreach (var defect in defects.Defects)
                {
                    var measurement = defect.IsHole
                        ? $"opening radius {defect.markerRadius * 1000f:F2} mm"
                        : $"deviation {defect.deviationMillimeters:F2} mm";
                    report.AppendLine($"{defect.id} | {defect.title} | {defect.severity} | {measurement} | local {defect.localPosition:F4} | cluster {defect.sourceClusterSize}");
                }
            }
            File.WriteAllText("Builds/ValidationReport.txt", report.ToString());
        }
    }
}
