using Xbim.Common.Geometry;
using Xbim.Ifc;
using Xbim.Ifc.Extensions;
using Xbim.Ifc4.Interfaces;
using RoomAcoustics.Core;

namespace RoomAcoustics.Core;

/// <summary>One room volume (<c>IfcSpace</c>) of an IFC file: its name (number and long name), its volume, and whether it is an area object rather than a room.</summary>
public sealed record IfcSpaceInfo(string Name, double VolumeM3, bool IsArea);

/// <summary>What was read from an IFC file: the surfaces, the file's spaces (sorted by name), and which of them the surfaces are (null when read from the building's elements).</summary>
public sealed record IfcContent(SurfaceMesh Mesh, IReadOnlyList<IfcSpaceInfo> Spaces, int? Space);

/// <summary>
/// Reads the room from an IFC building model (.ifc, schema 2x3, 4 or 4.3). A BIM model's walls and slabs are separate solids with
/// thickness, which do not make one closed surface, so what is read is the room volume itself: an <c>IfcSpace</c>. The file's spaces are listed and one is
/// chosen; by default the largest that is a room, not an area object (Revit exports its area plans, such as a floor's gross internal area, as spaces whose
/// long name is "Area"). Its faces are grouped by direction: Floor, Ceiling and Walls.
/// </summary>
/// <remarks>
/// <para>
/// A file with no spaces (as SketchUp exports, where the room is drawn as faces) is read as the surfaces of its building elements instead, grouped by the
/// presentation layer they are on (SketchUp's tags), or else by kind (Walls, Slabs, ...). Furniture, openings and services are left out of that.
/// </para>
/// <para>
/// The file's length unit is applied. Shapes drawn as an extruded profile (a rectangle, or a polyline outline), a faceted brep, a triangulated or polygonal
/// face set, a surface model or a mapped (repeated) item are read; a boolean result is read as its first operand, so a wall cut by a roof is read uncut.
/// Curved outlines are not read.
/// </para>
/// </remarks>
public static class IfcMeshReader
{
    // A space's corners are joined when within a millimetre, as a Revit space's faces are drawn to about that; a building's own surfaces (SketchUp
    // furniture, say) have smaller detail than that, so theirs are joined only when within a tenth of a millimetre.
    private const double SpaceWeldM = 1e-3, ElementWeldM = 1e-4;

    public static SurfaceMesh ReadFile(string path) => Read(path).Mesh;

