using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Globalization;
using Microsoft.UI.Xaml.Media.Media3D;

namespace Microsoft.UI.Xaml.Media.Media3D
{
    public struct ObjVertexKey : IEquatable<ObjVertexKey>
    {
        public int PositionIndex;
        public int TextureIndex;
        public int NormalIndex;

        public ObjVertexKey(int positionIndex, int textureIndex, int normalIndex)
        {
            PositionIndex = positionIndex;
            TextureIndex = textureIndex;
            NormalIndex = normalIndex;
        }

        public bool Equals(ObjVertexKey other) =>
            PositionIndex == other.PositionIndex &&
            TextureIndex == other.TextureIndex &&
            NormalIndex == other.NormalIndex;

        public override bool Equals(object? obj) =>
            obj is ObjVertexKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(PositionIndex, TextureIndex, NormalIndex);
    }

    public class LoadedObjModel
    {
        public List<ObjPart> Parts { get; } = new();
    }

    public class ObjPart
    {
        public string Name { get; set; } = "Part";
        public string MaterialName { get; set; } = "Default";
        public MeshGeometry3D Geometry { get; set; } = new();
        public Vector4 Color { get; set; } = new Vector4(0.70f, 0.70f, 0.72f, 1.0f);
        public float Opacity { get; set; } = 1.0f;
    }

    public static class ObjReader
    {
        private class PartBuilder
        {
            public string MaterialName { get; }
            public List<int> FaceIndices { get; } = new(4096);
            public List<Vector3> OutPositions { get; } = new(2048);
            public List<Vector3> OutNormals { get; } = new(2048);
            public List<Vector2> OutTexCoords { get; } = new(2048);
            public Dictionary<ObjVertexKey, int> VertexCache { get; } = new(2048);

            public PartBuilder(string materialName)
            {
                MaterialName = materialName;
            }
        }

        public static MeshGeometry3D Load(string filePath)
        {
            var model = LoadObj(filePath);
            return MergeParts(model);
        }

        public static MeshGeometry3D Load(Stream stream)
        {
            var model = LoadObj(stream, null);
            return MergeParts(model);
        }

        public static MeshGeometry3D Load(ReadOnlySpan<byte> fileBytes)
        {
            var model = LoadObj(fileBytes, null);
            return MergeParts(model);
        }

        public static LoadedObjModel LoadObj(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            {
                return LoadObj(stream, Path.GetDirectoryName(filePath));
            }
        }

