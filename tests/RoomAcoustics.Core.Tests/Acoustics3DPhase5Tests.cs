using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using RoomAcoustics.Core;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>Footprints and surfaces shared by the Phase 5 tests.</summary>
internal static class Phase5Fixtures
{
    public static SurfaceFinish Finish(double alpha, double? scatter = null) =>
        new(new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)alpha, 8).ToArray()),
            scatter is { } s ? OctaveSpectrum.Flat(s) : null);

    /// <summary>An L-shaped room: a 4 x 1 m arm along x and a 1 x 4 m arm along y, meeting at the origin corner.</summary>
    public static readonly (double X, double Y)[] LFootprint =
        { (0.0, 0.0), (4.0, 0.0), (4.0, 1.0), (1.0, 1.0), (1.0, 4.0), (0.0, 4.0) };

    public static Room3D LRoom(SurfaceFinish finish, double height = 3) =>
        Room3D.Extrude(LFootprint, height, finish, finish, finish);

    /// <summary>The six faces of a box as quads: corners in order around each face.</summary>
    public static IEnumerable<Vec3[]> BoxQuads(double l, double w, double h)
    {
        yield return new[] { new Vec3(0, 0, 0), new Vec3(l, 0, 0), new Vec3(l, w, 0), new Vec3(0, w, 0) };
        yield return new[] { new Vec3(0, 0, h), new Vec3(l, 0, h), new Vec3(l, w, h), new Vec3(0, w, h) };
        yield return new[] { new Vec3(0, 0, 0), new Vec3(l, 0, 0), new Vec3(l, 0, h), new Vec3(0, 0, h) };
        yield return new[] { new Vec3(0, w, 0), new Vec3(l, w, 0), new Vec3(l, w, h), new Vec3(0, w, h) };
        yield return new[] { new Vec3(0, 0, 0), new Vec3(0, w, 0), new Vec3(0, w, h), new Vec3(0, 0, h) };
        yield return new[] { new Vec3(l, 0, 0), new Vec3(l, w, 0), new Vec3(l, w, h), new Vec3(l, 0, h) };
    }

    /// <summary>The L room as OBJ text: a floor and ceiling of four triangles each, and six wall quads.</summary>
    public static string LRoomObj(double height)
    {
        var text = new StringBuilder();
        foreach (var (x, y) in LFootprint) text.AppendLine(FormattableString.Invariant($"v {x} {y} 0"));
        foreach (var (x, y) in LFootprint) text.AppendLine(FormattableString.Invariant($"v {x} {y} {height}"));
        int[][] floor = { new[] { 1, 2, 3 }, new[] { 1, 3, 4 }, new[] { 1, 4, 5 }, new[] { 1, 5, 6 } };
        text.AppendLine("usemtl Floor");
        foreach (var t in floor) text.AppendLine($"f {t[0]} {t[1]} {t[2]}");
        text.AppendLine("usemtl Ceiling");
        foreach (var t in floor) text.AppendLine($"f {t[0] + 6} {t[2] + 6} {t[1] + 6}");
        text.AppendLine("usemtl Wall");
        for (var i = 0; i < 6; i++)
        {
            var j = (i + 1) % 6;
            text.AppendLine($"f {i + 1}//1 {j + 1}//1 {j + 7}//1 {i + 7}//1");
        }
        return text.ToString();
    }
}

public class NonConvexRoomTests
{
    private static SurfaceFinish Finish(double alpha, double? scatter = null) => Phase5Fixtures.Finish(alpha, scatter);

    [Fact]
    public void LRoom_ContainsTheArms_AndNotTheNotchOrOutside()
    {
        var room = Phase5Fixtures.LRoom(Finish(0.1));

        Assert.True(room.Contains(new Vec3(2.5, 0.5, 1.5)));   // horizontal arm
        Assert.True(room.Contains(new Vec3(0.5, 3.0, 1.5)));   // vertical arm
        Assert.False(room.Contains(new Vec3(3.0, 3.0, 1.5)));  // the notch, inside the bounding box
        Assert.False(room.Contains(new Vec3(2.5, 0.5, 3.5)));  // above the ceiling
        Assert.False(room.Contains(new Vec3(-1, 0.5, 1.5)));
    }

