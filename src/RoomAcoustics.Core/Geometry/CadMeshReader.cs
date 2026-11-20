using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using RoomAcoustics.Core;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;

namespace RoomAcoustics.Core;

/// <summary>
/// Reads the 3-D surfaces of a CAD drawing, DXF or DWG: <c>3DFACE</c> entities, which is how most CAD and BIM tools
/// write a 3-D model as plain faces (AutoCAD <c>3DFACE</c>, and the faces of a mesh exploded to faces). The layer of each
/// face is its surface group, so put walls, floor, ceiling and glazing on separate layers to give each its own finish.
/// </summary>
/// <remarks>
/// <para>
/// A three-dimensional <c>MESH</c>, <c>POLYFACE</c> or ACIS <c>3DSOLID</c> entity is not read; <c>EXPLODE</c> it first, which
/// turns it into 3DFACEs. Drawing units are taken as metres unless <c>scale</c> says otherwise (0.001 for millimetres).
/// Entities inside blocks (inserted drawings) are not followed: <c>EXPLODE</c> or bind them.
/// </para>
/// <para>
/// DWG is read with the MIT-licensed ACadSharp library, DXF with IxMilia.Dxf. The result reports how many entities of
/// each kind were skipped, so a model that comes back with no faces says why.
/// </para>
/// </remarks>
public static class CadMeshReader
{
    /// <summary>The mesh and a count of the entities that were not read, by type name.</summary>
    public sealed record Result(SurfaceMesh Mesh, IReadOnlyDictionary<string, int> Skipped);

    public static Result ReadFile(string path, double scale = 1, double weldToleranceM = 1e-4)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase)) return ReadDwg(path, scale, weldToleranceM);
        if (extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            return ReadDxf(stream, scale, weldToleranceM);
        }
        throw new NotSupportedException($"'{extension}' is not a DXF or DWG file.");
    }

    public static Result ReadDxf(Stream stream, double scale = 1, double weldToleranceM = 1e-4)
    {
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale), "The scale must be positive.");
        var builder = new SurfaceMeshBuilder(weldToleranceM);
        var skipped = new Dictionary<string, int>();

        foreach (var entity in DxfFile.Load(stream).Entities)
        {
            if (entity is Dxf3DFace face)
            {
                builder.AddQuad(Point(face.FirstCorner, scale), Point(face.SecondCorner, scale),
                    Point(face.ThirdCorner, scale), Point(face.FourthCorner, scale), LayerName(face.Layer));
            }
            else if (IsSolidGeometry(entity.GetType().Name))
            {
                skipped[entity.GetType().Name] = skipped.GetValueOrDefault(entity.GetType().Name) + 1;
            }
        }
        return Finish(builder, skipped);
    }

    public static Result ReadDwg(string path, double scale = 1, double weldToleranceM = 1e-4)
    {
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale), "The scale must be positive.");
        var builder = new SurfaceMeshBuilder(weldToleranceM);
        var skipped = new Dictionary<string, int>();

        var document = DwgReader.Read(path);
        foreach (var entity in document.Entities)
        {
            if (entity is Face3D face)
            {
                builder.AddQuad(Point(face.FirstCorner, scale), Point(face.SecondCorner, scale),
                    Point(face.ThirdCorner, scale), Point(face.FourthCorner, scale), LayerName(entity.Layer.Name));
            }
            else if (IsSolidGeometry(entity.GetType().Name))
            {
                skipped[entity.GetType().Name] = skipped.GetValueOrDefault(entity.GetType().Name) + 1;
            }
        }
        return Finish(builder, skipped);
    }

    private static Result Finish(SurfaceMeshBuilder builder, Dictionary<string, int> skipped)
    {
        if (builder.TriangleCount == 0)
        {
            var detail = skipped.Count == 0
                ? "It holds no 3DFACE entities."
                : "It holds " + string.Join(", ", skipped.Select(s => $"{s.Value} {s.Key}")) + ", which are not read: EXPLODE them to 3DFACEs first.";
            throw new FormatException($"No 3-D faces were found. {detail}");
        }
        return new Result(builder.Build(), skipped);
    }

    /// <summary>Entity types that carry 3-D surfaces but are not read, so they can be reported.</summary>
    private static bool IsSolidGeometry(string typeName) =>
        typeName is "Mesh" or "DxfMesh" or "PolyfaceMesh" or "Solid3D" or "DxfModelerGeometry" or "DxfBody" or "DxfRegion" or "DxfSolid3D" or "Surface" or "Region" or "Body";

    private static string LayerName(string layer) => string.IsNullOrWhiteSpace(layer) ? "0" : layer;

    private static Vec3 Point(DxfPoint p, double scale) => new(p.X * scale, p.Y * scale, p.Z * scale);

    private static Vec3 Point(CSMath.XYZ p, double scale) => new(p.X * scale, p.Y * scale, p.Z * scale);
}