    /// <param name="space">The index into the sorted list of spaces to read, or null for the largest room.</param>
    public static IfcContent Read(string path, int? space = null)
    {
        // never the Windows-only database that large files would otherwise be moved into
        using var model = IfcStore.Open(path, null, -1);
        var perMetre = model.ModelFactors.OneMetre;
        var unsupported = new SortedSet<string>();

        var spaces = new List<(IfcSpaceInfo Info, List<Vec3[]> Triangles)>();
        foreach (var s in model.Instances.OfType<IIfcSpace>())
        {
            var triangles = new List<Vec3[]>();
            var placement = s.ObjectPlacement?.ToMatrix3D() ?? XbimMatrix3D.Identity;
            foreach (var item in s.Representation?.Representations.SelectMany(r => r.Items) ?? Enumerable.Empty<IIfcRepresentationItem>())
                if (!AddItem(item, placement, perMetre, triangles)) unsupported.Add(item.GetType().Name.TrimStart('I'));
            if (triangles.Count == 0) continue;
            // a closed shell encloses (sum of the signed tetrahedra to the origin)
            var volume = Math.Abs(triangles.Sum(t => t[0].Dot(t[1].Cross(t[2]))) / 6);
            var name = string.Join(" ", new[] { s.Name?.ToString(), s.LongName?.ToString() }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
            var isArea = string.Equals(s.LongName?.ToString(), "Area", StringComparison.OrdinalIgnoreCase);
            spaces.Add((new IfcSpaceInfo(name.Length > 0 ? name : $"Space {spaces.Count + 1}", volume, isArea), triangles));
        }

        if (spaces.Count > 0)
        {
            spaces.Sort((a, b) => string.Compare(a.Info.Name, b.Info.Name, StringComparison.OrdinalIgnoreCase));
            int chosen;
            if (space is { } wanted)
            {
                if (wanted < 0 || wanted >= spaces.Count) throw new FormatException($"The file has {spaces.Count} spaces; there is no space {wanted}.");
                chosen = wanted;
            }
            else
            {
                var rooms = spaces.Where(s => !s.Info.IsArea).ToList();
                var largest = (rooms.Count > 0 ? rooms : spaces).MaxBy(s => s.Info.VolumeM3);
                chosen = spaces.IndexOf(largest);
            }
            var shift = Offset(spaces.SelectMany(s => s.Triangles));
            var triangles = SplitAtCorners(spaces[chosen].Triangles.Select(t => t.Select(p => p - shift).ToArray()).ToList()).Select(r => r.Triangle).ToList();
            var middle = triangles.SelectMany(t => t).Aggregate(Vec3.Zero, (sum, p) => sum + p) * (1.0 / (triangles.Count * 3));
            var builder = new SurfaceMeshBuilder(SpaceWeldM);
            foreach (var t in triangles) builder.AddTriangle(t[0], t[1], t[2], Direction(t, middle));
            return new IfcContent(builder.Build(), spaces.Select(s => s.Info).ToList(), chosen);
        }

        var building = Elements(model, perMetre);
        if (building is not null) return new IfcContent(building, Array.Empty<IfcSpaceInfo>(), null);

        if (model.Instances.OfType<IIfcSpace>().Any())
            throw new FormatException("The spaces' shapes could not be read" + (unsupported.Count > 0 ? $" (they are drawn as {string.Join(", ", unsupported)})" : "") + ". Export the room as OBJ or COLLADA instead.");
        var furniture = model.Instances.OfType<IIfcFurnishingElement>().Count();
        throw new FormatException(furniture > 0
            ? $"The file holds no rooms (IfcSpace) and no walls, floors or other building elements: only furniture ({furniture} IfcFurnishingElement). Export the architectural model, with its spaces."
            : "The file has no IfcSpace (room volume) objects and no building elements whose shapes can be read. Export the model with its spaces, or export the room itself as OBJ or COLLADA.");
    }

    /// <summary>The surfaces of the building's elements (not furniture, openings or services), grouped by layer or kind, or null when there are none.</summary>
    private static SurfaceMesh? Elements(Xbim.Common.IModel model, double perMetre)
    {
        var found = new List<(Vec3[] Triangle, string Group)>();
        foreach (var element in model.Instances.OfType<IIfcElement>())
        {
            if (element is IIfcFeatureElement or IIfcFurnishingElement or IIfcDistributionElement or IIfcVirtualElement || element.Representation is null) continue;
            var placement = element.ObjectPlacement?.ToMatrix3D() ?? XbimMatrix3D.Identity;
            foreach (var representation in element.Representation.Representations)
            {
                if (representation.RepresentationIdentifier?.ToString() is "Axis" or "FootPrint" or "Box" or "Annotation" or "Profile" or "Clearance" or "CoG" or "Lighting") continue;
                foreach (var item in representation.Items)
                {
                    var triangles = new List<Vec3[]>();
                    AddItem(item, placement, perMetre, triangles);
                    if (triangles.Count == 0) continue;
                    var group = Layer(item, representation) ?? Kind(element);
                    found.AddRange(triangles.Select(t => (t, group)));
                }
            }
        }
        if (found.Count == 0) return null;
        var shift = Offset(found.Select(f => f.Triangle));
        var builder = new SurfaceMeshBuilder(ElementWeldM);
        foreach (var (t, group) in found) builder.AddTriangle(t[0] - shift, t[1] - shift, t[2] - shift, group);
        return builder.Build();
    }

    /// <summary>
    /// The triangles with each edge split where a corner of another triangle lies along it (a T-junction, within the tolerance), as a space drawn as a brep
    /// often has: a wall face running the full length of an edge where the ceiling beside it is in two faces. Then every edge is shared by two faces. Each
    /// piece comes with the index of the triangle it was cut from.
    /// </summary>
    private static List<(Vec3[] Triangle, int From)> SplitAtCorners(IReadOnlyList<Vec3[]> triangles, double toleranceM = 2e-3)
    {
        // the corners in a grid of half-metre cells, so each edge looks only at those near it
        const double cell = 0.5;
        (long, long, long) Cell(Vec3 p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
        var grid = new Dictionary<(long, long, long), List<Vec3>>();
        foreach (var p in triangles.SelectMany(t => t).Distinct())
        {
            if (!grid.TryGetValue(Cell(p), out var list)) grid[Cell(p)] = list = new List<Vec3>();
            list.Add(p);
        }
        IEnumerable<Vec3> Near(Vec3 a, Vec3 b)
        {
            var low = Cell(new Vec3(Math.Min(a.X, b.X) - toleranceM, Math.Min(a.Y, b.Y) - toleranceM, Math.Min(a.Z, b.Z) - toleranceM));
            var high = Cell(new Vec3(Math.Max(a.X, b.X) + toleranceM, Math.Max(a.Y, b.Y) + toleranceM, Math.Max(a.Z, b.Z) + toleranceM));
            for (var x = low.Item1; x <= high.Item1; x++)
                for (var y = low.Item2; y <= high.Item2; y++)
                    for (var z = low.Item3; z <= high.Item3; z++)
                        if (grid.TryGetValue((x, y, z), out var list))
                            foreach (var p in list) yield return p;
        }

        var result = new List<(Vec3[], int)>();
        for (var n = 0; n < triangles.Count; n++)
        {
            var t = triangles[n];
            var outline = new List<Vec3>();
            var split = false;
            for (var e = 0; e < 3; e++)
            {
                Vec3 a = t[e], b = t[(e + 1) % 3], along = b - a;
                var length2 = along.Dot(along);
                outline.Add(a);
                if (length2 < 1e-12) continue;
                var length = Math.Sqrt(length2);
                // a corner clear of the edge's own ends, and of the one before it, so no sliver is made
                var last = a;
                foreach (var c in Near(a, b).Select(p => (Point: p, At: (p - a).Dot(along) / length2))
                    .Where(c => c.At * length > toleranceM && (1 - c.At) * length > toleranceM && (c.Point - (a + along * c.At)).Length < toleranceM)
                    .OrderBy(c => c.At))
                    if ((c.Point - last).Length > toleranceM) { outline.Add(c.Point); last = c.Point; split = true; }
            }
            // the triangle's own winding is kept, as the ear clipping follows the outline's
            if (!split) result.Add((t, n));
            else foreach (var piece in Polygon.Triangulate(outline)) result.Add((piece, n));
        }
        // with no sliver left where three corners came to lie in a line
        return result.Where(r => (r.Item1[1] - r.Item1[0]).Cross(r.Item1[2] - r.Item1[0]).Length > 1e-9).ToList();
    }

    /// <summary>
    /// How far to move a model drawn in site coordinates (a Revit model placed on the national grid, hundreds of kilometres from the origin) so that it starts
    /// near the origin: its lowest x and y, in whole metres, when they are more than a kilometre out; otherwise nothing. Heights are kept.
    /// </summary>
    private static Vec3 Offset(IEnumerable<Vec3[]> triangles)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        foreach (var p in triangles.SelectMany(t => t)) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); }
        return Math.Abs(minX) > 1000 || Math.Abs(minY) > 1000 ? new Vec3(Math.Floor(minX), Math.Floor(minY), 0) : Vec3.Zero;
    }

    /// <summary>The presentation layer (a SketchUp tag, a CAD layer) of a shape item, or of its representation.</summary>
    private static string? Layer(IIfcRepresentationItem item, IIfcRepresentation representation)
    {
        var layer = item.LayerAssignment.Concat(representation.LayerAssignments).Select(l => l.Name.ToString()).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        return string.IsNullOrWhiteSpace(layer) ? null : layer.Trim();
    }

    private static string Kind(IIfcElement element) => element switch
    {
        IIfcWall => "Walls",
        IIfcSlab => "Slabs",
        IIfcRoof => "Roofs",
        IIfcCovering => "Coverings",
        IIfcDoor => "Doors",
        IIfcWindow => "Windows",
        IIfcCurtainWall or IIfcPlate or IIfcMember => "Curtain walls",
        IIfcStair or IIfcStairFlight or IIfcRamp or IIfcRampFlight => "Stairs",
        IIfcRailing => "Railings",
        IIfcColumn => "Columns",
        IIfcBeam => "Beams",
        _ => "Other elements",
    };

    /// <summary>Floor, Ceiling or Walls, from which way the triangle faces and where it lies; the sign of its normal does not matter, as the shapes' winding differs.</summary>
    private static string Direction(Vec3[] t, Vec3 middle)
    {
        var normal = (t[1] - t[0]).Cross(t[2] - t[0]).Normalized();
        return Math.Abs(normal.Z) < 0.7 ? "Walls" : t[0].Z < middle.Z ? "Floor" : "Ceiling";
    }

    /// <summary>Adds the triangles of one shape item, in metres, and says whether it was a kind that is read.</summary>
    private static bool AddItem(IIfcRepresentationItem item, XbimMatrix3D placement, double perMetre, List<Vec3[]> triangles)
    {
        Vec3 Place(XbimMatrix3D m, double x, double y, double z)
        {
            var p = m.Transform(new XbimPoint3D(x, y, z));
            return new Vec3(p.X / perMetre, p.Y / perMetre, p.Z / perMetre);
        }

        void AddFace(IIfcFace face, XbimMatrix3D m)
        {
            List<Vec3>? Loop(IIfcFaceBound bound) => bound.Bound is IIfcPolyLoop loop ? loop.Polygon.Select(p => Place(m, p.X, p.Y, p.Z)).ToList() : null;
            var bounds = face.Bounds.ToList();
            var outer = bounds.OfType<IIfcFaceOuterBound>().FirstOrDefault() ?? bounds.FirstOrDefault();
            if (outer is null || Loop(outer) is not { } outline) return;
            var holes = bounds.Where(b => b != outer).Select(Loop).OfType<List<Vec3>>().ToList();
            triangles.AddRange(Polygon.Triangulate(outline, holes));
        }

        switch (item)
        {
            case IIfcExtrudedAreaSolid solid:
            {
                var outline = Outline(solid.SweptArea);
                if (outline is null) return false;
                var m = (solid.Position?.ToMatrix3D() ?? XbimMatrix3D.Identity) * placement;
                var up = (double)solid.Depth;
                var d = solid.ExtrudedDirection;
                var bottom = outline.Select(p => Place(m, p.X, p.Y, 0)).ToList();
                var top = outline.Select(p => Place(m, p.X + d.X * up, p.Y + d.Y * up, d.Z * up)).ToList();
                // the bottom turned over, so that all the faces wind the same way round the solid and its volume can be summed
                foreach (var t in Polygon.Triangulate(bottom)) triangles.Add(new[] { t[0], t[2], t[1] });
                foreach (var t in Polygon.Triangulate(top)) triangles.Add(t);
                for (var i = 0; i < outline.Count; i++)
                {
                    var j = (i + 1) % outline.Count;
                    triangles.Add(new[] { bottom[i], bottom[j], top[j] });
                    triangles.Add(new[] { bottom[i], top[j], top[i] });
                }
                return true;
            }
            case IIfcFacetedBrep brep:
                foreach (var face in brep.Outer.CfsFaces) AddFace(face, placement);
                return true;
            case IIfcShellBasedSurfaceModel shells:
                foreach (var shell in shells.SbsmBoundary.OfType<IIfcConnectedFaceSet>())
                    foreach (var face in shell.CfsFaces) AddFace(face, placement);
                return true;
            case IIfcFaceBasedSurfaceModel faces:
                foreach (var set in faces.FbsmFaces)
                    foreach (var face in set.CfsFaces) AddFace(face, placement);
                return true;
            case IIfcTriangulatedFaceSet set:
            {
                var points = set.Coordinates.CoordList.Select(c => c.Select(v => (double)v).ToArray()).ToList();
                foreach (var index in set.CoordIndex.Select(c => c.Select(v => (int)v - 1).ToArray()))
                    triangles.Add(index.Select(i => Place(placement, points[i][0], points[i][1], points[i][2])).ToArray());
                return true;
            }
            case IIfcPolygonalFaceSet set:
            {
                var points = set.Coordinates.CoordList.Select(c => c.Select(v => (double)v).ToArray()).ToList();
                var pn = set.PnIndex.Select(v => (int)v - 1).ToList();
                List<Vec3> Ring(IEnumerable<Xbim.Ifc4.MeasureResource.IfcPositiveInteger> indices) => indices.Select(v =>
                {
                    var i = (int)v - 1;
                    var p = points[pn.Count > 0 ? pn[i] : i];
                    return Place(placement, p[0], p[1], p[2]);
                }).ToList();
                foreach (var face in set.Faces)
                {
                    var holes = face is IIfcIndexedPolygonalFaceWithVoids voids ? voids.InnerCoordIndices.Select(Ring).ToList() : new List<List<Vec3>>();
                    triangles.AddRange(Polygon.Triangulate(Ring(face.CoordIndex), holes));
                }
                return true;
            }
            case IIfcMappedItem mapped:
            {
                // the shared shape, moved from its own origin to where this item puts it
                var origin = mapped.MappingSource.MappingOrigin switch
                {
                    IIfcAxis2Placement3D p3 => p3.ToMatrix3D(),
                    IIfcAxis2Placement2D p2 => p2.ToMatrix3D(),
                    _ => XbimMatrix3D.Identity,
                };
                var m = origin * mapped.MappingTarget.ToMatrix3D() * placement;
                var read = false;
                foreach (var inner in mapped.MappingSource.MappedRepresentation.Items) read |= AddItem(inner, m, perMetre, triangles);
                return read;
            }
            case IIfcBooleanResult boolean when boolean.FirstOperand is IIfcRepresentationItem first:
                return AddItem(first, placement, perMetre, triangles);
            default:
                return false;
        }
    }

    /// <summary>The outline of an extruded profile in its own plane (a rectangle or a polyline, without the repeated last point), or null for a kind not read.</summary>
    private static List<(double X, double Y)>? Outline(IIfcProfileDef profile)
    {
        switch (profile)
        {
            case IIfcRectangleProfileDef rectangle:
            {
                double hx = rectangle.XDim / 2, hy = rectangle.YDim / 2;
                var m = rectangle.Position?.ToMatrix3D() ?? XbimMatrix3D.Identity;
                return new[] { (-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy) }
                    .Select(c => { var p = m.Transform(new XbimPoint3D(c.Item1, c.Item2, 0)); return (p.X, p.Y); }).ToList();
            }
            case IIfcArbitraryClosedProfileDef closed:
            {
                IEnumerable<(double X, double Y)>? points = closed.OuterCurve switch
                {
                    IIfcPolyline line => line.Points.Select(p => (p.X, p.Y)),
                    IIfcIndexedPolyCurve { Segments: null or { Count: 0 } } curve when curve.Points is IIfcCartesianPointList2D list => list.CoordList.Select(c => ((double)c.ElementAt(0), (double)c.ElementAt(1))),
                    _ => null,
                };
                var outline = points?.ToList();
                if (outline is { Count: > 1 } && outline[0] == outline[^1]) outline.RemoveAt(outline.Count - 1);
                return outline is { Count: >= 3 } ? outline : null;
            }
            default:
                return null;
        }
    }

}