    [Fact]
    public void LRoom_DistanceToBoundary_IsToTheNearestSurface_NotTheNearestPlane()
    {
        var room = Phase5Fixtures.LRoom(Finish(0.1));

        // Near the inner corner the plane y = 1 is 0.1 m away, but the wall on it starts at x = 1, so the nearest
        // surface is the x = 0 wall at 0.5 m (the inner corner itself is 0.51 m away).
        Assert.Equal(0.5, room.DistanceToBoundary(new Vec3(0.5, 0.9, 1.5)), 9);
        Assert.True(room.DistanceToBoundary(new Vec3(3.0, 3.0, 1.5)) < 0);
    }

    [Fact]
    public void ReceiverGrid_LeavesOutTheNotch()
    {
        var room = Phase5Fixtures.LRoom(Finish(0.1));

        var points = ReceiverGrid.Horizontal(room, 1.5, 1.0, 0.25);

        Assert.Equal(7, points.Count); // 4 along the x arm, 3 more up the y arm
        Assert.All(points, p => Assert.True(room.Contains(p)));
    }

    [Fact]
    public void Constructor_AcceptsEitherWinding_AndGivesTheSameRoom()
    {
        var finish = Finish(0.1);
        var faces = Phase5Fixtures.BoxQuads(10, 7, 3)
            .Select((corners, i) => new PlanarFace($"Face {i}", i % 2 == 0 ? corners : corners.Reverse().ToArray(), finish))
            .ToList();

        var room = new Room3D(faces);

        Assert.Equal(210, room.VolumeM3, 9);
        Assert.True(room.IsConvex);
        Assert.True(room.Contains(new Vec3(5, 3.5, 1.5)));
        Assert.All(room.Faces, f => Assert.True(f.InwardNormal.Dot(new Vec3(5, 3.5, 1.5) - f.Vertices[0]) > 0));
    }

    [Fact]
    public void Constructor_ReportsATJunction()
    {
        var finish = Finish(0.1);
        var faces = Phase5Fixtures.BoxQuads(10, 7, 3).Select((c, i) => new PlanarFace($"Face {i}", c, finish)).ToList();

        // Replace the floor with two halves split at x = 5. The walls still run the full length, so the cut leaves
        // a corner in the middle of each wall's bottom edge: a T-junction.
        faces.RemoveAt(0);
        faces.Add(new PlanarFace("Floor A", new[] { new Vec3(0, 0, 0), new Vec3(5, 0, 0), new Vec3(5, 7, 0), new Vec3(0, 7, 0) }, finish));
        faces.Add(new PlanarFace("Floor B", new[] { new Vec3(5, 0, 0), new Vec3(10, 0, 0), new Vec3(10, 7, 0), new Vec3(5, 7, 0) }, finish));

        var ex = Assert.Throws<ArgumentException>(() => new Room3D(faces));

        Assert.Contains("closed surface", ex.Message);
        Assert.Contains("T-junction", ex.Message);
    }

    [Fact]
    public void Constructor_RejectsTwoSeparateSurfaces()
    {
        var finish = Finish(0.1);
        var faces = Phase5Fixtures.BoxQuads(2, 2, 2).Select((c, i) => new PlanarFace($"A{i}", c, finish)).ToList();
        faces.AddRange(Phase5Fixtures.BoxQuads(1, 1, 1)
            .Select((c, i) => new PlanarFace($"B{i}", c.Select(p => p + new Vec3(10, 0, 0)).ToArray(), finish)));

        var ex = Assert.Throws<ArgumentException>(() => new Room3D(faces));

        Assert.Contains("separate", ex.Message);
    }

