using System.Text;
using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class ThreeDsTests
{
    /// <summary>A chunk: its id, its length (with the 6 header bytes) and its contents.</summary>
    private static byte[] Chunk(ushort id, params byte[][] contents)
    {
        var body = contents.SelectMany(c => c).ToArray();
        return BitConverter.GetBytes(id).Concat(BitConverter.GetBytes((uint)(body.Length + 6))).Concat(body).ToArray();
    }

    private static byte[] Name(string s) => Encoding.Latin1.GetBytes(s + "\0");
    private static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);

    /// <summary>A w x d x h box as one object: the floor's two faces with the material "Carpet", the rest with none.</summary>
    private static byte[] Box(double w, double d, double h, string objectName = "Room")
    {
        var v = new[] { (0, 0, 0), (w, 0, 0), (w, d, 0), (0, d, 0), (0, 0, h), (w, 0, h), (w, d, h), (0, d, h) };
        var quads = new[] { (0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7) };
        var faces = quads.SelectMany(q => new[] { (q.Item1, q.Item2, q.Item3), (q.Item1, q.Item3, q.Item4) }).ToArray();
        var vertices = U16(v.Length).Concat(v.SelectMany(p => new[] { (float)p.Item1, (float)p.Item2, (float)p.Item3 }.SelectMany(BitConverter.GetBytes))).ToArray();
        var faceList = U16(faces.Length).Concat(faces.SelectMany(f => U16(f.Item1).Concat(U16(f.Item2)).Concat(U16(f.Item3)).Concat(U16(0)))).ToArray();
        var carpet = Chunk(0x4130, Name("Carpet"), U16(2), U16(0), U16(1));
        var mesh = Chunk(0x4100, Chunk(0x4110, vertices), Chunk(0x4120, faceList, carpet));
        return Chunk(0x4D4D, Chunk(0x0002, BitConverter.GetBytes(3u)), Chunk(0x3D3D, Chunk(0x3D3E, BitConverter.GetBytes(3u)), Chunk(0x4000, Name(objectName), mesh)));
    }

    [Fact]
    public void ABox_IsReadWithItsMaterialsAndTheObjectsNameForTheRest()
    {
        var mesh = ThreeDsMeshReader.Read(Box(6, 5, 3));
        Assert.Equal(12, mesh.Triangles.Count);
        Assert.Equal(new[] { "Carpet", "Room" }, mesh.Groups.OrderBy(g => g));
        Assert.Equal(30, mesh.GroupAreasM2()["Carpet"], 6);
        var room = mesh.ToRoom(_ => new SurfaceFinish(AbsorptionMaterial.Presets.HardReflective));
        Assert.Equal(90, room.VolumeM3, 6);
    }

    [Fact]
    public void TheScale_TurnsItsUnitsToMetres()
    {
        var room = ThreeDsMeshReader.Read(Box(6000, 5000, 3000), 0.001).ToRoom(_ => new SurfaceFinish(AbsorptionMaterial.Presets.HardReflective));
        Assert.Equal(90, room.VolumeM3, 3);
    }

    [Fact]
    public void ADamagedFile_SaysSo()
    {
        Assert.Throws<FormatException>(() => ThreeDsMeshReader.Read(Encoding.ASCII.GetBytes("solid not a 3ds file")));
        var cut = Box(6, 5, 3);
        Assert.Throws<FormatException>(() => ThreeDsMeshReader.Read(cut.Take(cut.Length - 20).ToArray()));
    }
}
