using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;

namespace HelmetInspection.Editor
{
    /// <summary>Play Mode checks using the authored hands, real physics queries,
    /// XRI select/release callbacks, and scanner trigger/session code.</summary>
    public static class PropInteractionVerifier
    {
        static int s_Checks;

        public static void RepairAndVerify()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            EditorSceneManager.OpenScene(HelmetProjectStartupRepair.TrainingScenePath);
            var origin = UnityEngine.Object.FindFirstObjectByType<XROrigin>();
            var propMask = 1 << LayerMask.NameToLayer(HeldItemLocomotionInstaller.PropLayerName);
            foreach (var direct in origin.GetComponentsInChildren<XRDirectInteractor>(true))
                Debug.Log($"[PropInteraction] Before repair: {direct.handedness} direct mask=" +
                          $"{direct.physicsLayerMask.value}; detects props={(direct.physicsLayerMask.value & propMask) != 0}.");
            foreach (var far in origin.GetComponentsInChildren<CurveInteractionCaster>(true))
                Debug.Log($"[PropInteraction] Before repair: {far.name} far mask=" +
                          $"{far.raycastMask.value}; detects props={(far.raycastMask.value & propMask) != 0}.");
            HeldItemLocomotionInstaller.Install();
            HeldItemLocomotionVerifier.Verify();
        }

        public static void Verify(XROrigin origin)
        {
            s_Checks = 0;
            var manager = UnityEngine.Object.FindFirstObjectByType<XRInteractionManager>();
            var scanner = UnityEngine.Object.FindFirstObjectByType<InspectionScanner>();
            var helmets = UnityEngine.Object.FindObjectsByType<HelmetOutOfBoundsRecovery>();
            var props = helmets.Select(item => item.GetComponent<XRGrabInteractable>())
                .Append(scanner.GetComponent<XRGrabInteractable>()).ToArray();
            var session = UnityEngine.Object.FindFirstObjectByType<TrainingSessionController>();
            var directHands = origin.GetComponentsInChildren<XRDirectInteractor>(true);
            var rayHands = origin.GetComponentsInChildren<NearFarInteractor>(true);
            Require(directHands.Length == 2 && rayHands.Length == 2, "Both direct and near/far hands are configured.");

            var propMask = 1 << LayerMask.NameToLayer(HeldItemLocomotionInstaller.PropLayerName);
            foreach (var direct in directHands)
                Require((direct.physicsLayerMask.value & propMask) != 0,
                    $"{direct.handedness} direct hand detects the prop layer.");
            foreach (var hand in rayHands)
            {
                Require(hand.nearInteractionCaster is SphereInteractionCaster near &&
                        (near.physicsLayerMask.value & propMask) != 0,
                    $"{hand.handedness} near query detects the prop layer.");
                Require((hand.farInteractionCaster.raycastMask.value & propMask) != 0,
                    $"{hand.handedness} far query detects the prop layer.");
            }

            // Headless editor tests have no tracked Touch devices, so the input modality
            // manager can hide controller objects. Supply controller availability for
            // this synchronous test using the authored objects and their original masks.
            foreach (var hand in directHands.Cast<XRBaseInputInteractor>().Concat(rayHands))
            {
                for (var parent = hand.transform; parent != origin.transform; parent = parent.parent)
                    parent.gameObject.SetActive(true);
                hand.enabled = true;
            }

            try
            {
                // Supply the socket's starting selection after enabling controller objects;
                // no real tracked device is present to run the usual startup sequence.
                var scannerGrab = scanner.GetComponent<XRGrabInteractable>();
                var scannerSocket = origin.GetComponentsInChildren<XRSocketInteractor>(true)
                    .Single(item => item.startingSelectedInteractable == scannerGrab);
                Cancel(manager, scannerGrab);
                manager.SelectEnter((IXRSelectInteractor)scannerSocket, scannerGrab);
                Require(scanner.TryGetControllingHand(out var initialHand) && initialHand == XRNode.LeftHand,
                    "Scanner attached to the authored offhand socket reads the left trigger.");
                foreach (var prop in props)
                {
                    Cancel(manager, prop);
                    foreach (var hand in directHands)
                        VerifyDirect(manager, hand, prop, scanner);
                    foreach (var hand in rayHands)
                        VerifyNearFar(manager, hand, prop, scanner);
                }
                VerifyScanning(manager, directHands, scanner, session);
                Debug.Log($"[PropInteraction] PASS: {s_Checks} checks covering both hands, " +
                          "direct/near/far pickup, scanner trigger routing, individual and full-session authored scans, and reset.");
            }
            finally { session.ResetTraining(); }
        }

