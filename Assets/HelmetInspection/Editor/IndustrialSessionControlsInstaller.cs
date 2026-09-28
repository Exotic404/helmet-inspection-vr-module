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
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using Object = UnityEngine.Object;

namespace HelmetInspection.Editor
{
    /// <summary>Imports only the authored controls; preserves all helmet, scanner and room content.</summary>
    public static class IndustrialSessionControlsInstaller
    {
        public const string Root = "Assets/HelmetInspection/SessionControls";
        public const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        public const string ReviewPath = "Logs/SessionControlsSep23";
        const string VisualPrefix = "VISUAL - Industrial ";

        [Serializable] class Source
        {
            public int version;
            public string coordinateSystem;
            public MaterialData[] materials;
            public MeshData[] meshes;
        }
        [Serializable] class MaterialData
        {
            public string name;
            public float[] baseColor;
            public float metallic;
            public float roughness;
            public float[] emission;
            public float emissionStrength;
        }
        [Serializable] class MeshData
        {
            public string name;
            public int materialIndex;
            public float[] positions;
            public float[] normals;
            public float[] uv;
            public int[] triangles;
        }

        [MenuItem("Helmet Inspection/Session Controls/Install Blender Industrial Buttons")]
        public static void Install()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play Mode before installing controls.");
            for (var i = 0; i < SceneManager.sceneCount; ++i)
                Require(!SceneManager.GetSceneAt(i).isDirty, "Save open scene edits before installing controls.");
            foreach (var file in new[] { "IndustrialStartButton", "IndustrialRestartButton" })
                Require(File.Exists(Root + "/Source/" + file + ".mesh.json"), "Missing Blender export: " + file);
            Directory.CreateDirectory(ReviewPath);
            var backup = ReviewPath + "/Before_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + ".unity";
            File.Copy(ScenePath, backup);
            var scene = EditorSceneManager.OpenScene(ScenePath);
            var components = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Component>(true))
                .Where(c => c != null).ToArray();
            var buttons = components.OfType<MechanicalTrainingButtonBase>().ToArray();
            Require(buttons.Length == 2 && buttons.OfType<TrainingStartButton>().Count() == 1 &&
                buttons.OfType<TrainingResetButton>().Count() == 1, "Expected the two existing session controls.");
            var session = components.OfType<TrainingSessionController>().Single();
            Require(session.AvailableDefectCount == 12 && session.TargetCount == 10, "Unexpected defect/session configuration.");
            var plinth = Find("Shared Recessed Control Plinth");
            var trim = Find("Plinth Brushed Metal Trim");
            var identity = Find("Control Panel Identity");
            Require(plinth != null && identity != null, "Existing control mounting/identity missing.");

