using System.Text;

namespace RoomAcoustics.Core;

/// <summary>
/// Reads 3D Studio (.3ds) meshes. The file is a tree of chunks, each a 2-byte id and a 4-byte length that counts its own 6 header bytes: the main chunk
/// (0x4D4D) holds the editor chunk (0x3D3D), which holds an object (0x4000, its name first) for each mesh; a triangle mesh (0x4100) has its vertices
/// (0x4110: a count and x, y, z as 4-byte floats) and its faces (0x4120: a count and three vertex indices and a flags word each), and the faces chunk
/// ends with a material list (0x4130) for each material used: the material's name, a count and the faces that have it. Vertices are stored where they
/// are in the scene, z up (3ds Max's own axes), so the local axes (0x4160) are not needed. A face goes in the group of its material, or of its object when
/// it has none. The file has no units: <paramref name="scale"/> is metres per unit, as for the other formats.
/// </summary>
public static class ThreeDsMeshReader
{
    private const ushort Main = 0x4D4D, Editor = 0x3D3D, Object = 0x4000, TriangleMesh = 0x4100, Vertices = 0x4110, Faces = 0x4120, MaterialList = 0x4130;

    public static SurfaceMesh Read(Stream stream, double scale = 1, double weldToleranceM = 1e-4)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Read(memory.ToArray(), scale, weldToleranceM);
    }

    public static SurfaceMesh Read(byte[] data, double scale = 1, double weldToleranceM = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!(scale > 0) || !double.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(scale), "The scale must be positive.");
        if (data.Length < 6 || BitConverter.ToUInt16(data, 0) != Main) throw new FormatException("This is not a 3D Studio file (it does not start with the main chunk).");

        var builder = new SurfaceMeshBuilder(weldToleranceM);
        foreach (var (id, start, end) in Children(data, 6, ChunkEnd(data, 0)))
            if (id == Editor)
                foreach (var (objectId, objectStart, objectEnd) in Children(data, start, end))
                    if (objectId == Object) ReadObject(data, objectStart, objectEnd, scale, builder);

        if (builder.TriangleCount == 0) throw new FormatException("The file contains no triangle meshes.");
        return builder.Build();
    }

    private static void ReadObject(byte[] data, int start, int end, double scale, SurfaceMeshBuilder builder)
    {
        var name = CString(data, start, end, out var next);
        foreach (var (id, meshStart, meshEnd) in Children(data, next, end))
        {
            if (id != TriangleMesh) continue;
            Vec3[] points = Array.Empty<Vec3>();
            (int A, int B, int C)[] faces = Array.Empty<(int, int, int)>();
            var groupOf = new Dictionary<int, string>();
            foreach (var (part, partStart, partEnd) in Children(data, meshStart, meshEnd))
            {
                if (part == Vertices)
                {
                    var count = Count(data, partStart, partEnd, 12);
                    points = Enumerable.Range(0, count).Select(i => partStart + 2 + 12 * i)
                        .Select(o => new Vec3(BitConverter.ToSingle(data, o) * scale, BitConverter.ToSingle(data, o + 4) * scale, BitConverter.ToSingle(data, o + 8) * scale)).ToArray();
                }
                else if (part == Faces)
                {
                    var count = Count(data, partStart, partEnd, 8);
                    faces = Enumerable.Range(0, count).Select(i => partStart + 2 + 8 * i)
                        .Select(o => ((int)BitConverter.ToUInt16(data, o), (int)BitConverter.ToUInt16(data, o + 2), (int)BitConverter.ToUInt16(data, o + 4))).ToArray();
                    // after the faces come the material lists
                    foreach (var (list, listStart, listEnd) in Children(data, partStart + 2 + 8 * count, partEnd))
                    {
                        if (list != MaterialList) continue;
                        var material = CString(data, listStart, listEnd, out var at);
                        var used = Count(data, at, listEnd, 2);
                        for (var k = 0; k < used; k++) groupOf[BitConverter.ToUInt16(data, at + 2 + 2 * k)] = material;
                    }
                }
            }
            for (var f = 0; f < faces.Length; f++)
            {
                var (a, b, c) = faces[f];
                if (a >= points.Length || b >= points.Length || c >= points.Length) throw new FormatException($"Object '{name}' has a face with a vertex it does not have.");
                builder.AddTriangle(points[a], points[b], points[c], groupOf.GetValueOrDefault(f, name));
            }
        }
    }

    /// <summary>The chunks one after another from <paramref name="start"/> to <paramref name="end"/>: each one's id and where its contents start and end.</summary>
    private static IEnumerable<(ushort Id, int Start, int End)> Children(byte[] data, int start, int end)
    {
        for (var at = start; at + 6 <= end;)
        {
            var chunkEnd = ChunkEnd(data, at);
            if (chunkEnd > end || chunkEnd <= at) throw new FormatException("A chunk runs past the end of the one it is in: the file is damaged or cut short.");
            yield return (BitConverter.ToUInt16(data, at), at + 6, chunkEnd);
            at = chunkEnd;
        }
    }

    private static int ChunkEnd(byte[] data, int at)
    {
        var length = BitConverter.ToUInt32(data, at + 2);
        return length < 6 || at + (long)length > data.Length ? throw new FormatException("A chunk's length is wrong: the file is damaged or cut short.") : at + (int)length;
    }

    /// <summary>A 2-byte count at <paramref name="at"/>, checked against the room its items of <paramref name="size"/> bytes take before <paramref name="end"/>.</summary>
    private static int Count(byte[] data, int at, int end, int size)
    {
        if (at + 2 > end) throw new FormatException("A list is cut short.");
        var count = BitConverter.ToUInt16(data, at);
        return at + 2 + count * size > end ? throw new FormatException("A list is longer than its chunk.") : count;
    }

    private static string CString(byte[] data, int start, int end, out int next)
    {
        var zero = Array.IndexOf(data, (byte)0, start, end - start);
        if (zero < 0) throw new FormatException("A name is not ended.");
        next = zero + 1;
        var name = Encoding.Latin1.GetString(data, start, zero - start).Trim();
        return name.Length == 0 ? "Default" : name;
    }
}