        static void VerifyDirect(XRInteractionManager manager, XRDirectInteractor hand,
            XRGrabInteractable prop, InspectionScanner scanner)
        {
            var attach = hand.GetAttachTransform(null);
            var position = attach.position;
            var rotation = attach.rotation;
            try
            {
                var solid = Solid(prop);
                attach.position = solid.bounds.center;
                Physics.SyncTransforms();
                // Two samples settle the inter-frame sphere sweep at the new position.
                hand.PreprocessInteractor(XRInteractionUpdateOrder.UpdatePhase.Dynamic);
                hand.PreprocessInteractor(XRInteractionUpdateOrder.UpdatePhase.Dynamic);
                var targets = new List<IXRInteractable>();
                hand.GetValidTargets(targets);
                Require(targets.Contains(prop), $"{hand.handedness} direct query finds {prop.name}.");
                SelectAndRelease(manager, hand, prop, scanner);
            }
            finally { attach.SetPositionAndRotation(position, rotation); Physics.SyncTransforms(); }
        }

        static void VerifyNearFar(XRInteractionManager manager, NearFarInteractor hand,
            XRGrabInteractable prop, InspectionScanner scanner)
        {
            var near = (SphereInteractionCaster)hand.nearInteractionCaster;
            var far = (CurveInteractionCaster)hand.farInteractionCaster;
            var nearPosition = near.castOrigin.position;
            var nearRotation = near.castOrigin.rotation;
            var farPosition = far.castOrigin.position;
            var farRotation = far.castOrigin.rotation;
            var stabilized = far.enableStabilization;
            try
            {
                far.enableStabilization = false; // Test supplies settled poses in a single frame.
                var solid = Solid(prop);
                near.castOrigin.position = solid.bounds.center;
                Physics.SyncTransforms();
                var colliders = new List<Collider>();
                near.TryGetColliderTargets(manager, colliders);
                near.TryGetColliderTargets(manager, colliders);
                Require(colliders.Contains(solid), $"{hand.handedness} near caster finds {prop.name}.");

                // Approach from above in the real scene to avoid the display stand.
                far.castOrigin.SetPositionAndRotation(solid.bounds.center + Vector3.up * 0.65f,
                    Quaternion.LookRotation(Vector3.down, Vector3.forward));
                Physics.SyncTransforms();
                far.TryGetColliderTargets(manager, colliders);
                Require(colliders.Contains(solid), $"{hand.handedness} far caster finds {prop.name}.");
                hand.PreprocessInteractor(XRInteractionUpdateOrder.UpdatePhase.Dynamic);
                hand.PreprocessInteractor(XRInteractionUpdateOrder.UpdatePhase.Dynamic);
                var targets = new List<IXRInteractable>();
                hand.GetValidTargets(targets);
                Require(targets.Contains(prop), $"{hand.handedness} near/far hand exposes {prop.name} as selectable.");
                SelectAndRelease(manager, hand, prop, scanner);
            }
            finally
            {
                near.castOrigin.SetPositionAndRotation(nearPosition, nearRotation);
                far.castOrigin.SetPositionAndRotation(farPosition, farRotation);
                far.enableStabilization = stabilized;
                Physics.SyncTransforms();
            }
        }