            // Compare every unrelated component before/after, including authored transforms and mesh references.
            var editableRoots = buttons.Select(b => b.gameObject).Concat(new[] {
                plinth, trim, identity, Find("START Label"), Find("RESET Label"), Find("Session Control Interaction Hint")
            }).Where(g => g != null).ToArray();
            var editable = new HashSet<Component>(editableRoots.SelectMany(g => g.GetComponentsInChildren<Component>(true)));
            var protectedComponents = components.Where(c => !editable.Contains(c)).ToDictionary(c => c, EditorJsonUtility.ToJson);
            try
            {
                EnsureFolders();
                foreach (var oldLabel in new[] { Find("START Label"), Find("RESET Label") })
                    if (oldLabel != null) HideVisuals(oldLabel);
                if (trim != null) foreach (var r in trim.GetComponentsInChildren<Renderer>(true)) r.enabled = false;

                plinth.transform.SetPositionAndRotation(new Vector3(.20f, 1.31f, 2.385f), Quaternion.identity);
                plinth.transform.localScale = new Vector3(.72f, .54f, .026f);
                plinth.GetComponent<Renderer>().sharedMaterial = MakeMaterial("ControlMount_Powdercoat",
                    new Color(.055f, .073f, .079f), .25f, .26f, Color.black);
                plinth.GetComponent<Renderer>().enabled = true;
                foreach (var b in buttons)
                    InstallButton(b, session, b is TrainingStartButton);

                SetLabel(identity, "SESSION CONTROL", new Vector3(.20f, 1.535f, 2.366f), .024f, .64f,
                    new Color(.78f, .83f, .82f));
                var hint = Find("Session Control Interaction Hint") ?? Find("RESET Label");
                Require(hint != null, "Missing reusable control label.");
                hint.name = "Session Control Interaction Hint";
                SetLabel(hint, "POINT + SQUEEZE GRIP", new Vector3(.20f, 1.084f, 2.366f), .013f, .62f,
                    new Color(.61f, .70f, .70f));
                foreach (var pair in protectedComponents)
                    Require(pair.Key != null && EditorJsonUtility.ToJson(pair.Key) == pair.Value,
                        "Protected scene component changed: " + pair.Key);
                Physics.SyncTransforms();
                Require(buttons.All(b => new SerializedObject(b).FindProperty("session").objectReferenceValue == session),
                    "Session bindings changed.");
                EditorSceneManager.MarkSceneDirty(scene);
                Require(EditorSceneManager.SaveScene(scene), "Could not save updated controls.");
                AssetDatabase.SaveAssets();
                Debug.Log($"[IndustrialControls] Installed industrial Start/Restart under instrument screens; " +
                    $"{protectedComponents.Count} unrelated components/poses preserved. Scene backup: {backup}");
            }
            catch
            {
                // The disk scene is only saved after all preservation checks succeed.
                EditorSceneManager.OpenScene(ScenePath);
                throw;
            }
        }

        static void InstallButton(MechanicalTrainingButtonBase button, TrainingSessionController session, bool start)
        {
            var existing = button.transform.Find(VisualPrefix + (start ? "Start" : "Restart"));
            if (existing != null)
            {
                Require(existing.GetComponentsInChildren<Collider>(true).Length == 0 &&
                    existing.GetComponentsInChildren<MonoBehaviour>(true).Length == 0,
                    "Generated control art acquired gameplay components; preserve them before rebuilding.");
                Object.DestroyImmediate(existing.gameObject);
            }
            HideVisuals(button.gameObject);
            // Legacy children are retained for any external references, but no old collider can intercept input.
            Require(button.GetComponentsInChildren<Collider>(true).All(c => c.gameObject == button.gameObject),
                "Unexpected collider on legacy button art.");
            button.transform.SetPositionAndRotation(new Vector3(start ? .035f : .365f, 1.30f, 2.371f), Quaternion.identity);
            button.transform.localScale = Vector3.one;
            var visual = new GameObject(VisualPrefix + (start ? "Start" : "Restart"));
            visual.transform.SetParent(button.transform, false);
            Import(start ? "IndustrialStartButton" : "IndustrialRestartButton", visual.transform);
            var cap = visual.transform.Find("ButtonCap");
            Require(cap != null, "Blender export lacks independent ButtonCap mesh.");
            var capRenderer = cap.GetComponent<Renderer>();
            var idle = capRenderer.sharedMaterial.GetColor("_BaseColor");
            button.SetEditorReferences(session, cap, capRenderer, idle, Color.Lerp(idle, Color.white, .70f),
                Color.white);
            var settings = new SerializedObject(button);
            settings.FindProperty("pressTravel").floatValue = .009f;
            settings.FindProperty("pressDuration").floatValue = .22f;
            settings.ApplyModifiedPropertiesWithoutUndo();
            button.AlignInteractionColliderToVisibleCap();
            var collider = button.GetComponent<BoxCollider>();
            var interaction = button.GetComponent<XRSimpleInteractable>();
            interaction.colliders.Clear();
            interaction.colliders.Add(collider);
            collider.enabled = true;
            EditorUtility.SetDirty(button);
            EditorUtility.SetDirty(interaction);
            EditorUtility.SetDirty(collider);
        }

        static void Import(string filename, Transform parent)
        {
            var src = JsonUtility.FromJson<Source>(File.ReadAllText(Root + "/Source/" + filename + ".mesh.json"));
            Require(src.version == 1 && src.coordinateSystem == "UnityLeftHandedYUp", "Unsupported asset coordinates.");
            Require(src.meshes.Sum(m => m.triangles.Length / 3) <= 8000 && src.meshes.Length <= 9,
                "Control mesh exceeds the Quest asset budget.");
            var materials = src.materials.Select(m => MakeMaterial(m.name, ColorOf(m.baseColor), m.metallic,
                1 - m.roughness, m.emission != null ? ColorOf(m.emission) * m.emissionStrength : Color.black)).ToArray();
            foreach (var data in src.meshes)
            {
                var path = Root + "/Generated/Meshes/" + filename + "_" + data.name + ".asset";
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh == null) { mesh = new Mesh(); AssetDatabase.CreateAsset(mesh, path); }
                mesh.Clear();
                mesh.name = filename + "_" + data.name;
                mesh.indexFormat = data.positions.Length / 3 > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.vertices = Vectors(data.positions);
                mesh.normals = Vectors(data.normals);
                mesh.uv = Enumerable.Range(0, data.uv.Length / 2).Select(i => new Vector2(data.uv[i * 2], data.uv[i * 2 + 1])).ToArray();
                mesh.triangles = data.triangles;
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                EditorUtility.SetDirty(mesh);
                var part = new GameObject(data.name);
                part.transform.SetParent(parent, false);
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = part.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = materials[data.materialIndex];
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            }
        }

        static Vector3[] Vectors(float[] values) => Enumerable.Range(0, values.Length / 3)
            .Select(i => new Vector3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2])).ToArray();
        static Color ColorOf(float[] value) => new Color(value[0], value[1], value[2], value.Length > 3 ? value[3] : 1);

        static Material MakeMaterial(string name, Color color, float metal, float smooth, Color emission)
        {
            var path = Root + "/Generated/Materials/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }
            material.name = name;
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metal);
            material.SetFloat("_Smoothness", smooth);
            material.SetColor("_EmissionColor", emission);
            if (emission.maxColorComponent > 0) material.EnableKeyword("_EMISSION");
            else material.DisableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            return material;
        }

        static void SetLabel(GameObject obj, string value, Vector3 position, float height, float width, Color color)
        {
            var label = obj.GetComponent<TMP_Text>();
            Require(label != null, "Missing existing label component: " + obj.name);
            obj.transform.SetPositionAndRotation(position, Quaternion.identity);
            obj.transform.localScale = Vector3.one * .01f;
            label.text = value;
            label.fontSize = height * 1000;
            label.color = color;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.rectTransform.sizeDelta = new Vector2(width * 100, 6);
            label.raycastTarget = false;
            label.enabled = true;
            label.outlineWidth = 0;
            label.GetComponent<Renderer>().enabled = true;
            label.ForceMeshUpdate();
        }

        static void EnsureFolders()
        {
            foreach (var p in new[] { Root, Root + "/Generated", Root + "/Generated/Materials", Root + "/Generated/Meshes" })
                if (!AssetDatabase.IsValidFolder(p))
                    AssetDatabase.CreateFolder(Path.GetDirectoryName(p).Replace('\\', '/'), Path.GetFileName(p));
        }

        static void HideVisuals(GameObject obj)
        {
            foreach (var text in obj.GetComponentsInChildren<TMP_Text>(true)) text.enabled = false;
            foreach (var renderer in obj.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
        }

        public static void InstallReviewAndVerify()
        {
            Install();
            RenderReview();
            SessionControlsVerifier.RunPlayMode();
        }

        [MenuItem("Helmet Inspection/Session Controls/Render In-Scene Review")]
        public static void RenderReview()
        {
            Directory.CreateDirectory(ReviewPath);
            EditorSceneManager.OpenScene(ScenePath);
            var rig = Object.FindFirstObjectByType<XROrigin>();
            var scanner = Object.FindFirstObjectByType<InspectionScanner>();
            var hidden = rig.GetComponentsInChildren<Renderer>(true).Concat(scanner.GetComponentsInChildren<Renderer>(true))
                .Distinct().ToArray();
            var states = hidden.Select(r => r.enabled).ToArray();
            var obj = new GameObject("Temporary Session Controls Review");
            var camera = obj.AddComponent<Camera>();
            camera.nearClipPlane = .02f;
            camera.farClipPlane = 25;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.GetUniversalAdditionalCameraData().allowXRRendering = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.05f, .06f, .065f);
            try
            {
                foreach (var r in hidden) r.enabled = false;
                View("ControlsDetail", new Vector3(-.14f, 1.58f, 1.45f), new Vector3(.20f, 1.33f, 2.34f), 42);
                View("ControlsWall", new Vector3(.18f, 1.60f, -.75f), new Vector3(.20f, 1.57f, 2.38f), 49);
                View("ControlsPlayerView", rig.Camera.transform.position, new Vector3(.20f, 1.36f, 2.38f), 65);
            }
            finally
            {
                for (var i = 0; i < hidden.Length; ++i) hidden[i].enabled = states[i];
                Object.DestroyImmediate(obj);
                EditorSceneManager.OpenScene(ScenePath);
            }

            void View(string name, Vector3 position, Vector3 at, float fov)
            {
                camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(at - position));
                camera.fieldOfView = fov;
                camera.aspect = 1.6f;
                foreach (var text in Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include))
                    if (text.enabled) text.ForceMeshUpdate();
                Canvas.ForceUpdateCanvases();
                var image = ArtRefreshInstaller.Capture(camera, 1600, 1000);
                File.WriteAllBytes(ReviewPath + "/" + name + ".png", image.EncodeToPNG());
                Object.DestroyImmediate(image);
                Debug.Log("[IndustrialControls] Rendered " + name);
            }
        }

        static GameObject Find(string name) => Object.FindObjectsByType<Transform>(FindObjectsInactive.Include)
            .FirstOrDefault(t => t.name == name)?.gameObject;
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
