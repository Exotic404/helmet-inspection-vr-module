using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;

namespace HelmetInspection.Editor
{
    /// <summary>Isolated native-physics checks for calibrated standing height and unchanged tracked poses.</summary>
    public static class StandingHeightVerifier
    {
        const float Tolerance = 0.001f;
        const float Step = 1f / 72f;
        static readonly List<string> Failures = new List<string>();
        static int s_Checks;

        public static void VerifyInPlayMode()
        {
            if (!Application.isPlaying)
                throw new InvalidOperationException("Standing-height verification requires Play Mode.");
            Failures.Clear();
            s_Checks = 0;
            foreach (var rawHeight in new[] { 0.35f, 1.15f, 1.68f, 1.95f })
                VerifyPhysicalHeight(rawHeight);
            VerifyHandoverAndMenuResume();
            VerifySdkOffsetOwnership(TrackingOriginModeFlags.Floor, 1.15f);
            VerifySdkOffsetOwnership(TrackingOriginModeFlags.Device, 0.12f);
            VerifyGroundedMovement();
            Debug.Log($"[StandingHeight] Completed {s_Checks} checks: {Failures.Count} failed.");
            if (Failures.Count != 0)
                throw new InvalidOperationException("Standing-height verification failed:\n" + string.Join("\n", Failures));
        }

        static void VerifyPhysicalHeight(float rawHeight)
        {
            using var fixture = new Fixture(rawHeight);
            var label = $"Raw eye height {rawHeight:F2} m";
            var headLocal = fixture.Head.localPosition;
            var headRotation = fixture.Head.localRotation;
            var leftLocal = fixture.LeftHand.localPosition;
            var rightLocal = fixture.RightHand.localPosition;
            var headWorld = fixture.Head.position;
            var leftWorld = fixture.LeftHand.position;
            var rightWorld = fixture.RightHand.position;
            var originY = fixture.Rig.transform.position.y;
            fixture.Recenter.AlignTrackingSpace(headWorld, fixture.Head.forward, true);
            var expectedDelta = Vector3.up * (fixture.Recenter.StandingEyeHeight - rawHeight);
            Check(Near(fixture.Origin.CameraInOriginSpaceHeight, 1.68f) &&
                  Near(fixture.Rig.transform.position.y, originY),
                $"{label}: calibration sets 1.68 m eye height without raising the grounded origin.");
            Check(Near(fixture.Head.position - headWorld, expectedDelta) &&
                  Near(fixture.LeftHand.position - leftWorld, expectedDelta) &&
                  Near(fixture.RightHand.position - rightWorld, expectedDelta),
                $"{label}: head and both hands receive the same one-time height adjustment.");
            Check(Near(fixture.Head.localPosition, headLocal) &&
                  Near(fixture.LeftHand.localPosition, leftLocal) &&
                  Near(fixture.RightHand.localPosition, rightLocal) &&
                  Quaternion.Angle(fixture.Head.localRotation, headRotation) < Tolerance,
                $"{label}: local tracked poses and head rotation are untouched.");
            var calibration = fixture.Offset.parent;
            var calibratedHead = fixture.Head.position;
            for (var i = 0; i < 3; i++)
                fixture.Recenter.AlignTrackingSpace(calibratedHead, fixture.Head.forward, true);
            Check(calibration != fixture.Rig.transform && fixture.Offset.parent == calibration &&
                  fixture.Rig.GetComponentsInChildren<Transform>(true).Count(item => item.name == "Standing Height Calibration") == 1 &&
                  Near(fixture.Head.position, calibratedHead) && Near(calibration.localScale, Vector3.one),
                $"{label}: repeated calibration is idempotent and does not duplicate or scale the tracking parent.");
            var lean = new Vector3(0.04f, -0.03f, 0.02f);
            fixture.Head.localPosition += lean;
            Check(Near(fixture.Head.position - calibratedHead, fixture.Offset.TransformVector(lean)),
                $"{label}: subsequent leaning and crouching remain exactly 1:1.");
            Check(fixture.Character.enabled && fixture.Character.excludeLayers.value == 256,
                $"{label}: calibration preserves the enabled character and carried-prop collision exclusions.");
        }

