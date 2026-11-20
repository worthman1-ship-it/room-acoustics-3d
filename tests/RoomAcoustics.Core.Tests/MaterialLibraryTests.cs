using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

public class MaterialLibraryTests
{
    [Fact]
    public void NoMaterialAbsorbsMoreThanEverything()
    {
        // published tables have values over 1 (people standing in a group); an absorption coefficient is at most 1
        foreach (var material in AbsorptionMaterial.Presets.Load(null))
            Assert.All(material.Alpha.Where(a => a is not null), a => Assert.InRange(a!.Value, 0.0, 1.0));
    }

    [Fact]
    public void AValueAboveOneIsHeldAtOne_AndTheRestAreLeftAlone()
    {
        var material = AbsorptionMaterial.Presets.Load(null).Single(m => m.Name == "People in a group standing, 1 per 6 m2, upper");
        Assert.Equal(new double?[] { null, 0.12, 0.45, 0.8, 1.0, 1.0, 1.0, null }, material.Alpha);
    }

    [Fact]
    public void TheUsersOwnLibrary_ComesAfterThePresets_AndBeforeTheBuiltInOne()
    {
        var path = Path.Combine(Path.GetTempPath(), $"materials-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """[{"Name":"My panel","Category":"Mine","Alpha":[null,0.2,0.4,0.6,0.8,0.9,0.9,null],"Nrc":null,"Notes":null,"Reference":null}]""");
        try
        {
            var builtIn = AbsorptionMaterial.Presets.Load(null);
            var all = AbsorptionMaterial.Presets.Load(path);
            Assert.Equal(builtIn.Count + 1, all.Count);
            Assert.Equal("My panel", all[2].Name);
            Assert.Equal(builtIn[2].Name, all[3].Name);
        }
        finally { File.Delete(path); }
    }
}
