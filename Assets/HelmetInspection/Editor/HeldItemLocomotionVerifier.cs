using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;

namespace HelmetInspection.Editor
{
    /// <summary>Native physics regressions for carrying props through locomotion/recenter transitions.</summary>
    public static class HeldItemLocomotionVerifier
    {
        const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        const float Step = 1f / 72f;
        const string PendingKey = "HelmetInspection.HeldLocomotion.Pending";
        const string StrictKey = "HelmetInspection.HeldLocomotion.Strict";
        static readonly List<string> Failures = new List<string>();
        static double s_Deadline;

        [MenuItem("Helmet Inspection/Diagnostics/Diagnose Held Item Collision Reset")]
        public static void DiagnoseRecenterCollisionPair() => StartVerification(false);

        [MenuItem("Helmet Inspection/Diagnostics/Verify Held Item Locomotion")]
        public static void Verify() => StartVerification(true);

        // Native isolated physics scenes require Play Mode. Batch callers omit -quit;
        // this runner exits Unity after the actual physics checks complete.
        static void StartVerification(bool strict)
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Start the verifier outside Play Mode.");
            EditorSceneManager.OpenScene(ScenePath);
            SessionState.SetBool(PendingKey, true);
            SessionState.SetBool(StrictKey, strict);
            ResumeVerification();
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        static void ResumeVerification()
        {
            if (!SessionState.GetBool(PendingKey, false))
                return;
            s_Deadline = EditorApplication.timeSinceStartup + 120;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > s_Deadline)
                    throw new TimeoutException("Timed out entering Play Mode for held-item verification.");
                if (!EditorApplication.isPlaying || Time.timeSinceLevelLoad < 0.2f)
                    return;
                Run(SessionState.GetBool(StrictKey, false));
                Finish(0);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                Finish(1);
            }
        }

        static void Finish(int code)
        {
            SessionState.SetBool(PendingKey, false);
            EditorApplication.update -= Tick;
            if (Application.isBatchMode)
                EditorApplication.Exit(code);
            else
                EditorApplication.ExitPlaymode();
        }

        static void Run(bool strict)
        {
            Failures.Clear();
            var scene = SceneManager.GetSceneByPath(ScenePath);
            if (!scene.isLoaded)
                throw new InvalidOperationException("Training scene was not loaded for the Play Mode checks.");
                Physics.SyncTransforms();
                var roots = scene.GetRootGameObjects();
                var origin = roots.SelectMany(root => root.GetComponentsInChildren<XROrigin>(true)).Single();
                var character = origin.GetComponent<CharacterController>();
                var helmets = roots.SelectMany(root => root.GetComponentsInChildren<HelmetOutOfBoundsRecovery>(true)).ToArray();
                var scanner = roots.SelectMany(root => root.GetComponentsInChildren<InspectionScanner>(true)).Single();
                var props = helmets.Select(item => item.GetComponent<Rigidbody>()).Append(scanner.GetComponent<Rigidbody>()).ToArray();
                PropInteractionVerifier.Verify(origin);
                BulgeScanAccessibilityVerifier.VerifyInPlayMode();
                ComfortPresentationVerifier.VerifyInPlayMode();
                StandingHeightVerifier.VerifyInPlayMode();
                Check(origin.GetComponent<XRPhysicalTranslationLock>() is { enabled: false },
                    "Production camera preserves natural positional tracking (legacy lock is disabled).");
                Check(character != null && character.enabled, "Authored player has an enabled CharacterController.");
                Check(helmets.Length == 2 && props.Length == 3, "Both helmets and the scanner are included in carry checks.");
                if (character == null)
                    throw new InvalidOperationException("The training player has no CharacterController.");

                var propLayer = LayerMask.NameToLayer("InspectionProp");
                Check(propLayer >= 0 && (character.excludeLayers.value & (1 << propLayer)) != 0,
                    "CharacterController persistently excludes the InspectionProp physics layer.");
                foreach (var prop in props)
                {
                    var colliders = SolidColliders(prop);
                    Check(colliders.Length > 0 && colliders.All(collider => collider.gameObject.layer == propLayer),
                        $"{prop.name}: all solid attached colliders use InspectionProp.");
                    if (colliders.Length == 0)
                        continue;
                    DiagnosePair(character, colliders[0]);
                    VerifyWalking(character, colliders[0], prop != scanner.GetComponent<Rigidbody>());
                    VerifyFloor(colliders[0]);
                }
                if (helmets.Length == 2)
                    VerifyHelmetContact(SolidColliders(helmets[0].GetComponent<Rigidbody>())[0],
                        SolidColliders(helmets[1].GetComponent<Rigidbody>())[0]);
                VerifyTrackingBeforeMovement(character);
                VerifyNaturalTrackingAndHandover(character);
                Debug.Log($"[HeldLocomotion] {(strict ? "Verification" : "Baseline diagnosis")} completed: {Failures.Count} failed checks.");
                if (strict && Failures.Count != 0)
                    throw new InvalidOperationException("Held item locomotion verification failed:\n" + string.Join("\n", Failures));
        }

        static Collider[] SolidColliders(Rigidbody body) => body.GetComponentsInChildren<Collider>(true)
            .Where(collider => !collider.isTrigger && collider.attachedRigidbody == body).ToArray();

        static void DiagnosePair(CharacterController source, Collider prop)
        {
            using var arena = new Arena();
            var character = arena.Player(source);
            var carried = arena.Prop(prop, new Vector3(0f, 1.25f, 0.1f), true);
            Physics.SyncTransforms();
            Physics.IgnoreCollision(character, carried, true);
            var initial = Physics.GetIgnoreCollision(character, carried);
            character.enabled = false;
            character.enabled = true;
            var afterToggle = Physics.GetIgnoreCollision(character, carried);
            character.height += 0.01f;
            character.center += new Vector3(0.005f, 0f, 0.005f);
            var afterResize = Physics.GetIgnoreCollision(character, carried);
            Debug.Log($"[HeldLocomotion] {prop.name} pair ignore: selected={initial}, after controller disable/enable={afterToggle}, " +
                      $"after height/center update={afterResize}; persistent controller mask={character.excludeLayers.value}.");
        }

        static void VerifyWalking(CharacterController source, Collider prop, bool velocityTracked)
        {
            using var arena = new Arena();
            arena.Floor();
            arena.Box("Wall", new Vector3(0f, 1.4f, 2f), new Vector3(4f, 3f, 0.2f), 0);
            var character = arena.Player(source);
            var carried = arena.Prop(prop, new Vector3(0f, 1.2f, 0.12f), !velocityTracked);
            var body = carried.attachedRigidbody;
            Physics.SyncTransforms();
            Physics.IgnoreCollision(character, carried, true);
            // The app recenters after the auto-socket's first selection and again on resume.
            character.enabled = false;
            character.enabled = true;
            var maximumHeightError = 0f;
            var maximumSideError = 0f;
            for (var frame = 0; frame < 150; ++frame)
            {
                // Simulate XRI returning ownership of collision filtering after a
                // release/re-grab transition. Locomotion must not depend on the pair.
                if (frame == 30)
                    Physics.IgnoreCollision(character, carried, false);
                var target = character.transform.position + new Vector3(0f, 1.2f, 0.12f);
                if (velocityTracked)
                    body.linearVelocity = Vector3.ClampMagnitude((target - body.position) / Step, 3.5f);
                else
                    body.MovePosition(target);
                // XRI refreshes capsule dimensions from the body pose before every Move.
                character.height = source.height + Mathf.Sin(frame * 0.2f) * 0.002f;
                character.center = new Vector3(0f, character.height * 0.5f + source.skinWidth, 0f);
                character.Move(new Vector3(0f, -0.04f, 1.75f * Step));
                arena.Simulate();
                maximumHeightError = Mathf.Max(maximumHeightError, Mathf.Abs(character.transform.position.y));
                maximumSideError = Mathf.Max(maximumSideError, Mathf.Abs(character.transform.position.x));
            }
            var reached = character.transform.position.z;
            Check(reached > 1.45f && reached < 1.76f && maximumHeightError < 0.10f && maximumSideError < 0.025f,
                $"{prop.name}: carry movement reaches wall without player displacement or clipping " +
                $"(z={reached:F3}, vertical={maximumHeightError:F3}, sideways={maximumSideError:F3}).");
            for (var frame = 0; frame < 55; ++frame)
            {
                var target = character.transform.position + new Vector3(0f, 1.2f, 0.12f);
                if (velocityTracked)
                    body.linearVelocity = Vector3.ClampMagnitude((target - body.position) / Step, 3.5f);
                else
                    body.MovePosition(target);
                character.Move(new Vector3(0f, -0.04f, -1.75f * Step));
                arena.Simulate();
            }
            Check(character.transform.position.z < reached - 0.9f && Mathf.Abs(character.transform.position.y) < 0.10f,
                $"{prop.name}: walking backward while carrying works after contact with a wall.");
        }

        static void VerifyFloor(Collider prop)
        {
            using var arena = new Arena();
            arena.Floor();
            var dropped = arena.Prop(prop, new Vector3(0f, 0.9f, 0f), false);
            dropped.attachedRigidbody.useGravity = true;
            Physics.SyncTransforms();
            for (var frame = 0; frame < 180; ++frame)
                arena.Simulate();
            Check(dropped.bounds.min.y > -0.035f && dropped.bounds.min.y < 0.1f,
                $"{prop.name}: released item still lands on the environment floor (bottom={dropped.bounds.min.y:F3}).");
        }

        static void VerifyHelmetContact(Collider first, Collider second)
        {
            using var arena = new Arena();
            var a = arena.Prop(first, new Vector3(-0.10f, 1f, 0f), false);
            var b = arena.Prop(second, new Vector3(0.10f, 1f, 0f), false);
            Physics.SyncTransforms();
            var initialDistance = Vector3.Distance(a.transform.position, b.transform.position);
            for (var frame = 0; frame < 35; ++frame)
                arena.Simulate();
            var finalDistance = Vector3.Distance(a.transform.position, b.transform.position);
            Check(finalDistance > initialDistance + 0.02f,
                $"Helmets still resolve mutual solid contact (initial={initialDistance:F3}, final={finalDistance:F3}).");
        }

        static void VerifyTrackingBeforeMovement(CharacterController source)
        {
            using var arena = new Arena();
            var character = arena.Player(source);
            var rig = character.gameObject;
            rig.SetActive(false);
            var origin = rig.AddComponent<XROrigin>();
            origin.Origin = rig;
            var offset = arena.Object("Camera Offset");
            offset.transform.SetParent(rig.transform, false);
            var head = arena.Object("Head").AddComponent<Camera>();
            head.transform.SetParent(offset.transform, false);
            head.transform.localPosition = new Vector3(0f, 1.68f, 0f);
            origin.CameraFloorOffsetObject = offset;
            origin.Camera = head;
            var transformerObject = arena.Object("Body Transformer");
            transformerObject.transform.SetParent(rig.transform, false);
            var transformer = transformerObject.AddComponent<XRBodyTransformer>();
            transformer.xrOrigin = origin;
            var translationLock = rig.AddComponent<XRPhysicalTranslationLock>();
            translationLock.SetEditorReferences(origin, new Vector3(0f, 1.68f, 0f));
            rig.SetActive(true);
            try
            {
                translationLock.enabled = false;
                head.transform.localPosition = new Vector3(0.4f, 1.9f, -0.3f);
                transformer.QueueTransformation(new XROriginMovement { motion = new Vector3(0.01f, 0f, 0f) });
                InvokeLifecycle(transformer, "Update");
                Debug.Log($"[HeldLocomotion] Old LateUpdate-only timing reproduction: capsule center=" +
                          $"{character.center:F3}, rendered head anchor=(0.000, 1.680, 0.000).");
                Check(Mathf.Abs(character.center.x - 0.4f) < 0.001f &&
                      Mathf.Abs(character.center.z + 0.3f) < 0.001f,
                    "Control case reproduces the collider/head offset when correction runs after locomotion.");
                translationLock.enabled = true;
                head.transform.localPosition = new Vector3(0.4f, 1.9f, -0.3f);
                transformer.QueueTransformation(new XROriginMovement { motion = new Vector3(0.01f, 0f, 0f) });
                InvokeLifecycle(transformer, "Update");
                var headInOrigin = rig.transform.InverseTransformPoint(head.transform.position);
                Check(Vector3.Distance(headInOrigin, new Vector3(0f, 1.68f, 0f)) < 0.0001f &&
                      Mathf.Abs(character.center.x) < 0.0001f && Mathf.Abs(character.center.z) < 0.0001f,
                    $"Physical translation is anchored before the body movement/capsule update (head={headInOrigin:F3}).");
            }
            finally
            {
                rig.SetActive(false);
            }
        }

        static void InvokeLifecycle(object component, string name) => component.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(component, null);

        static void VerifyNaturalTrackingAndHandover(CharacterController source)
        {
            using var arena = new Arena();
            var character = arena.Player(source);
            var rig = character.gameObject;
            rig.SetActive(false);
            var origin = rig.AddComponent<XROrigin>();
            origin.Origin = rig;
            var offset = arena.Object("Natural tracking offset");
            offset.transform.SetParent(rig.transform, false);
            var head = arena.Object("Natural tracked head").AddComponent<Camera>();
            head.transform.SetParent(offset.transform, false);
            head.transform.localPosition = new Vector3(0f, 1.5f, 0f);
            origin.CameraFloorOffsetObject = offset;
            origin.Camera = head;
            var hand = arena.Object("Tracked hand");
            hand.transform.SetParent(offset.transform, false);
            hand.transform.localPosition = new Vector3(0.25f, 1.15f, 0.3f);
            var transformerObject = arena.Object("Natural body transformer");
            transformerObject.transform.SetParent(rig.transform, false);
            var transformer = transformerObject.AddComponent<XRBodyTransformer>();
            transformer.xrOrigin = origin;
            var recenter = rig.AddComponent<XRTrackingSpaceRecenter>();
            recenter.SetEditorReferences(origin, new Vector3(0f, 1.68f, -1.8f), Vector3.forward);
            rig.SetActive(true);
            var trackedLocalPose = new Vector3(0.12f, 1.35f, -0.08f);
            head.transform.localPosition = trackedLocalPose;
            head.transform.localRotation = Quaternion.Euler(12f, 25f, 4f);
            var trackedRotation = head.transform.localRotation;
            transformer.QueueTransformation(new XROriginMovement { motion = new Vector3(0.01f, 0f, 0f) });
            InvokeLifecycle(transformer, "Update");
            Check(Vector3.Distance(rig.transform.InverseTransformPoint(head.transform.position), trackedLocalPose) < 0.0001f &&
                  Quaternion.Angle(head.transform.localRotation, trackedRotation) < 0.001f,
                "Lean/crouch/rotation reach the camera 1:1 without a forced 1.68 m eye height.");
            Check(Mathf.Abs(character.center.x - trackedLocalPose.x) < 0.001f &&
                  Mathf.Abs(character.center.z - trackedLocalPose.z) < 0.001f,
                "Locomotion capsule follows the same latest tracked head position used for rendering.");

            var station = new Vector3(0.35f, head.transform.position.y, -0.8f);
            var heading = Quaternion.Euler(0, 35, 0) * Vector3.forward;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            void Field(string name, object value) => typeof(XRTrackingSpaceRecenter).GetField(name, flags).SetValue(recenter, value);
            Field("m_InitialPlacementComplete", true);
            Field("m_HasLastWornPose", true);
            Field("m_LastWornPosition", station);
            Field("m_LastWornForward", heading);
            var presence = typeof(XRTrackingSpaceRecenter).GetMethod("ObservePresence", flags);
            presence.Invoke(recenter, new object[] { true });
            presence.Invoke(recenter, new object[] { false });
            Check((bool)typeof(XRTrackingSpaceRecenter).GetField("m_HasReturnPose", flags).GetValue(recenter) &&
                  (Vector3)typeof(XRTrackingSpaceRecenter).GetField("m_ReturnPosition", flags).GetValue(recenter) == station,
                "Headset removal remembers the last worn virtual station before the device is carried.");
            head.transform.localPosition = new Vector3(8f, 1.15f, -6f);
            var localAfterHandover = head.transform.localPosition;
            var liveHeight = head.transform.position.y;
            var handLocal = hand.transform.localPosition;
            recenter.AlignTrackingSpace(station, heading);
            Check(Vector2.Distance(new Vector2(head.transform.position.x, head.transform.position.z),
                      new Vector2(station.x, station.z)) < 0.001f && Mathf.Abs(head.transform.position.y - liveHeight) < 0.001f,
                "Handover recenter restores the virtual station after large physical displacement and preserves the new user's eye height.");
            Check(head.transform.localPosition == localAfterHandover && hand.transform.localPosition == handLocal &&
                  Quaternion.Angle(head.transform.localRotation, trackedRotation) < 0.001f,
                "Recenter moves only the origin, never the tracked head/controller local poses.");
            Check(character.enabled && character.excludeLayers == source.excludeLayers,
                "Handover preserves the enabled collision capsule and persistent carried-prop exclusions.");
            var beforeLean = head.transform.position;
            var lean = new Vector3(0.04f, -0.03f, 0.02f);
            head.transform.localPosition += lean;
            Check(Vector3.Distance(head.transform.position - beforeLean, offset.transform.TransformVector(lean)) < 0.0001f,
                "Normal leaning remains 1:1 after handover instead of snapping back to a camera anchor.");
            Field("m_HeadsetAbsent", false);
            var focus = typeof(XRTrackingSpaceRecenter).GetMethod("OnApplicationFocus", flags);
            var pause = typeof(XRTrackingSpaceRecenter).GetMethod("OnApplicationPause", flags);
            var queue = typeof(XRTrackingSpaceRecenter).GetMethod("QueueRecenter", flags);
            var pendingRoutine = typeof(XRTrackingSpaceRecenter).GetField("m_RecenterRoutine", flags);
            var preMenuPose = rig.transform.position;
            focus.Invoke(recenter, new object[] { false });
            queue.Invoke(recenter, new object[] { "menu regression", true, true });
            Check(pendingRoutine.GetValue(recenter) == null && rig.transform.position == preMenuPose,
                "Open system menus cannot trigger background recentering while the headset is still worn.");
            presence.Invoke(recenter, new object[] { false });
            presence.Invoke(recenter, new object[] { true });
            Check(pendingRoutine.GetValue(recenter) == null,
                "Removal and re-wear during system UI keep recenter deferred until the app is focused.");
            pause.Invoke(recenter, new object[] { true });
            focus.Invoke(recenter, new object[] { true });
            Check((bool)typeof(XRTrackingSpaceRecenter).GetField("m_HandoverPending", flags).GetValue(recenter) &&
                  (bool)typeof(XRTrackingSpaceRecenter).GetField("m_HasReturnPose", flags).GetValue(recenter),
                "Short focus loss cannot discard a real headset handover or its saved standing position.");
            queue.Invoke(recenter, new object[] { "suspend regression", true, true });
            Check(pendingRoutine.GetValue(recenter) == null && rig.transform.position == preMenuPose,
                "Focus arriving before resume cannot recenter a still-suspended session.");
            rig.SetActive(false);
        }

        static void Check(bool passed, string message)
        {
            Debug.Log($"[HeldLocomotion] {(passed ? "PASS" : "FAIL")}: {message}");
            if (!passed)
                Failures.Add(message);
        }

        sealed class Arena : IDisposable
        {
            readonly Scene m_Scene = SceneManager.CreateScene("HeldLocomotionFixture-" + Guid.NewGuid().ToString("N"),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));

            public GameObject Object(string name)
            {
                var item = new GameObject(name);
                SceneManager.MoveGameObjectToScene(item, m_Scene);
                return item;
            }

            public BoxCollider Box(string name, Vector3 position, Vector3 size, int layer)
            {
                var item = Object(name);
                item.layer = layer;
                item.transform.position = position;
                var box = item.AddComponent<BoxCollider>();
                box.size = size;
                return box;
            }

            public void Floor() => Box("Floor", new Vector3(0f, -0.15f, 0f), new Vector3(12f, 0.3f, 12f), 0);

            public CharacterController Player(CharacterController source)
            {
                var item = Object("Player");
                item.layer = source.gameObject.layer;
                var character = item.AddComponent<CharacterController>();
                character.height = source.height;
                character.radius = source.radius;
                character.center = new Vector3(0f, source.height * 0.5f + source.skinWidth, 0f);
                character.skinWidth = source.skinWidth;
                character.stepOffset = source.stepOffset;
                character.slopeLimit = source.slopeLimit;
                character.minMoveDistance = source.minMoveDistance;
                character.includeLayers = source.includeLayers;
                character.excludeLayers = source.excludeLayers;
                character.layerOverridePriority = source.layerOverridePriority;
                return character;
            }

            public BoxCollider Prop(Collider source, Vector3 position, bool kinematic)
            {
                var size = source.bounds.size;
                if (size.sqrMagnitude < 0.00001f)
                    throw new InvalidOperationException($"Cannot measure solid collider {source.name}.");
                var collider = Box(source.name, position, size, source.gameObject.layer);
                collider.includeLayers = source.includeLayers;
                collider.excludeLayers = source.excludeLayers;
                collider.layerOverridePriority = source.layerOverridePriority;
                var body = collider.gameObject.AddComponent<Rigidbody>();
                body.mass = source.attachedRigidbody.mass;
                body.useGravity = false;
                body.isKinematic = kinematic;
                body.collisionDetectionMode = kinematic ? CollisionDetectionMode.ContinuousSpeculative : CollisionDetectionMode.ContinuousDynamic;
                return collider;
            }

            public void Simulate() => m_Scene.GetPhysicsScene().Simulate(Step);

            public void Dispose()
            {
                foreach (var root in m_Scene.GetRootGameObjects())
                    UnityEngine.Object.DestroyImmediate(root);
                SceneManager.UnloadSceneAsync(m_Scene);
            }
        }
    }
}
