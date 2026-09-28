using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using Object = UnityEngine.Object;

namespace HelmetInspection.Editor
{
    /// <summary>Checks the saved Blender controls and exercises their real XRI selection callbacks.
    /// RunPlayMode is asynchronous: batch callers must omit -quit. No scene is saved.</summary>
    public static class SessionControlsVerifier
    {
        const string PendingKey = "HelmetInspection.SessionControlsVerifier.Pending";
        const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly List<GameObject> TestObjects = new List<GameObject>();
        static IEnumerator s_Run;
        static double s_Deadline;
        static int s_LastFrame = -1;
        static int s_Checks;

        [MenuItem("Helmet Inspection/Session Controls/Verify Saved Controls")]
        public static void Verify()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Saved-scene verification runs outside Play Mode.");
            RequireNoUnsavedScenes();
            EditorSceneManager.OpenScene(ScenePath);
            s_Checks = 0;
            VerifySavedScene();
            Debug.Log($"[SessionControls] PASS: {s_Checks} saved-scene checks; Blender caps, registered hit volumes, unobstructed access, and twelve defects / ten findings.");
        }

        [MenuItem("Helmet Inspection/Session Controls/Verify Live Controls")]
        public static void RunPlayMode()
        {
            Verify();
            SessionState.SetBool(PendingKey, true);
            Resume();
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        static void Resume()
        {
            if (!SessionState.GetBool(PendingKey, false)) return;
            s_Run = null;
            s_LastFrame = -1;
            s_Deadline = EditorApplication.timeSinceStartup + 120;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > s_Deadline)
                    throw new TimeoutException("Session control Play Mode verification exceeded 120 seconds.");
                if (!EditorApplication.isPlaying || Time.timeSinceLevelLoad < 0.6f || s_LastFrame == Time.frameCount) return;
                s_LastFrame = Time.frameCount;
                s_Run ??= VerifyLiveScene();
                if (!s_Run.MoveNext())
                {
                    Debug.Log($"[SessionControls] PASS: {s_Checks} checks including real XRI start/restart selection, session gates, audible feedback, cap travel/return, debounce, and held-prop restoration.");
                    Finish(0);
                }
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
            (s_Run as IDisposable)?.Dispose();
            s_Run = null;
            foreach (var item in TestObjects)
                if (item != null) Object.DestroyImmediate(item);
            TestObjects.Clear();
            if (Application.isBatchMode) EditorApplication.Exit(code);
            else EditorApplication.ExitPlaymode();
        }