        static void SelectAndRelease(XRInteractionManager manager, XRBaseInputInteractor hand,
            XRGrabInteractable prop, InspectionScanner scanner)
        {
            SetGrip(hand, true);
            Require(manager.CanSelect(hand, prop), $"{hand.handedness} can select {prop.name}.");
            manager.SelectEnter((IXRSelectInteractor)hand, prop);
            Require(prop.isSelected && prop.interactorsSelecting.Contains(hand),
                $"{hand.handedness} grip selects {prop.name} through XRI.");
            if (prop.gameObject == scanner.gameObject)
                Require(scanner.TryGetControllingHand(out var controlling) &&
                        controlling == (hand.handedness == InteractorHandedness.Left ? XRNode.LeftHand : XRNode.RightHand),
                    $"Scanner trigger follows the {hand.handedness} hand after pickup.");
            manager.SelectExit((IXRSelectInteractor)hand, prop);
            SetGrip(hand, false);
            Require(!prop.isSelected, $"{prop.name} releases cleanly.");
        }

        static void SetGrip(XRBaseInputInteractor hand, bool pressed)
        {
            // Feed the actual input reader and logical selection state without a device.
            hand.selectInput.inputSourceMode = XRInputButtonReader.InputSourceMode.ManualValue;
            hand.selectInput.manualPerformed = pressed;
            hand.selectInput.manualValue = pressed ? 1f : 0f;
            hand.selectInput.manualFramePerformed = pressed ? Time.frameCount : -1;
            hand.selectInput.manualFrameCompleted = pressed ? -1 : Time.frameCount;
            hand.PreprocessInteractor(XRInteractionUpdateOrder.UpdatePhase.Dynamic);
        }

