using System.Text;
using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>IFC (building models) and COLLADA (SketchUp's export): each is read into a closed room of the right size, in metres, z up.</summary>
public class ColladaImportTests
{
    /// <summary>A box as six quads, each face its own material, inside a node placed by <paramref name="matrix"/>; sizes in the file's unit.</summary>
    private static MemoryStream Box(double a, double b, double c, string up, double unitMetres, string matrix = "1 0 0 0 0 1 0 0 0 0 1 0 0 0 0 1") =>
        new(Encoding.UTF8.GetBytes($$"""
        <?xml version="1.0"?>
        <COLLADA xmlns="http://www.collada.org/2005/11/COLLADASchema" version="1.4.1">
          <asset><unit name="unit" meter="{{unitMetres}}"/><up_axis>{{up}}</up_axis></asset>
          <library_materials>
            <material id="m_floor" name="Carpet"/><material id="m_wall" name="Plaster"/><material id="m_ceiling" name="Tile"/>
          </library_materials>
          <library_geometries>
            <geometry id="box" name="Box"><mesh>
              <source id="pos"><float_array id="pos-a" count="24">0 0 0  {{a}} 0 0  {{a}} {{b}} 0  0 {{b}} 0  0 0 {{c}}  {{a}} 0 {{c}}  {{a}} {{b}} {{c}}  0 {{b}} {{c}}</float_array>
                <technique_common><accessor source="#pos-a" count="8" stride="3"/></technique_common></source>
              <vertices id="verts"><input semantic="POSITION" source="#pos"/></vertices>
              <polylist material="floor" count="1"><input semantic="VERTEX" source="#verts" offset="0"/><vcount>4</vcount><p>0 3 2 1</p></polylist>
              <polylist material="ceiling" count="1"><input semantic="VERTEX" source="#verts" offset="0"/><vcount>4</vcount><p>4 5 6 7</p></polylist>
              <polylist material="wall" count="4"><input semantic="VERTEX" source="#verts" offset="0"/><vcount>4 4 4 4</vcount><p>0 1 5 4  1 2 6 5  2 3 7 6  3 0 4 7</p></polylist>
            </mesh></geometry>
          </library_geometries>
          <library_visual_scenes><visual_scene id="scene">
            <node id="n" name="Room"><matrix>{{matrix}}</matrix>
              <instance_geometry url="#box"><bind_material><technique_common>
                <instance_material symbol="floor" target="#m_floor"/><instance_material symbol="ceiling" target="#m_ceiling"/><instance_material symbol="wall" target="#m_wall"/>
              </technique_common></bind_material></instance_geometry>
            </node>
          </visual_scene></library_visual_scenes>
        </COLLADA>
        """));

    [Fact]
    public void ABoxInInches_IsAClosedRoomInMetres_WithOneGroupPerMaterial()
    {
        // 10 m x 6 m x 3 m, written in units of 0.5 m
        var mesh = ColladaMeshReader.Read(Box(20, 12, 6, "Z_UP", 0.5));

        Assert.Equal(new[] { "Carpet", "Tile", "Plaster" }, mesh.Groups);
        var areas = mesh.GroupAreasM2();
        Assert.Equal(60, areas["Carpet"], 6);
        Assert.Equal(60, areas["Tile"], 6);
        Assert.Equal(96, areas["Plaster"], 6);
        Assert.Equal(180, mesh.ToRoom(_ => Phase5Fixtures.Finish(0.1)).VolumeM3, 6);
    }

    [Fact]
    public void AYUpFile_HasItsHeightOnTheZAxis_AndTheNodeMatrixMovesIt()
    {
        // y is up in the file: 10 wide, 3 high, 6 deep; the matrix lifts it 2 units along y
        var mesh = ColladaMeshReader.Read(Box(10, 3, 6, "Y_UP", 1, "1 0 0 0  0 1 0 2  0 0 1 0  0 0 0 1"));

        Assert.Equal(2, mesh.Vertices.Min(v => v.Z), 9);
        Assert.Equal(5, mesh.Vertices.Max(v => v.Z), 9);
        Assert.Equal(6, mesh.Vertices.Max(v => v.Y) - mesh.Vertices.Min(v => v.Y), 9);
        Assert.Equal(180, mesh.ToRoom(_ => Phase5Fixtures.Finish(0.1)).VolumeM3, 6);
    }

    [Fact]
    public void ANodePlacedByRotate_IsRefusedRatherThanDrawnInTheWrongPlace()
    {
        var text = Encoding.UTF8.GetString(Box(1, 1, 1, "Z_UP", 1).ToArray()).Replace("<matrix>1 0 0 0 0 1 0 0 0 0 1 0 0 0 0 1</matrix>", "<rotate>0 0 1 90</rotate>");

        Assert.Throws<FormatException>(() => ColladaMeshReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(text))));
    }
}