        static void VerifySavedScene()
        {
            var session = Object.FindObjectsByType<TrainingSessionController>(FindObjectsInactive.Include).Single();
            var buttons = Object.FindObjectsByType<MechanicalTrainingButtonBase>(FindObjectsInactive.Include);
            Require(buttons.Length == 2 && buttons.Count(item => item is TrainingStartButton) == 1 &&
                    buttons.Count(item => item is TrainingResetButton) == 1,
                "Exactly the original START and RESTART behaviours exist.");
            var hotspots = Object.FindObjectsByType<DefectHotspot>(FindObjectsInactive.Include);
            Require(hotspots.Length == 12 && session.AvailableDefectCount == 12 && session.TargetCount == 10 &&
                    hotspots.Select(item => item.DefectIndex).OrderBy(index => index).SequenceEqual(Enumerable.Range(0, 12)),
                "The twelve independent authored defects and ten-finding target are preserved.");
            var transforms = SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true));
            Require(transforms.Sum(item => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(item.gameObject)) == 0,
                "The saved scene has no missing behaviour scripts.");
            Physics.SyncTransforms();
            foreach (var button in buttons)
            {
                var data = new SerializedObject(button);
                var cap = data.FindProperty("buttonCap").objectReferenceValue as Transform;
                var renderer = data.FindProperty("capRenderer").objectReferenceValue as MeshRenderer;
                var mesh = renderer != null ? renderer.GetComponent<MeshFilter>()?.sharedMesh : null;
                Require(button.isActiveAndEnabled && data.FindProperty("session").objectReferenceValue == session,
                    button.name + " is active and retains the shared session reference.");
                Require(cap != null && cap.name == "ButtonCap" && cap.IsChildOf(button.transform) &&
                        renderer != null && renderer.transform == cap && renderer.enabled &&
                        cap.gameObject.activeInHierarchy && mesh != null && AssetDatabase.Contains(mesh),
                    button.name + " animates an active imported Blender cap mesh.");
                long triangles = 0;
                foreach (var filter in button.GetComponentsInChildren<MeshFilter>(true)
                             .Where(item => item.GetComponent<Renderer>() is { enabled: true } && item.gameObject.activeInHierarchy))
                    if (filter.sharedMesh != null)
                        for (var submesh = 0; submesh < filter.sharedMesh.subMeshCount; ++submesh)
                            if (filter.sharedMesh.GetTopology(submesh) == MeshTopology.Triangles)
                                triangles += filter.sharedMesh.GetIndexCount(submesh) / 3;
                Require(triangles > 0 && triangles <= 8000, $"{button.name} has {triangles} mesh triangles / 8,000 budget.");
                var box = button.GetComponent<BoxCollider>();
                var interactable = button.GetComponent<XRSimpleInteractable>();
                Require(box != null && box.enabled && !box.isTrigger && interactable != null && interactable.enabled &&
                        interactable.colliders.Count == 1 && interactable.colliders[0] == box &&
                        button.GetComponentsInChildren<Collider>(true).Length == 1,
                    button.name + " registers only its fitted cap collider with XRI.");
                var capBounds = renderer.bounds;
                var boxBounds = box.bounds;
                var sizeMargin = boxBounds.size - capBounds.size;
                Require(Vector3.Distance(boxBounds.center, capBounds.center) <= 0.01f &&
                        sizeMargin.x >= -0.001f && sizeMargin.y >= -0.001f && sizeMargin.z >= -0.001f &&
                        sizeMargin.x <= 0.04f && sizeMargin.y <= 0.04f && sizeMargin.z <= 0.04f,
                    button.name + " has an accurately fitted hit volume around the visible cap.");
                Require(Vector3.Dot(button.transform.forward, Vector3.forward) > 0.95f &&
                        button.transform.position.y >= 1.15f && button.transform.position.y <= 1.5f,
                    button.name + " faces the room at the requested wall-console height.");
                VerifyRay(box, capBounds.center - button.transform.forward * 0.7f);
                var camera = Object.FindFirstObjectByType<XROrigin>()?.Camera;
                Require(camera != null, "The player camera exists.");
                VerifyRay(box, camera.transform.position);
                Require(data.FindProperty("pressTravel").floatValue > 0.002f && data.FindProperty("pressTravel").floatValue <= 0.02f &&
                        data.FindProperty("pressDuration").floatValue >= 0.1f,
                    button.name + " has visible, bounded mechanical travel.");
                Require(data.FindProperty("interactionVolume").floatValue >= 0.5f &&
                        data.FindProperty("soundSpatialBlend").floatValue <= 0.8f &&
                        data.FindProperty("soundMinDistance").floatValue >= 1f &&
                        data.FindProperty("soundMaxDistance").floatValue >= 5f &&
                        data.FindProperty("activationCooldown").floatValue >= 0.1f &&
                        data.FindProperty("activationCooldown").floatValue <= 0.5f,
                    button.name + " has audible room-distance feedback and a bounded repeat cooldown.");
            }
        }

        static void VerifyRay(BoxCollider box, Vector3 origin)
        {
            var delta = box.bounds.center - origin;
            Require(Physics.Raycast(origin, delta.normalized, out var hit, delta.magnitude + 0.02f,
                        Physics.AllLayers, QueryTriggerInteraction.Collide) && hit.collider == box,
                box.name + " is the first ray hit from " + origin.ToString("F2") + "; no panel or table blocks its cap.");
        }

        static IEnumerator VerifyLiveScene()
        {
            s_Checks = 0;
            var manager = Object.FindFirstObjectByType<XRInteractionManager>();
            var session = Object.FindFirstObjectByType<TrainingSessionController>();
            var start = Object.FindFirstObjectByType<TrainingStartButton>();
            var reset = Object.FindFirstObjectByType<TrainingResetButton>();
            var hotspots = Object.FindObjectsByType<DefectHotspot>();
            Require(manager != null && session != null && start != null && reset != null, "All live control components initialized.");
            Require(!session.IsStarted && !session.IsComplete && session.FoundCount == 0 && hotspots.All(item => !item.CanInspect),
                "Boot waits for START and closes defect scanning.");
            var ray = CreateRay(manager, "Session control test ray");
            yield return null;
            var startRaised = Cap(start).localPosition;
            Press(manager, ray, start);
            Require(session.IsStarted && !session.IsComplete && hotspots.All(item => item.CanInspect),
                "START through XRI opens the session and all defect targets.");
            VerifyAudio(start);
            Press(manager, ray, start);
            Require(session.IsStarted && session.FoundCount == 0, "Repeated START preserves the active attempt.");
            var startAnimation = ObserveAnimation(start, startRaised);
            while (startAnimation.MoveNext()) yield return null;
            var previousPress = LastPress(start);
            Press(manager, ray, start);
            Require(LastPress(start) > previousPress && session.IsStarted && session.FoundCount == 0,
                "START remains a responsive physical control without restarting an active attempt.");
            VerifyAudio(start);

            var helmets = Object.FindObjectsByType<HelmetOutOfBoundsRecovery>();
            var scanner = Object.FindFirstObjectByType<InspectionScanner>();
            var props = helmets.Select(item => item.GetComponent<XRGrabInteractable>())
                .Append(scanner.GetComponent<XRGrabInteractable>()).ToArray();
            Require(helmets.Length == 2 && scanner.HomeMount != null, "Both helmets and scanner have reset support.");
            // Each synthetic controller uses the real manager's public SelectEnter path.
            // All assertions happen synchronously before a physics frame can move the props again.
            foreach (var prop in props)
            {
                manager.CancelInteractableSelection((IXRSelectInteractable)prop);
                var hand = CreateRay(manager, "Held prop test ray - " + prop.name);
                hand.interactionLayers = prop.interactionLayers;
                hand.transform.position = prop.transform.position - Vector3.forward * 0.3f;
                SetGrip(hand, true);
                Require(manager.CanSelect(hand, prop), "Test controller can select " + prop.name + ".");
                manager.SelectEnter((IXRSelectInteractor)hand, prop);
                Require(prop.isSelected && prop.interactorsSelecting.Contains(hand), prop.name + " is genuinely held through XRI.");
                prop.transform.position += new Vector3(0.25f, 0.12f, -0.15f);
                var body = prop.GetComponent<Rigidbody>();
                if (body != null) body.position = prop.transform.position;
            }
            Require(session.RegisterDefect(hotspots[0].DefectIndex, hotspots[0]) && session.FoundCount == 1,
                "A finding exists to verify RESTART clears progress.");
            var generation = SessionGeneration(session);
            var resetRaised = Cap(reset).localPosition;
            Press(manager, ray, reset);
            Require(SessionGeneration(session) == generation + 1 && !session.IsStarted && !session.IsComplete && session.FoundCount == 0 &&
                    hotspots.All(item => !item.CanInspect && !item.IsFound),
                "RESTART through XRI clears progress and closes the session exactly once.");
            Require(props.All(item => !item.isSelected), "RESTART releases every held training prop.");
            foreach (var helmet in helmets) VerifyHome(helmet.transform, helmet.Home);
            VerifyHome(scanner.transform, scanner.HomeMount);
            VerifyAudio(reset);
            Press(manager, ray, reset);
            Require(SessionGeneration(session) == generation + 1, "An immediate repeated select is debounced before a second physical reset.");
            var resetAnimation = ObserveAnimation(reset, resetRaised);
            while (resetAnimation.MoveNext()) yield return null;

            // Observe the post-reset gate via another actual START selection after feedback has settled.
            Press(manager, ray, start);
            Require(session.IsStarted && session.FoundCount == 0 && hotspots.All(item => item.CanInspect),
                "START remains usable after a complete physical reset.");
            for (var i = 0; i < session.TargetCount; ++i)
                Require(session.RegisterDefect(hotspots[i].DefectIndex, hotspots[i]), "Accepted finding " + (i + 1) + ".");
            Require(session.IsComplete && session.FoundCount == 10 && hotspots.All(item => !item.CanInspect),
                "The session still completes at any ten of twelve defects.");
            Press(manager, ray, start);
            Require(session.IsComplete && session.FoundCount == 10, "START cannot reopen or erase a completed attempt.");
            var wait = WaitRealtime(0.8f);
            while (wait.MoveNext()) yield return null;
            Press(manager, ray, reset);
            Require(!session.IsStarted && !session.IsComplete && session.FoundCount == 0 && SessionGeneration(session) == generation + 2,
                "RESTART is usable again after debounce and clears completion.");
        }

        static XRRayInteractor CreateRay(XRInteractionManager manager, string name)
        {
            var item = new GameObject(name);
            item.SetActive(false);
            TestObjects.Add(item);
            var ray = item.AddComponent<XRRayInteractor>();
            ray.interactionManager = manager;
            ray.enableUIInteraction = false;
            ray.hitClosestOnly = true;
            ray.maxRaycastDistance = 3f;
            ray.raycastMask = Physics.AllLayers;
            ray.raycastTriggerInteraction = QueryTriggerInteraction.Collide;
            ray.selectActionTrigger = XRBaseInputInteractor.InputTriggerType.State;
            ray.selectInput.inputSourceMode = XRInputButtonReader.InputSourceMode.ManualValue;
            item.SetActive(true);
            return ray;
        }

        static void Press(XRInteractionManager manager, XRRayInteractor ray, MechanicalTrainingButtonBase button)
        {
            var interactable = button.GetComponent<XRSimpleInteractable>();
            ray.interactionLayers = interactable.interactionLayers;
            var center = button.GetComponent<BoxCollider>().bounds.center;
            ray.transform.SetPositionAndRotation(center - button.transform.forward * 0.65f,
                Quaternion.LookRotation(button.transform.forward, button.transform.up));
            Physics.SyncTransforms();
            SetGrip(ray, true);
            var targets = new List<IXRInteractable>();
            ray.GetValidTargets(targets);
            Require(targets.Contains(interactable) && manager.CanSelect(ray, interactable),
                button.name + " is discoverable by the real XRI ray and selectable.");
            manager.SelectEnter((IXRSelectInteractor)ray, interactable);
            Require(interactable.isSelected && interactable.interactorsSelecting.Contains(ray),
                button.name + " receives a real XRI select.");
            manager.SelectExit((IXRSelectInteractor)ray, interactable);
            SetGrip(ray, false);
        }

        static void SetGrip(XRBaseInputInteractor hand, bool pressed)
        {
            hand.selectInput.manualPerformed = pressed;
            hand.selectInput.manualValue = pressed ? 1f : 0f;
            hand.selectInput.manualFramePerformed = pressed ? Time.frameCount : -1;
            hand.selectInput.manualFrameCompleted = pressed ? -1 : Time.frameCount;
            hand.PreprocessInteractor(XRInteractionUpdateOrder.UpdatePhase.Dynamic);
        }

        static IEnumerator ObserveAnimation(MechanicalTrainingButtonBase button, Vector3 raised)
        {
            var cap = Cap(button);
            var data = new SerializedObject(button);
            var travel = data.FindProperty("pressTravel").floatValue;
            var duration = data.FindProperty("pressDuration").floatValue;
            var maxForward = 0f;
            var until = Time.realtimeSinceStartup + Mathf.Max(0.65f, duration + 0.2f);
            while (Time.realtimeSinceStartup < until)
            {
                var delta = cap.localPosition - raised;
                maxForward = Mathf.Max(maxForward, delta.z);
                Require(Mathf.Abs(delta.x) < 0.0001f && Mathf.Abs(delta.y) < 0.0001f && delta.z >= -0.0001f,
                    button.name + " moves only inward along local +Z.");
                yield return null;
            }
            Require(maxForward >= travel * 0.25f, button.name + " visibly depresses over live frames.");
            Require(Vector3.Distance(cap.localPosition, raised) < 0.0001f, button.name + " returns exactly to its raised pose.");
        }

        static void VerifyAudio(MechanicalTrainingButtonBase button)
        {
            var sources = button.GetComponents<AudioSource>();
            Require(sources.Any(source => source.enabled && !source.mute && source.volume >= 0.25f &&
                        !source.loop && !source.playOnAwake && source.spatialBlend <= 0.8f && source.isPlaying),
                button.name + " actually plays audible confirmation audio after selection.");
            var data = new SerializedObject(button);
            var clip = data.FindProperty("interactionClip").objectReferenceValue as AudioClip;
            if (clip == null)
                clip = (AudioClip)typeof(MechanicalTrainingButtonBase).GetField("m_GeneratedClip", PrivateInstance).GetValue(button);
            Require(clip != null && clip.samples > 1000 && clip.length >= 0.1f && clip.length <= 1f,
                button.name + " owns a short confirmation clip.");
            var samples = new float[clip.samples * clip.channels];
            Require(clip.GetData(samples, 0) && samples.Any(value => Mathf.Abs(value) > 0.08f) &&
                    samples.All(value => !float.IsNaN(value) && Mathf.Abs(value) <= 1f),
                button.name + " confirmation samples contain audible non-clipping audio.");
        }

        static IEnumerator WaitRealtime(float duration)
        {
            var until = Time.realtimeSinceStartup + duration;
            while (Time.realtimeSinceStartup < until) yield return null;
        }

        static Transform Cap(MechanicalTrainingButtonBase button) =>
            (Transform)new SerializedObject(button).FindProperty("buttonCap").objectReferenceValue;

        static int SessionGeneration(TrainingSessionController session) =>
            (int)typeof(TrainingSessionController).GetField("m_SessionGeneration", PrivateInstance).GetValue(session);

        static float LastPress(MechanicalTrainingButtonBase button) =>
            (float)typeof(MechanicalTrainingButtonBase).GetField("m_LastActivationTime", PrivateInstance).GetValue(button);

        static void VerifyHome(Transform prop, Transform home) =>
            Require(home != null && Vector3.Distance(prop.position, home.position) < 0.0001f &&
                    Quaternion.Angle(prop.rotation, home.rotation) < 0.01f, prop.name + " returns to its authored home.");

        static void RequireNoUnsavedScenes()
        {
            for (var i = 0; i < SceneManager.sceneCount; ++i)
                Require(!SceneManager.GetSceneAt(i).isDirty, "Save open scene edits before running the verifier.");
        }

        static void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("[SessionControls] " + description);
            ++s_Checks;
        }
    }
}