        static void VerifyScanning(XRInteractionManager manager, XRDirectInteractor[] hands,
            InspectionScanner scanner, TrainingSessionController session)
        {
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            var beam = (Transform)typeof(InspectionScanner).GetField("beamOrigin", privateInstance).GetValue(scanner);
            var localPosition = beam.localPosition;
            var localRotation = beam.localRotation;
            var processTrigger = typeof(InspectionScanner).GetMethod("ProcessTrigger", privateInstance);
            var grab = scanner.GetComponent<XRGrabInteractable>();
            var targets = UnityEngine.Object.FindObjectsByType<DefectHotspot>()
                .OrderBy(item => item.DefectIndex).ToArray();
            Require(targets.Length == session.DefectSet.Defects.Count &&
                    targets.Select(item => item.DefectIndex).SequenceEqual(Enumerable.Range(0, targets.Length)),
                $"All {targets.Length} authored hotspots map one-to-one to independent defect records.");
            Require(targets.Length == 12 && session.AvailableDefectCount == 12 && session.TargetCount == 10,
                "The session retains twelve independent defects and requires any ten findings.");
            foreach (var target in targets)
            {
                var record = session.DefectSet.Defects[target.DefectIndex];
                Require(target.name.StartsWith(record.id + " - ", StringComparison.Ordinal) &&
                        target.IsHole == record.IsHole,
                    $"{target.name} retains its own identity and hole/surface type.");
                var halo = new SerializedObject(target).FindProperty("haloRenderer").objectReferenceValue as Renderer;
                Require(halo != null && halo.transform.IsChildOf(target.transform) && !halo.enabled,
                    $"{target.name} owns an initially hidden confirmation rim.");
                if (target.IsHole)
                {
                    var body = target.GetComponentInParent<Rigidbody>();
                    var outside = body.GetComponentsInChildren<Collider>()
                        .Where(item => !item.isTrigger && item.attachedRigidbody == body)
                        .All(item => Vector3.Distance(item.ClosestPoint(target.MarkerCenter), target.MarkerCenter) > 0.0001f);
                    Require(outside, $"{target.name} exterior scan target is outside the solid helmet collider.");
                }
            }
            try
            {
                foreach (var hand in hands)
                foreach (var target in targets)
                foreach (var approach in target.IsHole ? new[] { 0, 1, 2 } : new[] { 0 })
                {
                    session.ResetTraining();
                    session.BeginTraining();
                    SetGrip(hand, true);
                    manager.SelectEnter((IXRSelectInteractor)hand, grab);
                    Require(scanner.TryGetControllingHand(out var node), "Held scanner has a controlling trigger.");
                    var distance = approach == 0 ? 0.035f : 0.3f;
                    var outward = approach == 2
                        ? (target.MeasuredCenter - Solid(target.GetComponentInParent<XRGrabInteractable>()).bounds.center).normalized
                        : target.MarkerNormal;
                    beam.SetPositionAndRotation(target.MeasuredCenter + outward * distance,
                        Quaternion.LookRotation(-outward, target.transform.up));
                    processTrigger.Invoke(scanner, new object[] { true, node });
                    Require(target.IsFound && session.FoundCount == 1,
                        $"{hand.handedness} scanner trigger confirms {target.name} from {distance:F3} m (approach {approach}).");
                    processTrigger.Invoke(scanner, new object[] { false, node });
                    SetGrip(hand, false);
                }
                // Every target remains individually scannable (above), but an attempt
                // must stop at any ten, regardless of the order chosen by the trainee.
                foreach (var hand in hands)
                {
                    session.ResetTraining();
                    session.BeginTraining();
                    SetGrip(hand, true);
                    manager.SelectEnter((IXRSelectInteractor)hand, grab);
                    Require(scanner.TryGetControllingHand(out var node), "Full-session scanner has a controlling trigger.");
                    Require(SessionText(session, "progressText").Contains("00 / 10") &&
                            SessionText(session, "objectiveText").Contains("FIND ANY 10 OF 12"),
                        "The active objective and counter explain the ten-of-twelve target.");
                    var expected = 0;
                    var ordered = hand.handedness == InteractorHandedness.Left ? targets : targets.Reverse().ToArray();
                    foreach (var target in ordered)
                    {
                        beam.SetPositionAndRotation(target.MeasuredCenter + target.MarkerNormal * 0.035f,
                            Quaternion.LookRotation(-target.MarkerNormal, target.transform.up));
                        processTrigger.Invoke(scanner, new object[] { false, node });
                        processTrigger.Invoke(scanner, new object[] { true, node });
                        var shouldFind = expected < session.TargetCount;
                        if (shouldFind)
                            ++expected;
                        Require(target.IsFound == shouldFind && session.FoundCount == expected,
                            $"{hand.handedness} session {(shouldFind ? "counts" : "refuses extra finding")} " +
                            $"{target.name}: {expected}/{session.TargetCount}.");
                        Require(!target.TryInspect() && session.FoundCount == expected,
                            $"{target.name} cannot be counted twice or after completion.");
                        var halo = (Renderer)new SerializedObject(target).FindProperty("haloRenderer").objectReferenceValue;
                        var block = new MaterialPropertyBlock();
                        halo.GetPropertyBlock(block);
                        var color = block.GetColor("_BaseColor");
                        Require(halo.enabled == shouldFind && (!shouldFind || (color.g > color.r && color.g > color.b)),
                            $"{target.name} confirmation rim is visible and green only after detection.");
                        if (expected == session.TargetCount)
                            Require(session.IsComplete && targets.All(item => !item.CanInspect),
                                "The tenth accepted finding immediately closes every scan target before completion feedback.");
                        if (!shouldFind)
                            Require(!session.RegisterDefect(target.DefectIndex, target) &&
                                    session.FoundCount == session.TargetCount && !target.IsFound,
                                "Direct registration also rejects an eleventh or twelfth finding.");
                    }
                    Require(session.IsComplete && session.FoundCount == session.TargetCount &&
                            targets.Count(item => item.IsFound) == session.TargetCount &&
                            targets.Count(item => !item.IsFound) == targets.Length - session.TargetCount &&
                            SessionText(session, "progressText").Contains("10 / 10"),
                        $"{hand.handedness} completes with any ten distinct findings and leaves two unneeded defects hidden.");
                    VerifyCompletionFeedback(session);
                    session.BeginTraining();
                    Require(session.FoundCount == 10 && session.IsComplete && targets.All(item => !item.CanInspect),
                        "START cannot reopen or clear a completed attempt; RESET is required.");
                    processTrigger.Invoke(scanner, new object[] { false, node });
                    SetGrip(hand, false);
                }
                VerifyResetDuringCompletion(session, targets);
                session.ResetTraining();
                Require(session.FoundCount == 0 && !session.IsStarted && !session.IsComplete &&
                        targets.All(item => !item.IsFound && !item.CanInspect),
                    "Reset clears findings and exits active scanning.");
                foreach (var helmet in UnityEngine.Object.FindObjectsByType<HelmetOutOfBoundsRecovery>())
                    Require(!helmet.GetComponent<XRGrabInteractable>().isSelected &&
                            Vector3.Distance(helmet.transform.position, helmet.Home.position) < 0.0001f,
                        $"Reset releases and restores {helmet.name}.");
                Require(!grab.isSelected && Vector3.Distance(scanner.transform.position, scanner.HomeMount.position) < 0.0001f,
                    "Reset releases and restores the held scanner.");
            }
            finally { beam.SetLocalPositionAndRotation(localPosition, localRotation); }
        }