        static void VerifyHandoverAndMenuResume()
        {
            using var fixture = new Fixture(1.95f);
            fixture.Recenter.AlignTrackingSpace(fixture.Head.position, Vector3.forward, true);
            var standingParent = fixture.Offset.parent;
            var station = new Vector3(0.35f, fixture.Head.position.y, -0.8f);
            fixture.Head.localPosition = new Vector3(8f, 1.10f, -6f);
            fixture.LeftHand.localPosition = new Vector3(7.75f, 0.75f, -5.7f);
            fixture.RightHand.localPosition = new Vector3(8.25f, 0.75f, -5.7f);
            var localHead = fixture.Head.localPosition;
            var localLeft = fixture.LeftHand.localPosition;
            var localRight = fixture.RightHand.localPosition;
            var heading = Quaternion.Euler(0f, 35f, 0f) * Vector3.forward;
            fixture.Recenter.AlignTrackingSpace(station, heading, true);
            Check(Near(fixture.Origin.CameraInOriginSpaceHeight, 1.68f) &&
                  Vector2.Distance(Xz(fixture.Head.position), Xz(station)) < Tolerance &&
                  Near(fixture.Rig.transform.position.y, 0f),
                "A shorter new wearer returns to the saved station at standing eye height after a large physical displacement.");
            Check(fixture.Offset.parent == standingParent && Near(fixture.Head.localPosition, localHead) &&
                  Near(fixture.LeftHand.localPosition, localLeft) && Near(fixture.RightHand.localPosition, localRight),
                "Handover reuses the same height parent and preserves all tracked local poses.");

            fixture.Head.localPosition += new Vector3(0.03f, -0.30f, 0.02f);
            var crouchedHeight = fixture.Origin.CameraInOriginSpaceHeight;
            var crouchedLocalHead = fixture.Head.localPosition;
            var parentPosition = standingParent.localPosition;
            fixture.Recenter.AlignTrackingSpace(station, heading, false);
            Check(Near(crouchedHeight, 1.38f) && Near(fixture.Origin.CameraInOriginSpaceHeight, crouchedHeight) &&
                  Near(standingParent.localPosition, parentPosition) && Near(fixture.Head.localPosition, crouchedLocalHead),
                "Menu-only positional recenter preserves a deliberate crouch and does not recalibrate height.");
            var leanStart = fixture.Head.position;
            var lean = new Vector3(-0.04f, 0.05f, 0.01f);
            fixture.Head.localPosition += lean;
            Check(Near(fixture.Head.position - leanStart, fixture.Offset.TransformVector(lean)),
                "Natural leaning remains 1:1 after handover and menu resume.");
        }

