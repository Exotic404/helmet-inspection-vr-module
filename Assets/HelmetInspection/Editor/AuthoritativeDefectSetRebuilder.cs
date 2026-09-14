using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace HelmetInspection.Editor
{
    /// <summary>
    /// Replaces the scene/data defect set from the measured source-mesh coordinates.
    /// Raw values remain in their supplied Z-up millimetre coordinate system and are
    /// converted exactly once to the imported Unity mesh's Y-up metre coordinates.
    /// </summary>
    public static class AuthoritativeDefectSetRebuilder
    {
        const string ScenePath = "Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity";
        const string DefectSetPath = "Assets/HelmetInspection/Data/DefectSet_A2.asset";
        const string MeshPath = "Assets/HelmetInspection/Models/Generated/A2_defective_scan.asset";
        const string RingMeshPath = "Assets/HelmetInspection/Meshes/DefectHaloRing.asset";
        const string FoundMaterialPath = "Assets/HelmetInspection/Materials/M_GreenEmission.mat";
        const string VerificationReportPath = "Logs/AuthoritativeDefectVerification.txt";
        const string VerificationImageDirectory = "Logs/DefectVerificationViews";

        readonly struct SourceDefect
        {
            public readonly string title;
            public readonly Vector3 rawMillimeters;
            public readonly float radiusMeters;
            public readonly bool isHole;
            public readonly DefectCategory category;
            public readonly DefectSeverity severity;
            public readonly float deviationMillimeters;

            public SourceDefect(string title, Vector3 rawMillimeters, float radiusMeters, bool isHole,
                DefectCategory category, DefectSeverity severity, float deviationMillimeters = 0f)
            {
                this.title = title;
                this.rawMillimeters = rawMillimeters;
                this.radiusMeters = radiusMeters;
                this.isHole = isHole;
                this.category = category;
                this.severity = severity;
                this.deviationMillimeters = deviationMillimeters;
            }
        }

        sealed class BoundaryComponent
        {
            public readonly List<int> vertices = new List<int>();
            public Vector3 center;
            public float radius;
        }

        static readonly SourceDefect[] SourceDefects =
        {
            new SourceDefect("Large Missing-Geometry Hole (upper hole cluster, near crown)",
                new Vector3(-10.20f, 74.36f, 100.27f), 0.025f, true,
                DefectCategory.MissingGeometryHole, DefectSeverity.Critical),
            new SourceDefect("Large Missing-Geometry Hole (upper-rear hole)",
                new Vector3(-76.25f, 98.28f, -14.61f), 0.026f, true,
                DefectCategory.MissingGeometryHole, DefectSeverity.Critical),
            new SourceDefect("Missing-Geometry Hole (adjacent to H1, crown area)",
                new Vector3(-50.07f, 64.49f, 96.24f), 0.019f, true,
                DefectCategory.MissingGeometryHole, DefectSeverity.Critical),
            new SourceDefect("Missing-Geometry Hole (left side, rear)",
                new Vector3(-111.58f, 7.55f, -57.19f), 0.024f, true,
                DefectCategory.MissingGeometryHole, DefectSeverity.Critical),
            new SourceDefect("Largest Missing-Geometry Hole (front-right, most severe hole)",
                new Vector3(78.23f, -65.42f, 72.88f), 0.028f, true,
                DefectCategory.MissingGeometryHole, DefectSeverity.Critical),
            new SourceDefect("Missing-Geometry Hole (right side, rear)",
                new Vector3(109.07f, 21.55f, -59.29f), 0.022f, true,
                DefectCategory.MissingGeometryHole, DefectSeverity.Critical),
            new SourceDefect("Deep Structural Dent",
                new Vector3(-100.00f, -40.00f, 90.00f), 0.018f, false,
                DefectCategory.ImpactDent, DefectSeverity.Critical, 16.6f),
            new SourceDefect("Localized Impact Deformation",
                new Vector3(9.29f, -115.76f, 34.74f), 0.016f, false,
                DefectCategory.ImpactDent, DefectSeverity.Critical, 9.9f),
            new SourceDefect("Material Bulge",
                new Vector3(89.97f, 58.16f, 57.02f), 0.016f, false,
                DefectCategory.SurfaceBulge, DefectSeverity.Critical, 9.0f),
            new SourceDefect("Surface Warping",
                new Vector3(28.66f, -104.95f, 67.16f), 0.013f, false,
                DefectCategory.LocalDeformation, DefectSeverity.Moderate, 5.5f),
        };

        [MenuItem("Helmet Inspection/Rebuild Authoritative 6 Holes + 4 Dents")]
        public static void RebuildAndVerify()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var session = UnityEngine.Object.FindFirstObjectByType<TrainingSessionController>(FindObjectsInactive.Include);
            var defectSet = AssetDatabase.LoadAssetAtPath<DefectSet>(DefectSetPath);
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            var ringMesh = AssetDatabase.LoadAssetAtPath<Mesh>(RingMeshPath);
            var foundMaterial = AssetDatabase.LoadAssetAtPath<Material>(FoundMaterialPath);
            if (session == null || defectSet == null || mesh == null || ringMesh == null || foundMaterial == null)
                throw new InvalidOperationException("Authoritative rebuild dependencies are missing.");

            var oldHotspots = UnityEngine.Object.FindObjectsByType<DefectHotspot>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (oldHotspots.Length == 0 || oldHotspots[0].transform.parent == null)
                throw new InvalidOperationException("Existing hotspot parent was not found; refusing to guess the helmet hierarchy.");
            var hotspotParent = oldHotspots[0].transform.parent;
            if (hotspotParent.lossyScale != Vector3.one)
                throw new InvalidOperationException($"Hotspot parent must use baked metre units at scale (1,1,1), found {hotspotParent.lossyScale}.");

            foreach (var hotspot in oldHotspots)
                UnityEngine.Object.DestroyImmediate(hotspot.gameObject);
            DeleteBaldSpotPlaceholders(scene);
            hotspotParent.name = "Authored Defect Hotspots - Exactly 10 (6 Holes + 4 Dents)";

            var boundaryComponents = AnalyzeBoundaryComponents(mesh);
            var records = BuildRecords(mesh, boundaryComponents, out var verification);
            defectSet.SetEditorData(2f, records);
            EditorUtility.SetDirty(defectSet);

            var sceneHotspots = new List<DefectHotspot>(SourceDefects.Length);
            for (var i = 0; i < records.Count; ++i)
            {
                var record = records[i];
                var source = SourceDefects[i];
                var root = new GameObject($"{record.id} - {record.title}");
                root.transform.SetParent(hotspotParent, false);
                root.transform.localPosition = record.localPosition;
                root.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, record.localNormal.normalized);

                var collider = root.AddComponent<SphereCollider>();
                collider.center = Vector3.zero;
                collider.radius = source.radiusMeters;
                collider.isTrigger = true;
                root.AddComponent<XRSimpleInteractable>();

                var halo = new GameObject(source.isHole ? "Found Hole Rim Decal" : "Found Defect Ring");
                halo.transform.SetParent(root.transform, false);
                halo.transform.localPosition = Vector3.forward * 0.0015f;
                halo.transform.localScale = Vector3.one * source.radiusMeters;
                halo.AddComponent<MeshFilter>().sharedMesh = ringMesh;
                var renderer = halo.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = foundMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.enabled = false;

                var hotspot = root.AddComponent<DefectHotspot>();
                hotspot.SetEditorReferences(i, session, renderer, source.isHole);
                hotspot.SetEditorGizmoVisible(false);
                sceneHotspots.Add(hotspot);
            }

            var serializedSession = new SerializedObject(session);
            serializedSession.FindProperty("defectSet").objectReferenceValue = defectSet;
            var hotspotList = serializedSession.FindProperty("hotspots");
            hotspotList.arraySize = sceneHotspots.Count;
            for (var i = 0; i < sceneHotspots.Count; ++i)
                hotspotList.GetArrayElementAtIndex(i).objectReferenceValue = sceneHotspots[i];
            serializedSession.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(session);

            foreach (var text in UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (text.name == "Intro Body")
                    text.text = Regex.Replace(text.text, @"Log all \d+ findings", "Log all 10 findings");
                else if (text.name == "Progress")
                    text.text = "QA FINDINGS  00 / 10";
                EditorUtility.SetDirty(text);
            }

            foreach (var button in UnityEngine.Object.FindObjectsByType<MechanicalTrainingButtonBase>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                button.AlignInteractionColliderToVisibleCap();
                EditorUtility.SetDirty(button.GetComponent<BoxCollider>());
            }

            VerifyScene(sceneHotspots, records, hotspotParent, scene);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new InvalidOperationException("Failed to save the authoritative defect scene.");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var reportDirectory = Path.GetDirectoryName(VerificationReportPath);
            if (!string.IsNullOrEmpty(reportDirectory))
                Directory.CreateDirectory(reportDirectory);
            File.WriteAllText(VerificationReportPath, verification.ToString());
            Debug.Log("[AuthoritativeDefects] Rebuilt exactly 6 measured holes + 4 measured dents and verified them against the imported mesh.\n" + verification);
        }

        [MenuItem("Helmet Inspection/Diagnostics/Dump Open Boundary Components")]
        public static void DumpBoundaryDiagnostics()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            if (mesh == null)
                throw new InvalidOperationException("The imported A2 mesh is missing.");
            var boundaries = AnalyzeBoundaryComponents(mesh);
            var significant = boundaries
                .Where(item => item.vertices.Count >= 10 && item.vertices.Count <= 120)
                .OrderByDescending(item => item.vertices.Count)
                .ToArray();
            var report = new StringBuilder();
            report.AppendLine($"OPEN BOUNDARY COMPONENTS: all={boundaries.Count}, significant={significant.Length}");
            for (var i = 0; i < significant.Length; ++i)
                report.AppendLine($"B{i + 1:00}: vertices={significant[i].vertices.Count}, center={significant[i].center:F5}, radius={significant[i].radius:F5}");
            report.AppendLine();
            for (var i = 0; i < 6; ++i)
            {
                var converted = RawMillimetersToImportedLocal(SourceDefects[i].rawMillimeters);
                var direct = SourceDefects[i].rawMillimeters * 0.001f;
                report.AppendLine($"H{i + 1} converted={converted:F5}");
                foreach (var candidate in significant.OrderBy(item => Vector3.Distance(item.center, converted)).Take(4))
                    report.AppendLine($"  converted -> {candidate.center:F5}, error={Vector3.Distance(candidate.center, converted) * 1000f:F2}mm, vertices={candidate.vertices.Count}");
                report.AppendLine($"H{i + 1} direct={direct:F5}");
                foreach (var candidate in significant.OrderBy(item => Vector3.Distance(item.center, direct)).Take(4))
                    report.AppendLine($"  direct -> {candidate.center:F5}, error={Vector3.Distance(candidate.center, direct) * 1000f:F2}mm, vertices={candidate.vertices.Count}");
            }
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/OpenBoundaryDiagnostics.txt", report.ToString());
            Debug.Log("[BoundaryDiagnostics]\n" + report);
        }

        [MenuItem("Helmet Inspection/Diagnostics/Render Defect Verification Views")]
        public static void RenderVerificationViews()
        {
            const string helmetPrefabPath = "Assets/HelmetInspection/Prefabs/A2_InspectionHelmet.prefab";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            var defectSet = AssetDatabase.LoadAssetAtPath<DefectSet>(DefectSetPath);
            var helmetPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(helmetPrefabPath);
            var markerMaterial = AssetDatabase.LoadAssetAtPath<Material>(FoundMaterialPath);
            if (mesh == null || defectSet == null || defectSet.Defects.Count != 10 || helmetPrefab == null ||
                markerMaterial == null)
                throw new InvalidOperationException("Authoritative preview dependencies are missing.");

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var helmet = PrefabUtility.InstantiatePrefab(helmetPrefab) as GameObject;
            if (helmet == null)
                throw new InvalidOperationException("Could not instantiate the A2 helmet preview.");
            helmet.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            helmet.transform.localScale = Vector3.one;
            var previewRigidbody = helmet.GetComponent<Rigidbody>();
            if (previewRigidbody != null)
                previewRigidbody.isKinematic = true;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.22f, 0.24f, 0.27f);

            Directory.CreateDirectory(VerificationImageDirectory);
            var cameraObject = new GameObject("Temporary Defect Verification Camera", typeof(Camera));
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.03f, 0.035f, 1f);
            camera.fieldOfView = 28f;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 3f;
            camera.allowHDR = false;
            camera.allowMSAA = true;

            var lightObject = new GameObject("Temporary Defect Verification Light", typeof(Light));
            var light = lightObject.GetComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.9f, 0.94f, 1f);
            light.intensity = 1.25f;
            light.shadows = LightShadows.None;

            var renderTexture = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 4
            };
            var texture = new Texture2D(1024, 1024, TextureFormat.RGB24, false);
            var previousActive = RenderTexture.active;
            camera.targetTexture = renderTexture;

            try
            {
                for (var i = 0; i < defectSet.Defects.Count; ++i)
                {
                    var defect = defectSet.Defects[i];
                    var reviewColor = defect.IsHole
                        ? new Color(0.15f, 1f, 0.35f, 1f)
                        : new Color(0.1f, 0.85f, 1f, 1f);
                    var markerObject = CreateWireSphere(defect.markerRadius, reviewColor, markerMaterial);
                    markerObject.transform.SetParent(helmet.transform, false);
                    markerObject.transform.localPosition = defect.localPosition;
                    markerObject.transform.localRotation = Quaternion.identity;
                    markerObject.transform.localScale = Vector3.one;

                    var center = defect.localPosition;
                    var outward = (defect.localPosition - mesh.bounds.center).normalized;
                    camera.transform.position = center + outward * 0.36f;
                    var up = Vector3.up;
                    if (Mathf.Abs(Vector3.Dot(up, outward)) > 0.92f)
                        up = Vector3.forward;
                    camera.transform.rotation = Quaternion.LookRotation(center - camera.transform.position, up);
                    lightObject.transform.rotation = Quaternion.LookRotation(center - camera.transform.position, up);

                    camera.Render();
                    RenderTexture.active = renderTexture;
                    texture.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0, false);
                    texture.Apply(false, false);
                    var kind = defect.IsHole ? "Hole" : "Dent";
                    var path = Path.Combine(VerificationImageDirectory, $"{i + 1:00}_{kind}.png");
                    File.WriteAllBytes(path, texture.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(markerObject);
                }
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(renderTexture);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(lightObject);
                UnityEngine.Object.DestroyImmediate(helmet);
            }

            Debug.Log($"[AuthoritativeDefects] Rendered {defectSet.Defects.Count} isolated visual verification views to {VerificationImageDirectory}.");
        }

        static GameObject CreateWireSphere(float radius, Color color, Material fallbackMaterial)
        {
            var root = new GameObject("Temporary Measured Hotspot Wire Sphere");
            const int segments = 64;
            for (var plane = 0; plane < 3; ++plane)
            {
                var circle = new GameObject($"Wire Circle {plane + 1}");
                circle.transform.SetParent(root.transform, false);
                var line = circle.AddComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.loop = true;
                line.positionCount = segments;
                line.startWidth = 0.0018f;
                line.endWidth = 0.0018f;
                line.numCapVertices = 2;
                line.shadowCastingMode = ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.startColor = color;
                line.endColor = color;
                line.sharedMaterial = fallbackMaterial;
                for (var i = 0; i < segments; ++i)
                {
                    var angle = i * Mathf.PI * 2f / segments;
                    var a = Mathf.Cos(angle) * radius;
                    var b = Mathf.Sin(angle) * radius;
                    line.SetPosition(i, plane == 0
                        ? new Vector3(a, b, 0f)
                        : plane == 1
                            ? new Vector3(a, 0f, b)
                            : new Vector3(0f, a, b));
                }
            }
            return root;
        }

        static List<DefectRecord> BuildRecords(Mesh mesh, IReadOnlyList<BoundaryComponent> boundaries,
            out StringBuilder verification)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var records = new List<DefectRecord>(SourceDefects.Length);
            verification = new StringBuilder();
            verification.AppendLine("AUTHORITATIVE DEFECT VERIFICATION");
            verification.AppendLine("Importer: raw Z-up millimetres -> Unity Y-up metres as (x,z,y) * 0.001; parent scale = 1.");
            verification.AppendLine();
            var usedBoundaries = new HashSet<BoundaryComponent>();

            for (var i = 0; i < SourceDefects.Length; ++i)
            {
                var source = SourceDefects[i];
                var localPosition = RawMillimetersToImportedLocal(source.rawMillimeters);
                var nearestVertex = FindNearestVertex(vertices, localPosition, out var nearestVertexDistance);
                var normal = normals != null && normals.Length == vertices.Length
                    ? normals[nearestVertex].normalized
                    : (localPosition - mesh.bounds.center).normalized;
                var sourceClusterSize = Mathf.Max(2, vertices.Count(vertex =>
                    Vector3.Distance(vertex, localPosition) <= source.radiusMeters));

                if (source.isHole)
                {
                    var boundary = boundaries
                        .Where(item => item.vertices.Count >= 20 && item.vertices.Count <= 80 && !usedBoundaries.Contains(item))
                        .OrderBy(item => Vector3.Distance(item.center, localPosition))
                        .FirstOrDefault();
                    if (boundary == null)
                        throw new InvalidOperationException($"No significant open boundary loop was available for H{i + 1}.");
                    var centerError = Vector3.Distance(boundary.center, localPosition);
                    if (centerError > 0.012f)
                        throw new InvalidOperationException($"H{i + 1} is {centerError * 1000f:0.0} mm from the nearest significant open boundary, indicating a unit/axis mismatch.");
                    usedBoundaries.Add(boundary);
                    sourceClusterSize = boundary.vertices.Count;
                    normal = (localPosition - mesh.bounds.center).normalized;
                    verification.AppendLine($"H{i + 1}: local {localPosition:F5}, radius {source.radiusMeters:F3} m, " +
                                            $"boundary vertices {boundary.vertices.Count}, measured boundary center error {centerError * 1000f:F2} mm, PASS");
                }
                else
                {
                    if (nearestVertexDistance > source.radiusMeters)
                        throw new InvalidOperationException($"D{i - 5} is {nearestVertexDistance * 1000f:0.0} mm from the imported surface.");
                    if (Vector3.Dot(normal, localPosition - mesh.bounds.center) < 0f)
                        normal = -normal;
                    verification.AppendLine($"D{i - 5}: local {localPosition:F5}, radius {source.radiusMeters:F3} m, " +
                                            $"nearest surface {nearestVertexDistance * 1000f:F2} mm, PASS");
                }

                records.Add(new DefectRecord
                {
                    id = $"A2-D{i + 1:00}",
                    title = source.title,
                    category = source.category,
                    severity = source.severity,
                    localPosition = localPosition,
                    localNormal = normal.sqrMagnitude > 0.5f ? normal : Vector3.up,
                    markerRadius = source.radiusMeters,
                    deviationMillimeters = source.deviationMillimeters,
                    inspectionNote = source.isHole
                        ? $"Open-boundary mesh inspection confirms missing shell geometry with a measured {source.radiusMeters * 1000f:0} mm opening radius."
                        : $"Measured A2-to-A1 surface deviation: {source.deviationMillimeters:0.0} mm. This is a scan-confirmed closed-surface deformation.",
                    correctiveAction = source.isHole || source.severity == DefectSeverity.Critical
                        ? "Reject from service, quarantine the helmet, and escalate for engineering disposition."
                        : "Place on quality hold and complete dimensional review before release.",
                    sourceVertex = nearestVertex,
                    sourceClusterSize = sourceClusterSize
                });
            }

            return records;
        }

        static List<BoundaryComponent> AnalyzeBoundaryComponents(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var edgeCounts = new Dictionary<ulong, int>();
            var triangles = mesh.triangles;
            for (var i = 0; i + 2 < triangles.Length; i += 3)
            {
                // Use the mesh's actual indexed topology. An open boundary edge is an
                // indexed edge owned by exactly one triangle; welding by position can
                // incorrectly join two neighboring but independent hole rims.
                CountEdge(triangles[i], triangles[i + 1], edgeCounts);
                CountEdge(triangles[i + 1], triangles[i + 2], edgeCounts);
                CountEdge(triangles[i + 2], triangles[i], edgeCounts);
            }

            var adjacency = new Dictionary<int, List<int>>();
            foreach (var pair in edgeCounts)
            {
                if (pair.Value != 1)
                    continue;
                var a = (int)(pair.Key >> 32);
                var b = (int)(pair.Key & uint.MaxValue);
                AddNeighbor(adjacency, a, b);
                AddNeighbor(adjacency, b, a);
            }

            var result = new List<BoundaryComponent>();
            var visited = new HashSet<int>();
            foreach (var start in adjacency.Keys)
            {
                if (!visited.Add(start))
                    continue;
                var component = new BoundaryComponent();
                var queue = new Queue<int>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    component.vertices.Add(current);
                    foreach (var neighbor in adjacency[current])
                        if (visited.Add(neighbor))
                            queue.Enqueue(neighbor);
                }
                component.center = component.vertices.Aggregate(Vector3.zero,
                    (sum, vertex) => sum + vertices[vertex]) / component.vertices.Count;
                component.radius = component.vertices.Max(vertex =>
                    Vector3.Distance(vertices[vertex], component.center));
                result.Add(component);
            }
            // Scanned shell openings can expose an outer and an inner rim as two indexed
            // boundary components only a few millimetres apart. They are one physical
            // hole. Group those paired rims and compute the vertex-weighted centroid used
            // by the authoritative source measurements. Tiny components remain excluded
            // as ordinary reconstruction noise.
            var significant = result.Where(item => item.vertices.Count >= 10).ToArray();
            var grouped = new List<BoundaryComponent>();
            var consumed = new HashSet<BoundaryComponent>();
            foreach (var seed in significant)
            {
                if (!consumed.Add(seed))
                    continue;
                var physicalHole = new BoundaryComponent();
                physicalHole.vertices.AddRange(seed.vertices);
                foreach (var candidate in significant)
                {
                    if (consumed.Contains(candidate) || Vector3.Distance(seed.center, candidate.center) > 0.012f)
                        continue;
                    consumed.Add(candidate);
                    physicalHole.vertices.AddRange(candidate.vertices);
                }
                physicalHole.center = physicalHole.vertices.Aggregate(Vector3.zero,
                    (sum, vertex) => sum + vertices[vertex]) / physicalHole.vertices.Count;
                physicalHole.radius = physicalHole.vertices.Max(vertex =>
                    Vector3.Distance(vertices[vertex], physicalHole.center));
                grouped.Add(physicalHole);
            }
            return grouped;
        }

        static void CountEdge(int a, int b, IDictionary<ulong, int> edgeCounts)
        {
            if (a == b)
                return;
            var minimum = (uint)Mathf.Min(a, b);
            var maximum = (uint)Mathf.Max(a, b);
            var key = ((ulong)minimum << 32) | maximum;
            edgeCounts.TryGetValue(key, out var count);
            edgeCounts[key] = count + 1;
        }

        static void AddNeighbor(IDictionary<int, List<int>> adjacency, int from, int to)
        {
            if (!adjacency.TryGetValue(from, out var values))
            {
                values = new List<int>();
                adjacency.Add(from, values);
            }
            values.Add(to);
        }

        static int FindNearestVertex(IReadOnlyList<Vector3> vertices, Vector3 position, out float distance)
        {
            var best = 0;
            var bestSquared = float.PositiveInfinity;
            for (var i = 0; i < vertices.Count; ++i)
            {
                var squared = (vertices[i] - position).sqrMagnitude;
                if (squared >= bestSquared)
                    continue;
                best = i;
                bestSquared = squared;
            }
            distance = Mathf.Sqrt(bestSquared);
            return best;
        }

        static Vector3 RawMillimetersToImportedLocal(Vector3 rawMillimeters) =>
            new Vector3(rawMillimeters.x, rawMillimeters.z, rawMillimeters.y) * 0.001f;

        static void DeleteBaldSpotPlaceholders(Scene scene)
        {
            var candidates = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Where(item => item != null && item.name.IndexOf("Bald spot", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(item => item.gameObject)
                .Distinct()
                .ToArray();
            foreach (var candidate in candidates)
                UnityEngine.Object.DestroyImmediate(candidate);
        }

        static void VerifyScene(IReadOnlyList<DefectHotspot> hotspots, IReadOnlyList<DefectRecord> records,
            Transform hotspotParent, Scene scene)
        {
            if (hotspots.Count != 10 || records.Count != 10)
                throw new InvalidOperationException("The authoritative scene/data set must contain exactly 10 defects.");
            if (hotspots.Count(item => item.IsHole) != 6 || records.Count(item => item.IsHole) != 6)
                throw new InvalidOperationException("The authoritative set must contain exactly 6 hole defects.");
            if (hotspotParent.childCount != 10)
                throw new InvalidOperationException("The hotspot parent contains unaccounted-for marker children.");
            for (var i = 0; i < hotspots.Count; ++i)
            {
                var hotspot = hotspots[i];
                var collider = hotspot.GetComponent<SphereCollider>();
                if (hotspot.DefectIndex != i || collider == null || !collider.isTrigger ||
                    Vector3.Distance(hotspot.transform.localPosition, records[i].localPosition) > 0.000001f ||
                    Mathf.Abs(collider.radius - records[i].markerRadius) > 0.000001f)
                    throw new InvalidOperationException($"Scene hotspot {i + 1} does not exactly match its authoritative record.");
            }
            var baldSpots = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Count(item => item.name.IndexOf("Bald spot", StringComparison.OrdinalIgnoreCase) >= 0);
            if (baldSpots != 0)
                throw new InvalidOperationException("A leftover Bald spot placeholder remains in the scene.");
        }
    }
}
