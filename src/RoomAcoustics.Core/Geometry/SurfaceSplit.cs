namespace RoomAcoustics.Core;

/// <summary>
/// Splits a surface group into its flat surfaces, so that a model drawn all on one layer (SketchUp's export of a model with no materials, or a CAD
/// file on layer 0) can have its floor, ceiling and walls given finishes of their own.
/// </summary>
/// <remarks>
/// A surface is the group's faces that lie in one plane and are joined edge to edge: a wall drawn in several pieces is one surface, two walls in
/// line but apart are two. Each is named for where it lies, after the group: "Group: Floor" or "Group: Ceiling" for a level surface in the bottom
/// or top tenth of the model's height, "Group: Level" for any other level one, "Group: Wall" for an upright one and "Group: Sloped" for the rest,
/// numbered from the largest when there are more ("Group: Wall 1", "Group: Wall 2"). Surfaces under <see cref="SmallM2"/> (the facets of
/// something curved, a step's riser) go together into "Group: Small surfaces", so a curved object does not become hundreds of groups.
/// Keeping the group's name keeps a group named as screens a screen.
/// </remarks>
public static class SurfaceSplit
{
    /// <summary>The smallest surface kept on its own, m².</summary>
    public const double SmallM2 = 0.5;

    /// <summary>How many flat surfaces the group would be split into (counting the small ones together as one).</summary>
    public static int Count(SurfaceMesh mesh, string group) => Surfaces(mesh, group).Names.Distinct().Count();

