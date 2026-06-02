using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
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

    public static class ObjReader
    {
        public static MeshGeometry3D Load(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            {
                return Load(stream);
            }
        }

        public static MeshGeometry3D Load(Stream stream)
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
                        return Load(new ReadOnlySpan<byte>(rented, 0, totalRead));
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

                return Load(new ReadOnlySpan<byte>(buffer, 0, totalBytesRead));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static MeshGeometry3D Load(ReadOnlySpan<byte> fileBytes)
        {
            var tempPositions = new List<Vector3>(8192);
            var tempNormals = new List<Vector3>(8192);
            var tempTexCoords = new List<Vector2>(8192);

            var outPositions = new List<Vector3>(8192);
            var outNormals = new List<Vector3>(8192);
            var outTexCoords = new List<Vector2>(8192);
            var outIndices = new List<int>(16384);

            var vertexCache = new Dictionary<ObjVertexKey, int>(8192);

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
                else if (commandToken.Length == 1 && commandToken[0] == 'f')
                {
                    var faceIndices = new List<int>(4);
                    while (NextToken(line, ref start, out var vToken) == 0)
                    {
                        faceIndices.Add(ParseVertex(vToken, tempPositions, tempTexCoords, tempNormals,
                                                    outPositions, outTexCoords, outNormals, vertexCache));
                    }

                    // Triangulate face using a triangle fan
                    for (int i = 1; i < faceIndices.Count - 1; i++)
                    {
                        outIndices.Add(faceIndices[0]);
                        outIndices.Add(faceIndices[i]);
                        outIndices.Add(faceIndices[i + 1]);
                    }
                }
            }

            var mesh = new MeshGeometry3D
            {
                Positions = outPositions.ToArray(),
                TriangleIndices = outIndices.ToArray()
            };

            if (outNormals.Count == outPositions.Count) mesh.Normals = outNormals.ToArray();
            else mesh.Normals = mesh.GetNormalsOrCompute();

            if (outTexCoords.Count == outPositions.Count) mesh.TextureCoordinates = outTexCoords.ToArray();

            return mesh;
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

            // Map standard OBJ 1-based or negative indices to 0-based
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