public class IfcImportTests
{
    private const string Header = """
        ISO-10303-21;
        HEADER;
        FILE_DESCRIPTION(('ViewDefinition [ReferenceView]'),'2;1');
        FILE_NAME('room.ifc','2026-01-01T00:00:00',(''),(''),'','','');
        FILE_SCHEMA(('IFC4'));
        ENDSEC;
        DATA;
        #1=IFCPROJECT('0YvctVUKr0kugbFTf53O9L',$,'P',$,$,$,$,(#20),#10);
        #10=IFCUNITASSIGNMENT((#11));
        #20=IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-05,#21,$);
        #21=IFCAXIS2PLACEMENT3D(#22,$,$);
        #22=IFCCARTESIANPOINT((0.,0.,0.));
        #9=IFCDIRECTION((0.,0.,1.));
        """;
    private const string Footer = "ENDSEC;\nEND-ISO-10303-21;\n";
    private const string Metres = "#11=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);\n";
    private const string Millimetres = "#11=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);\n";

    private static SurfaceMesh Read(string unit, string body) => ReadContent(unit, body).Mesh;

    private static IfcContent ReadContent(string unit, string body, int? space = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}.ifc");
        try
        {
            File.WriteAllText(path, Header + "\n" + unit + body + Footer);
            return IfcMeshReader.Read(path, space);
        }
        finally { File.Delete(path); }
    }

    /// <summary>An element of the given IFC class at the origin, its shape the item <paramref name="item"/>, on the presentation layer <paramref name="layer"/> if given.</summary>
    private static string Element(int id, string ifcClass, string item, string? layer = null) => $"""
        #{id}2=IFCAXIS2PLACEMENT3D(#22,$,$);
        #{id}3=IFCLOCALPLACEMENT($,#{id}2);
        #{id}4=IFCPRODUCTDEFINITIONSHAPE($,$,(#{id}5));
        #{id}5=IFCSHAPEREPRESENTATION(#20,'Body','Tessellation',({item}));
        #{id}0={ifcClass}('2b9Z1X8xT1B8bnRvlaZx{id:00}',$,'E{id}',$,$,#{id}3,#{id}4,$,$);
        {(layer is null ? "" : $"#{id}6=IFCPRESENTATIONLAYERASSIGNMENT('{layer}',$,(#{id}5),$);")}

        """;

    /// <summary>A space named <paramref name="name"/> at (x, y, z) whose shape is the extruded item <paramref name="item"/>.</summary>
    private static string Space(int id, string name, double x, double y, double z, string item, string? longName = null) => $"""
        #{id}1=IFCCARTESIANPOINT(({x:0.0###},{y:0.0###},{z:0.0###}));
        #{id}2=IFCAXIS2PLACEMENT3D(#{id}1,$,$);
        #{id}3=IFCLOCALPLACEMENT($,#{id}2);
        #{id}4=IFCPRODUCTDEFINITIONSHAPE($,$,(#{id}5));
        #{id}5=IFCSHAPEREPRESENTATION(#20,'Body','SweptSolid',({item}));
        #{id}0=IFCSPACE('1b9Z1X8xT1B8bnRvlaZx{id:00}',$,'{name}',$,$,#{id}3,#{id}4,{(longName is null ? "$" : $"'{longName}'")},.ELEMENT.,.INTERNAL.,$);

        """;

    private static string Rectangle(int id, double width, double depth, double height) => $"""
        #{id}6=IFCCARTESIANPOINT(({width / 2:0.0###},{depth / 2:0.0###}));
        #{id}7=IFCAXIS2PLACEMENT2D(#{id}6,$);
        #{id}8=IFCRECTANGLEPROFILEDEF(.AREA.,$,#{id}7,{width:0.0###},{depth:0.0###});
        #{id}9=IFCEXTRUDEDAREASOLID(#{id}8,#21,#9,{height:0.0###});

        """;

    private static double Volume(SurfaceMesh mesh) => mesh.ToRoom(_ => Phase5Fixtures.Finish(0.1)).VolumeM3;

    [Fact]
    public void AnExtrudedRectangleSpace_IsAClosedRoom_PlacedAndSplitIntoFloorCeilingAndWalls()
    {
        var mesh = Read(Metres, Rectangle(5, 10, 6, 3) + Space(3, "Hall", 2, 1, 0.5, "#59"));

        var areas = mesh.GroupAreasM2();
        Assert.Equal(60, areas["Floor"], 6);
        Assert.Equal(60, areas["Ceiling"], 6);
        Assert.Equal(96, areas["Walls"], 6);
        Assert.Equal(180, Volume(mesh), 6);
        // the profile's corner is the space's origin: x 2 to 12, y 1 to 7, z 0.5 to 3.5
        Assert.Equal((2.0, 12.0), (mesh.Vertices.Min(v => v.X), mesh.Vertices.Max(v => v.X)));
        Assert.Equal((1.0, 7.0), (mesh.Vertices.Min(v => v.Y), mesh.Vertices.Max(v => v.Y)));
        Assert.Equal((0.5, 3.5), (mesh.Vertices.Min(v => v.Z), mesh.Vertices.Max(v => v.Z)));
    }

    [Fact]
    public void AFileInMillimetres_IsReadInMetres()
    {
        var mesh = Read(Millimetres, Rectangle(5, 10000, 6000, 3000) + Space(3, "Hall", 0, 0, 0, "#59"));

        Assert.Equal(180, Volume(mesh), 6);
    }

    [Fact]
    public void AnLShapedOutline_IsCoveredExactly()
    {
        const string outline = """
            #61=IFCCARTESIANPOINT((0.,0.)); #62=IFCCARTESIANPOINT((6.,0.)); #63=IFCCARTESIANPOINT((6.,2.));
            #64=IFCCARTESIANPOINT((2.,2.)); #65=IFCCARTESIANPOINT((2.,5.)); #66=IFCCARTESIANPOINT((0.,5.));
            #67=IFCPOLYLINE((#61,#62,#63,#64,#65,#66,#61));
            #68=IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,$,#67);
            #69=IFCEXTRUDEDAREASOLID(#68,#21,#9,3.);

            """;
        var mesh = Read(Metres, outline + Space(3, "L", 0, 0, 0, "#69"));

        Assert.Equal(18, mesh.GroupAreasM2()["Floor"], 6);
        Assert.Equal(54, Volume(mesh), 6);
    }

    [Fact]
    public void ATriangulatedFaceSetSpace_IsRead()
    {
        const string cube = """
            #71=IFCCARTESIANPOINTLIST3D(((0.,0.,0.),(4.,0.,0.),(4.,4.,0.),(0.,4.,0.),(0.,0.,4.),(4.,0.,4.),(4.,4.,4.),(0.,4.,4.)));
            #72=IFCTRIANGULATEDFACESET(#71,$,.T.,((1,3,2),(1,4,3),(5,6,7),(5,7,8),(1,2,6),(1,6,5),(2,3,7),(2,7,6),(3,4,8),(3,8,7),(4,1,5),(4,5,8)),$);

            """;
        var mesh = Read(Metres, cube + Space(3, "Cube", 0, 0, 0, "#72"));

        Assert.Equal(64, Volume(mesh), 6);
        Assert.Equal(16, mesh.GroupAreasM2()["Floor"], 6);
    }

    [Fact]
    public void WithSeveralSpaces_TheLargestIsRead()
    {
        var mesh = Read(Metres, Rectangle(5, 10, 6, 3) + Rectangle(7, 2, 2, 2) + Space(3, "Hall", 0, 0, 0, "#59") + Space(4, "Closet", 20, 0, 0, "#79"));

        Assert.Equal(180, Volume(mesh), 6);
    }

    [Fact]
    public void TheSpacesAreListedByName_AndAnAreaObjectIsNotTakenForTheRoom()
    {
        // Revit exports a floor's gross internal area as a space whose long name is "Area": larger than any room, but not one
        var content = ReadContent(Metres, Rectangle(5, 10, 6, 3) + Rectangle(7, 2, 2, 2) + Rectangle(8, 40, 40, 3)
            + Space(3, "02 Hall", 0, 0, 0, "#59") + Space(4, "01 Closet", 20, 0, 0, "#79") + Space(6, "GIA 00", 0, 0, 0, "#89", "Area"));

        Assert.Equal(new[] { "01 Closet", "02 Hall", "GIA 00 Area" }, content.Spaces.Select(s => s.Name));
        Assert.Equal(new[] { 8.0, 180, 4800 }, content.Spaces.Select(s => Math.Round(s.VolumeM3, 6)));
        Assert.True(content.Spaces[2].IsArea);
        Assert.Equal(1, content.Space);
        Assert.Equal(180, Volume(content.Mesh), 6);
    }

    [Fact]
    public void AnotherSpace_IsReadWhenAskedFor()
    {
        var body = Rectangle(5, 10, 6, 3) + Rectangle(7, 2, 2, 2) + Space(3, "Hall", 0, 0, 0, "#59") + Space(4, "Closet", 20, 0, 0, "#79");

        var closet = ReadContent(Metres, body, space: 0);
        Assert.Equal(0, closet.Space);
        Assert.Equal(8, Volume(closet.Mesh), 6);
        Assert.Throws<FormatException>(() => ReadContent(Metres, body, space: 2));
    }

    [Fact]
    public void ASpaceOnTheNationalGrid_IsMovedNearTheOrigin()
    {
        var mesh = Read(Metres, Rectangle(5, 10, 6, 3) + Space(3, "Hall", 430396.7, 566285.9, 0, "#59"));

        Assert.InRange(mesh.Vertices.Min(v => v.X), 0, 1);
        Assert.InRange(mesh.Vertices.Min(v => v.Y), 0, 1);
        Assert.Equal(180, Volume(mesh), 6);
    }

    [Fact]
    public void ABrepSpaceWithAT_Junction_IsJoinedIntoAClosedRoom()
    {
        // a 4 x 4 x 3 m box whose ceiling is two faces, so a corner of each lies along the top edges of the front and back walls
        const string brep = """
            #101=IFCCARTESIANPOINT((0.,0.,0.)); #102=IFCCARTESIANPOINT((4.,0.,0.)); #103=IFCCARTESIANPOINT((4.,4.,0.)); #104=IFCCARTESIANPOINT((0.,4.,0.));
            #105=IFCCARTESIANPOINT((0.,0.,3.)); #106=IFCCARTESIANPOINT((4.,0.,3.)); #107=IFCCARTESIANPOINT((4.,4.,3.)); #108=IFCCARTESIANPOINT((0.,4.,3.));
            #109=IFCCARTESIANPOINT((2.,0.,3.)); #110=IFCCARTESIANPOINT((2.,4.,3.));
            #111=IFCPOLYLOOP((#101,#104,#103,#102)); #121=IFCFACEOUTERBOUND(#111,.T.); #131=IFCFACE((#121));
            #112=IFCPOLYLOOP((#105,#109,#110,#108)); #122=IFCFACEOUTERBOUND(#112,.T.); #132=IFCFACE((#122));
            #113=IFCPOLYLOOP((#109,#106,#107,#110)); #123=IFCFACEOUTERBOUND(#113,.T.); #133=IFCFACE((#123));
            #114=IFCPOLYLOOP((#101,#102,#106,#105)); #124=IFCFACEOUTERBOUND(#114,.T.); #134=IFCFACE((#124));
            #115=IFCPOLYLOOP((#104,#108,#107,#103)); #125=IFCFACEOUTERBOUND(#115,.T.); #135=IFCFACE((#125));
            #116=IFCPOLYLOOP((#101,#105,#108,#104)); #126=IFCFACEOUTERBOUND(#116,.T.); #136=IFCFACE((#126));
            #117=IFCPOLYLOOP((#102,#103,#107,#106)); #127=IFCFACEOUTERBOUND(#117,.T.); #137=IFCFACE((#127));
            #140=IFCCLOSEDSHELL((#131,#132,#133,#134,#135,#136,#137));
            #141=IFCFACETEDBREP(#140);

            """;
        var mesh = Read(Metres, brep + Space(3, "Box", 0, 0, 0, "#141"));

        Assert.Equal(48, Volume(mesh), 6);
        Assert.Equal(16, mesh.GroupAreasM2()["Ceiling"], 6);
    }

    [Fact]
    public void AFileWithNoSpaces_IsReadAsItsElementsSurfaces_GroupedByLayerOrKind()
    {
        // as SketchUp exports: the room drawn as faces of proxies on its tags; a floor with a hole cut in it; and a wall on no layer
        const string shapes = """
            #201=IFCCARTESIANPOINTLIST3D(((0.,0.,0.),(4.,0.,0.),(4.,4.,0.),(0.,4.,0.),(0.,0.,4.),(4.,0.,4.),(4.,4.,4.),(0.,4.,4.)));
            #202=IFCINDEXEDPOLYGONALFACE((1,4,3,2)); #203=IFCINDEXEDPOLYGONALFACE((5,6,7,8)); #204=IFCINDEXEDPOLYGONALFACE((1,2,6,5));
            #205=IFCINDEXEDPOLYGONALFACE((2,3,7,6)); #206=IFCINDEXEDPOLYGONALFACE((3,4,8,7)); #207=IFCINDEXEDPOLYGONALFACE((4,1,5,8));
            #208=IFCPOLYGONALFACESET(#201,.T.,(#202,#203,#204,#205,#206,#207),$);
            #211=IFCCARTESIANPOINTLIST3D(((10.,0.,0.),(14.,0.,0.),(14.,4.,0.),(10.,4.,0.),(11.,1.,0.),(12.,1.,0.),(12.,2.,0.),(11.,2.,0.)));
            #212=IFCINDEXEDPOLYGONALFACEWITHVOIDS((1,2,3,4),((5,6,7,8)));
            #213=IFCPOLYGONALFACESET(#211,$,(#212),$);

            """;
        var content = ReadContent(Metres, shapes + Rectangle(5, 1, 2, 3)
            + Element(30, "IFCBUILDINGELEMENTPROXY", "#208", "Room shell") + Element(31, "IFCBUILDINGELEMENTPROXY", "#213", "Vinyl floor") + Element(32, "IFCWALL", "#59"));

        Assert.Empty(content.Spaces);
        Assert.Null(content.Space);
        var areas = content.Mesh.GroupAreasM2();
        Assert.Equal(96, areas["Room shell"], 6);
        Assert.Equal(15, areas["Vinyl floor"], 6);
        Assert.Equal(2 * 2 + 2 * 3 * (1 + 2), areas["Walls"], 6);
    }

    [Fact]
    public void AFileOfFurnitureOnly_SaysThatIsAllItHolds()
    {
        var error = Assert.Throws<FormatException>(() => Read(Metres, Rectangle(5, 1, 1, 1) + """
            #32=IFCAXIS2PLACEMENT3D(#22,$,$); #33=IFCLOCALPLACEMENT($,#32); #34=IFCPRODUCTDEFINITIONSHAPE($,$,(#35));
            #35=IFCSHAPEREPRESENTATION(#20,'Body','SweptSolid',(#59));
            #30=IFCFURNISHINGELEMENT('3b9Z1X8xT1B8bnRvlaZx30',$,'Table',$,$,#33,#34,$);

            """));

        Assert.Contains("only furniture (1 IfcFurnishingElement)", error.Message);
    }

    [Fact]
    public void AFileWithNoSpaces_SaysSo()
    {
        var error = Assert.Throws<FormatException>(() => Read(Metres, ""));

        Assert.Contains("IfcSpace", error.Message);
    }
}