        static void VerifySdkOffsetOwnership(TrackingOriginModeFlags mode, float rawHeight)
        {
            using var fixture = new Fixture(rawHeight);
            // Set only this fixture's bookkeeping; never request a mode change on the live XR subsystem.
            var modeProperty = typeof(XROrigin).GetProperty(nameof(XROrigin.CurrentTrackingOriginMode));
            if (modeProperty == null)
                throw new MissingMemberException("XROrigin.CurrentTrackingOriginMode is unavailable.");
            modeProperty.SetValue(fixture.Origin, mode);
            fixture.Origin.CameraYOffset = 1.68f;
            // Reproduce the authored edit-time 1.68 m offset before Floor initialization settles.
            fixture.Offset.localPosition = new Vector3(0f, 1.68f, 0f);
            var headLocal = fixture.Head.localPosition;
            var leftLocal = fixture.LeftHand.localPosition;
            fixture.Recenter.AlignTrackingSpace(fixture.Head.position, Vector3.forward, true);
            var sdkY = mode == TrackingOriginModeFlags.Floor ? 0f : fixture.Origin.CameraYOffset;
            Check(Near(fixture.Offset.localPosition.y, sdkY) && Near(fixture.Origin.CameraInOriginSpaceHeight, 1.68f),
                $"{mode}: calibration samples the normalized SDK-owned floor offset, not stale authored height.");
            var calibrationPosition = fixture.Offset.parent.localPosition;
            var calibratedHead = fixture.Head.position;
            var moveOffset = typeof(XROrigin).GetMethod("MoveOffsetHeight", BindingFlags.Instance | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (moveOffset == null)
                throw new MissingMethodException("XROrigin.MoveOffsetHeight is unavailable.");
            moveOffset.Invoke(fixture.Origin, null);
            Check(Near(fixture.Offset.parent.localPosition, calibrationPosition) &&
                  Near(fixture.Head.position, calibratedHead) && Near(fixture.Origin.CameraInOriginSpaceHeight, 1.68f) &&
                  Near(fixture.Head.localPosition, headLocal) && Near(fixture.LeftHand.localPosition, leftLocal),
                $"{mode}: SDK floor-offset reset leaves the separate calibration parent and tracked poses intact.");
        }

        static void VerifyGroundedMovement()
        {
            using var fixture = new Fixture(0.35f);
            fixture.Recenter.AlignTrackingSpace(fixture.Head.position, Vector3.forward, true);
            var floor = fixture.Object("Standing-height floor").AddComponent<BoxCollider>();
            floor.transform.position = new Vector3(0f, -0.15f, 0f);
            floor.size = new Vector3(12f, 0.3f, 12f);
            Physics.SyncTransforms();
            var maximumHeightError = 0f;
            for (var i = 0; i < 150; i++)
            {
                // Exercise XRI's actual capsule-resize and constrained-movement path
                // with the same downward origin transformation produced by gravity.
                fixture.Transformer.QueueTransformation(new XROriginMovement { motion = new Vector3(0.001f, -0.04f, 0.002f) });
                InvokeTransformerUpdate(fixture.Transformer);
                fixture.PhysicsScene.Simulate(Step);
                maximumHeightError = Mathf.Max(maximumHeightError, Mathf.Abs(fixture.Rig.transform.position.y));
            }
            Check(maximumHeightError < 0.06f && fixture.Character.isGrounded &&
                  Near(fixture.Origin.CameraInOriginSpaceHeight, 1.68f) &&
                  Mathf.Abs(fixture.Head.position.y - 1.68f) < 0.06f,
                $"Repeated grounded locomotion/gravity cannot undo calibrated eye height (root drift={maximumHeightError:F4} m).");
            Check(Near(fixture.Character.height, 1.68f) &&
                  Near(fixture.Character.center.y, 0.84f + fixture.Character.skinWidth) &&
                  Near(fixture.Character.center.x, fixture.Origin.CameraInOriginSpacePos.x) &&
                  Near(fixture.Character.center.z, fixture.Origin.CameraInOriginSpacePos.z) &&
                  fixture.Character.excludeLayers.value == 256,
                "XRI uses calibrated height for the floor-based capsule while preserving its horizontal alignment and prop exclusions.");
        }

        static void InvokeTransformerUpdate(XRBodyTransformer transformer)
        {
            var method = typeof(XRBodyTransformer).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException("XRBodyTransformer.Update is unavailable.");
            method.Invoke(transformer, null);
        }

        static Vector2 Xz(Vector3 value) => new Vector2(value.x, value.z);
        static bool Near(float a, float b) => Mathf.Abs(a - b) < Tolerance;
        static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < Tolerance;

        static void Check(bool passed, string message)
        {
            s_Checks++;
            Debug.Log($"[StandingHeight] {(passed ? "PASS" : "FAIL")}: {message}");
            if (!passed) Failures.Add(message);
        }

        sealed class Fixture : IDisposable
        {
            readonly Scene m_Scene;
            readonly UnderCameraBodyPositionEvaluator m_BodyEvaluator;
            readonly CharacterControllerBodyManipulator m_BodyManipulator;
            public GameObject Rig { get; }
            public XROrigin Origin { get; }
            public Transform Offset { get; }
            public Transform Head { get; }
            public Transform LeftHand { get; }
            public Transform RightHand { get; }
            public CharacterController Character { get; }
            public XRBodyTransformer Transformer { get; }
            public XRTrackingSpaceRecenter Recenter { get; }
            public PhysicsScene PhysicsScene => m_Scene.GetPhysicsScene();

            public Fixture(float rawHeadHeight)
            {
                m_Scene = SceneManager.CreateScene("StandingHeightFixture-" + Guid.NewGuid().ToString("N"),
                    new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                Rig = Object("Standing-height player");
                Rig.SetActive(false);
                Character = Rig.AddComponent<CharacterController>();
                Character.height = 1.72f;
                Character.radius = 0.24f;
                Character.skinWidth = 0.02f;
                Character.center = new Vector3(0f, 0.88f, 0f);
                Character.stepOffset = 0.18f;
                Character.excludeLayers = 256;
                Origin = Rig.AddComponent<XROrigin>();
                Origin.Origin = Rig;
                Offset = Object("Standing-height SDK floor offset").transform;
                Offset.SetParent(Rig.transform, false);
                var camera = Object("Standing-height tracked head").AddComponent<Camera>();
                camera.enabled = false;
                Head = camera.transform;
                Head.SetParent(Offset, false);
                Head.localPosition = new Vector3(0.12f, rawHeadHeight, -0.08f);
                Head.localRotation = Quaternion.Euler(8f, 0f, 3f);
                Origin.Camera = camera;
                Origin.CameraFloorOffsetObject = Offset.gameObject;
                LeftHand = Object("Standing-height left hand").transform;
                LeftHand.SetParent(Offset, false);
                LeftHand.localPosition = Head.localPosition + new Vector3(-0.25f, -0.35f, 0.3f);
                RightHand = Object("Standing-height right hand").transform;
                RightHand.SetParent(Offset, false);
                RightHand.localPosition = Head.localPosition + new Vector3(0.25f, -0.35f, 0.3f);
                var transformerObject = Object("Standing-height body transformer");
                transformerObject.transform.SetParent(Rig.transform, false);
                Transformer = transformerObject.AddComponent<XRBodyTransformer>();
                Transformer.xrOrigin = Origin;
                // XRI's fallback manipulator is a global singleton and linking a
                // fixture to it would unlink the live player's body. Own both helpers.
                m_BodyEvaluator = ScriptableObject.CreateInstance<UnderCameraBodyPositionEvaluator>();
                m_BodyManipulator = ScriptableObject.CreateInstance<CharacterControllerBodyManipulator>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(XRBodyTransformer).GetField("m_BodyPositionEvaluatorObject", flags)
                    .SetValue(Transformer, m_BodyEvaluator);
                typeof(XRBodyTransformer).GetField("m_ConstrainedBodyManipulatorObject", flags)
                    .SetValue(Transformer, m_BodyManipulator);
                Recenter = Rig.AddComponent<XRTrackingSpaceRecenter>();
                Recenter.SetEditorReferences(Origin, new Vector3(0f, 1.68f, -1.8f), Vector3.forward);
                Rig.SetActive(true);
            }

            public GameObject Object(string name)
            {
                var item = new GameObject(name);
                SceneManager.MoveGameObjectToScene(item, m_Scene);
                return item;
            }

            public void Dispose()
            {
                Rig.SetActive(false);
                foreach (var root in m_Scene.GetRootGameObjects())
                    UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(m_BodyManipulator);
                UnityEngine.Object.DestroyImmediate(m_BodyEvaluator);
                SceneManager.UnloadSceneAsync(m_Scene);
            }
        }
    }
}
