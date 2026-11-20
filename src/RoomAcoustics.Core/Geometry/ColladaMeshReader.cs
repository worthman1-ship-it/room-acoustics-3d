using System.Globalization;
using System.Xml.Linq;
using RoomAcoustics.Core;

namespace RoomAcoustics.Core;

/// <summary>
/// Reads COLLADA (.dae), the XML mesh format SketchUp exports (File, Export, 3D Model) along with Blender, Revit and others.
/// Surface groups come from the material a polygon is drawn with (the <c>bind_material</c> of the instance), else the geometry's name.
/// </summary>
/// <remarks>
/// The file's own unit (<c>unit meter</c>) and up axis (Z_UP or Y_UP) are applied, so <paramref name="scale"/> is only an extra factor
/// (leave it 1). Nodes are placed by their <c>matrix</c>, which is what SketchUp writes for groups and components; a node placed by
/// separate translate, rotate or scale elements is refused rather than drawn in the wrong place. Polygons with more than three
/// corners are fanned from the first corner, as in <see cref="ObjMeshReader"/>.
/// </remarks>
public static class ColladaMeshReader
{
    public static SurfaceMesh Read(Stream stream, double scale = 1, double weldToleranceM = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale), "The scale must be positive.");

        var doc = XDocument.Load(stream);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        XElement? First(XElement from, string name) => from.Descendants(ns + name).FirstOrDefault();
        var unit = (double?)First(doc.Root!, "unit")?.Attribute("meter") ?? 1;
        var up = (string?)First(doc.Root!, "up_axis")?.Value.Trim() ?? "Y_UP"; // COLLADA's default
        if (up == "X_UP") throw new FormatException("The file's up axis is X; export it with Y or Z up.");
        var factor = unit * scale;

        var byId = doc.Descendants().Where(e => e.Attribute("id") is not null).GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
        XElement Target(string? url, string what) => url is { Length: > 1 } && byId.TryGetValue(url[1..], out var e) ? e : throw new FormatException($"The file refers to a {what} ({url}) that it does not contain.");
        string MaterialName(string target) { var m = Target(target, "material"); return (string?)m.Attribute("name") ?? (string)m.Attribute("id")!; }

        var builder = new SurfaceMeshBuilder(weldToleranceM);
        var scene = First(doc.Root!, "visual_scene") ?? throw new FormatException("The file has no visual scene.");

        void Visit(XElement node, double[] parent, int depth)
        {
            if (depth > 64) throw new FormatException("The file's nodes nest too deeply.");
            if (node.Elements().Any(e => e.Name.LocalName is "translate" or "rotate" or "scale" or "lookat" or "skew"))
                throw new FormatException($"Node '{(string?)node.Attribute("name") ?? (string?)node.Attribute("id")}' is placed by translate, rotate or scale elements, which are not read; export with matrices.");
            var matrix = Multiply(parent, node.Elements(ns + "matrix").Select(m => Numbers(m.Value)).FirstOrDefault() is { Length: 16 } own ? own : Identity);

            foreach (var instance in node.Elements(ns + "instance_geometry"))
            {
                var symbols = instance.Descendants(ns + "instance_material").ToDictionary(m => (string)m.Attribute("symbol")!, m => MaterialName((string)m.Attribute("target")!));
                AddGeometry(Target((string?)instance.Attribute("url"), "geometry"), matrix, symbols);
            }
            foreach (var reference in node.Elements(ns + "instance_node")) Visit(Target((string?)reference.Attribute("url"), "node"), matrix, depth + 1);
            foreach (var child in node.Elements(ns + "node")) Visit(child, matrix, depth + 1);
        }

        void AddGeometry(XElement geometry, double[] m, Dictionary<string, string> symbols)
        {
            var mesh = geometry.Element(ns + "mesh") ?? throw new FormatException($"Geometry '{(string?)geometry.Attribute("id")}' is not a mesh.");
            var name = (string?)geometry.Attribute("name") ?? (string)geometry.Attribute("id")!;
            var sources = mesh.Elements(ns + "source").ToDictionary(s => (string)s.Attribute("id")!, s => Numbers(s.Element(ns + "float_array")?.Value ?? ""));
            var verticesElement = mesh.Element(ns + "vertices");
            var positionSource = (string?)verticesElement?.Elements(ns + "input").FirstOrDefault(i => (string?)i.Attribute("semantic") == "POSITION")?.Attribute("source");
            if (verticesElement is null || positionSource is null || !sources.TryGetValue(positionSource[1..], out var xyz)) throw new FormatException($"Geometry '{name}' has no vertex positions.");
            var verticesId = (string)verticesElement.Attribute("id")!;

            Vec3 Point(int index)
            {
                if (index < 0 || index * 3 + 2 >= xyz.Length) throw new FormatException($"Geometry '{name}' has a vertex index outside its positions.");
                double x = xyz[index * 3], y = xyz[index * 3 + 1], z = xyz[index * 3 + 2];
                double X = m[0] * x + m[1] * y + m[2] * z + m[3], Y = m[4] * x + m[5] * y + m[6] * z + m[7], Z = m[8] * x + m[9] * y + m[10] * z + m[11];
                X *= factor; Y *= factor; Z *= factor;
                return up == "Y_UP" ? new Vec3(X, -Z, Y) : new Vec3(X, Y, Z);
            }

            foreach (var primitive in mesh.Elements().Where(e => e.Name.LocalName is "triangles" or "polylist" or "polygons"))
            {
                var inputs = primitive.Elements(ns + "input").ToList();
                var stride = inputs.Max(i => (int)i.Attribute("offset")!) + 1;
                var vertexOffset = (int)(inputs.FirstOrDefault(i => (string?)i.Attribute("source") == "#" + verticesId)?.Attribute("offset") ?? throw new FormatException($"Geometry '{name}' has polygons without vertices."));
                var symbol = (string?)primitive.Attribute("material");
                var group = symbol is not null && symbols.TryGetValue(symbol, out var material) ? material : symbol ?? name;

                // each polygon as its list of corner vertex indices
                var polygons = new List<int[]>();
                int[] Corners(string text) => Numbers(text).Where((_, k) => k % stride == vertexOffset).Select(v => (int)v).ToArray();
                if (primitive.Name.LocalName == "polygons")
                    polygons.AddRange(primitive.Elements(ns + "p").Select(p => Corners(p.Value)));
                else
                {
                    var all = Corners(primitive.Element(ns + "p")?.Value ?? "");
                    var counts = primitive.Name.LocalName == "triangles" ? Enumerable.Repeat(3, all.Length / 3).ToArray() : Numbers(primitive.Element(ns + "vcount")?.Value ?? "").Select(v => (int)v).ToArray();
                    var at = 0;
                    foreach (var count in counts) { polygons.Add(all[at..(at + count)]); at += count; }
                }
                foreach (var corners in polygons)
                    for (var k = 1; k < corners.Length - 1; k++)
                        builder.AddTriangle(Point(corners[0]), Point(corners[k]), Point(corners[k + 1]), group);
            }
        }

        foreach (var node in scene.Elements(ns + "node")) Visit(node, Identity, 0);
        if (builder.TriangleCount == 0) throw new FormatException("The file contains no faces.");
        return builder.Build();
    }

    private static readonly double[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    private static double[] Numbers(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>Row-major 4x4 product: <paramref name="a"/> applied after <paramref name="b"/>.</summary>
    private static double[] Multiply(double[] a, double[] b)
    {
        var r = new double[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                for (var k = 0; k < 4; k++)
                    r[i * 4 + j] += a[i * 4 + k] * b[k * 4 + j];
        return r;
    }
}
