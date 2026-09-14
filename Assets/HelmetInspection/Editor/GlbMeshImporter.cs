using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HelmetInspection.Editor
{
    public static class GlbMeshImporter
    {
        public const float MillimetersToMeters = 0.001f;
        const uint GlbMagic = 0x46546C67;
        const uint JsonChunk = 0x4E4F534A;
        const uint BinChunk = 0x004E4942;

        [Serializable]
        sealed class GltfRoot
        {
            public Accessor[] accessors;
            public BufferView[] bufferViews;
            public GltfMesh[] meshes;
        }

        [Serializable]
        sealed class GltfMesh
        {
            public Primitive[] primitives;
        }

        [Serializable]
        sealed class Primitive
        {
            public Attributes attributes;
            public int indices;
            public int mode = 4;
        }

        [Serializable]
        sealed class Attributes
        {
            public int POSITION = -1;
            public int NORMAL = -1;
        }

        [Serializable]
        sealed class Accessor
        {
            public int bufferView;
            public int byteOffset;
            public int componentType;
            public int count;
            public string type;
        }

        [Serializable]
        sealed class BufferView
        {
            public int buffer;
            public int byteOffset;
            public int byteLength;
            public int byteStride;
        }

        public static Mesh ImportSingleMesh(string assetPath, string meshAssetPath, string expectedSha256)
        {
            var absolutePath = Path.GetFullPath(assetPath);
            if (!File.Exists(absolutePath))
                throw new FileNotFoundException("Helmet source GLB is missing.", absolutePath);

            var actualHash = ComputeSha256(absolutePath);
            if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Source model hash mismatch for {Path.GetFileName(assetPath)}. Expected {expectedSha256}, found {actualHash}.");

            ReadGlb(absolutePath, out var json, out var binary);
            var root = JsonUtility.FromJson<GltfRoot>(json);
            if (root?.meshes == null || root.meshes.Length != 1 || root.meshes[0].primitives == null || root.meshes[0].primitives.Length != 1)
                throw new InvalidDataException($"{assetPath} must contain exactly one mesh with one primitive.");

            var primitive = root.meshes[0].primitives[0];
            if (primitive.mode != 4)
                throw new InvalidDataException($"{assetPath} is not a triangle-list mesh.");
            if (primitive.attributes == null || primitive.attributes.POSITION < 0)
                throw new InvalidDataException($"{assetPath} has no POSITION accessor.");

            var vertices = ReadVector3(root, binary, primitive.attributes.POSITION, true);
            var normals = primitive.attributes.NORMAL >= 0
                ? ReadVector3(root, binary, primitive.attributes.NORMAL, false)
                : null;
            var indices = ReadIndices(root, binary, primitive.indices);

            // The files are authored Z-up in millimeters. Swapping Y/Z changes handedness,
            // so triangle winding is reversed at the same time.
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var tmp = indices[i + 1];
                indices[i + 1] = indices[i + 2];
                indices[i + 2] = tmp;
            }

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
            if (existing != null)
                AssetDatabase.DeleteAsset(meshAssetPath);

            var mesh = new Mesh
            {
                name = Path.GetFileNameWithoutExtension(assetPath),
                indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(indices, 0, true);
            if (normals != null && normals.Length == vertices.Length)
                mesh.SetNormals(normals);
            else
                mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false);
            AssetDatabase.CreateAsset(mesh, meshAssetPath);
            return mesh;
        }

        static Vector3[] ReadVector3(GltfRoot root, byte[] binary, int accessorIndex, bool convertUnits)
        {
            var accessor = root.accessors[accessorIndex];
            if (accessor.componentType != 5126 || accessor.type != "VEC3")
                throw new InvalidDataException("Helmet vector accessors must be float32 VEC3.");
            var view = root.bufferViews[accessor.bufferView];
            var stride = view.byteStride > 0 ? view.byteStride : 12;
            var start = view.byteOffset + accessor.byteOffset;
            var values = new Vector3[accessor.count];
            var scale = convertUnits ? MillimetersToMeters : 1f;
            for (var i = 0; i < values.Length; ++i)
            {
                var offset = start + i * stride;
                var x = BitConverter.ToSingle(binary, offset);
                var y = BitConverter.ToSingle(binary, offset + 4);
                var z = BitConverter.ToSingle(binary, offset + 8);
                values[i] = new Vector3(x, z, y) * scale;
                if (!convertUnits)
                    values[i].Normalize();
            }
            return values;
        }

        static int[] ReadIndices(GltfRoot root, byte[] binary, int accessorIndex)
        {
            var accessor = root.accessors[accessorIndex];
            if (accessor.type != "SCALAR")
                throw new InvalidDataException("Helmet index accessor must be SCALAR.");
            var view = root.bufferViews[accessor.bufferView];
            var componentSize = accessor.componentType == 5121 ? 1 : accessor.componentType == 5123 ? 2 : accessor.componentType == 5125 ? 4 : 0;
            if (componentSize == 0)
                throw new InvalidDataException($"Unsupported GLB index component type {accessor.componentType}.");
            var stride = view.byteStride > 0 ? view.byteStride : componentSize;
            var start = view.byteOffset + accessor.byteOffset;
            var values = new int[accessor.count];
            for (var i = 0; i < values.Length; ++i)
            {
                var offset = start + i * stride;
                values[i] = accessor.componentType switch
                {
                    5121 => binary[offset],
                    5123 => BitConverter.ToUInt16(binary, offset),
                    5125 => checked((int)BitConverter.ToUInt32(binary, offset)),
                    _ => throw new InvalidDataException()
                };
            }
            return values;
        }

        static void ReadGlb(string path, out string json, out byte[] binary)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != GlbMagic)
                throw new InvalidDataException($"{path} is not a GLB file.");
            var version = reader.ReadUInt32();
            if (version != 2)
                throw new InvalidDataException($"Only GLB 2.0 is supported, found {version}.");
            var declaredLength = reader.ReadUInt32();
            if (declaredLength != stream.Length)
                throw new InvalidDataException("GLB declared length does not match file length.");

            json = null;
            binary = null;
            while (stream.Position < stream.Length)
            {
                var length = reader.ReadUInt32();
                var type = reader.ReadUInt32();
                var data = reader.ReadBytes(checked((int)length));
                if (type == JsonChunk)
                    json = Encoding.UTF8.GetString(data).TrimEnd('\0', ' ', '\r', '\n', '\t');
                else if (type == BinChunk)
                    binary = data;
            }
            if (string.IsNullOrWhiteSpace(json) || binary == null)
                throw new InvalidDataException("GLB is missing its JSON or binary chunk.");
        }

        public static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var value in hash)
                builder.Append(value.ToString("X2"));
            return builder.ToString();
        }
    }
}