        static string SessionText(TrainingSessionController session, string field)
        {
            var text = (TMPro.TMP_Text)typeof(TrainingSessionController)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session);
            if (text == null)
                throw new InvalidOperationException("[PropInteraction] Missing session text: " + field);
            return text.text;
        }

        static GameObject CompletionPanel(TrainingSessionController session) =>
            (GameObject)typeof(TrainingSessionController)
                .GetField("completionPanel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session);

        static IEnumerator PendingCompletion(TrainingSessionController session)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var generation = (int)typeof(TrainingSessionController).GetField("m_SessionGeneration", flags).GetValue(session);
            return (IEnumerator)typeof(TrainingSessionController).GetMethod("CompleteAfterFeedback", flags)
                .Invoke(session, new object[] { generation });
        }

        static void VerifyCompletionFeedback(TrainingSessionController session)
        {
            // Advance the actual feedback routine explicitly so the synchronous Play
            // Mode verifier can test both sides of its 0.55 second wait without sleeping.
            var completion = PendingCompletion(session);
            Require(completion.MoveNext() && completion.Current is WaitForSeconds && !CompletionPanel(session).activeSelf,
                "Completion feedback waits briefly after the tenth accepted scan.");
            Require(!completion.MoveNext() && CompletionPanel(session).activeSelf &&
                    SessionText(session, "objectiveText").Contains("10 OF 12 DEVIATIONS DOCUMENTED"),
                "Completion opens the result panel and describes ten findings, not all twelve.");
        }

        static void VerifyResetDuringCompletion(TrainingSessionController session, DefectHotspot[] targets)
        {
            var oldFeedback = PendingCompletion(session);
            Require(oldFeedback.MoveNext(), "A previous attempt has pending completion feedback.");
            session.ResetTraining();
            Require(!CompletionPanel(session).activeSelf && session.FoundCount == 0 && !session.IsComplete &&
                    SessionText(session, "progressText").Contains("00 / 10"),
                "RESET immediately hides results and clears the ten-finding counter.");
            session.BeginTraining();
            Require(targets.All(item => item.CanInspect) && !session.IsComplete,
                "A repeated attempt re-enables all twelve choices, not only the previous ten.");
            foreach (var target in targets.Take(session.TargetCount))
                Require(target.TryInspect(), $"A repeated attempt can rediscover {target.name}.");
            Require(session.IsComplete && !CompletionPanel(session).activeSelf,
                "The repeated attempt owns its own pending completion feedback.");
            Require(!oldFeedback.MoveNext() && !CompletionPanel(session).activeSelf &&
                    SessionText(session, "objectiveText").Contains("ACTIVE INSPECTION"),
                "Stale completion from before RESET cannot overwrite a later attempt, even after its tenth finding.");
            VerifyCompletionFeedback(session);
        }

        static Collider Solid(XRGrabInteractable prop) => prop.GetComponentsInChildren<Collider>()
            .First(item => !item.isTrigger && item.attachedRigidbody == prop.GetComponent<Rigidbody>());

        static void Cancel(XRInteractionManager manager, XRGrabInteractable prop)
        {
            if (prop.isSelected)
                manager.CancelInteractableSelection((IXRSelectInteractable)prop);
        }

        static void Require(bool passed, string message)
        {
            if (!passed)
                throw new InvalidOperationException("[PropInteraction] FAIL: " + message);
            ++s_Checks;
            Debug.Log("[PropInteraction] PASS: " + message);
        }
    }
}