    [Fact]
    public void DirectSound_IsBlockedAroundTheCorner()
    {
        var room = Phase5Fixtures.LRoom(Finish(0.2, scatter: 1));
        var source = new Vec3(3.5, 0.5, 1.5);   // in the x arm
        var receiver = new Vec3(0.5, 3.5, 1.5); // in the y arm; the straight line crosses the notch
        Assert.False(room.IsClear(source, receiver));
        Assert.True(room.IsClear(source, new Vec3(1.5, 0.5, 1.5)));

        const double radius = 0.25;
        var options = new RayTraceOptions(RayCount: 40_000, ReceiverRadiusM: radius, MaxTimeS: 0.2, BinWidthS: 0.0005);
        var ir = EnergyRayTracer.Trace(room, source, receiver, options);

        // Nothing can arrive before the shortest path round the inner corner at (1, 1), less the receiver radius.
        var around = (new Vec3(1, 1, 1.5) - source).Length + (receiver - new Vec3(1, 1, 1.5)).Length;
        var earliest = (around - radius) / EnergyRayTracer.SpeedOfSound(20);
        var firstBin = Enumerable.Range(0, ir.Energy[4].Length).First(i => ir.Energy[4][i] > 0);
        Assert.True(firstBin * options.BinWidthS >= earliest - options.BinWidthS,
            $"First energy at {firstBin * options.BinWidthS * 1000:0.0} ms; the shortest path allows {earliest * 1000:0.0} ms.");
    }

    [Fact]
    public void DirectSound_IsPresentWhenTheLineOfSightIsClear()
    {
        var room = Phase5Fixtures.LRoom(Finish(0.2, scatter: 1));
        var source = new Vec3(3.5, 0.5, 1.5);
        var receiver = new Vec3(1.5, 0.5, 1.5);
        var options = new RayTraceOptions(RayCount: 20_000, ReceiverRadiusM: 0.25, MaxTimeS: 0.1, BinWidthS: 0.0005);

        var ir = EnergyRayTracer.Trace(room, source, receiver, options);

        var directBin = (int)(2.0 / EnergyRayTracer.SpeedOfSound(20) / options.BinWidthS);
        Assert.True(ir.Energy[4][directBin] > 0);
    }

    [Fact]
    public void ImageSources_RejectPathsThroughTheNotch()
    {
        var room = Phase5Fixtures.LRoom(Finish(0.1));

        // Same arm: floor, ceiling, and the four walls of the arm all give clear first-order reflections.
        var sameArm = ImageSourceEnumerator.Enumerate(room, new Vec3(3, 0.5, 1.5), new Vec3(3.5, 0.5, 1.5), 1);
        Assert.Equal(6, sameArm.Count);

        // Different arms: each mirror path would cross the notch, or hit the floor outside the L, so none is valid.
        var otherArm = ImageSourceEnumerator.Enumerate(room, new Vec3(0.5, 3.5, 1.5), new Vec3(3.5, 0.5, 1.5), 1);
        Assert.Empty(otherArm);
    }

    [Fact]
    public void DiffuseDecayInAnLRoom_FallsBetweenEyringAndSabine()
    {
        var room = Phase5Fixtures.LRoom(Finish(0.2, scatter: 1), height: 3);
        var surfaces = room.Faces.Select(f => new Surface(f.Name, f.AreaM2, f.Finish.Material)).ToList();
        var eyring = ReverberationCalculator.Calculate(room.VolumeM3, surfaces, new ReverberationOptions(ReverberationMethod.EyringNorris)).T60[3];
        var sabine = ReverberationCalculator.CalculateT60(room.VolumeM3, surfaces)[3];

        var options = new RayTraceOptions(RayCount: 100_000, ReceiverRadiusM: 0.25, MaxTimeS: 2.0, Seed: 5);
        var ir = EnergyRayTracer.Trace(room, new Vec3(3, 0.5, 1.5), new Vec3(0.5, 3, 1.5), options);
        var t30 = RoomAcousticsCalculator.Calculate(ir).T30S[3];

        Assert.InRange(t30, eyring * 0.95, sabine * 1.05);
    }

    [Fact]
    public void HybridMode_AgreesWithPureRayTracing_InAnLRoom()
    {
        var room = Phase5Fixtures.LRoom(Finish(0.2), height: 3);
        var source = new Vec3(3, 0.5, 1.5);
        var receiver = new Vec3(0.5, 3, 1.5);

        double Early(int order)
        {
            var options = new RayTraceOptions(RayCount: 200_000, ReceiverRadiusM: 0.25, MaxTimeS: 0.5, Seed: 3, ImageSourceOrder: order);
            var ir = EnergyRayTracer.Trace(room, source, receiver, options);
            var bins = (int)(0.08 / options.BinWidthS);
            return ir.Energy[4].Take(bins).Sum();
        }

        var pure = Early(0);
        var hybrid = Early(3);

        Assert.InRange(hybrid / pure, 0.9, 1.1);
    }
}

