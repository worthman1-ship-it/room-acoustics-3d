using RoomAcoustics.Core;

namespace RoomAcoustics.Core;

/// <summary>One traced ray, drawn as a polyline.</summary>
/// <param name="Points">The source, then each point where the ray hit a surface, then where it ended.</param>
/// <param name="Reflections">The number of surfaces the ray hit and was reflected from.</param>
/// <param name="Scattered">True if any reflection sent the ray off diffusely rather than specularly.</param>
/// <param name="LevelsDb">
/// The level the ray carries leaving each point, in dB against an omnidirectional source (the source's own pattern is included), so
/// its energy can be shown falling as it is absorbed and spreads through the air. The last point holds the level it arrived with.
/// Null for a ray built by hand.
/// </param>
public sealed record BilliardRay(IReadOnlyList<Vec3> Points, int Reflections, bool Scattered, IReadOnlyList<double>? LevelsDb = null);

/// <summary>
/// The rays of the ray tracer, kept as paths so they can be drawn: a "billiard" view of how sound leaves the source and
/// bounces round the room. Ray <c>i</c> here is ray <c>i</c> of <see cref="EnergyRayTracer"/> for the same seed and band: it
/// starts in the same direction and takes the same random decisions at each surface, so the picture shows rays the
/// calculation really used, not look-alikes.
/// </summary>
public static class BilliardRays
{
    public const int MaxRays = 10_000;
    public const int MaxReflectionsLimit = 100;

    /// <summary>The band index that draws the rays for the overall (broadband) response: the 500 Hz and 1 kHz bands' mean absorption and scattering, air absorption and pattern of 1 kHz.</summary>
    public const int Overall = -1;

    /// <summary>
    /// Traces <paramref name="rayCount"/> rays from the source. A ray ends when it has reflected
    /// <paramref name="maxReflections"/> times, when it has travelled for the options' time limit, or when its energy has
    /// fallen below the options' cut-off, as in the ray tracer. Surface absorption and scattering are those of
    /// <paramref name="bandIndex"/> (0 for 63 Hz up to 7 for 8 kHz), or the mean of the 500 Hz and 1 kHz bands for <see cref="Overall"/>.
    /// </summary>
    public static IReadOnlyList<BilliardRay> Trace(Room3D room, Vec3 source, int rayCount, int maxReflections, int bandIndex,
        RayTraceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (rayCount < 1 || rayCount > MaxRays) throw new ArgumentOutOfRangeException(nameof(rayCount), $"The ray count must be between 1 and {MaxRays}.");
        if (maxReflections < 0 || maxReflections > MaxReflectionsLimit)
            throw new ArgumentOutOfRangeException(nameof(maxReflections), $"The reflection limit must be between 0 and {MaxReflectionsLimit}.");
        if (bandIndex is < Overall or > 7) throw new ArgumentOutOfRangeException(nameof(bandIndex), "The band index must be 0 to 7, or -1 for the overall response.");
        options ??= new RayTraceOptions();
        if (!room.Contains(source)) throw new ArgumentException("The source must be inside the room.", nameof(source));

        var speed = EnergyRayTracer.SpeedOfSound(options.TemperatureC);
        var bands = bandIndex == Overall ? new[] { 3, 4 } : new[] { bandIndex };
        var pattern = bandIndex == Overall ? 4 : bandIndex;
        var airDbPerM = AirAbsorption.AttenuationDbPerMetre(
            OctaveSpectrum.Frequencies[pattern], AirAbsorption.ReferencePressurePa,
            options.TemperatureC, options.RelativeHumidityPercent);
        var airFactorPerM = Math.Pow(10, -airDbPerM / 10);
        var alpha = room.Faces.Select(f => bands.Average(b => f.Finish.Material.ToOctaveSpectrum()[b])).ToArray();
        var scatter = room.Faces.Select(f => bands.Average(b => f.Finish.ScatteringCoefficients[b])).ToArray();
        var directivity = (options.Directivity ?? SourceDirectivity.Omnidirectional).ForBand(pattern);
        var maxPathM = options.MaxTimeS * speed;

        var rays = new BilliardRay[rayCount];
        for (var i = 0; i < rayCount; i++)
        {
            // The same stream as EnergyRayTracer: the start direction, then one draw per surface hit (and the Lambert
            // draws when that hit scatters).
            var rng = new EnergyRayTracer.SplitMix64(EnergyRayTracer.Mix(((ulong)options.Seed << 32) ^ (ulong)i));
            var position = source;
            var direction = EnergyRayTracer.RandomDirection(ref rng);
            var energy = directivity.Gain(direction);
            var cutoff = Math.Pow(10, -options.CutoffDb / 10);
            var path = 0.0;
            var lastFace = -1;
            var reflections = 0;
            var scattered = false;
            var points = new List<Vec3> { source };
            var levels = new List<double> { Db(energy) };

            while (true)
            {
                var (length, face) = room.NearestHit(position, direction, lastFace);
                if (face < 0) break;

                position += direction * length;
                path += length;
                energy *= Math.Pow(airFactorPerM, length);
                points.Add(position);
                levels.Add(Db(energy));
                if (reflections >= maxReflections || path >= maxPathM) break;

                energy *= 1 - alpha[face];
                levels[^1] = Db(energy);
                if (energy < cutoff) break;

                direction = EnergyRayTracer.Reflect(direction, EnergyRayTracer.FacingNormal(room.Faces[face], direction), scatter[face], options.ScatteringModel, ref rng, out var sendDiffusely);
                scattered |= sendDiffusely;
                lastFace = face;
                reflections++;
            }
            rays[i] = new BilliardRay(points, reflections, scattered, levels);
        }
        return rays;
    }

    private static double Db(double energy) => energy > 1e-12 ? 10 * Math.Log10(energy) : -120;

    /// <summary>
    /// True if the ray passes through the receiver sphere of the given radius anywhere along its route, on the
    /// direct leg or after any reflection.
    /// </summary>
    public static bool ReachesReceiver(BilliardRay ray, Vec3 receiver, double radiusM)
    {
        ArgumentNullException.ThrowIfNull(ray);
        for (var leg = 0; leg + 1 < ray.Points.Count; leg++)
        {
            var start = ray.Points[leg];
            var span = ray.Points[leg + 1] - start;
            var length = span.Length;
            if (length <= 0) continue;
            if (EnergyRayTracer.SphereEntry(start, span * (1 / length), length, receiver, radiusM) >= 0) return true;
        }
        return false;
    }
}
