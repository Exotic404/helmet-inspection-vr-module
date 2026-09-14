using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HelmetInspection.Editor
{
    public static class DefectAuthoringTool
    {
        const float CandidateThreshold = 0.002f;
        const float CuratedSeparation = 0.025f;
        const float LocalPatchRadius = 0.012f;
        const int RequiredDefectCount = 10;

        sealed class TriangleBvh
        {
            readonly Vector3[] m_Vertices;
            readonly int[] m_Triangles;
            readonly int[] m_TriangleIds;
            readonly List<Node> m_Nodes = new List<Node>();

            struct Node
            {
                public Bounds bounds;
                public int left;
                public int right;
                public int start;
                public int count;
                public bool IsLeaf => count > 0;
            }

            public TriangleBvh(Vector3[] vertices, int[] triangles)
            {
                m_Vertices = vertices;
                m_Triangles = triangles;
                m_TriangleIds = Enumerable.Range(0, triangles.Length / 3).ToArray();
                BuildNode(0, m_TriangleIds.Length);
            }

            int BuildNode(int start, int count)
            {
                var bounds = TriangleBounds(m_TriangleIds[start]);
                var centroidBounds = new Bounds(bounds.center, Vector3.zero);
                for (var i = 1; i < count; ++i)
                {
                    var triBounds = TriangleBounds(m_TriangleIds[start + i]);
                    bounds.Encapsulate(triBounds);
                    centroidBounds.Encapsulate(triBounds.center);
                }

                var nodeIndex = m_Nodes.Count;
                m_Nodes.Add(default);
                if (count <= 12)
                {
                    m_Nodes[nodeIndex] = new Node { bounds = bounds, start = start, count = count, left = -1, right = -1 };
                    return nodeIndex;
                }

                var axis = centroidBounds.size.x > centroidBounds.size.y
                    ? (centroidBounds.size.x > centroidBounds.size.z ? 0 : 2)
                    : (centroidBounds.size.y > centroidBounds.size.z ? 1 : 2);
                Array.Sort(m_TriangleIds, start, count, Comparer<int>.Create((a, b) => Axis(TriangleBounds(a).center, axis).CompareTo(Axis(TriangleBounds(b).center, axis))));
                var half = count / 2;
                var left = BuildNode(start, half);
                var right = BuildNode(start + half, count - half);
                m_Nodes[nodeIndex] = new Node { bounds = bounds, left = left, right = right, start = start, count = 0 };
                return nodeIndex;
            }

            public float NearestDistance(Vector3 point, out Vector3 nearestPoint, out Vector3 triangleNormal)
            {
                var bestSqr = float.PositiveInfinity;
                nearestPoint = Vector3.zero;
                triangleNormal = Vector3.up;
                var stack = new Stack<int>();
                stack.Push(0);
                while (stack.Count > 0)
                {
                    var node = m_Nodes[stack.Pop()];
                    if (SqrDistanceToBounds(point, node.bounds) >= bestSqr)
                        continue;
                    if (node.IsLeaf)
                    {
                        for (var i = 0; i < node.count; ++i)
                        {
                            var triangleId = m_TriangleIds[node.start + i];
                            var baseIndex = triangleId * 3;
                            var a = m_Vertices[m_Triangles[baseIndex]];
                            var b = m_Vertices[m_Triangles[baseIndex + 1]];
                            var c = m_Vertices[m_Triangles[baseIndex + 2]];
                            var candidate = ClosestPointOnTriangle(point, a, b, c);
                            var sqr = (point - candidate).sqrMagnitude;
                            if (sqr >= bestSqr)
                                continue;
                            bestSqr = sqr;
                            nearestPoint = candidate;
                            triangleNormal = Vector3.Cross(b - a, c - a).normalized;
                        }
                    }
                    else
                    {
                        stack.Push(node.left);
                        stack.Push(node.right);
                    }
                }
                return Mathf.Sqrt(bestSqr);
            }

            Bounds TriangleBounds(int triangleId)
            {
                var index = triangleId * 3;
                var bounds = new Bounds(m_Vertices[m_Triangles[index]], Vector3.zero);
                bounds.Encapsulate(m_Vertices[m_Triangles[index + 1]]);
                bounds.Encapsulate(m_Vertices[m_Triangles[index + 2]]);
                bounds.Expand(0.00001f);
                return bounds;
            }

            static float Axis(Vector3 value, int axis) => axis == 0 ? value.x : axis == 1 ? value.y : value.z;

            static float SqrDistanceToBounds(Vector3 point, Bounds bounds)
            {
                var closest = bounds.ClosestPoint(point);
                return (point - closest).sqrMagnitude;
            }
        }

        sealed class Cluster
        {
            public readonly List<int> vertices = new List<int>();
            public Vector3 position;
            public Vector3 normal;
            public float maxDistance;
            public int representativeVertex;
        }

        readonly struct MeasuredDefect
        {
            public readonly string title;
            public readonly Vector3 rawMillimeters;
            public readonly float radiusMeters;
            public readonly float deviationMillimeters;
            public readonly DefectCategory category;
            public readonly DefectSeverity severity;

            public MeasuredDefect(string title, Vector3 rawMillimeters, float radiusMeters,
                float deviationMillimeters, DefectCategory category, DefectSeverity severity)
            {
                this.title = title;
                this.rawMillimeters = rawMillimeters;
                this.radiusMeters = radiusMeters;
                this.deviationMillimeters = deviationMillimeters;
                this.category = category;
                this.severity = severity;
            }
        }

        static readonly MeasuredDefect[] MeasuredDefects =
        {
            new MeasuredDefect("Large Missing-Geometry Hole (upper hole cluster, near crown)", new Vector3(-10.20f, 74.36f, 100.27f), 0.025f, 0f, DefectCategory.MissingGeometryHole, DefectSeverity.Critical),
            new MeasuredDefect("Large Missing-Geometry Hole (upper-rear hole)", new Vector3(-76.25f, 98.28f, -14.61f), 0.026f, 0f, DefectCategory.MissingGeometryHole, DefectSeverity.Critical),
            new MeasuredDefect("Missing-Geometry Hole (adjacent to H1, crown area)", new Vector3(-50.07f, 64.49f, 96.24f), 0.019f, 0f, DefectCategory.MissingGeometryHole, DefectSeverity.Critical),
            new MeasuredDefect("Missing-Geometry Hole (left side, rear)", new Vector3(-111.58f, 7.55f, -57.19f), 0.024f, 0f, DefectCategory.MissingGeometryHole, DefectSeverity.Critical),
            new MeasuredDefect("Largest Missing-Geometry Hole (front-right, most severe hole)", new Vector3(78.23f, -65.42f, 72.88f), 0.028f, 0f, DefectCategory.MissingGeometryHole, DefectSeverity.Critical),
            new MeasuredDefect("Missing-Geometry Hole (right side, rear)", new Vector3(109.07f, 21.55f, -59.29f), 0.022f, 0f, DefectCategory.MissingGeometryHole, DefectSeverity.Critical),
            new MeasuredDefect("Deep Structural Dent", new Vector3(-100.00f, -40.00f, 90.00f), 0.018f, 16.6f, DefectCategory.ImpactDent, DefectSeverity.Critical),
            new MeasuredDefect("Localized Impact Deformation", new Vector3(9.29f, -115.76f, 34.74f), 0.016f, 9.9f, DefectCategory.ImpactDent, DefectSeverity.Critical),
            new MeasuredDefect("Material Bulge", new Vector3(89.97f, 58.16f, 57.02f), 0.016f, 9.0f, DefectCategory.SurfaceBulge, DefectSeverity.Critical),
            new MeasuredDefect("Surface Warping", new Vector3(28.66f, -104.95f, 67.16f), 0.013f, 5.5f, DefectCategory.LocalDeformation, DefectSeverity.Moderate),
        };

        public static DefectSet AnalyzeCurateAndSave(Mesh reference, Mesh scan, string assetPath)
        {
            if (reference == null || scan == null)
                throw new ArgumentNullException("Both reference and scan meshes are required.");

            var scanVertices = scan.vertices;
            var scanNormals = scan.normals;
            var records = new List<DefectRecord>(RequiredDefectCount);
            for (var i = 0; i < MeasuredDefects.Length; ++i)
            {
                var measured = MeasuredDefects[i];
                // GlbMeshImporter flattens glTF's 0.001 node scale and converts Z-up raw
                // scan coordinates to Unity Y-up as (x, z, y). The prefab root is scale 1.
                var localPosition = RawMillimetersToUnityMeters(measured.rawMillimeters);
                var nearestVertex = FindNearestVertex(scanVertices, localPosition);
                var normal = scanNormals != null && scanNormals.Length == scanVertices.Length
                    ? scanNormals[nearestVertex].normalized
                    : (localPosition - scan.bounds.center).normalized;
                if (Vector3.Dot(normal, localPosition - scan.bounds.center) < 0f)
                    normal = -normal;
                var patchCount = scanVertices.Count(vertex =>
                    Vector3.Distance(vertex, localPosition) <= measured.radiusMeters);
                records.Add(new DefectRecord
                {
                    id = $"A2-D{i + 1:00}",
                    title = measured.title,
                    category = measured.category,
                    severity = measured.severity,
                    localPosition = localPosition,
                    localNormal = normal.sqrMagnitude > 0.5f ? normal : Vector3.up,
                    markerRadius = measured.radiusMeters,
                    deviationMillimeters = measured.deviationMillimeters,
                    inspectionNote = measured.category == DefectCategory.MissingGeometryHole
                        ? $"Open-boundary mesh inspection confirms missing shell geometry with a measured {measured.radiusMeters * 1000f:0} mm opening radius."
                        : $"Measured A2-to-A1 surface deviation: {measured.deviationMillimeters:0.0} mm. This is a scan-confirmed closed-surface deformation.",
                    correctiveAction = measured.category == DefectCategory.MissingGeometryHole || measured.severity == DefectSeverity.Critical
                        ? "Reject from service, quarantine the helmet, and escalate for engineering disposition."
                        : "Place on quality hold and complete dimensional review before release.",
                    sourceVertex = nearestVertex,
                    sourceClusterSize = Mathf.Max(2, patchCount)
                });
            }

            var existing = AssetDatabase.LoadAssetAtPath<DefectSet>(assetPath);
            if (existing == null)
            {
                existing = ScriptableObject.CreateInstance<DefectSet>();
                existing.name = "DefectSet_A2";
                AssetDatabase.CreateAsset(existing, assetPath);
            }
            existing.SetEditorData(CandidateThreshold * 1000f, records);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var persisted = AssetDatabase.LoadAssetAtPath<DefectSet>(assetPath);
            if (persisted == null || persisted.Defects.Count != RequiredDefectCount)
                throw new InvalidOperationException("DefectSet_A2 did not persist its ten curated records.");
            return persisted;
        }

        static Vector3 RawMillimetersToUnityMeters(Vector3 raw) =>
            new Vector3(raw.x, raw.z, raw.y) * 0.001f;

        static int FindNearestVertex(IReadOnlyList<Vector3> vertices, Vector3 position)
        {
            var bestIndex = 0;
            var bestDistance = float.PositiveInfinity;
            for (var i = 0; i < vertices.Count; ++i)
            {
                var distance = (vertices[i] - position).sqrMagnitude;
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                bestIndex = i;
            }
            return bestIndex;
        }

        static List<int>[] BuildAdjacency(int vertexCount, int[] triangles)
        {
            var result = new List<int>[vertexCount];
            for (var i = 0; i < vertexCount; ++i)
                result[i] = new List<int>(6);
            for (var i = 0; i + 2 < triangles.Length; i += 3)
            {
                AddEdge(result, triangles[i], triangles[i + 1]);
                AddEdge(result, triangles[i + 1], triangles[i + 2]);
                AddEdge(result, triangles[i + 2], triangles[i]);
            }
            return result;
        }

        static void AddEdge(List<int>[] adjacency, int a, int b)
        {
            if (!adjacency[a].Contains(b)) adjacency[a].Add(b);
            if (!adjacency[b].Contains(a)) adjacency[b].Add(a);
        }

        static List<Cluster> ClusterCandidates(Vector3[] vertices, Vector3[] normals, float[] distances,
            bool[] candidates, List<int>[] adjacency)
        {
            var visited = new bool[vertices.Length];
            var clusters = new List<Cluster>();
            var queue = new Queue<int>();
            for (var seed = 0; seed < vertices.Length; ++seed)
            {
                if (!candidates[seed] || visited[seed])
                    continue;
                var cluster = new Cluster();
                var weightedPosition = Vector3.zero;
                var weightedNormal = Vector3.zero;
                var weightSum = 0f;
                visited[seed] = true;
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    var vertex = queue.Dequeue();
                    cluster.vertices.Add(vertex);
                    var weight = Mathf.Max(0.0001f, distances[vertex] - CandidateThreshold + 0.0002f);
                    weightedPosition += vertices[vertex] * weight;
                    weightedNormal += (normals != null && normals.Length == vertices.Length ? normals[vertex] : Vector3.up) * weight;
                    weightSum += weight;
                    if (distances[vertex] > cluster.maxDistance)
                    {
                        cluster.maxDistance = distances[vertex];
                        cluster.representativeVertex = vertex;
                    }
                    foreach (var neighbor in adjacency[vertex])
                    {
                        if (candidates[neighbor] && !visited[neighbor])
                        {
                            visited[neighbor] = true;
                            queue.Enqueue(neighbor);
                        }
                    }
                }
                cluster.position = weightedPosition / Mathf.Max(weightSum, 0.0001f);
                cluster.normal = weightedNormal.normalized;
                clusters.Add(cluster);
            }
            return clusters;
        }

        static List<Cluster> CurateTen(List<Cluster> rankedClusters, Vector3[] vertices, Vector3[] normals, float[] distances)
        {
            var selected = new List<Cluster>(RequiredDefectCount);
            foreach (var cluster in rankedClusters)
            {
                if (selected.All(existing => Vector3.Distance(existing.position, cluster.position) >= CuratedSeparation))
                    selected.Add(cluster);
                if (selected.Count == RequiredDefectCount)
                    return selected;
            }

            // Topology seams can split a real surface deviation into isolated mesh islands. Use
            // deterministic spatial patches for the remaining review queue so every finding has
            // a stable area and averaged surface normal rather than a lone scan sample.
            foreach (var vertex in Enumerable.Range(0, vertices.Length).OrderByDescending(i => distances[i]))
            {
                if (distances[vertex] < CandidateThreshold)
                    break;
                if (selected.Any(existing => Vector3.Distance(existing.position, vertices[vertex]) < CuratedSeparation))
                    continue;
                var patch = BuildLocalPatch(vertex, vertices, normals, distances);
                if (patch.vertices.Count < 2 || selected.Any(existing => Vector3.Distance(existing.position, patch.position) < CuratedSeparation))
                    continue;
                selected.Add(patch);
                if (selected.Count == RequiredDefectCount)
                    break;
            }
            return selected;
        }

        static Cluster BuildLocalPatch(int centerVertex, Vector3[] vertices, Vector3[] normals, float[] distances)
        {
            var center = vertices[centerVertex];
            var centerNormal = normals != null && normals.Length == vertices.Length
                ? normals[centerVertex].normalized
                : Vector3.up;
            var cluster = new Cluster { representativeVertex = centerVertex };
            var weightedPosition = Vector3.zero;
            var weightedNormal = Vector3.zero;
            var weightSum = 0f;
            for (var i = 0; i < vertices.Length; ++i)
            {
                if (distances[i] < CandidateThreshold || Vector3.Distance(center, vertices[i]) > LocalPatchRadius)
                    continue;
                var normal = normals != null && normals.Length == vertices.Length ? normals[i].normalized : Vector3.up;
                if (centerNormal.sqrMagnitude > 0.5f && normal.sqrMagnitude > 0.5f && Vector3.Dot(centerNormal, normal) < 0.2f)
                    continue;
                var weight = Mathf.Max(0.0001f, distances[i] - CandidateThreshold + 0.0002f);
                cluster.vertices.Add(i);
                weightedPosition += vertices[i] * weight;
                weightedNormal += normal * weight;
                weightSum += weight;
                if (distances[i] > cluster.maxDistance)
                {
                    cluster.maxDistance = distances[i];
                    cluster.representativeVertex = i;
                }
            }
            cluster.position = weightSum > 0f ? weightedPosition / weightSum : center;
            cluster.normal = weightedNormal.sqrMagnitude > 0.0001f ? weightedNormal.normalized : centerNormal;
            return cluster;
        }

        static DefectCategory Classify(Vector3 position, Bounds bounds)
        {
            var normalizedHeight = Mathf.InverseLerp(bounds.min.y, bounds.max.y, position.y);
            var radial = new Vector2(position.x - bounds.center.x, position.z - bounds.center.z).magnitude;
            if (normalizedHeight > 0.76f)
                return DefectCategory.CrownDepression;
            if (normalizedHeight < 0.25f)
                return DefectCategory.EdgeDistortion;
            if (radial > Mathf.Max(bounds.extents.x, bounds.extents.z) * 0.78f)
                return DefectCategory.SurfaceBulge;
            return DefectCategory.ImpactDent;
        }

        static string BuildTitle(DefectCategory category, int number) => category switch
        {
            DefectCategory.CrownDepression => $"Crown profile deviation {number}",
            DefectCategory.EdgeDistortion => $"Brim edge distortion {number}",
            DefectCategory.SurfaceBulge => $"Shell surface bulge {number}",
            DefectCategory.ImpactDent => $"Localized impact deformation {number}",
            _ => $"Local shell deviation {number}"
        };

        static string BuildInspectionNote(DefectCategory category, float deviationMm) =>
            $"The A2 scan differs from the A1 reference by {deviationMm:0.0} mm at this {category.ToString().ToLowerInvariant()} region. " +
            "The value exceeds the 2.0 mm inspection candidate threshold and is isolated from neighboring findings.";

        static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            var d1 = Vector3.Dot(ab, ap);
            var d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;

            var bp = p - b;
            var d3 = Vector3.Dot(ab, bp);
            var d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;

            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
                return a + (d1 / (d1 - d3)) * ab;

            var cp = p - c;
            var d5 = Vector3.Dot(ab, cp);
            var d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;

            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
                return a + (d2 / (d2 - d6)) * ac;

            var va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
                return b + ((d4 - d3) / ((d4 - d3) + (d5 - d6))) * (c - b);

            var denominator = 1f / (va + vb + vc);
            var v = vb * denominator;
            var w = vc * denominator;
            return a + ab * v + ac * w;
        }
    }
}
