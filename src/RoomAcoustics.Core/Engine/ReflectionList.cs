namespace RoomAcoustics.Core;

/// <summary>
/// One specular reflection path from the source to the receiver. <c>Faces</c> and <c>FaceNames</c> are the surfaces it bounces off, in order from the source;
/// <c>DelayMs</c> is how long after the direct sound it arrives; <c>LevelDb</c> is its level against the direct sound's (which is 0 dB), per octave band 63 Hz
/// to 8 kHz, NaN where nothing of it is left; <c>Path</c> is source, each reflection point, receiver.
/// </summary>
public sealed record ReflectionPath(int Order, IReadOnlyList<int> Faces, IReadOnlyList<string> FaceNames, double DistanceM, double DelayMs, double[] LevelDb, IReadOnlyList<Vec3> Path);

/// <summary>
/// The image-source paths at a receiver as a list, with the energy each carries worked out exactly as the simulation works it out
/// (<see cref="EnergyRayTracer.SpecularPathEnergy"/>): every bounce keeps (1 - absorption) and a specular share (1 - scattering, with the edges' diffraction: <see cref="EdgeScattering"/>), then the spreading over the longer path,
/// the air absorption and the source's gain in the direction the path leaves it. The level is against the direct sound as if it were not blocked, so it is
/// still meaningful round a corner. Only specular paths are listed: the energy that scattering sends off in other directions is in the simulation's
/// ray tracing but belongs to no single path.
/// </summary>
public static class ReflectionList
{
    public static IReadOnlyList<ReflectionPath> For(Room3D room, Vec3 source, Vec3 receiver, int maxOrder, double temperatureC, double relativeHumidityPercent,
        SourceDirectivity? directivity = null, double receiverRadiusM = 0.5)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (maxOrder == 0) return Array.Empty<ReflectionPath>();
        var arrivals = ImageSourceEnumerator.Enumerate(room, source, receiver, maxOrder, withPaths: true);
        var speed = EnergyRayTracer.SpeedOfSound(temperatureC);
        var directDistance = (receiver - source).Length;
        var toReceiver = (receiver - source).Normalized();
        var pattern = directivity ?? SourceDirectivity.Omnidirectional;

        var scattering = room.Faces.Select(f => f.Finish.ScatteringCoefficients).ToArray();
        var bandData = Enumerable.Range(0, 8).Select(band => (
            Alpha: EnergyRayTracer.ReflectionLoss(room, band),
            Band: band,
            Scatter: scattering.Select(s => s[band]).ToArray(),
            Pattern: pattern.ForBand(band),
            AirDbPerM: AirAbsorption.AttenuationDbPerMetre(OctaveSpectrum.Frequencies[band], AirAbsorption.ReferencePressurePa, temperatureC, relativeHumidityPercent),
            WavelengthM: speed / OctaveSpectrum.Frequencies[band],
            Edges: new EdgeScattering(room, OctaveSpectrum.Frequencies[band], speed))).ToArray();

        return arrivals.OrderBy(a => a.DistanceM).Select(a =>
        {
            var levels = bandData.Select(b =>
            {
                var path = EnergyRayTracer.SpecularPathEnergy(a, source, b.Alpha, b.Scatter, receiverRadiusM, b.AirDbPerM, b.Pattern, room, b.WavelengthM, b.Band, b.Edges);
                var direct = EnergyRayTracer.DirectEnergy(directDistance, receiverRadiusM, b.AirDbPerM, b.Pattern.Gain(toReceiver));
                return path > 0 && direct > 0 ? 10 * Math.Log10(path / direct) : double.NaN;
            }).ToArray();
            return new ReflectionPath(a.Faces.Count, a.Faces.ToArray(), a.Faces.Select(f => room.Faces[f].Name).ToArray(), a.DistanceM,
                (a.DistanceM - directDistance) / speed * 1000, levels, a.Path!);
        }).Where(p => p.LevelDb.Any(double.IsFinite)).ToList();
    }
}