public class MeshImportTests
{
    private static SurfaceFinish Finish(double alpha) => Phase5Fixtures.Finish(alpha);

    private static string BoxObj(double l, double w, double h)
    {
        var text = new StringBuilder();
        text.AppendLine("# a box");
        foreach (var (x, y, z) in new[] { (0, 0, 0), (l, 0, 0), (l, w, 0), (0, w, 0), (0, 0, h), (l, 0, h), (l, w, h), (0, w, h) })
            text.AppendLine(FormattableString.Invariant($"v {x} {y} {z}"));
        text.AppendLine("o Room");
        text.AppendLine("usemtl Floor");
        text.AppendLine("f 1/1/1 2/2/1 3/3/1 4/4/1");
        text.AppendLine("usemtl Ceiling");
        text.AppendLine("f 5 6 7 8");
        text.AppendLine("usemtl Plaster");
        text.AppendLine("f 1 2 6 5");
        text.AppendLine("f 2 3 7 6");
        text.AppendLine("f 3 4 8 7");
        text.AppendLine("f 4 1 5 8");
        return text.ToString();
    }

    private static Stream Utf8(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Obj_ReadsQuadsAndMaterials_AndBuildsARoom()
    {
        var mesh = ObjMeshReader.Read(Utf8(BoxObj(10, 7, 3)));

        Assert.Equal(new[] { "Floor", "Ceiling", "Plaster" }, mesh.Groups);
        Assert.Equal(8, mesh.Vertices.Count);
        Assert.Equal(12, mesh.Triangles.Count);
        Assert.Equal(70, mesh.GroupAreasM2()["Floor"], 9);
        Assert.Equal(2 * (10 + 7) * 3, mesh.GroupAreasM2()["Plaster"], 9);

        var room = mesh.ToRoom(group => Finish(group == "Plaster" ? 0.05 : 0.3));

        Assert.Equal(210, room.VolumeM3, 9);
        Assert.Equal(242, room.TotalAreaM2, 9);
        Assert.Equal(0.3, room.Faces.First(f => f.Name.StartsWith("Floor")).Finish.Material.ToOctaveSpectrum()[3], 9);
        Assert.Equal(0.05, room.Faces.First(f => f.Name.StartsWith("Plaster")).Finish.Material.ToOctaveSpectrum()[3], 9);
    }

    [Fact]
    public void Obj_UnderstandsNegativeIndices_AndScale()
    {
        // The same box in millimetres, with each face's corners as negative (relative) indices.
        var text = new StringBuilder();
        foreach (var (x, y, z) in new[] { (0, 0, 0), (2000, 0, 0), (2000, 2000, 0), (0, 2000, 0), (0, 0, 2000), (2000, 0, 2000), (2000, 2000, 2000), (0, 2000, 2000) })
            text.AppendLine($"v {x} {y} {z}");
        foreach (var quad in new[] { "1 2 3 4", "5 6 7 8", "1 2 6 5", "2 3 7 6", "3 4 8 7", "4 1 5 8" })
            text.AppendLine("f " + string.Join(' ', quad.Split(' ').Select(i => int.Parse(i) - 9)));

        var room = ObjMeshReader.Read(Utf8(text.ToString()), scale: 0.001).ToRoom(_ => Finish(0.1));

        Assert.Equal(8, room.VolumeM3, 9);
    }

    [Fact]
    public void Obj_YUp_IsTurnedToZUp()
    {
        // A box 10 wide (x), 3 tall (y, up in the file), 7 deep (z).
        var text = new StringBuilder();
        foreach (var (x, y, z) in new[] { (0, 0, 0), (10, 0, 0), (10, 0, 7), (0, 0, 7), (0, 3, 0), (10, 3, 0), (10, 3, 7), (0, 3, 7) })
            text.AppendLine($"v {x} {y} {z}");
        foreach (var quad in new[] { "1 2 3 4", "5 6 7 8", "1 2 6 5", "2 3 7 6", "3 4 8 7", "4 1 5 8" })
            text.AppendLine("f " + quad);

        var room = ObjMeshReader.Read(Utf8(text.ToString()), yUp: true).ToRoom(_ => Finish(0.1));

        Assert.Equal(3, room.Faces.SelectMany(f => f.Vertices).Max(v => v.Z), 9);
        Assert.Equal(210, room.VolumeM3, 9);
    }

    [Theory]
    [InlineData("v 0 0\nf 1 1 1", "three coordinates")]
    [InlineData("v 0 0 x\nf 1 1 1", "not a number")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 9", "outside")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2", "at least three")]
    [InlineData("v 0 0 0\nv 1 0 0\nv 0 1 0", "no faces")]
    public void Obj_ReportsBadInput(string text, string expected)
    {
        var ex = Assert.Throws<FormatException>(() => ObjMeshReader.Read(Utf8(text)));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Mesh_WeldsCornersThatAlmostCoincide()
    {
        // The second wall's corners are 0.02 mm off the first's; the default 0.1 mm tolerance joins them.
        var text = BoxObj(4, 4, 3).Replace("v 4 4 0", "v 4.00002 3.99998 0");

        var room = ObjMeshReader.Read(Utf8(text)).ToRoom(_ => Finish(0.1));

        Assert.Equal(48, room.VolumeM3, 3);
    }

    [Fact]
    public void Mesh_WithAMissingFace_ReportsAnOpenSurface()
    {
        var text = BoxObj(4, 4, 3).Replace("f 5 6 7 8\n", "").Replace("f 5 6 7 8\r\n", "");

        var mesh = ObjMeshReader.Read(Utf8(text));
        var ex = Assert.Throws<ArgumentException>(() => mesh.ToRoom(_ => Finish(0.1)));

        Assert.Contains("closed surface", ex.Message);
    }

    [Fact]
    public void Obj_NonConvexRoom_BuildsAndMatchesTheExtrusion()
    {
        var mesh = ObjMeshReader.Read(Utf8(Phase5Fixtures.LRoomObj(3)));

        var room = mesh.ToRoom(group => Finish(group == "Wall" ? 0.1 : 0.3));

        Assert.False(room.IsConvex);
        Assert.Equal(21, room.VolumeM3, 9);
        Assert.True(room.Contains(new Vec3(3, 0.5, 1.5)));
        Assert.False(room.Contains(new Vec3(3, 3, 1.5)));
    }

    private static byte[] BinaryStl(IEnumerable<Vec3[]> quads)
    {
        var triangles = new List<Vec3[]>();
        foreach (var q in quads)
        {
            triangles.Add(new[] { q[0], q[1], q[2] });
            triangles.Add(new[] { q[0], q[2], q[3] });
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[80]);
        writer.Write((uint)triangles.Count);
        foreach (var t in triangles)
        {
            writer.Write(0f); writer.Write(0f); writer.Write(0f);
            foreach (var p in t)
            {
                writer.Write((float)p.X); writer.Write((float)p.Y); writer.Write((float)p.Z);
            }
            writer.Write((ushort)0);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static string AsciiStl(IEnumerable<Vec3[]> quads)
    {
        var text = new StringBuilder("solid box\n");
        foreach (var q in quads)
            foreach (var t in new[] { new[] { q[0], q[1], q[2] }, new[] { q[0], q[2], q[3] } })
            {
                text.AppendLine("facet normal 0 0 0\n outer loop");
                foreach (var p in t) text.AppendLine(FormattableString.Invariant($"  vertex {p.X} {p.Y} {p.Z}"));
                text.AppendLine(" endloop\nendfacet");
            }
        text.AppendLine("endsolid box");
        return text.ToString();
    }

    [Fact]
    public void Stl_Binary_ReadsABox()
    {
        var room = StlMeshReader.Read(BinaryStl(Phase5Fixtures.BoxQuads(10, 7, 3))).ToRoom(_ => Finish(0.1));

        Assert.Equal(210, room.VolumeM3, 4);
    }

    [Fact]
    public void Stl_Ascii_ReadsABox_WithScale()
    {
        var mesh = StlMeshReader.Read(Encoding.ASCII.GetBytes(AsciiStl(Phase5Fixtures.BoxQuads(10000, 7000, 3000))), scale: 0.001);

        Assert.Equal(new[] { "Default" }, mesh.Groups);
        Assert.Equal(210, mesh.ToRoom(_ => Finish(0.1)).VolumeM3, 9);
    }

    [Fact]
    public void Stl_ReportsATruncatedFile()
    {
        Assert.Throws<FormatException>(() => StlMeshReader.Read(Encoding.ASCII.GetBytes("solid x\nvertex 0 0 0\nvertex 1 0 0\n")));
        Assert.Throws<FormatException>(() => StlMeshReader.Read(Encoding.ASCII.GetBytes("solid empty\nendsolid empty\n")));
    }

    [Fact]
    public void Dxf_3DFaces_BecomeSurfacesByLayer()
    {
        var file = new DxfFile();
        var quads = Phase5Fixtures.BoxQuads(10, 7, 3).ToList();
        for (var i = 0; i < quads.Count; i++)
        {
            var q = quads[i];
            file.Entities.Add(new Dxf3DFace { FirstCorner = P(q[0]), SecondCorner = P(q[1]), ThirdCorner = P(q[2]), FourthCorner = P(q[3]), Layer = i == 0 ? "Floor" : "Other" });
        }
        file.Entities.Add(new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(1, 1, 1)));
        using var stream = new MemoryStream();
        file.Save(stream);
        stream.Position = 0;

        var result = CadMeshReader.ReadDxf(stream);

        Assert.Equal(new[] { "Floor", "Other" }, result.Mesh.Groups);
        Assert.Equal(210, result.Mesh.ToRoom(_ => Finish(0.1)).VolumeM3, 9);
    }

    private static DxfPoint P(Vec3 v) => new(v.X, v.Y, v.Z);

    [Fact]
    public void Dxf_WithoutFaces_SaysSo()
    {
        var file = new DxfFile();
        file.Entities.Add(new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(1, 1, 1)));
        using var stream = new MemoryStream();
        file.Save(stream);
        stream.Position = 0;

        var ex = Assert.Throws<FormatException>(() => CadMeshReader.ReadDxf(stream));

        Assert.Contains("3DFACE", ex.Message);
    }

    [Fact]
    public void Dwg_3DFaces_AreReadByLayer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acousticcalc-{Guid.NewGuid():N}.dwg");
        try
        {
            var document = new CadDocument();
            document.Layers.Add(new Layer("Walls"));
            var quads = Phase5Fixtures.BoxQuads(10, 7, 3).ToList();
            for (var i = 0; i < quads.Count; i++)
            {
                var q = quads[i];
                var face = new Face3D
                {
                    FirstCorner = X(q[0]), SecondCorner = X(q[1]), ThirdCorner = X(q[2]), FourthCorner = X(q[3]),
                };
                if (i >= 2) face.Layer = document.Layers["Walls"];
                document.ModelSpace.Entities.Add(face);
            }
            DwgWriter.Write(path, document);

            var result = CadMeshReader.ReadFile(path);

            Assert.Contains("Walls", result.Mesh.Groups);
            Assert.Equal(210, result.Mesh.ToRoom(_ => Finish(0.1)).VolumeM3, 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CSMath.XYZ X(Vec3 v) => new(v.X, v.Y, v.Z);

    [Fact]
    public void Dwg_WithoutFaces_SaysSo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"acousticcalc-{Guid.NewGuid():N}.dwg");
        try
        {
            var document = new CadDocument();
            document.ModelSpace.Entities.Add(new Line { StartPoint = new CSMath.XYZ(0, 0, 0), EndPoint = new CSMath.XYZ(1, 0, 0) });
            DwgWriter.Write(path, document);

            var ex = Assert.Throws<FormatException>(() => CadMeshReader.ReadFile(path));

            Assert.Contains("3DFACE", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CadReader_RejectsOtherExtensions()
    {
        Assert.Throws<NotSupportedException>(() => CadMeshReader.ReadFile("model.skp"));
    }
}
