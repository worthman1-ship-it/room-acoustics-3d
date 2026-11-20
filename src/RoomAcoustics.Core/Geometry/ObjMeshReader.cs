using System.Globalization;
using RoomAcoustics.Core;

namespace RoomAcoustics.Core;

/// <summary>
/// Reads Wavefront OBJ, the plain-text mesh format that SketchUp, Revit, Rhino, Blender and most other modelling tools
/// export. Surface groups come from <c>usemtl</c> (the material) where there is one, otherwise from <c>g</c> or
/// <c>o</c> (the group or object name).
/// </summary>
/// <remarks>
/// Polygons with more than three corners are split into triangles by fanning from the first corner, which is exact
/// for convex polygons. Normals, texture coordinates and smoothing groups are ignored. SketchUp's OBJ export has a
/// "Swap YZ coordinates" option for Y-up; pass <c>yUp: true</c> for files made that way. Units are those of the file,
/// scaled by <c>scale</c> to metres (0.001 for millimetres, 0.0254 for inches).
/// </remarks>
public static class ObjMeshReader
{
    public static SurfaceMesh ReadFile(string path, double scale = 1, bool yUp = false, double weldToleranceM = 1e-4)
    {
        using var reader = File.OpenText(path);
        return Read(reader, scale, yUp, weldToleranceM);
    }

    public static SurfaceMesh Read(Stream stream, double scale = 1, bool yUp = false, double weldToleranceM = 1e-4)
    {
        using var reader = new StreamReader(stream);
        return Read(reader, scale, yUp, weldToleranceM);
    }

    public static SurfaceMesh Read(TextReader reader, double scale = 1, bool yUp = false, double weldToleranceM = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale), "The scale must be positive.");

        var points = new List<Vec3>();
        var builder = new SurfaceMeshBuilder(weldToleranceM);
        string? material = null;
        string? name = null;
        var lineNumber = 0;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var space = line.IndexOfAny(new[] { ' ', '\t' });
            var keyword = space < 0 ? line : line[..space];
            var rest = space < 0 ? "" : line[(space + 1)..].Trim();

            switch (keyword)
            {
                case "v":
                {
                    var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) throw new FormatException($"Line {lineNumber}: a vertex needs three coordinates.");
                    var x = ParseNumber(parts[0], lineNumber) * scale;
                    var y = ParseNumber(parts[1], lineNumber) * scale;
                    var z = ParseNumber(parts[2], lineNumber) * scale;
                    points.Add(yUp ? new Vec3(x, -z, y) : new Vec3(x, y, z));
                    break;
                }
                case "usemtl":
                    material = rest.Length == 0 ? null : rest;
                    break;
                case "g":
                case "o":
                    name = rest.Length == 0 ? null : rest;
                    break;
                case "f":
                {
                    var corners = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .Select(token => ResolveIndex(token, points.Count, lineNumber)).ToList();
                    if (corners.Count < 3) throw new FormatException($"Line {lineNumber}: a face needs at least three corners.");
                    var group = material ?? name ?? "Default";
                    for (var k = 1; k < corners.Count - 1; k++)
                        builder.AddTriangle(points[corners[0]], points[corners[k]], points[corners[k + 1]], group);
                    break;
                }
            }
        }

        if (builder.TriangleCount == 0) throw new FormatException("The file contains no faces.");
        return builder.Build();
    }

    private static double ParseNumber(string text, int lineNumber)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"Line {lineNumber}: '{text}' is not a number.");
        return value;
    }

    /// <summary>A face corner such as "7", "7/2", "7//3" or "7/2/3": the vertex index, 1-based, or negative from the end.</summary>
    private static int ResolveIndex(string token, int vertexCount, int lineNumber)
    {
        var slash = token.IndexOf('/');
        var text = slash < 0 ? token : token[..slash];
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index == 0)
            throw new FormatException($"Line {lineNumber}: '{token}' is not a vertex index.");
        var resolved = index > 0 ? index - 1 : vertexCount + index;
        if (resolved < 0 || resolved >= vertexCount)
            throw new FormatException($"Line {lineNumber}: vertex index {index} is outside the {vertexCount} vertices read so far.");
        return resolved;
    }
}
