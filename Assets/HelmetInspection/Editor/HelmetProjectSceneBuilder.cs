using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
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
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Gravity;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace HelmetInspection.Editor
{
    public static partial class HelmetProjectBuilder
    {
        readonly struct UiReferences
        {
            public readonly TMP_Text objective;
            public readonly TMP_Text progress;
            public readonly TMP_Text details;
            public readonly GameObject startPanel;
            public readonly GameObject completionPanel;

            public UiReferences(TMP_Text objectiveText, TMP_Text progressText, TMP_Text detailText,
                GameObject intro, GameObject completion)
            {
                objective = objectiveText;
                progress = progressText;
                details = detailText;
                startPanel = intro;
                completionPanel = completion;
            }
        }

        static void BuildScene(DefectSet defectSet,
            (Material helmet, Material floor, Material wall, Material metal, Material darkMetal,
                Material glass, Material cyan, Material amber, Material green, Material white, Material black) materials)
        {
            if (defectSet == null || defectSet.Defects.Count != 10)
                throw new InvalidOperationException("DefectSet_A2 was not available at scene build start.");
            // Retain plain serialized records while scene construction creates and imports other
            // native assets. Unity is then free to reload the backing ScriptableObject safely.
            var defectRecords = defectSet.Defects.ToArray();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "Helmet Defect Inspection";

            var environment = new GameObject("ENVIRONMENT - QA Metrology Lab");
            var stations = new GameObject("STATION - Helmet Comparison");
            var systems = new GameObject("SYSTEMS - Training and XR");

            CreateArchitecture(environment.transform, materials);
            CreateLabProps(environment.transform, materials);
            var globalVolume = CreateLightingAndVolume(systems.transform);
            var rig = CreateXrRig(systems.transform, materials.darkMetal);
            var comfortMove = rig.GetComponentInChildren<ComfortContinuousMoveProvider>(true);
            var comfortVignette = globalVolume.gameObject.AddComponent<LocomotionComfortVignette>();
            comfortVignette.SetEditorReferences(comfortMove, globalVolume);
            var interactionManager = new GameObject("XR Interaction Manager");
            interactionManager.transform.SetParent(systems.transform);
            interactionManager.AddComponent<XRInteractionManager>();
            var eventSystem = new GameObject("XR UI Event System");
            eventSystem.transform.SetParent(systems.transform);
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<XRUIInputModule>();

            CreateInspectionTable(stations.transform, materials, out var a1Stand, out var a2Stand);
            var a1 = InstantiateHelmet(A1PrefabPath, "A1 - REFERENCE HELMET", new Vector3(-0.45f, 1.047f, 0.60f), a1Stand);
            var a2 = InstantiateHelmet(A2PrefabPath, "A2 - DEFECTIVE SCAN", new Vector3(0.45f, 1.048f, 0.60f), a2Stand);
            CreateHelmetLabel(stations.transform, "A1  /  REFERENCE", new Vector3(-0.45f, 1.25f, 0.59f), WarmWhite);
            CreateHelmetLabel(stations.transform, "A2  /  INSPECT", new Vector3(0.45f, 1.25f, 0.59f), Amber);

            var ui = CreateWorldSpaceUi(systems.transform, materials);
            var sessionObject = new GameObject("Training Session Controller");
            sessionObject.transform.SetParent(systems.transform);
            var session = sessionObject.AddComponent<TrainingSessionController>();
            sessionObject.AddComponent<QuestRuntimeSettings>();
            var hotspots = CreateHotspots(a2.transform, defectRecords, session, materials);
            var persistedDefectSet = AssetDatabase.LoadAssetAtPath<DefectSet>(DefectSetPath);
            session.SetEditorReferences(persistedDefectSet, a2.transform, ui.progress, ui.details, ui.objective,
                ui.startPanel, ui.completionPanel, hotspots);

            CreateTrainingButtons(stations.transform, session, materials);
            CreateScanner(rig, session, materials);
            CreateFloorBoundary(environment.transform, materials);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException($"Failed to save scene {ScenePath}.");
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            CreatePreviewScreenshot(scene);
        }

        static GameObject CreateXrRig(Transform parent, Material controllerMaterial)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(XrRigPrefabPath);
            if (prefab == null)
                throw new InvalidOperationException($"Official XRI Starter Assets rig was not found at {XrRigPrefabPath}.");
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            rig.name = "XR Origin - Room Scale Quest 3";
            rig.transform.SetParent(parent);
            rig.transform.SetPositionAndRotation(new Vector3(0f, 0f, -1.8f), Quaternion.identity);
            var origin = rig.GetComponent<XROrigin>();
            if (origin == null)
                throw new InvalidOperationException("Starter Assets prefab does not contain an XROrigin.");
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
            origin.CameraYOffset = 1.68f;
            if (origin.CameraFloorOffsetObject != null)
            {
                var offset = origin.CameraFloorOffsetObject.transform.localPosition;
                offset.y = 1.68f;
                origin.CameraFloorOffsetObject.transform.localPosition = offset;
            }
            var recenter = rig.AddComponent<XRTrackingSpaceRecenter>();
            recenter.SetEditorReferences(origin, new Vector3(0f, 1.68f, -1.8f), Vector3.forward);
            var translationLock = rig.AddComponent<XRPhysicalTranslationLock>();
            translationLock.SetEditorReferences(origin, new Vector3(0f, 1.68f, 0f));

            // Starter Assets controller meshes use built-in-pipeline materials. Replace only
            // their visual materials with a Quest-compatible URP material.
            foreach (var controllerName in new[] { "XR Controller Left", "XR Controller Right" })
            {
                var controllerVisual = FindDeep(rig.transform, controllerName);
                if (controllerVisual == null)
                    continue;
                foreach (var controllerRenderer in controllerVisual.GetComponentsInChildren<Renderer>(true))
                    controllerRenderer.sharedMaterial = controllerMaterial;
            }

            var directPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DirectInteractorPrefabPath);
            if (directPrefab == null)
                throw new InvalidOperationException($"Official XRI direct interactor was not found at {DirectInteractorPrefabPath}.");
            foreach (var controllerName in new[] { "Left Controller", "Right Controller" })
            {
                var controller = FindDeep(rig.transform, controllerName);
                var nearFar = controller != null ? controller.GetComponentInChildren<NearFarInteractor>(true) : null;
                if (controller == null || nearFar == null)
                    throw new InvalidOperationException($"{controllerName} is missing its near/far interaction setup.");
                var directObject = (GameObject)PrefabUtility.InstantiatePrefab(directPrefab);
                directObject.name = controllerName.Replace(" Controller", " Direct Interactor");
                directObject.transform.SetParent(controller, false);
                var direct = directObject.GetComponent<XRDirectInteractor>();
                direct.handedness = nearFar.handedness;
                direct.selectInput = nearFar.selectInput;
                direct.activateInput = nearFar.activateInput;
                var directCollider = directObject.GetComponent<SphereCollider>();
                if (directCollider != null)
                    directCollider.radius = 0.075f;
            }

            var moveObject = FindDeep(rig.transform, "Move");
            var originalMove = moveObject != null ? moveObject.GetComponent<ContinuousMoveProvider>() : null;
            if (moveObject == null || originalMove == null)
                throw new InvalidOperationException("XRI rig has no configured Continuous Move Provider.");
            var moveMediator = originalMove.mediator;
            var movePriority = originalMove.transformationPriority;
            var leftMoveInput = originalMove.leftHandMoveInput;
            UnityEngine.Object.DestroyImmediate(originalMove);
            var comfortMove = moveObject.gameObject.AddComponent<ComfortContinuousMoveProvider>();
            comfortMove.mediator = moveMediator;
            comfortMove.transformationPriority = movePriority;
            comfortMove.leftHandMoveInput = leftMoveInput;
            comfortMove.rightHandMoveInput = new XRInputValueReader<Vector2>("Right Hand Move",
                XRInputValueReader.InputSourceMode.Unused);
            comfortMove.moveSpeed = 1.75f;
            comfortMove.enableStrafe = true;
            comfortMove.enableFly = false;
            foreach (var provider in rig.GetComponentsInChildren<ContinuousTurnProvider>(true))
                provider.enabled = false;
            foreach (var provider in rig.GetComponentsInChildren<TeleportationProvider>(true))
                provider.enabled = false;
            foreach (var childName in new[] { "Teleportation", "Grab Move", "Jump", "Climb", "Climb Teleport" })
            {
                var child = FindDeep(rig.transform, childName);
                if (child != null)
                    child.gameObject.SetActive(false);
            }
            moveObject.gameObject.SetActive(true);
            comfortMove.enabled = true;
            var gravityObject = FindDeep(rig.transform, "Gravity");
            if (gravityObject == null || gravityObject.GetComponent<GravityProvider>() == null)
                throw new InvalidOperationException("XRI rig has no Gravity Provider.");
            gravityObject.gameObject.SetActive(true);
            gravityObject.GetComponent<GravityProvider>().enabled = true;
            var snapProviders = rig.GetComponentsInChildren<SnapTurnProvider>(true);
            if (snapProviders.Length == 0)
                throw new InvalidOperationException("XRI rig has no SnapTurnProvider.");
            foreach (var snap in snapProviders)
            {
                snap.enabled = true;
                snap.turnAmount = 30f;
                snap.debounceTime = 0.35f;
                snap.enableTurnLeftRight = true;
                snap.enableTurnAround = false;
            }

            var camera = rig.GetComponentInChildren<Camera>(true);
            if (camera != null)
            {
                camera.name = "Main Camera - Tracked HMD";
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 40f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.035f, 0.043f, 0.047f, 1f);
                // Eye height belongs on the XROrigin's Camera Offset, not on the tracked
                // camera itself. Offsetting both puts the HMD at about 3.04 m and clips it
                // into the 3 m ceiling when no floor-tracking runtime is active.
                camera.transform.localPosition = Vector3.zero;
                camera.allowHDR = false;
                camera.allowMSAA = true;
            }
            comfortMove.forwardSource = camera != null ? camera.transform : null;
            var character = rig.GetComponent<CharacterController>();
            if (character == null)
                character = rig.AddComponent<CharacterController>();
            character.radius = 0.24f;
            character.height = 1.72f;
            character.center = new Vector3(0f, 0.86f, 0f);
            character.stepOffset = 0.18f;
            character.skinWidth = 0.02f;
            rig.AddComponent<RecenterOnSecondaryButton>();
            return rig;
        }

        static void CreateArchitecture(Transform parent,
            (Material helmet, Material floor, Material wall, Material metal, Material darkMetal,
                Material glass, Material cyan, Material amber, Material green, Material white, Material black) m)
        {
            var room = new GameObject("Room Shell - 5m x 5m x 3m").transform;
            room.SetParent(parent);
            CreateCube("Floor", room, new Vector3(0f, -0.05f, 0f), new Vector3(5f, 0.10f, 5f), m.floor, true);
            CreateCube("Back Wall", room, new Vector3(0f, 1.5f, 2.48f), new Vector3(5f, 3f, 0.08f), m.wall, true);
            CreateCube("Front Wall", room, new Vector3(0f, 1.5f, -2.48f), new Vector3(5f, 3f, 0.08f), m.wall, true);
            CreateCube("Left Wall", room, new Vector3(-2.48f, 1.5f, 0f), new Vector3(0.08f, 3f, 5f), m.wall, true);
            CreateCube("Right Wall", room, new Vector3(2.48f, 1.5f, 0f), new Vector3(0.08f, 3f, 5f), m.wall, true);
            CreateCube("Ceiling", room, new Vector3(0f, 3.0f, 0f), new Vector3(5f, 0.06f, 5f), m.white, true);
            CreateCube("Back Wall Navy Band", room, new Vector3(0f, 2.62f, 2.425f), new Vector3(4.82f, 0.36f, 0.035f), m.darkMetal, false);
            CreateCube("Floor Inlay Left", room, new Vector3(-1.75f, 0.006f, 0f), new Vector3(0.025f, 0.012f, 4.5f), m.cyan, false);
            CreateCube("Floor Inlay Right", room, new Vector3(1.75f, 0.006f, 0f), new Vector3(0.025f, 0.012f, 4.5f), m.cyan, false);
        }

        static void CreateLabProps(Transform parent,
            (Material helmet, Material floor, Material wall, Material metal, Material darkMetal,
                Material glass, Material cyan, Material amber, Material green, Material white, Material black) m)
        {
            var props = new GameObject("Professional QA Lab Fixtures").transform;
            props.SetParent(parent);
            CreateCube("Safety Cabinet", props, new Vector3(-2.18f, 0.82f, 1.68f), new Vector3(0.48f, 1.64f, 0.55f), m.metal, true);
            CreateCube("Cabinet Door", props, new Vector3(-1.925f, 0.88f, 1.67f), new Vector3(0.025f, 1.32f, 0.45f), m.amber, false);
            CreateCube("Instrument Cart", props, new Vector3(2.08f, 0.49f, 1.35f), new Vector3(0.58f, 0.08f, 0.72f), m.metal, true);
            CreateCube("Instrument Cart Shelf", props, new Vector3(2.08f, 0.84f, 1.35f), new Vector3(0.58f, 0.06f, 0.72f), m.metal, true);
            for (var x = -1; x <= 1; x += 2)
            for (var z = -1; z <= 1; z += 2)
                CreateCylinder("Cart Wheel", props, new Vector3(2.08f + x * 0.22f, 0.10f, 1.35f + z * 0.27f),
                    new Vector3(0.065f, 0.028f, 0.065f), Quaternion.Euler(0f, 0f, 90f), m.black, false);
            CreateCube("Wall Monitor Housing", props, new Vector3(1.75f, 2.05f, 2.405f), new Vector3(0.88f, 0.53f, 0.06f), m.darkMetal, false);
            CreateCube("Wall Monitor Screen", props, new Vector3(1.75f, 2.05f, 2.365f), new Vector3(0.78f, 0.43f, 0.012f), m.glass, false);
            CreateCube("Ventilation Header", props, new Vector3(-1.45f, 2.72f, 2.36f), new Vector3(1.45f, 0.16f, 0.12f), m.metal, false);
            for (var i = 0; i < 7; ++i)
                CreateCube($"Vent Slot {i + 1}", props, new Vector3(-2.01f + i * 0.19f, 2.72f, 2.29f),
                    new Vector3(0.11f, 0.055f, 0.02f), m.black, false);
            CreateCylinder("Calibration Gas Cylinder", props, new Vector3(2.18f, 0.55f, -1.80f),
                new Vector3(0.18f, 0.55f, 0.18f), Quaternion.identity, m.cyan, true);
            CreateCube("Cylinder Restraint", props, new Vector3(2.31f, 0.72f, -1.80f), new Vector3(0.04f, 0.08f, 0.45f), m.darkMetal, false);
            CreateCube("Observation Window", props, new Vector3(-1.0f, 1.68f, 2.39f), new Vector3(1.18f, 0.66f, 0.035f), m.glass, false);
            CreateCube("Observation Window Header", props, new Vector3(-1.0f, 2.04f, 2.365f), new Vector3(1.3f, 0.07f, 0.08f), m.darkMetal, false);
        }

        static void CreateInspectionTable(Transform parent,
            (Material helmet, Material floor, Material wall, Material metal, Material darkMetal,
                Material glass, Material cyan, Material amber, Material green, Material white, Material black) m,
            out Transform a1Stand, out Transform a2Stand)
        {
            var table = new GameObject("Metrology Inspection Table - Top 0.85m").transform;
            table.SetParent(parent);
            CreateCube("Table Top", table, new Vector3(0f, 0.80f, 0.60f), new Vector3(1.60f, 0.10f, 0.80f), m.metal, true);
            CreateCube("Front Fascia", table, new Vector3(0f, 0.69f, 0.22f), new Vector3(1.55f, 0.13f, 0.06f), m.darkMetal, false);
            for (var x = -1; x <= 1; x += 2)
            for (var z = -1; z <= 1; z += 2)
                CreateCube("Table Leg", table, new Vector3(x * 0.66f, 0.39f, 0.60f + z * 0.30f),
                    new Vector3(0.085f, 0.78f, 0.085f), m.darkMetal, true);
            CreateCube("Reference Zone Inlay", table, new Vector3(-0.45f, 0.856f, 0.60f), new Vector3(0.52f, 0.012f, 0.52f), m.cyan, false);
            CreateCube("Inspection Zone Inlay", table, new Vector3(0.45f, 0.856f, 0.60f), new Vector3(0.52f, 0.012f, 0.52f), m.amber, false);
            a1Stand = CreateStand("A1 Return Stand", table, new Vector3(-0.45f, 0.91f, 0.60f), m.darkMetal, m.cyan);
            a2Stand = CreateStand("A2 Return Stand", table, new Vector3(0.45f, 0.91f, 0.60f), m.darkMetal, m.amber);
        }

        static Transform CreateStand(string name, Transform parent, Vector3 center, Material body, Material accent)
        {
            var root = new GameObject(name).transform;
            root.SetParent(parent);
            CreateCylinder("Stand Base", root, center, new Vector3(0.12f, 0.035f, 0.12f), Quaternion.identity, body, true);
            CreateCylinder("Stand Collar", root, center + Vector3.up * 0.055f, new Vector3(0.075f, 0.025f, 0.075f), Quaternion.identity, accent, false);
            return root;
        }

        static GameObject InstantiateHelmet(string prefabPath, string name, Vector3 position, Transform stand)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                throw new InvalidOperationException($"Helmet prefab missing: {prefabPath}");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = name;
            instance.transform.SetPositionAndRotation(position, Quaternion.Euler(12f, 0f, 0f));
            var grab = instance.GetComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;
            grab.velocityDamping = 1f;
            grab.velocityScale = 1f;
            grab.limitLinearVelocity = true;
            grab.limitAngularVelocity = true;
            grab.maxLinearVelocityDelta = 3.5f;
            grab.maxAngularVelocityDelta = 12f;
            var socketObject = new GameObject(name.StartsWith("A1", StringComparison.Ordinal) ? "A1 Return Socket" : "A2 Return Socket");
            socketObject.transform.SetParent(stand);
            socketObject.transform.SetPositionAndRotation(position, instance.transform.rotation);
            var trigger = socketObject.AddComponent<SphereCollider>();
            trigger.radius = 0.17f;
            trigger.isTrigger = true;
            var socket = socketObject.AddComponent<XRSocketInteractor>();
            socket.showInteractableHoverMeshes = true;
            socket.startingSelectedInteractable = grab;
            socket.attachTransform = socketObject.transform;
            var recovery = instance.GetComponent<HelmetOutOfBoundsRecovery>();
            if (recovery == null)
                throw new InvalidOperationException($"Helmet prefab {prefabPath} is missing out-of-bounds recovery.");
            recovery.SetEditorHome(socketObject.transform);
            recovery.SetEditorSafetyBounds(new Vector3(-2.25f, 0.08f, -2.25f),
                new Vector3(2.25f, 2.75f, 2.25f), 0.35f);
            return instance;
        }

        static List<DefectHotspot> CreateHotspots(Transform helmet, IReadOnlyList<DefectRecord> defects, TrainingSessionController session,
            (Material helmet, Material floor, Material wall, Material metal, Material darkMetal,
                Material glass, Material cyan, Material amber, Material green, Material white, Material black) m)
        {
            if (defects == null || defects.Count != 10)
                throw new InvalidOperationException("DefectSet_A2 must contain exactly 10 curated defects.");
            var parent = new GameObject("Authored Defect Hotspots - Exactly 10").transform;
            parent.SetParent(helmet, false);
            var ringMesh = CreateRingMesh();
            var result = new List<DefectHotspot>(10);
            for (var i = 0; i < defects.Count; ++i)
            {
                var defect = defects[i];
                var root = new GameObject($"{defect.id} - {defect.title}");
                root.transform.SetParent(parent, false);
                root.transform.localPosition = defect.localPosition;
                root.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, defect.localNormal.normalized);
                var collider = root.AddComponent<SphereCollider>();
                collider.radius = defect.markerRadius;
                collider.isTrigger = true;
                root.AddComponent<XRSimpleInteractable>();
                var halo = new GameObject("Cyan Inspection Halo");
                halo.transform.SetParent(root.transform, false);
                halo.transform.localPosition = Vector3.forward * 0.0015f;
                halo.transform.localScale = Vector3.one * defect.markerRadius;
                halo.AddComponent<MeshFilter>().sharedMesh = ringMesh;
                var renderer = halo.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = m.cyan;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.enabled = false;
                var hotspot = root.AddComponent<DefectHotspot>();
                hotspot.SetEditorReferences(i, session, renderer, defect.IsHole);
                result.Add(hotspot);
            }
            return result;
        }

        static Mesh CreateRingMesh()
        {
            const string path = Root + "/Meshes/DefectHaloRing.asset";
            DeleteAssetIfPresent(path);
            const int segments = 40;
            const float inner = 0.76f;
            const float outer = 1.0f;
            var vertices = new Vector3[segments * 2];
            var triangles = new int[segments * 6];
            for (var i = 0; i < segments; ++i)
            {
                var angle = i * Mathf.PI * 2f / segments;
                var direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                vertices[i * 2] = direction * inner;
                vertices[i * 2 + 1] = direction * outer;
                var next = (i + 1) % segments;
                var t = i * 6;
                triangles[t] = i * 2;
                triangles[t + 1] = next * 2 + 1;
                triangles[t + 2] = i * 2 + 1;
                triangles[t + 3] = i * 2;
                triangles[t + 4] = next * 2;
                triangles[t + 5] = next * 2 + 1;
            }
            var mesh = new Mesh { name = "Defect Halo Ring" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        static void CreateScanner(GameObject rig, TrainingSessionController session,
            (Material helmet, Material floor, Material wall, Material metal, Material darkMetal,
                Material glass, Material cyan, Material amber, Material green, Material white, Material black) m)
        {
            var leftController = FindDeep(rig.transform, "Left Controller");
            if (leftController == null)
                throw new InvalidOperationException("Left Controller was not found in the XRI rig.");
            var mount = new GameObject("Offhand Scanner Auto-Attach Socket");
            mount.transform.SetParent(leftController, false);
            mount.transform.localPosition = new Vector3(0.025f, -0.015f, 0.08f);
            mount.transform.localRotation = Quaternion.Euler(8f, 0f, 0f);
            var socketCollider = mount.AddComponent<SphereCollider>();
            socketCollider.radius = 0.12f;
            socketCollider.isTrigger = true;
            var socket = mount.AddComponent<XRSocketInteractor>();
            socket.showInteractableHoverMeshes = false;

            var scanner = new GameObject("INS-01 Surface Deviation Scanner");
            scanner.transform.position = mount.transform.position;
            scanner.transform.rotation = mount.transform.rotation;
            var bodyCollider = scanner.AddComponent<BoxCollider>();
            bodyCollider.center = new Vector3(0f, -0.008f, 0.008f);
            bodyCollider.size = new Vector3(0.078f, 0.105f, 0.158f);
            var rigidbody = scanner.AddComponent<Rigidbody>();
            rigidbody.mass = 0.32f;
            rigidbody.useGravity = true;
            rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var grab = scanner.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.Kinematic;
            grab.throwOnDetach = false;
            grab.useDynamicAttach = true;
            socket.startingSelectedInteractable = grab;
            socket.attachTransform = mount.transform;

            var scannerMaterial = AssetDatabase.LoadAssetAtPath<Material>(Root + "/Materials/M_ScannerPolymer.mat");
            if (scannerMaterial == null)
                throw new InvalidOperationException("Scanner polymer material was not generated.");
            var bodyVisual = new GameObject("Tapered Charcoal Scanner Body");
            bodyVisual.transform.SetParent(scanner.transform, false);
            bodyVisual.AddComponent<MeshFilter>().sharedMesh = CreateScannerBodyMesh();
            bodyVisual.AddComponent<MeshRenderer>().sharedMaterial = scannerMaterial;

            var grip = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            grip.name = "Ergonomic Polymer Grip";
            grip.transform.SetParent(scanner.transform, false);
            grip.transform.localPosition = new Vector3(0f, -0.047f, -0.025f);
            grip.transform.localRotation = Quaternion.Euler(-12f, 0f, 0f);
            grip.transform.localScale = new Vector3(0.052f, 0.038f, 0.052f);
            grip.GetComponent<MeshRenderer>().sharedMaterial = scannerMaterial;
            UnityEngine.Object.DestroyImmediate(grip.GetComponent<Collider>());

            CreateCylinder("Brushed Metal Grip Ring", scanner.transform, new Vector3(0f, 0f, 0.048f),
                new Vector3(0.031f, 0.006f, 0.031f), Quaternion.Euler(90f, 0f, 0f), m.metal, false, Space.Self);
            CreateCube("Recessed Trigger Detail", scanner.transform, new Vector3(0f, -0.034f, 0.006f),
                new Vector3(0.024f, 0.008f, 0.025f), m.black, false, Space.Self);
            CreateCube("Cyan QA Accent Stripe", scanner.transform, new Vector3(0f, 0.031f, 0.013f),
                new Vector3(0.042f, 0.003f, 0.035f), m.cyan, false, Space.Self);

            var lens = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            lens.name = "Pulsing Cyan Emitter Lens";
            lens.transform.SetParent(scanner.transform, false);
            lens.transform.localPosition = new Vector3(0f, 0f, 0.087f);
            lens.transform.localScale = new Vector3(0.029f, 0.023f, 0.012f);
            var lensRenderer = lens.GetComponent<MeshRenderer>();
            lensRenderer.sharedMaterial = m.cyan;
            UnityEngine.Object.DestroyImmediate(lens.GetComponent<Collider>());
            var tip = new GameObject("Scan Beam Origin").transform;
            tip.SetParent(scanner.transform, false);
            tip.localPosition = new Vector3(0f, 0f, 0.094f);
            var proximityLight = tip.gameObject.AddComponent<Light>();
            proximityLight.type = LightType.Spot;
            proximityLight.color = new Color(0.35f, 0.94f, 1f);
            proximityLight.range = 0.15f;
            proximityLight.spotAngle = 48f;
            proximityLight.innerSpotAngle = 28f;
            proximityLight.intensity = 0f;
            proximityLight.shadows = LightShadows.None;
            proximityLight.enabled = false;
            var line = scanner.AddComponent<LineRenderer>();
            line.name = "Cyan Measurement Beam";
            line.sharedMaterial = m.cyan;
            line.startWidth = 0.004f;
            line.endWidth = 0.0015f;
            line.useWorldSpace = true;
            line.textureMode = LineTextureMode.Stretch;
            var scannerRuntime = scanner.AddComponent<InspectionScanner>();
            scannerRuntime.SetEditorReferences(tip, line, XRNode.LeftHand, lensRenderer, proximityLight, mount.transform);
            CreateHelmetLabel(scanner.transform, "INS-01", new Vector3(0f, 0.033f, -0.021f), Cyan, true);
        }

        static Mesh CreateScannerBodyMesh()
        {
            const string path = Root + "/Meshes/ScannerTaperedBody.asset";
            DeleteAssetIfPresent(path);
            const int segments = 18;
            var rings = new[]
            {
                new Vector3(0.030f, 0.025f, -0.066f),
                new Vector3(0.038f, 0.032f, -0.040f),
                new Vector3(0.039f, 0.033f, 0.008f),
                new Vector3(0.031f, 0.027f, 0.048f),
                new Vector3(0.020f, 0.018f, 0.080f),
            };
            var vertices = new List<Vector3>(rings.Length * segments);
            var uvs = new List<Vector2>(rings.Length * segments);
            for (var ring = 0; ring < rings.Length; ++ring)
            for (var i = 0; i < segments; ++i)
            {
                var angle = i * Mathf.PI * 2f / segments;
                var spec = rings[ring];
                vertices.Add(new Vector3(Mathf.Cos(angle) * spec.x, Mathf.Sin(angle) * spec.y, spec.z));
                uvs.Add(new Vector2(i / (float)segments, ring / (float)(rings.Length - 1)));
            }
            var triangles = new List<int>((rings.Length - 1) * segments * 6);
            for (var ring = 0; ring < rings.Length - 1; ++ring)
            for (var i = 0; i < segments; ++i)
            {
                var next = (i + 1) % segments;
                var a = ring * segments + i;
                var b = ring * segments + next;
                var c = (ring + 1) * segments + i;
                var d = (ring + 1) * segments + next;
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
                triangles.Add(b); triangles.Add(d); triangles.Add(c);
            }
            var rearCenter = vertices.Count;
            vertices.Add(new Vector3(0f, 0f, rings[0].z));
            uvs.Add(new Vector2(0.5f, 0f));
            var frontCenter = vertices.Count;
            vertices.Add(new Vector3(0f, 0f, rings[rings.Length - 1].z));
            uvs.Add(new Vector2(0.5f, 1f));
            for (var i = 0; i < segments; ++i)
            {
                var next = (i + 1) % segments;
                triangles.Add(rearCenter); triangles.Add(next); triangles.Add(i);
                var front = (rings.Length - 1) * segments;
                triangles.Add(frontCenter); triangles.Add(front + i); triangles.Add(front + next);
            }
            var mesh = new Mesh { name = "INS-01 tapered ergonomic scanner body" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        static void CreateTrainingButtons(Transform parent, TrainingSessionController session,
            (Material helmet, Material floor, Material wall, Material metal, Material darkMetal,
                Material glass, Material cyan, Material amber, Material green, Material white, Material black) m)
        {
            var controls = new GameObject("Physical XR Training Controls").transform;
            controls.SetParent(parent);
            CreateCube("Shared Recessed Control Plinth", controls, new Vector3(0f, 1.07f, 0.13f),
                new Vector3(1.16f, 0.25f, 0.16f), m.darkMetal, true);
            CreateCube("Plinth Brushed Metal Trim", controls, new Vector3(0f, 1.07f, 0.043f),
                new Vector3(1.08f, 0.19f, 0.016f), m.metal, false);
            CreatePressableButton<TrainingStartButton>("BEGIN Training Button", controls, session,
                new Vector3(-0.30f, 1.08f, 0.025f), "BEGIN", m.darkMetal, m.green,
                new Color(0.18f, 0.82f, 0.38f), new Color(0.35f, 1f, 0.58f), new Color(0.82f, 1f, 0.86f));
            CreatePressableButton<TrainingResetButton>("RESTART Training Button", controls, session,
                new Vector3(0.30f, 1.08f, 0.025f), "RESTART", m.darkMetal, m.cyan,
                new Color(0.12f, 0.52f, 0.68f), new Color(0.25f, 0.84f, 1f), new Color(0.78f, 0.96f, 1f));
        }

        static void CreatePressableButton<T>(string name, Transform parent, TrainingSessionController session,
            Vector3 position, string labelText, Material housingMaterial, Material capMaterial,
            Color idle, Color hover, Color pressed) where T : MechanicalTrainingButtonBase
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent);
            root.transform.position = position;
            var collider = root.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, 0f, -0.026f);
            collider.size = new Vector3(0.42f, 0.16f, 0.06f);
            root.AddComponent<XRSimpleInteractable>();

            var bezel = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            bezel.name = "Rounded Recessed Bezel";
            bezel.transform.SetParent(root.transform, false);
            bezel.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            bezel.transform.localScale = new Vector3(0.095f, 0.22f, 0.045f);
            bezel.GetComponent<MeshRenderer>().sharedMaterial = housingMaterial;
            UnityEngine.Object.DestroyImmediate(bezel.GetComponent<Collider>());

            var cap = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            cap.name = "Raised 3D Button Cap - 18mm";
            cap.transform.SetParent(root.transform, false);
            cap.transform.localPosition = new Vector3(0f, 0f, -0.035f);
            cap.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            cap.transform.localScale = new Vector3(0.075f, 0.19f, 0.035f);
            var capRenderer = cap.GetComponent<MeshRenderer>();
            capRenderer.sharedMaterial = capMaterial;
            UnityEngine.Object.DestroyImmediate(cap.GetComponent<Collider>());

            var label = new GameObject("Face Label - " + labelText).AddComponent<TextMeshPro>();
            label.transform.SetParent(cap.transform, false);
            label.transform.localPosition = new Vector3(0f, 0f, -0.53f);
            label.transform.localRotation = Quaternion.Euler(0f, 0f, -90f);
            // Counter the cap's non-uniform scale so the face label remains broad and readable.
            label.transform.localScale = new Vector3(0.15f, 0.34f, 0.30f);
            label.text = labelText;
            label.fontSize = 3.3f;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.outlineWidth = 0.18f;
            label.outlineColor = new Color32(0, 18, 24, 220);

            var behavior = root.AddComponent<T>();
            behavior.SetEditorReferences(session, cap.transform, capRenderer, idle, hover, pressed);
        }

        static UiReferences CreateWorldSpaceUi(Transform parent,
            (Material helmet, Material floor, Material wall, Material metal, Material darkMetal,
                Material glass, Material cyan, Material amber, Material green, Material white, Material black) m)
        {
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            if (font == null)
                throw new InvalidOperationException("TMP essential font is not available after import.");

            var headerCanvas = CreateCanvas("Module Header Canvas", parent, new Vector3(0f, 2.50f, 2.39f), new Vector2(1900f, 220f));
            CreateUiImage("Header Navy Plate", headerCanvas.transform, new Vector2(1900f, 220f), new Color(0.025f, 0.055f, 0.09f, 0.96f));
            var objective = CreateUiText("Objective", headerCanvas.transform, new Vector2(1760f, 160f), Vector2.zero,
                "MODULE 1  /  HELMET DEFECT INSPECTION", 64f, font, TextAlignmentOptions.Center, Color.white);

            var introCanvas = CreateCanvas("Start Panel", parent, new Vector3(-1.30f, 1.55f, 2.39f), new Vector2(1020f, 900f));
            CreateUiImage("Intro Plate", introCanvas.transform, new Vector2(1020f, 900f), new Color(0.035f, 0.07f, 0.10f, 0.94f));
            CreateUiText("Intro Eyebrow", introCanvas.transform, new Vector2(850f, 76f), new Vector2(0f, 354f),
                "QUALITY ASSURANCE TRAINING", 36f, font, TextAlignmentOptions.Left, Cyan);
            CreateUiText("Intro Title", introCanvas.transform, new Vector2(850f, 190f), new Vector2(0f, 235f),
                "Find dimensional defects\nin the A2 helmet", 62f, font, TextAlignmentOptions.Left, Color.white);
            CreateUiText("Intro Body", introCanvas.transform, new Vector2(850f, 390f), new Vector2(0f, -54f),
                "A1 is the approved reference.\nA2 is the suspect scan.\n\n1  Compare both shell profiles\n2  Aim INS-01 at A2\n3  Press the scanner trigger\n4  Log all 10 findings\n\nPress BEGIN to start.",
                38f, font, TextAlignmentOptions.TopLeft, new Color(0.83f, 0.87f, 0.88f, 1f));
            CreateUiText("Comfort Hint", introCanvas.transform, new Vector2(850f, 100f), new Vector2(0f, -376f),
                "LEFT STICK: move   RIGHT STICK: snap   B: recenter", 29f, font, TextAlignmentOptions.Left, new Color(0.66f, 0.74f, 0.76f, 1f));

            var findingsCanvas = CreateCanvas("Findings Panel", parent, new Vector3(1.29f, 1.55f, 2.39f), new Vector2(1020f, 900f));
            CreateUiImage("Findings Plate", findingsCanvas.transform, new Vector2(1020f, 900f), new Color(0.035f, 0.07f, 0.10f, 0.94f));
            var progress = CreateUiText("Progress", findingsCanvas.transform, new Vector2(850f, 90f), new Vector2(0f, 354f),
                "QA FINDINGS  00 / 10", 43f, font, TextAlignmentOptions.Left, Amber);
            var details = CreateUiText("Defect Detail", findingsCanvas.transform, new Vector2(850f, 590f), new Vector2(0f, -12f),
                "Press BEGIN.\nThen inspect A2 with the scanner.\nCompare it against reference helmet A1.",
                37f, font, TextAlignmentOptions.TopLeft, new Color(0.85f, 0.88f, 0.89f, 1f));
            CreateUiText("Data Provenance", findingsCanvas.transform, new Vector2(850f, 92f), new Vector2(0f, -374f),
                "SOURCE: measured A1-to-A2 surface deviation", 27f, font, TextAlignmentOptions.Left,
                new Color(0.54f, 0.63f, 0.66f, 1f));

            var completion = new GameObject("Completion Panel");
            completion.transform.SetParent(findingsCanvas.transform, false);
            var rect = completion.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(850f, 210f);
            rect.anchoredPosition = new Vector2(0f, -240f);
            var completionImage = completion.AddComponent<Image>();
            completionImage.color = new Color(0.05f, 0.35f, 0.18f, 0.96f);
            CreateUiText("Completion Text", completion.transform, new Vector2(770f, 160f), Vector2.zero,
                "INSPECTION COMPLETE\nA2 placed on quality hold", 42f, font, TextAlignmentOptions.Center, Color.white);
            completion.SetActive(false);
            return new UiReferences(objective, progress, details, introCanvas, completion);
        }

        static GameObject CreateCanvas(string name, Transform parent, Vector3 position, Vector2 size)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(TrackedDeviceGraphicRaycaster));
            root.transform.SetParent(parent);
            root.transform.SetPositionAndRotation(position, Quaternion.identity);
            root.transform.localScale = Vector3.one * 0.001f;
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 2;
            var rect = root.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            return root;
        }

        static Image CreateUiImage(string name, Transform parent, Vector2 size, Color color)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            root.transform.SetParent(parent, false);
            var rect = root.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;
            var image = root.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        static TMP_Text CreateUiText(string name, Transform parent, Vector2 size, Vector2 anchoredPosition,
            string text, float fontSize, TMP_FontAsset font, TextAlignmentOptions alignment, Color color)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            root.transform.SetParent(parent, false);
            var rect = root.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;
            var tmp = root.GetComponent<TextMeshProUGUI>();
            tmp.font = font;
            tmp.fontSize = fontSize;
            tmp.text = text;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.extraPadding = true;
            tmp.lineSpacing = 4f;
            tmp.outlineWidth = 0.12f;
            tmp.outlineColor = new Color32(0, 12, 18, 210);
            tmp.raycastTarget = false;
            return tmp;
        }

        static void CreateHelmetLabel(Transform parent, string text, Vector3 position, Color color, bool local = false)
        {
            var root = new GameObject($"Label - {text}");
            root.transform.SetParent(parent, false);
            if (local)
            {
                root.transform.localPosition = position;
                root.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                root.transform.localScale = Vector3.one * 0.012f;
            }
            else
            {
                root.transform.position = position;
                root.transform.rotation = Quaternion.identity;
                root.transform.localScale = Vector3.one * 0.043f;
            }
            var label = root.AddComponent<TextMeshPro>();
            label.text = text;
            label.fontSize = 3.8f;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.color = color;
            label.outlineWidth = 0.14f;
            label.outlineColor = new Color32(0, 12, 18, 220);
            label.textWrappingMode = TextWrappingModes.NoWrap;
        }

        static Volume CreateLightingAndVolume(Transform parent)
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.23f, 0.29f, 0.34f, 1f);
            RenderSettings.ambientEquatorColor = new Color(0.15f, 0.17f, 0.18f, 1f);
            RenderSettings.ambientGroundColor = new Color(0.06f, 0.07f, 0.08f, 1f);
            RenderSettings.ambientIntensity = 0.72f;

            var lights = new GameObject("Lighting - 5 Lights Total").transform;
            lights.SetParent(parent);
            var directional = new GameObject("Directional - Soft Lab Fill");
            directional.transform.SetParent(lights);
            directional.transform.rotation = Quaternion.Euler(42f, -28f, 0f);
            var sun = directional.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.91f, 0.78f);
            sun.intensity = 0.72f;
            sun.shadows = LightShadows.Soft;

            CreateSpot("A1 Inspection Spot", lights, new Vector3(-0.65f, 2.65f, -0.25f), new Vector3(-0.45f, 0.9f, 0.60f), new Color(0.75f, 0.89f, 1f), 6.5f, 52f);
            CreateSpot("A2 Inspection Spot", lights, new Vector3(0.65f, 2.65f, -0.25f), new Vector3(0.45f, 0.9f, 0.60f), new Color(1f, 0.86f, 0.66f), 6.5f, 52f);
            CreateSpot("Panel Wash Left", lights, new Vector3(-1.45f, 2.80f, 1.25f), new Vector3(-1.28f, 1.55f, 2.38f), new Color(0.72f, 0.88f, 1f), 4.2f, 58f);
            CreateSpot("Panel Wash Right", lights, new Vector3(1.45f, 2.80f, 1.25f), new Vector3(1.28f, 1.55f, 2.38f), new Color(1f, 0.82f, 0.60f), 4.2f, 58f);

            const string volumePath = Root + "/Settings/HelmetLabVolume.asset";
            DeleteAssetIfPresent(volumePath);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "Helmet Lab Global Volume";
            AssetDatabase.CreateAsset(profile, volumePath);
            var color = profile.Add<ColorAdjustments>();
            AssetDatabase.AddObjectToAsset(color, profile);
            color.postExposure.Override(0.1f);
            color.contrast.Override(7f);
            color.saturation.Override(-4f);
            var tone = profile.Add<Tonemapping>();
            AssetDatabase.AddObjectToAsset(tone, profile);
            tone.mode.Override(TonemappingMode.Neutral);
            var vignette = profile.Add<Vignette>();
            AssetDatabase.AddObjectToAsset(vignette, profile);
            vignette.active = true;
            vignette.color.Override(Color.black);
            vignette.center.Override(new Vector2(0.5f, 0.5f));
            vignette.intensity.Override(0.08f);
            vignette.smoothness.Override(0.72f);
            vignette.rounded.Override(true);
            EditorUtility.SetDirty(color);
            EditorUtility.SetDirty(tone);
            EditorUtility.SetDirty(vignette);
            EditorUtility.SetDirty(profile);
            var volumeObject = new GameObject("Global Volume - One Only");
            volumeObject.transform.SetParent(parent);
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1f;
            volume.sharedProfile = profile;
            return volume;
        }

        static void CreateSpot(string name, Transform parent, Vector3 position, Vector3 target,
            Color color, float intensity, float angle)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent);
            root.transform.position = position;
            root.transform.rotation = Quaternion.LookRotation(target - position);
            var light = root.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = color;
            light.intensity = intensity;
            light.range = 4.5f;
            light.spotAngle = angle;
            light.innerSpotAngle = angle * 0.72f;
            light.shadows = LightShadows.Soft;
        }

        static void CreateFloorBoundary(Transform parent,
            (Material helmet, Material floor, Material wall, Material metal, Material darkMetal,
                Material glass, Material cyan, Material amber, Material green, Material white, Material black) m)
        {
            var boundary = new GameObject("Room Scale Safety Boundary - 2.8m x 2.2m").transform;
            boundary.SetParent(parent);
            CreateCube("Boundary Front", boundary, new Vector3(0f, 0.008f, -2.10f), new Vector3(2.8f, 0.016f, 0.025f), m.amber, false);
            CreateCube("Boundary Back", boundary, new Vector3(0f, 0.008f, 0.10f), new Vector3(2.8f, 0.016f, 0.025f), m.amber, false);
            CreateCube("Boundary Left", boundary, new Vector3(-1.40f, 0.008f, -1f), new Vector3(0.025f, 0.016f, 2.2f), m.amber, false);
            CreateCube("Boundary Right", boundary, new Vector3(1.40f, 0.008f, -1f), new Vector3(0.025f, 0.016f, 2.2f), m.amber, false);
        }

        static GameObject CreateCube(string name, Transform parent, Vector3 position, Vector3 scale,
            Material material, bool keepCollider, Space positionSpace = Space.World)
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.name = name;
            root.transform.SetParent(parent, false);
            if (positionSpace == Space.Self)
                root.transform.localPosition = position;
            else
                root.transform.position = position;
            root.transform.localScale = scale;
            root.GetComponent<MeshRenderer>().sharedMaterial = material;
            if (!keepCollider)
                UnityEngine.Object.DestroyImmediate(root.GetComponent<Collider>());
            return root;
        }

        static GameObject CreateCylinder(string name, Transform parent, Vector3 position, Vector3 scale,
            Quaternion rotation, Material material, bool keepCollider, Space positionSpace = Space.World)
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            root.name = name;
            root.transform.SetParent(parent, false);
            if (positionSpace == Space.Self)
            {
                root.transform.localPosition = position;
                root.transform.localRotation = rotation;
            }
            else
            {
                root.transform.position = position;
                root.transform.rotation = rotation;
            }
            root.transform.localScale = scale;
            root.GetComponent<MeshRenderer>().sharedMaterial = material;
            if (!keepCollider)
                UnityEngine.Object.DestroyImmediate(root.GetComponent<Collider>());
            return root;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name)
                return root;
            foreach (Transform child in root)
            {
                var result = FindDeep(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }

        static void CreatePreviewScreenshot(Scene scene)
        {
            if (Application.isBatchMode && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.Log("[HelmetBuilder] Skipping documentation preview render on the batch-mode null graphics device.");
                return;
            }
            const string texturePath = Root + "/Documentation/HelmetModule1_Preview.png";
            var cameraObject = new GameObject("Editor Documentation Camera");
            SceneManager.MoveGameObjectToScene(cameraObject, scene);
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, 1.58f, -2.18f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 1.22f, 0.70f) - camera.transform.position);
            camera.fieldOfView = 67f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.032f, 0.035f, 1f);
            camera.allowHDR = false;
            var rig = GameObject.Find("XR Origin - Room Scale Quest 3");
            var hiddenRenderers = new List<Renderer>();
            if (rig != null)
                hiddenRenderers.AddRange(rig.GetComponentsInChildren<Renderer>(true));
            var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>(FindObjectsInactive.Include);
            if (scanner != null)
                hiddenRenderers.AddRange(scanner.GetComponentsInChildren<Renderer>(true));
            var rendererStates = hiddenRenderers.Select(item => item.enabled).ToArray();
            foreach (var item in hiddenRenderers)
                item.enabled = false;
            var target = new RenderTexture(1600, 1000, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            var texture = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0);
            texture.Apply();
            File.WriteAllBytes(texturePath, texture.EncodeToPNG());
            camera.targetTexture = null;
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(texture);
            UnityEngine.Object.DestroyImmediate(cameraObject);
            for (var i = 0; i < hiddenRenderers.Count; ++i)
                if (hiddenRenderers[i] != null)
                    hiddenRenderers[i].enabled = rendererStates[i];
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
