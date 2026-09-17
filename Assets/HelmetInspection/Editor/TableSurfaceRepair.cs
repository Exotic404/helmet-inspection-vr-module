using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HelmetInspection.Editor
{
    /// <summary>Removes only the hidden, coplanar bench-body lid. Never runs automatically.</summary>
    public static class TableSurfaceRepair
    {
        const string BodyName = "environment_Lab_DeepPetrolPowdercoat";
        const string CapName = "environment_Lab_SatinAluminum";
        const string SourcePath = "Assets/HelmetInspection/ArtRefresh/Source/MetrologyLab.mesh.json";
        const string BodyPath = "Assets/HelmetInspection/ArtRefresh/Generated/Meshes/MetrologyLab_" + BodyName + ".asset";
        const string BodyGuid = "51e60197365fc214cbb74ab428f6696c";
        const float Epsilon = .000001f;
        static readonly Vector3[] CoveredCorners =
        {
            new Vector3(-.793f, .85f, .207f), new Vector3(.793f, .85f, .207f),
            new Vector3(-.793f, .85f, .993f), new Vector3(.793f, .85f, .993f),
        };

        [Serializable] sealed class SourceData
        {
            public int version;
            public string coordinateSystem;
            public int triangleCount;
            public MeshData[] meshes;
        }
        [Serializable] sealed class MeshData
        {
            public string name;
            public int materialIndex;
            public float[] positions;
            public float[] normals;
            public float[] uv;
            public int[] triangles;
        }

        [MenuItem("Helmet Inspection/Metrology Studio/Repair Coplanar Table Surface")]
        public static void Apply()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play Mode before repairing the table.");
            Require(AssetDatabase.AssetPathToGUID(BodyPath) == BodyGuid, "Unexpected bench mesh GUID; no changes made.");
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(BodyPath);
            Require(mesh != null && mesh.isReadable && mesh.subMeshCount == 1, "Expected one readable bench-body submesh.");
            Require(!EditorUtility.IsDirty(mesh), "Save any pending bench mesh edits before running this repair.");
            var colliders = Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Require(colliders.OfType<MeshCollider>().All(c => c.sharedMesh != mesh), "Bench visual mesh is used by a collider; refusing to alter collision geometry.");
            var colliderSnapshot = colliders.ToDictionary(c => c, c => EditorJsonUtility.ToJson(c));
            var transformSnapshot = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .ToDictionary(t => t, t => EditorJsonUtility.ToJson(t));

            var originalText = File.ReadAllText(SourcePath);
            var source = JsonUtility.FromJson<SourceData>(originalText);
            Require(source != null && source.version == 1 && source.coordinateSystem == "UnityLeftHandedYUp", "Unexpected exported mesh schema.");
            Require(source.meshes != null && source.meshes.Count(m => m.name == BodyName) == 1 &&
                    source.meshes.Count(m => m.name == CapName) == 1, "Expected exactly one body and one surface mesh.");
            Require(source.triangleCount == source.meshes.Sum(m => m.triangles.Length / 3), "Exported triangle count is inconsistent.");
            var body = source.meshes.Single(m => m.name == BodyName);
            var cap = source.meshes.Single(m => m.name == CapName);
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uv = mesh.uv;
            var bounds = mesh.bounds;
            var originalIndices = mesh.triangles;
            var sourceVertices = Vertices(body);
            Require(vertices.Length == sourceVertices.Length && vertices.Zip(sourceVertices, (a, b) => (a - b).sqrMagnitude < Epsilon * Epsilon).All(x => x),
                    "Generated mesh vertices differ from the export; refusing to overwrite authored geometry.");

            var capVertices = Vertices(cap);
            var capFaces = FlatTableFaces(capVertices, cap.triangles);
            Require(capFaces.Count == 2, "Expected exactly two horizontal surface-panel triangles.");
            Require(CoveredCorners.All(point => capFaces.Any(i => CoversPoint(point, capVertices, cap.triangles, i))),
                    "The surface panel does not fully cover the body lid.");
            var patchedIndices = WithoutLid(vertices, originalIndices, out var meshRemoved);
            var patchedSourceIndices = WithoutLid(sourceVertices, body.triangles, out var sourceRemoved);
            Require(patchedIndices.SequenceEqual(patchedSourceIndices), "Runtime/export triangles disagree outside the hidden lid.");

            if (meshRemoved == 0 && sourceRemoved == 0)
            {
                AssertUntouched(colliderSnapshot, transformSnapshot);
                Debug.Log("[TableSurfaceRepair] Already repaired: no hidden coplanar lid; surface coverage and runtime/export agreement verified.");
                return;
            }

            var patchedText = sourceRemoved == 0 ? originalText : PatchOnlyIndicesAndCount(originalText, patchedSourceIndices, source.triangleCount - sourceRemoved);
            var checkedSource = JsonUtility.FromJson<SourceData>(patchedText);
            AssertSourceUnchangedExceptLid(source, checkedSource, patchedSourceIndices, sourceRemoved);

            // Back up original files before either asset is modified. Do not overwrite prior backups.
            var backup = Path.Combine("Logs/ComfortSep16", "TableSurfaceRepair_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(backup);
            foreach (var path in new[] { SourcePath, BodyPath, BodyPath + ".meta" })
                File.Copy(path, Path.Combine(backup, Path.GetFileName(path)), false);

            try
            {
                if (meshRemoved != 0)
                {
                    mesh.triangles = patchedIndices;
                    mesh.bounds = bounds;
                    EditorUtility.SetDirty(mesh);
                    AssetDatabase.SaveAssetIfDirty(mesh);
                }
                if (sourceRemoved != 0)
                {
                    File.WriteAllText(SourcePath, patchedText, new UTF8Encoding(false));
                    AssetDatabase.ImportAsset(SourcePath, ImportAssetOptions.ForceSynchronousImport);
                }
                Require(mesh.vertices.SequenceEqual(vertices) && mesh.normals.SequenceEqual(normals) &&
                        mesh.tangents.SequenceEqual(tangents) && mesh.uv.SequenceEqual(uv) && mesh.bounds == bounds,
                        "Unrelated vertex data changed during index-only repair.");
                Require(mesh.triangles.SequenceEqual(patchedIndices) && FlatTableFaces(mesh.vertices, mesh.triangles).Count == 0,
                        "Hidden lid remains after repair.");
                Require(AssetDatabase.AssetPathToGUID(BodyPath) == BodyGuid, "Bench mesh GUID changed.");
                AssertSourceUnchangedExceptLid(source, JsonUtility.FromJson<SourceData>(File.ReadAllText(SourcePath)), patchedSourceIndices, sourceRemoved);
                AssertUntouched(colliderSnapshot, transformSnapshot);
                Debug.Log($"[TableSurfaceRepair] Removed {meshRemoved} hidden body triangles from runtime mesh and {sourceRemoved} from export. " +
                          $"Surface panel, vertices, normals, UVs, materials, colliders, and scene transforms unchanged. Backup: {Path.GetFullPath(backup)}");
            }
            catch
            {
                mesh.triangles = originalIndices;
                mesh.bounds = bounds;
                EditorUtility.SetDirty(mesh);
                AssetDatabase.SaveAssetIfDirty(mesh);
                File.Copy(Path.Combine(backup, Path.GetFileName(SourcePath)), SourcePath, true);
                AssetDatabase.ImportAsset(SourcePath, ImportAssetOptions.ForceSynchronousImport);
                Debug.LogError("[TableSurfaceRepair] Verification failed; restored original mesh indices and source JSON. Backup: " + Path.GetFullPath(backup));
                throw;
            }
        }

        static Vector3[] Vertices(MeshData data)
        {
            Require(data.positions != null && data.positions.Length % 3 == 0, "Invalid exported positions.");
            return Enumerable.Range(0, data.positions.Length / 3)
                .Select(i => new Vector3(data.positions[i * 3], data.positions[i * 3 + 1], data.positions[i * 3 + 2])).ToArray();
        }

        static List<int> FlatTableFaces(Vector3[] vertices, int[] indices)
        {
            Require(indices.Length % 3 == 0 && indices.All(i => i >= 0 && i < vertices.Length), "Invalid triangle indices.");
            var faces = new List<int>();
            for (var i = 0; i < indices.Length; i += 3)
            {
                var a = vertices[indices[i]]; var b = vertices[indices[i + 1]]; var c = vertices[indices[i + 2]];
                if (AtTableTop(a) && AtTableTop(b) && AtTableTop(c))
                {
                    Require(Vector3.Cross(b - a, c - a).y > Epsilon, "Unexpected table-top triangle winding or area.");
                    faces.Add(i);
                }
            }
            return faces;
        }
        static bool AtTableTop(Vector3 v) => Mathf.Abs(v.y - .85f) < Epsilon && Mathf.Abs(v.x) < .84f && v.z > .16f && v.z < 1.04f;

        static int[] WithoutLid(Vector3[] vertices, int[] indices, out int removed)
        {
            var faces = FlatTableFaces(vertices, indices);
            removed = faces.Count;
            Require(removed == 0 || removed == 2, "Bench has an unexpected number of coplanar faces; no repair attempted.");
            if (removed == 0) return indices;
            var corners = faces.SelectMany(i => new[] { vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]] }).Distinct().ToArray();
            Require(corners.Length == 4 && corners.All(p => CoveredCorners.Any(c => (p - c).sqrMagnitude < Epsilon * Epsilon)),
                    "Coplanar faces are not the known hidden body lid.");
            var area = faces.Sum(i => Vector3.Cross(vertices[indices[i + 1]] - vertices[indices[i]], vertices[indices[i + 2]] - vertices[indices[i]]).y * .5f);
            Require(Mathf.Abs(area - 1.586f * .786f) < .00001f, "Hidden body lid footprint changed.");
            var excluded = new HashSet<int>(faces.SelectMany(i => new[] { i, i + 1, i + 2 }));
            return indices.Where((_, i) => !excluded.Contains(i)).ToArray();
        }

        static bool CoversPoint(Vector3 p, Vector3[] vertices, int[] indices, int start)
        {
            var a = vertices[indices[start]]; var b = vertices[indices[start + 1]]; var c = vertices[indices[start + 2]];
            var total = Area(a, b, c);
            return Mathf.Abs(Area(p, a, b) + Area(p, b, c) + Area(p, c, a) - total) < Epsilon;
        }
        static float Area(Vector3 a, Vector3 b, Vector3 c) => Mathf.Abs((b.x - a.x) * (c.z - a.z) - (b.z - a.z) * (c.x - a.x)) * .5f;

        static string PatchOnlyIndicesAndCount(string json, int[] indices, int triangleCount)
        {
            // Keep all float literals, material records, fields, and unrelated mesh bytes intact.
            var pattern = "\\\"name\\\"\\s*:\\s*\\\"" + BodyName + "\\\"[^{}]*?\\\"triangles\\\"\\s*:\\s*\\[(?<indices>[0-9,\\s]+)\\]";
            var matches = Regex.Matches(json, pattern);
            Require(matches.Count == 1, "Cannot uniquely locate body triangle array in exported JSON.");
            var group = matches[0].Groups["indices"];
            json = json.Remove(group.Index, group.Length).Insert(group.Index, string.Join(",", indices.Select(i => i.ToString(CultureInfo.InvariantCulture))));
            var countMatches = Regex.Matches(json, "\\\"triangleCount\\\"\\s*:\\s*(?<count>[0-9]+)");
            Require(countMatches.Count == 1, "Cannot uniquely locate exported triangleCount.");
            group = countMatches[0].Groups["count"];
            return json.Remove(group.Index, group.Length).Insert(group.Index, triangleCount.ToString(CultureInfo.InvariantCulture));
        }

        static void AssertSourceUnchangedExceptLid(SourceData before, SourceData after, int[] bodyIndices, int removed)
        {
            Require(after != null && after.version == before.version && after.coordinateSystem == before.coordinateSystem &&
                    after.triangleCount == before.triangleCount - removed && after.meshes.Length == before.meshes.Length,
                    "Unexpected export metadata change.");
            for (var i = 0; i < before.meshes.Length; i++)
            {
                var a = before.meshes[i]; var b = after.meshes[i];
                Require(a.name == b.name && a.materialIndex == b.materialIndex && a.positions.SequenceEqual(b.positions) &&
                        a.normals.SequenceEqual(b.normals) && a.uv.SequenceEqual(b.uv) &&
                        b.triangles.SequenceEqual(a.name == BodyName ? bodyIndices : a.triangles), "Unrelated exported mesh data changed: " + a.name);
            }
            Require(after.triangleCount == after.meshes.Sum(m => m.triangles.Length / 3), "Repaired export triangle count is inconsistent.");
        }

        static void AssertUntouched(Dictionary<Collider, string> colliders, Dictionary<Transform, string> transforms)
        {
            Require(Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length == colliders.Count &&
                    colliders.All(pair => pair.Key != null && EditorJsonUtility.ToJson(pair.Key) == pair.Value), "Scene collider state changed.");
            Require(Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length == transforms.Count &&
                    transforms.All(pair => pair.Key != null && EditorJsonUtility.ToJson(pair.Key) == pair.Value), "Scene transform state changed.");
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("[TableSurfaceRepair] " + message);
        }
    }
}
