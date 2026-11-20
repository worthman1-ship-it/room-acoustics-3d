using RoomAcoustics.Core;
using Xunit;

namespace RoomAcoustics.Core.Tests;

/// <summary>
/// Checks against published qualitative results rather than measured numbers. Hodgson (Canadian Acoustics,
/// "When is diffuse-field theory accurate?", https://jcaa.caa-aca.ca/index.php/jcaa/article/view/840) finds that Eyring
/// prediction is accurate for specularly reflecting, regularly-shaped (quasi-cubic) rooms, and that its accuracy drops as
/// the aspect ratio grows. The tests below check that ordering in the ray tracer.
/// </summary>
public class Acoustics3DValidationTests
{
    private const int Band500 = 3;

    private static double RayToEyringRatio(Room3D room, Vec3 source, Vec3 receiver)
    {
        var options = new RayTraceOptions(RayCount: 200_000, Seed: 7);
        var t30 = RoomAcousticsCalculator.Calculate(EnergyRayTracer.Trace(room, source, receiver, options)).T30S[Band500];

        var surfaces = room.Faces.Select(f => new Surface(f.Name, f.AreaM2, f.Finish.Material)).ToList();
        var eyring = ReverberationCalculator.Calculate(room.VolumeM3, surfaces,
            new ReverberationOptions(ReverberationMethod.EyringNorris)).T60[Band500];
        return t30 / eyring;
    }

    private static SurfaceFinish Specular(double alpha) =>
        new(new AbsorptionMaterial("Test", "Test", Enumerable.Repeat((double?)alpha, 8).ToArray()), OctaveSpectrum.Flat(0));

    [Fact]
    public void SpecularCubicRoom_IsCloserToEyringThanAnElongatedRoom()
    {
        var cube = Room3D.Shoebox(7, 7, 7, Specular(0.2));
        var elongated = Room3D.Shoebox(10, 7, 3, Specular(0.2));

        var cubeRatio = RayToEyringRatio(cube, new Vec3(2, 2, 2), new Vec3(5, 5, 4));
        var elongatedRatio = RayToEyringRatio(elongated, new Vec3(2, 2, 1.5), new Vec3(7, 5, 1.2));

        // Hodgson's ordering: the less elongated room agrees better with Eyring.
        Assert.True(Math.Abs(cubeRatio - 1) < Math.Abs(elongatedRatio - 1),
            $"Cube T30/Eyring = {cubeRatio:0.000}; elongated T30/Eyring = {elongatedRatio:0.000}.");
    }
}