        public static LoadedObjModel LoadObj(Stream stream, string? directory)
        {
            if (stream.CanSeek)
            {
                long length = stream.Length - stream.Position;
                if (length > 0)
                {
                    byte[] rented = ArrayPool<byte>.Shared.Rent((int)length);
                    try
                    {
                        int totalRead = 0;
                        while (totalRead < length)
                        {
                            int read = stream.Read(rented, totalRead, (int)length - totalRead);
                            if (read <= 0) break;
                            totalRead += read;
                        }
                        return LoadObj(new ReadOnlySpan<byte>(rented, 0, totalRead), directory);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }

            // Fallback for non-seekable streams
            byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
            int totalBytesRead = 0;
            try
            {
                while (true)
                {
                    if (totalBytesRead == buffer.Length)
                    {
                        byte[] newBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        Buffer.BlockCopy(buffer, 0, newBuffer, 0, totalBytesRead);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = newBuffer;
                    }

                    int read = stream.Read(buffer, totalBytesRead, buffer.Length - totalBytesRead);
                    if (read <= 0) break;
                    totalBytesRead += read;
                }

                return LoadObj(new ReadOnlySpan<byte>(buffer, 0, totalBytesRead), directory);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static LoadedObjModel LoadObj(ReadOnlySpan<byte> fileBytes, string? directory)
        {
            var tempPositions = new List<Vector3>(8192);
            var tempNormals = new List<Vector3>(8192);
            var tempTexCoords = new List<Vector2>(8192);

            var partBuilders = new Dictionary<string, PartBuilder>(StringComparer.OrdinalIgnoreCase);
            var materials = new Dictionary<string, (Vector4 Color, float Opacity)>(StringComparer.OrdinalIgnoreCase);
            string activeMaterial = "Default";

            int startOffset = 0;
            while (startOffset < fileBytes.Length)
            {
                // Find next newline
                int endOffset = startOffset;
                while (endOffset < fileBytes.Length && fileBytes[endOffset] != '\n' && fileBytes[endOffset] != '\r')
                {
                    endOffset++;
                }

                ReadOnlySpan<byte> line = fileBytes.Slice(startOffset, endOffset - startOffset);

                // Move startOffset past the newline characters
                startOffset = endOffset;
                while (startOffset < fileBytes.Length && (fileBytes[startOffset] == '\n' || fileBytes[startOffset] == '\r'))
                {
                    startOffset++;
                }

                line = Trim(line);
                if (line.IsEmpty || line[0] == '#') continue;

                int start = 0;
                if (NextToken(line, ref start, out var commandToken) < 0) continue;

                if (commandToken.Length == 1 && commandToken[0] == 'v')
                {
                    if (NextToken(line, ref start, out var xToken) == 0 &&
                        NextToken(line, ref start, out var yToken) == 0 &&
                        NextToken(line, ref start, out var zToken) == 0)
                    {
                        Utf8Parser.TryParse(xToken, out float x, out _);
                        Utf8Parser.TryParse(yToken, out float y, out _);
                        Utf8Parser.TryParse(zToken, out float z, out _);
                        tempPositions.Add(new Vector3(x, y, z));
                    }
                }
                else if (commandToken.Length == 2 && commandToken[0] == 'v' && commandToken[1] == 't')
                {
                    if (NextToken(line, ref start, out var uToken) == 0 &&
                        NextToken(line, ref start, out var vToken) == 0)
                    {
                        Utf8Parser.TryParse(uToken, out float u, out _);
                        Utf8Parser.TryParse(vToken, out float v, out _);
                        tempTexCoords.Add(new Vector2(u, 1.0f - v)); // Flip V for WebGPU standard
                    }
                }
                else if (commandToken.Length == 2 && commandToken[0] == 'v' && commandToken[1] == 'n')
                {
                    if (NextToken(line, ref start, out var nxToken) == 0 &&
                        NextToken(line, ref start, out var nyToken) == 0 &&
                        NextToken(line, ref start, out var nzToken) == 0)
                    {
                        Utf8Parser.TryParse(nxToken, out float nx, out _);
                        Utf8Parser.TryParse(nyToken, out float ny, out _);
                        Utf8Parser.TryParse(nzToken, out float nz, out _);
                        tempNormals.Add(new Vector3(nx, ny, nz));
                    }
                }
                else if (commandToken.Length == 6 &&
                         commandToken[0] == 'm' && commandToken[1] == 't' && commandToken[2] == 'l' &&
                         commandToken[3] == 'l' && commandToken[4] == 'i' && commandToken[5] == 'b')
                {
                    if (NextToken(line, ref start, out var mtlToken) == 0)
                    {
                        var mtlName = System.Text.Encoding.UTF8.GetString(mtlToken);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            var mtlPath = Path.Combine(directory, mtlName);
                            var loaded = LoadMtl(mtlPath);
                            foreach (var pair in loaded)
                            {
                                materials[pair.Key] = pair.Value;
                            }
                        }
                    }
                }
                else if (commandToken.Length == 6 &&
                         commandToken[0] == 'u' && commandToken[1] == 's' && commandToken[2] == 'e' &&
                         commandToken[3] == 'm' && commandToken[4] == 't' && commandToken[5] == 'l')
                {
                    if (NextToken(line, ref start, out var mtlToken) == 0)
                    {
                        activeMaterial = System.Text.Encoding.UTF8.GetString(mtlToken);
                    }
                }
                else if (commandToken.Length == 1 && commandToken[0] == 'f')
                {
                    if (!partBuilders.TryGetValue(activeMaterial, out var builder))
                    {
                        builder = new PartBuilder(activeMaterial);
                        partBuilders[activeMaterial] = builder;
                    }

                    var faceIndices = new List<int>(4);
                    while (NextToken(line, ref start, out var vToken) == 0)
                    {
                        faceIndices.Add(ParseVertex(vToken, tempPositions, tempTexCoords, tempNormals,
                                                    builder.OutPositions, builder.OutTexCoords, builder.OutNormals, builder.VertexCache));
                    }

                    // Triangulate face using a triangle fan
                    for (int i = 1; i < faceIndices.Count - 1; i++)
                    {
                        builder.FaceIndices.Add(faceIndices[0]);
                        builder.FaceIndices.Add(faceIndices[i]);
                        builder.FaceIndices.Add(faceIndices[i + 1]);
                    }
                }
            }

            var model = new LoadedObjModel();

            foreach (var builder in partBuilders.Values)
            {
                if (builder.FaceIndices.Count == 0) continue;

                var mesh = new MeshGeometry3D
                {
                    Positions = builder.OutPositions.ToArray(),
                    TriangleIndices = builder.FaceIndices.ToArray()
                };

                if (builder.OutNormals.Count == builder.OutPositions.Count) mesh.Normals = builder.OutNormals.ToArray();
                else mesh.Normals = mesh.GetNormalsOrCompute();

                if (builder.OutTexCoords.Count == builder.OutPositions.Count) mesh.TextureCoordinates = builder.OutTexCoords.ToArray();

                Vector4 materialColor = new Vector4(0.70f, 0.70f, 0.72f, 1.0f);
                float opacity = 1.0f;

                if (materials.TryGetValue(builder.MaterialName, out var matInfo))
                {
                    materialColor = matInfo.Color;
                    opacity = matInfo.Opacity;
                }

                model.Parts.Add(new ObjPart
                {
                    Name = builder.MaterialName,
                    MaterialName = builder.MaterialName,
                    Geometry = mesh,
                    Color = materialColor,
                    Opacity = opacity
                });
            }

            // Fallback for models without faces but containing vertices
            if (model.Parts.Count == 0 && tempPositions.Count > 0)
            {
                var mesh = new MeshGeometry3D
                {
                    Positions = tempPositions.ToArray(),
                    TriangleIndices = Array.Empty<int>(),
                    Normals = tempNormals.ToArray(),
                    TextureCoordinates = tempTexCoords.ToArray()
                };
                model.Parts.Add(new ObjPart
                {
                    Name = "Default",
                    Geometry = mesh
                });
            }

            return model;
        }

        private static Dictionary<string, (Vector4 Color, float Opacity)> LoadMtl(string filePath)
        {
            var materials = new Dictionary<string, (Vector4 Color, float Opacity)>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(filePath)) return materials;

            try
            {
                var lines = File.ReadAllLines(filePath);
                string? currentMaterial = null;
                Vector4 color = new Vector4(0.70f, 0.70f, 0.72f, 1.0f);
                float opacity = 1.0f;

                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    if (parts[0].Equals("newmtl", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                    {
                        if (currentMaterial != null)
                        {
                            materials[currentMaterial] = (color, opacity);
                        }
                        currentMaterial = parts[1];
                        color = new Vector4(0.70f, 0.70f, 0.72f, 1.0f);
                        opacity = 1.0f;
                    }
                    else if (parts[0].Equals("Kd", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
                    {
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float r);
                        float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float g);
                        float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float b);
                        color = new Vector4(r, g, b, 1.0f);
                    }
                    else if ((parts[0].Equals("d", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("Tr", StringComparison.OrdinalIgnoreCase)) && parts.Length >= 2)
                    {
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float dVal);
                        opacity = dVal;
                    }
                }

                if (currentMaterial != null)
                {
                    materials[currentMaterial] = (color, opacity);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ObjReader] Failed to parse MTL file '{filePath}': {ex.Message}");
            }

            return materials;
        }

        public static MeshGeometry3D MergeParts(LoadedObjModel model)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var texCoords = new List<Vector2>();
            var indices = new List<int>();

            foreach (var part in model.Parts)
            {
                int indexOffset = positions.Count;
                positions.AddRange(part.Geometry.Positions);
                if (part.Geometry.Normals != null) normals.AddRange(part.Geometry.Normals);
                if (part.Geometry.TextureCoordinates != null) texCoords.AddRange(part.Geometry.TextureCoordinates);

                foreach (var idx in part.Geometry.TriangleIndices)
                {
                    indices.Add(idx + indexOffset);
                }
            }

            var merged = new MeshGeometry3D
            {
                Positions = positions.ToArray(),
                TriangleIndices = indices.ToArray()
            };

            if (normals.Count == positions.Count) merged.Normals = normals.ToArray();
            else merged.Normals = merged.GetNormalsOrCompute();

            if (texCoords.Count == positions.Count) merged.TextureCoordinates = texCoords.ToArray();

            return merged;
        }

        private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> span)
        {
            int start = 0;
            while (start < span.Length && IsWhiteSpace(span[start]))
            {
                start++;
            }

            int end = span.Length - 1;
            while (end >= start && IsWhiteSpace(span[end]))
            {
                end--;
            }

            return span.Slice(start, end - start + 1);
        }