    /// <summary>The mesh with the group's faces put into a group for each of its flat surfaces; the other groups as they were.</summary>
    public static SurfaceMesh Split(SurfaceMesh mesh, string group)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!mesh.Groups.Contains(group)) throw new ArgumentException($"The model has no surface group \"{group}\".", nameof(group));
        var (triangles, names) = Surfaces(mesh, group);
        // the new groups go where the group was, in order: floor, ceiling, level, walls, sloped (each by number), small surfaces
        var kinds = new[] { "Floor", "Ceiling", "Level", "Wall", "Sloped", "Small surfaces" };
        (int Kind, int Number) Order(string name)
        {
            var kind = name[(group.Length + 2)..];
            var space = kind.LastIndexOf(' ');
            var number = 0;
            var numbered = space > 0 && int.TryParse(kind[(space + 1)..], out number);
            return (Array.IndexOf(kinds, numbered ? kind[..space] : kind), numbered ? number : 0);
        }
        var sorted = Enumerable.Range(0, triangles.Count).OrderBy(k => Order(names[k])).ToList();
        var builder = new SurfaceMeshBuilder();
        for (var i = 0; i < mesh.Triangles.Count; i++)
        {
            var t = mesh.Triangles[i];
            if (t.Group != group) builder.AddTriangle(mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C], t.Group);
            else if (i == triangles[0])
                foreach (var k in sorted)
                {
                    var s = mesh.Triangles[triangles[k]];
                    builder.AddTriangle(mesh.Vertices[s.A], mesh.Vertices[s.B], mesh.Vertices[s.C], names[k]);
                }
        }
        return builder.Build();
    }

    /// <summary>The group's triangles (their places in the mesh, in order) and the name of the surface each goes to.</summary>
    private static (List<int> Triangles, List<string> Names) Surfaces(SurfaceMesh mesh, string group)
    {
        var mine = Enumerable.Range(0, mesh.Triangles.Count).Where(i => mesh.Triangles[i].Group == group).ToList();
        var normals = mine.Select(i => Normal(mesh, mesh.Triangles[i])).ToList();
        var areas = normals.Select(n => n.Length / 2).ToList();

        // faces joined across an edge and lying in one plane are one surface; a face with no area (a sliver) has no plane, and joins a neighbour's
        var byEdge = new Dictionary<(int, int), List<int>>();
        for (var k = 0; k < mine.Count; k++)
            foreach (var e in Edges(mesh.Triangles[mine[k]]))
                (byEdge.TryGetValue(e, out var list) ? list : byEdge[e] = new List<int>()).Add(k);
        var surface = Enumerable.Repeat(-1, mine.Count).ToArray();
        var count = 0;
        for (var start = 0; start < mine.Count; start++)
        {
            if (surface[start] >= 0 || areas[start] < 1e-9) continue;
            var unit = normals[start] * (1 / normals[start].Length);
            surface[start] = count;
            var queue = new Queue<int>(new[] { start });
            while (queue.Count > 0)
            {
                var k = queue.Dequeue();
                foreach (var e in Edges(mesh.Triangles[mine[k]]))
                    foreach (var n in byEdge[e])
                        if (surface[n] < 0 && areas[n] >= 1e-9 && Math.Abs(unit.Dot(normals[n]) / normals[n].Length) > 0.9995) { surface[n] = count; queue.Enqueue(n); }
            }
            count++;
        }
        for (var pass = 0; pass < 3 && surface.Any(s => s < 0); pass++)
            for (var k = 0; k < mine.Count; k++)
                if (surface[k] < 0)
                    surface[k] = Edges(mesh.Triangles[mine[k]]).SelectMany(e => byEdge[e]).Select(n => surface[n]).FirstOrDefault(s => s >= 0, -1);
        for (var k = 0; k < mine.Count; k++) if (surface[k] < 0) surface[k] = count; // a sliver on its own: counted with the small surfaces below

        // each surface's area, kind and name
        var (bottom, top) = (mesh.Vertices.Min(v => v.Z), mesh.Vertices.Max(v => v.Z));
        var height = top - bottom;
        var bySurface = Enumerable.Range(0, mine.Count).ToLookup(k => surface[k]);
        var info = Enumerable.Range(0, count + 1).Select(s =>
        {
            var members = bySurface[s].ToList();
            var area = members.Sum(k => areas[k]);
            if (members.Count == 0 || area < SmallM2) return (Area: area, Kind: "Small surfaces");
            var normal = members.Aggregate(Vec3.Zero, (sum, k) => sum + (normals[k].Dot(normals[members[0]]) < 0 ? normals[k] * -1 : normals[k]));
            var up = Math.Abs(normal.Z) / normal.Length;
            var z = members.Sum(k => areas[k] * Centre(mesh, mesh.Triangles[mine[k]]).Z) / area;
            var kind = up >= 0.98 ? (z <= bottom + 0.1 * height ? "Floor" : z >= top - 0.1 * height ? "Ceiling" : "Level") : up <= 0.1 ? "Wall" : "Sloped";
            return (Area: area, Kind: kind);
        }).ToList();
        var name = new string[count + 1];
        foreach (var kind in info.Select(i => i.Kind).Distinct())
        {
            var ofKind = Enumerable.Range(0, count + 1).Where(s => info[s].Kind == kind).OrderByDescending(s => info[s].Area).ToList();
            for (var r = 0; r < ofKind.Count; r++)
                name[ofKind[r]] = kind == "Small surfaces" || ofKind.Count == 1 ? $"{group}: {kind}" : $"{group}: {kind} {r + 1}";
        }
        return (mine, surface.Select(s => name[s]).ToList());
    }

    private static Vec3 Normal(SurfaceMesh mesh, MeshTriangle t) => (mesh.Vertices[t.B] - mesh.Vertices[t.A]).Cross(mesh.Vertices[t.C] - mesh.Vertices[t.A]);

    private static Vec3 Centre(SurfaceMesh mesh, MeshTriangle t) => (mesh.Vertices[t.A] + mesh.Vertices[t.B] + mesh.Vertices[t.C]) * (1.0 / 3);

    private static IEnumerable<(int, int)> Edges(MeshTriangle t) =>
        new[] { (Math.Min(t.A, t.B), Math.Max(t.A, t.B)), (Math.Min(t.B, t.C), Math.Max(t.B, t.C)), (Math.Min(t.C, t.A), Math.Max(t.C, t.A)) };
}
