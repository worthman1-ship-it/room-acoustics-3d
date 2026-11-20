using System.Globalization;
using System.Text;
using RoomAcoustics.Core;

namespace RoomAcoustics.Core;

/// <summary>
/// Reads STL meshes, binary or ASCII. STL carries no materials, so every triangle goes in one group called
/// "Default"; assign the finish for the whole room, or split the model by exporting parts separately.
/// </summary>
public static class StlMeshReader
{
    public const string GroupName = "Default";

    public static SurfaceMesh ReadFile(string path, double scale = 1, double weldToleranceM = 1e-4) =>
        Read(File.ReadAllBytes(path), scale, weldToleranceM);

    public static SurfaceMesh Read(Stream stream, double scale = 1, double weldToleranceM = 1e-4)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Read(memory.ToArray(), scale, weldToleranceM);
    }

    public static SurfaceMesh Read(byte[] data, double scale = 1, double weldToleranceM = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale), "The scale must be positive.");

        var builder = new SurfaceMeshBuilder(weldToleranceM);
        if (IsBinary(data)) ReadBinary(data, scale, builder);
        else ReadAscii(Encoding.ASCII.GetString(data), scale, builder);

        if (builder.TriangleCount == 0) throw new FormatException("The file contains no triangles.");
        return builder.Build();
    }

    /// <summary>A binary STL is an 80-byte header, a triangle count, and 50 bytes per triangle: the length gives it away.</summary>
    private static bool IsBinary(byte[] data)
    {
        if (data.Length < 84) return false;
        var count = BitConverter.ToUInt32(data, 80);
        return data.Length == 84L + 50L * count;
    }

    private static void ReadBinary(byte[] data, double scale, SurfaceMeshBuilder builder)
    {
        var count = (int)BitConverter.ToUInt32(data, 80);
        for (var i = 0; i < count; i++)
        {
            var offset = 84 + 50 * i + 12; // skip the stored normal
            builder.AddTriangle(Corner(data, offset, scale), Corner(data, offset + 12, scale), Corner(data, offset + 24, scale), GroupName);
        }
    }

    private static Vec3 Corner(byte[] data, int offset, double scale) =>
        new(BitConverter.ToSingle(data, offset) * scale, BitConverter.ToSingle(data, offset + 4) * scale, BitConverter.ToSingle(data, offset + 8) * scale);

    private static void ReadAscii(string text, double scale, SurfaceMeshBuilder builder)
    {
        var corners = new List<Vec3>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                throw new FormatException($"'{line}' is not a vertex line.");
            corners.Add(new Vec3(x * scale, y * scale, z * scale));
            if (corners.Count == 3)
            {
                builder.AddTriangle(corners[0], corners[1], corners[2], GroupName);
                corners.Clear();
            }
        }
        if (corners.Count != 0) throw new FormatException("The file ends part-way through a triangle.");
    }
}