        private static int NextToken(ReadOnlySpan<byte> span, ref int start, out ReadOnlySpan<byte> token)
        {
            while (start < span.Length && IsWhiteSpace(span[start]))
            {
                start++;
            }

            if (start >= span.Length)
            {
                token = ReadOnlySpan<byte>.Empty;
                return -1;
            }

            int end = start;
            while (end < span.Length && !IsWhiteSpace(span[end]))
            {
                end++;
            }

            token = span.Slice(start, end - start);
            start = end;
            return 0;
        }

        private static bool IsWhiteSpace(byte b)
        {
            return b == ' ' || b == '\t' || b == '\r' || b == '\n';
        }

        private static int ParseVertex(
            ReadOnlySpan<byte> token,
            List<Vector3> tempPositions,
            List<Vector2> tempTexCoords,
            List<Vector3> tempNormals,
            List<Vector3> outPositions,
            List<Vector2> outTexCoords,
            List<Vector3> outNormals,
            Dictionary<ObjVertexKey, int> vertexCache)
        {
            int slash1 = token.IndexOf((byte)'/');
            int rawV = 0;
            int rawVt = 0;
            int rawVn = 0;

            if (slash1 == -1)
            {
                Utf8Parser.TryParse(token, out rawV, out _);
            }
            else
            {
                Utf8Parser.TryParse(token.Slice(0, slash1), out rawV, out _);

                var rest = token.Slice(slash1 + 1);
                int slash2 = rest.IndexOf((byte)'/');
                if (slash2 == -1)
                {
                    Utf8Parser.TryParse(rest, out rawVt, out _);
                }
                else
                {
                    var vtSpan = rest.Slice(0, slash2);
                    if (!vtSpan.IsEmpty)
                    {
                        Utf8Parser.TryParse(vtSpan, out rawVt, out _);
                    }

                    var vnSpan = rest.Slice(slash2 + 1);
                    if (!vnSpan.IsEmpty)
                    {
                        Utf8Parser.TryParse(vnSpan, out rawVn, out _);
                    }
                }
            }

            int posIndex = rawV < 0 ? tempPositions.Count + rawV : rawV - 1;
            int texIndex = rawVt == 0 ? -1 : (rawVt < 0 ? tempTexCoords.Count + rawVt : rawVt - 1);
            int normIndex = rawVn == 0 ? -1 : (rawVn < 0 ? tempNormals.Count + rawVn : rawVn - 1);

            if (posIndex < 0 || posIndex >= tempPositions.Count) return 0;

            var key = new ObjVertexKey(posIndex, texIndex, normIndex);

            if (vertexCache.TryGetValue(key, out int cachedIndex))
            {
                return cachedIndex;
            }

            int newIndex = outPositions.Count;
            outPositions.Add(tempPositions[posIndex]);
            outTexCoords.Add(texIndex >= 0 && texIndex < tempTexCoords.Count ? tempTexCoords[texIndex] : Vector2.Zero);
            outNormals.Add(normIndex >= 0 && normIndex < tempNormals.Count ? tempNormals[normIndex] : Vector3.UnitY);

            vertexCache[key] = newIndex;
            return newIndex;
        }
    }
}
