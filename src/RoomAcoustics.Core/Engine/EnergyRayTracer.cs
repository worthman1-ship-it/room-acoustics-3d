using RoomAcoustics.Core;

namespace RoomAcoustics.Core;

/// <summary>Settings for <see cref="EnergyRayTracer"/>.</summary>
/// <param name="RayCount">Rays launched from the source, per octave band. More rays give a smoother impulse response.</param>
/// <param name="ReceiverRadiusM">Radius of the receiver sphere. Rays that pass through it are counted as received energy.</param>
/// <param name="MaxTimeS">Length of the impulse response. Must be well beyond the decay you want to measure.</param>
/// <param name="BinWidthS">Width of each time bin.</param>
/// <param name="CutoffDb">A ray is abandoned once its energy has fallen this far below its launch energy.</param>
/// <param name="TemperatureC">Air temperature, for the speed of sound and air absorption.</param>
/// <param name="RelativeHumidityPercent">Relative humidity, percent, for air absorption.</param>
/// <param name="Seed">Seed for the random ray directions and diffuse reflections. The same seed gives the same result.</param>
/// <param name="ImageSourceOrder">
/// Hybrid mode when above zero: specular paths with up to this many reflections are
/// computed exactly by the image-source method, and the ray tracer counts only the
/// paths the images do not cover. Zero is pure ray tracing.
/// </param>
/// <param name="Directivity">The source's radiation pattern. Null means omnidirectional.</param>
/// <param name="ScatteringModel">How a reflection uses the surface's scattering coefficient; see <see cref="ScatteringModel"/>.</param>
/// <param name="Cancellation">Optional. When cancelled, the trace stops soon after and throws <see cref="OperationCanceledException"/>.</param>
/// <param name="Progress">Optional. Called with the fraction done, 0 to 1, as each chunk of rays finishes; may be called from several threads at once, so it must be thread-safe and quick.</param>
/// <param name="BandCentresHz">Optional. The frequency of each of the eight band slots for air absorption (null: the octave centres 63 Hz to 8 kHz). A third-octave run puts eight third-octave centres here; see <see cref="ThirdOctaveSimulation"/>.</param>
/// <param name="EdgeDiffraction">Adds to each reflection's scattering what diffraction at the surface's edges gives it (see <see cref="EdgeScattering"/>), so that small surfaces, low frequencies and grazing sound are scattered as a real room scatters them.</param>
/// <param name="Interaural">Gathers each receiver's energy by the lateral angle it comes from, in the early and late windows after the first arrival, for its IACC (see <see cref="InterauralEnergy"/>). The first arrival is the direct sound or the earliest image path; with neither, nothing is gathered.</param>
/// <param name="AngleDependentAbsorption">Each reflection takes the absorption of its own angle of incidence, from a locally reacting surface whose diffuse-field average is the surface's coefficient (see <see cref="Core.AngleDependentAbsorption"/>), instead of the coefficient itself.</param>
/// <param name="CornerDiffraction">Where the walls hide the source from a receiver, the direct sound bends round the room's outward-pointing edges to it (see <see cref="Core.CornerDiffraction"/>),
/// and so, with image sources, do the reflections of up to <see cref="Core.CornerDiffraction.MaxReflections"/> orders that a corner hides.</param>
/// <param name="HeadLeft">The unit vector out of the left ear of the heads the interaural energy is gathered for (a listener facing a way of their own). Null: each head faces the source, its left ear along the receiver's lateral axis.</param>
public sealed record RayTraceOptions(
    int RayCount = 200_000,
    double ReceiverRadiusM = 0.5,
    double MaxTimeS = 2.0,
    double BinWidthS = 0.001,
    double CutoffDb = 70,
    double TemperatureC = 20,
    double RelativeHumidityPercent = 60,
    int Seed = 1,
    int ImageSourceOrder = 0,
    SourceDirectivity? Directivity = null,
    ScatteringModel ScatteringModel = ScatteringModel.Probabilistic,
    IReadOnlyList<double>? BandCentresHz = null,
    CancellationToken Cancellation = default,
    Action<double>? Progress = null,
    bool ArrivalDirections = false,
    IReadOnlyList<Func<int, Vec3, double>>? EarGains = null,
    bool EdgeDiffraction = true,
    bool Interaural = false,
    Vec3? HeadLeft = null,
    bool AngleDependentAbsorption = false,
    bool CornerDiffraction = true);

/// <summary>How a ray leaves a surface that has a scattering coefficient s.</summary>
public enum ScatteringModel
{
    /// <summary>
    /// With probability s the ray leaves in a Lambert (cosine-law) direction and otherwise exactly specularly. A purely specular path is
    /// therefore a real thing, which the image-source method can count exactly for the early reflections (weighted by 1 - s).
    /// </summary>
    Probabilistic,

    /// <summary>
    /// Every ray leaves along the unit vector of (1 - s) times the specular direction plus s times a random Lambert direction, so a surface
    /// with a little scattering turns every reflection a little, not a few of them completely, and none loses energy to the scattering. This
    /// is the vector-based scattering of Christensen and Rindel. Because no ray stays exactly specular, the image-source method has no
    /// separate specular part to count, so this model runs as pure ray tracing whatever the image-source order is.
    /// </summary>
    VectorBased,
}

/// <summary>
/// Three-dimensional energy ray tracing in a room, one octave band at a
/// time. Rays leave an omnidirectional point source in random directions and
/// carry energy. At each wall a ray loses the fraction α of its energy and
/// leaves either specularly or, with the face's scattering coefficient,
/// diffusely (Lambert). Air absorption is applied along each path. A ray
/// that passes through the receiver sphere deposits its current energy at the
/// time it first enters the sphere.
/// </summary>
/// <remarks>
/// <para>
/// Energy is normalised so the source emits one unit of energy per band. The
/// direct sound is added analytically as the fraction of the source's energy the receiver sphere
/// subtends, (1 - √(1 - r²/d²))/2, times the air absorption over d. Reflected energy is counted by
/// ray hits, which gives the same fraction in expectation, so direct and reflected energy share one scale.
/// </para>
/// <para>
/// Each reflection multiplies energy by (1-α), and the mean free path between
/// reflections is 4V/S for any convex room. With diffuse (Lambert) reflection,
/// ray directions are randomised at every wall. The decay then sits between
/// Eyring-Norris, T = 55.3 V / (c(-S ln(1-α)) + 4mV), and Sabine, and in the
/// rooms tested it is about a third of the way from Eyring to Sabine. Eyring
/// assumes every ray reflects at exactly the mean free path; the spread in
/// reflection counts between rays lengthens the decay (the likely cause of the
/// offset, not verified against a published benchmark). Air absorption, which
/// acts continuously rather than per reflection, follows Eyring with 4mV.
/// </para>
/// <para>
/// Purely specular reflection in a rectangular room does not randomise the rays:
/// each direction component's magnitude is preserved at every wall, so rays with
/// different directions keep different reflection rates. The ensemble decay is
/// then slower than Eyring and non-exponential. Use a scattering coefficient for
/// rooms like this; real rooms have the surface irregularity this stands in for.
/// </para>
/// <para>
/// The method is geometric. It is not reliable below the Schroeder frequency
/// (roughly 100-300 Hz for typical rooms), where wave effects dominate.
/// </para>
/// </remarks>
public static class EnergyRayTracer
{
    /// <summary>
    /// Fraction of an isotropic source's energy that a receiver sphere of radius r at distance d intercepts:
    /// its solid angle over 4π, (1 - √(1 - r²/d²))/2. This is exactly the expected share of rays that enter the sphere.
    /// </summary>
    internal static double SphereFraction(double distance, double radius) =>
        (1 - Math.Sqrt(1 - (radius / distance) * (radius / distance))) / 2;

    /// <summary>
    /// The energy of the direct sound at a receiver sphere: the sphere's share of the source energy in its direction, with
    /// air absorption over the distance. <paramref name="gain"/> is the source's gain towards the receiver.
    /// </summary>
    internal static double DirectEnergy(double distance, double radius, double airDbPerM, double gain) =>
        SphereFraction(distance, radius) * gain * Math.Pow(10, -airDbPerM * distance / 10);

    /// <summary>
    /// The energy of one specular image path at a receiver sphere. Each reflection keeps (1-α) of the energy, and a specular
    /// (not scattered) one has probability (1-s), so a path is weighted by Π(1-α)(1-s). The source's gain is taken in the
    /// direction the path leaves it. A leg a screen stands in the way of is cut by the screen's diffraction at <paramref name="wavelengthM"/>.
    /// </summary>
    internal static double SpecularPathEnergy(ImageArrival arrival, Vec3 source, double[] alpha, double[] scatter, double radius,
        double airDbPerM, SourceDirectivity directivity, Room3D? room = null, double wavelengthM = double.NaN, int band = -1, EdgeScattering? edges = null,
        AngleLoss? angles = null)
    {
        var weight = 1.0;
        for (var k = 0; k < arrival.Faces.Count; k++)
        {
            var face = arrival.Faces[k];
            var s = scatter[face];
            var loss = alpha[face];
            // the absorption at the angle the path meets the face at, from the leg that arrives there
            if (angles is not null && arrival.Path is { } route && room is not null)
            {
                var arriving = route[k + 1] - route[k];
                loss = angles.Of(face, arriving.Length > 0 ? Math.Abs(arriving.Dot(room.Faces[face].InwardNormal)) / arriving.Length : 1);
            }
            // with the edges' diffraction, from the legs either side of the reflection (the path runs source, reflections, receiver)
            if (edges is not null && arrival.Path is { } path && room is not null)
            {
                var incoming = path[k + 1] - path[k];
                var d1 = incoming.Length;
                s = edges.Combined(face, s, d1 > 0 ? Math.Abs(incoming.Dot(room.Faces[face].InwardNormal)) / d1 : 1, d1, (path[k + 2] - path[k + 1]).Length);
            }
            weight *= (1 - loss) * (1 - s);
        }
        if (arrival.ScreenedLegs is { } legs && room is not null)
            foreach (var (from, to) in legs)
                weight *= ScreenDiffraction.EnergyFactor(room, from, to, wavelengthM, band);
        if (arrival.Bend is { } bend) weight *= CornerDiffraction.Factor(bend, wavelengthM);
        if (weight == 0) return 0;

        var gain = directivity.Gain((arrival.FirstReflection - source).Normalized());
        return weight * gain * SphereFraction(arrival.DistanceM, radius) * Math.Pow(10, -airDbPerM * arrival.DistanceM / 10);
    }

    /// <summary>
    /// What each face takes from a reflection in one band at the angle it is met at, for <see cref="RayTraceOptions.AngleDependentAbsorption"/>: its absorption at
    /// that angle (<see cref="Core.AngleDependentAbsorption"/>), or what passes through it if that is more.
    /// </summary>
    internal sealed class AngleLoss
    {
        private readonly double[] _alpha, _xi, _through;

        public AngleLoss(Room3D room, int band)
        {
            _alpha = room.Faces.Select(f => f.Finish.Material.ToOctaveSpectrum()[band]).ToArray();
            _xi = _alpha.Select(Core.AngleDependentAbsorption.ImpedanceFor).ToArray();
            _through = room.Faces.Select(f => f.IsScreen ? f.Finish.Transmission(band) : 0).ToArray();
        }

        public double Of(int face, double cosIncidence) => Math.Max(Core.AngleDependentAbsorption.Alpha(_alpha[face], _xi[face], cosIncidence), _through[face]);
    }

    /// <summary>What each face of the room takes from a reflection in one band: its absorption, or what passes through it if that is more (see <see cref="SurfaceFinish"/>).</summary>
    internal static double[] ReflectionLoss(Room3D room, int band) =>
        room.Faces.Select(f => Math.Max(f.Finish.Material.ToOctaveSpectrum()[band], f.IsScreen ? f.Finish.Transmission(band) : 0)).ToArray();

    /// <summary>Speed of sound in air, m/s, at the given temperature (same formula as the reverberation calculator).</summary>
    public static double SpeedOfSound(double temperatureC) => 331.3 + 0.606 * temperatureC;

    public static ImpulseResponse Trace(Room3D room, Vec3 source, Vec3 receiver, RayTraceOptions? options = null) =>
        TraceReceivers(room, source, new[] { receiver }, options)[0];

    /// <summary>
    /// Impulse responses at several receivers from one set of rays. Each ray is tested against every receiver
    /// sphere, so the ray tracing is paid once for a whole receiver grid. Each receiver gets its own image-source paths.
    /// With <paramref name="earNormals"/> (one unit vector per receiver, pointing out of the ear) each response also carries the
    /// energy as the ear of a head would hear it, in <see cref="ImpulseResponse.ShadowedEnergy"/>.
    /// </summary>
    public static IReadOnlyList<ImpulseResponse> TraceReceivers(Room3D room, Vec3 source, IReadOnlyList<Vec3> receivers,
        RayTraceOptions? options = null, IReadOnlyList<Vec3>? earNormals = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(receivers);
        if (receivers.Count == 0) throw new ArgumentException("At least one receiver is needed.", nameof(receivers));
        if (earNormals is not null && earNormals.Count != receivers.Count)
            throw new ArgumentException("There must be one ear axis for each receiver.", nameof(earNormals));
        options ??= new RayTraceOptions();
        ValidateOptions(options);
        if (options.ScatteringModel == ScatteringModel.VectorBased) options = options with { ImageSourceOrder = 0 };

        if (!room.Contains(source)) throw new ArgumentException("The source must be inside the room.", nameof(source));
        var radius = options.ReceiverRadiusM;
        foreach (var receiver in receivers)
        {
            if (!room.Contains(receiver)) throw new ArgumentException("Every receiver must be inside the room.", nameof(receivers));
            if (room.DistanceToBoundary(receiver) < radius)
                throw new ArgumentException("A receiver sphere must lie entirely inside the room (too close to a surface).", nameof(receivers));
            if ((receiver - source).Length <= radius)
                throw new ArgumentException("The source must lie outside every receiver sphere.", nameof(source));
        }

        var speed = SpeedOfSound(options.TemperatureC);
        var binCount = (int)Math.Ceiling(options.MaxTimeS / options.BinWidthS);
        var scattering = room.Faces.Select(f => f.Finish.ScatteringCoefficients).ToArray();
        var sourceDirectivity = options.Directivity ?? SourceDirectivity.Omnidirectional;

        // the paths' reflection points are kept when the edges' diffraction needs the legs either side of each reflection
        var arrivals = receivers.Select(r => options.ImageSourceOrder > 0
            ? ImageSourceEnumerator.Enumerate(room, source, r, options.ImageSourceOrder, withPaths: options.EdgeDiffraction || options.AngleDependentAbsorption)
            : Array.Empty<ImageArrival>()).ToArray();

        var axes = receivers.Select(r => LateralAxis(source, r)).ToArray();
        // the ways the direct sound bends round the room's corners to a receiver the walls hide it from (the same in every band)
        var corners = options.CornerDiffraction && !room.IsConvex ? new CornerDiffraction(room) : null;
        var bends = receivers.Select(r => corners?.Paths(source, r) ?? Array.Empty<CornerDiffraction.Path>()).ToArray();
        // and the image paths a corner hides, bent round it (each cut by its bend in SpecularPathEnergy); a map's receivers are searched side by side
        if (corners is { Edges.Count: > 0 } && options.ImageSourceOrder > 0)
            Parallel.For(0, receivers.Count, new ParallelOptions { CancellationToken = options.Cancellation }, r =>
                arrivals[r] = arrivals[r].Concat(ImageSourceEnumerator.EnumerateRoundCorners(room, source, receivers[r],
                    Math.Min(options.ImageSourceOrder, CornerDiffraction.MaxReflections), corners,
                    withPaths: options.EdgeDiffraction || options.AngleDependentAbsorption)).ToArray());
        var index = receivers.Count >= ReceiverIndex.WorthwhileCount ? new ReceiverIndex(receivers, radius) : null;
        var energy = receivers.Select(_ => new double[8][]).ToArray();
        var lateral = receivers.Select(_ => new double[8][]).ToArray();
        var lateralAmplitude = receivers.Select(_ => new double[8][]).ToArray();
        var shadowed = receivers.Select(_ => new double[8][]).ToArray();
        var from = receivers.Select(_ => new double[3][][] { new double[8][], new double[8][], new double[8][] }).ToArray();
        var direct = receivers.Select(_ => new double[8]).ToArray();
        var early = receivers.Select(_ => new double[8][]).ToArray();
        var late = receivers.Select(_ => new double[8][]).ToArray();
        var reference = new double[8];
        for (var band = 0; band < 8; band++)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            var alpha = ReflectionLoss(room, band);
            var angles = options.AngleDependentAbsorption ? new AngleLoss(room, band) : null;
            var scatter = scattering.Select(s => s[band]).ToArray();
            // what passes through each face: only a screen's, as nothing is beyond the boundary
            var through = room.Faces.Select(f => f.IsScreen ? f.Finish.Transmission(band) : 0).ToArray();
            var directivity = sourceDirectivity.ForBand(band);
            var frequency = options.BandCentresHz?[band] ?? OctaveSpectrum.Frequencies[band];
            var airDbPerM = AirAbsorption.AttenuationDbPerMetre(frequency, AirAbsorption.ReferencePressurePa, options.TemperatureC, options.RelativeHumidityPercent);
            var wavelength = speed / frequency;
            var edges = options.EdgeDiffraction ? new EdgeScattering(room, frequency, speed) : null;

            // The direct sound and the specular image paths, worked out before the rays so that the first arrival is known (the interaural windows
            // are timed from it), and added after them.
            var analytic = new List<(int Bin, double Energy, Vec3 Direction)>[receivers.Count];
            for (var r = 0; r < receivers.Count; r++)
            {
                analytic[r] = new List<(int, double, Vec3)>();
                var receiver = receivers[r];
                var distance = (receiver - source).Length;

                // Direct sound, analytic: the sphere's share of the source energy in its direction, with air absorption,
                // arriving at t = d/c.
                // A wall between source and receiver blocks it: there is no direct sound round a corner. A screen cuts it to what bends round its edges.
                var directBin = (int)(distance / speed / options.BinWidthS);
                if (directBin < binCount && room.IsClearOfWalls(source, receiver))
                {
                    var toReceiver = (receiver - source).Normalized();
                    var past = room.HasScreens ? ScreenDiffraction.EnergyFactor(room, source, receiver, wavelength, band) : 1;
                    direct[r][band] = DirectEnergy(distance, radius, airDbPerM, directivity.Gain(toReceiver)) * past;
                    if (past > 0) analytic[r].Add((directBin, direct[r][band], toReceiver));
                }
                // round a corner instead: spread over the route, cut by the bend, arriving from the corner
                foreach (var bend in bends[r])
                {
                    var bin = (int)(bend.LengthM / speed / options.BinWidthS);
                    if (bin >= binCount) continue;
                    var bent = SphereFraction(bend.LengthM, radius) * directivity.Gain((bend.Turn - source).Normalized())
                               * Math.Pow(10, -airDbPerM * bend.LengthM / 10) * CornerDiffraction.Factor(bend, wavelength);
                    analytic[r].Add((bin, bent, (receiver - bend.Turn).Normalized()));
                }

                // Specular image paths; the rays count the scattered ones.
                foreach (var arrival in arrivals[r])
                {
                    var pathEnergy = SpecularPathEnergy(arrival, source, alpha, scatter, radius, airDbPerM, directivity, room, wavelength, band, edges, angles);
                    if (pathEnergy == 0) continue;

                    var bin = (int)(arrival.DistanceM / speed / options.BinWidthS);
                    if (bin < binCount) analytic[r].Add((bin, pathEnergy, arrival.FinalDirection));
                }
            }
            var first = analytic.Select(a => a.Where(x => x.Energy > 0).Select(x => x.Bin).DefaultIfEmpty(-1).Min()).ToArray();

            var bins = TraceBand(room, source, receivers, axes, alpha, scatter, through, airDbPerM, options, speed, binCount, directivity, earNormals, band, index, edges, first, angles);

            for (var r = 0; r < receivers.Count; r++)
            {
                foreach (var (bin, e, toward) in analytic[r]) bins[r].Add(bin, e, toward, axes[r]);
                energy[r][band] = bins[r].Energy;
                lateral[r][band] = bins[r].Lateral;
                lateralAmplitude[r][band] = bins[r].LateralAmplitude;
                shadowed[r][band] = bins[r].Shadowed!;
                if (bins[r].From is { } comesFrom)
                    for (var k = 0; k < 3; k++) from[r][k][band] = comesFrom[k];
                early[r][band] = bins[r].EarlyCells ?? new double[InterauralCoherence.Cells];
                late[r][band] = bins[r].LateCells ?? new double[InterauralCoherence.Cells];
            }

            // ISO 3382 G reference: free-field direct sound at 10 m, same receiver cross-section (omnidirectional).
            reference[band] = SphereFraction(10, radius) * Math.Pow(10, -airDbPerM * 10 / 10);
        }

        var reference10m = new OctaveSpectrum(reference);
        return energy.Select((e, r) => new ImpulseResponse(options.BinWidthS, e, reference10m, lateral[r], lateralAmplitude[r],
            earNormals is null ? null : shadowed[r], new OctaveSpectrum(direct[r]), options.ArrivalDirections ? from[r] : null,
            options.Interaural ? new InterauralEnergy(early[r], late[r]) : null)).ToList();
    }

    /// <summary>
    /// Fixed chunk count, so the summation order (and therefore the floating-point result) does not depend on the
    /// number of processors on the machine. It also bounds the memory used for per-chunk partial sums.
    /// </summary>
    private const int ChunkCount = 32;

    /// <summary>
    /// The energy per time bin at one receiver, and the same energy weighted by cos² and |cos| of the angle between each
    /// arrival and the receiver's lateral axis.
    /// </summary>
    private sealed class Bins
    {
        private readonly Vec3? _earNormal;
        private readonly Func<int, Vec3, double>? _earGain;
        private readonly int _band;

        /// <param name="earNormal">If given, the unit vector out of the ear, so that the energy is also weighted by the head's shadow.</param>
        /// <param name="band">The octave band the shadow is worked out for.</param>
        /// <param name="directions">Whether to keep the energy-weighted direction each bin's sound comes from as well.</param>
        /// <param name="earGain">If given with an ear, the ear's power gain in a band for sound from a direction, in place of the spherical head's shadow (a measured HRTF).</param>
        /// <param name="interaural">If given, the bin of the first arrival (-1 for none), the windows' ends in bins after it and the head's left-ear axis (null: the lateral axis), so that the energy is also gathered by lateral angle (see <see cref="InterauralEnergy"/>).</param>
        public Bins(int count, Vec3? earNormal = null, int band = 0, bool directions = false, Func<int, Vec3, double>? earGain = null,
            (int First, int EarlyBins, int LateBins, Vec3? Left)? interaural = null)
        {
            _interaural = interaural;
            if (interaural is not null)
            {
                EarlyCells = new double[InterauralCoherence.Cells];
                LateCells = new double[InterauralCoherence.Cells];
            }
            _earGain = earGain;
            Energy = new double[count];
            Lateral = new double[count];
            LateralAmplitude = new double[count];
            _earNormal = earNormal;
            _band = band;
            if (earNormal is not null) Shadowed = new double[count];
            if (directions) From = new[] { new double[count], new double[count], new double[count] };
        }

        /// <summary>Each bin's energy times the x, y and z of the direction it comes from, summed: their sum over the energy is the mean direction.</summary>
        public double[][]? From { get; }

        private readonly (int First, int EarlyBins, int LateBins, Vec3? Left)? _interaural;

        /// <summary>The energy of the early and late windows by lateral angle, when it is gathered.</summary>
        public double[]? EarlyCells { get; }
        public double[]? LateCells { get; }

        public double[] Energy { get; }
        public double[] Lateral { get; }
        public double[] LateralAmplitude { get; }
        public double[]? Shadowed { get; }

        public void Add(int bin, double energy, Vec3 direction, Vec3 lateralAxis)
        {
            var cos = direction.Dot(lateralAxis);
            Energy[bin] += energy;
            Lateral[bin] += energy * cos * cos;
            LateralAmplitude[bin] += energy * Math.Abs(cos);
            // The direction is the way the sound travels, so it comes from the opposite side.
            if (Shadowed is not null)
                Shadowed[bin] += energy * (_earGain is null ? HeadShadow.PowerGain(_band, -direction.Dot(_earNormal!.Value)) : _earGain(_band, -direction));
            // the cell of where it comes from, against the lateral axis (which points to the left of a listener facing the source)
            if (_interaural is { First: >= 0 } w && bin >= w.First)
            {
                var after = bin - w.First;
                var cells = after < w.EarlyBins ? EarlyCells : after < w.LateBins ? LateCells : null;
                if (cells is not null) cells[InterauralCoherence.Cell(w.Left is { } left ? -direction.Dot(left) : -cos)] += energy;
            }
            if (From is not null)
            {
                From[0][bin] -= energy * direction.X;
                From[1][bin] -= energy * direction.Y;
                From[2][bin] -= energy * direction.Z;
            }
        }

        public void Add(Bins other)
        {
            for (var i = 0; i < Energy.Length; i++)
            {
                Energy[i] += other.Energy[i];
                Lateral[i] += other.Lateral[i];
                LateralAmplitude[i] += other.LateralAmplitude[i];
                if (Shadowed is not null && other.Shadowed is not null) Shadowed[i] += other.Shadowed[i];
                if (From is not null && other.From is not null)
                    for (var k = 0; k < 3; k++) From[k][i] += other.From[k][i];
            }
            if (EarlyCells is not null && other.EarlyCells is not null)
                for (var k = 0; k < EarlyCells.Length; k++) { EarlyCells[k] += other.EarlyCells[k]; LateCells![k] += other.LateCells![k]; }
        }
    }

    /// <summary>
    /// The receiver's lateral axis: horizontal and at right angles to the line to the source, so that a listener facing
    /// the source has it pointing to the ears, as ISO 3382-1 places the figure-of-eight microphone.
    /// </summary>
    internal static Vec3 LateralAxis(Vec3 source, Vec3 receiver)
    {
        var facing = source - receiver;
        var length = Math.Sqrt(facing.X * facing.X + facing.Y * facing.Y);
        return length < 1e-9 ? new Vec3(0, 1, 0) : new Vec3(-facing.Y / length, facing.X / length, 0);
    }

    private static Bins[] TraceBand(Room3D room, Vec3 source, IReadOnlyList<Vec3> receivers, IReadOnlyList<Vec3> axes,
        double[] alpha, double[] scatter, double[] through, double airDbPerM, RayTraceOptions options, double speed, int binCount, SourceDirectivity directivity,
        IReadOnlyList<Vec3>? earNormals, int band, ReceiverIndex? index, EdgeScattering? edges, int[] first, AngleLoss? angles)
    {
        (int, int, int, Vec3?)? Windows(int r) => options.Interaural
            ? (first[r], (int)Math.Round(InterauralCoherence.EarlyEndS / options.BinWidthS), (int)Math.Round(InterauralCoherence.LateEndS / options.BinWidthS), options.HeadLeft)
            : null;
        var rayCount = options.RayCount;
        var chunks = Math.Min(rayCount, ChunkCount);
        var partials = new Bins[chunks][];
        var airFactorPerM = Math.Pow(10, -airDbPerM / 10);
        var launchEnergy = 1.0 / rayCount;
        var cutoffEnergy = launchEnergy * Math.Pow(10, -options.CutoffDb / 10);

        var finished = 0;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = options.Cancellation }, chunk =>
        {
            var local = receivers.Select((_, r) => new Bins(binCount, earNormals?[r], band, options.ArrivalDirections, options.EarGains?[r], Windows(r))).ToArray();
            var scratch = index?.NewScratch();
            var start = (int)((long)chunk * rayCount / chunks);
            var end = (int)((long)(chunk + 1) * rayCount / chunks);
            for (var i = start; i < end; i++)
            {
                // a chunk can be tens of thousands of rays, seconds of work on a busy machine, so a cancelled run is noticed every 256 rays, not only between chunks
                if ((i & 255) == 0) options.Cancellation.ThrowIfCancellationRequested();
                var rng = new SplitMix64(Mix(((ulong)options.Seed << 32) ^ (ulong)i));
                TraceRay(room, source, receivers, axes, alpha, scatter, through, airFactorPerM, options, speed, binCount,
                    launchEnergy, directivity, cutoffEnergy, ref rng, local, index, scratch, edges, angles);
            }
            partials[chunk] = local;
            options.Progress?.Invoke((band + Interlocked.Increment(ref finished) / (double)chunks) / 8);
        });

        var total = receivers.Select((_, r) => new Bins(binCount, earNormals?[r], band, options.ArrivalDirections, options.EarGains?[r], Windows(r))).ToArray();
        foreach (var local in partials)
            for (var r = 0; r < receivers.Count; r++)
                total[r].Add(local[r]);
        return total;
    }

    private static void TraceRay(Room3D room, Vec3 source, IReadOnlyList<Vec3> receivers, IReadOnlyList<Vec3> axes, double[] alpha, double[] scatter,
        double[] through, double airFactorPerM, RayTraceOptions options, double speed, int binCount,
        double launchEnergy, SourceDirectivity directivity, double cutoffEnergy, ref SplitMix64 rng, Bins[] bins,
        ReceiverIndex? index, ReceiverIndex.Scratch? scratch, EdgeScattering? edges, AngleLoss? angles)
    {
        var position = source;
        var direction = RandomDirection(ref rng);
        var energy = launchEnergy * directivity.Gain(direction);
        var pathM = 0.0;
        var lastFace = -1;
        var segment = 0;
        var scattered = false;
        var maxTimeM = options.MaxTimeS * speed;
        var radius = options.ReceiverRadiusM;

        while (energy >= cutoffEnergy && pathM < maxTimeM)
        {
            var (length, face) = room.NearestHit(position, direction, lastFace);
            if (face < 0) break;

            // Segment 0 is the direct sound, which is added analytically; only later segments are counted.
            // In hybrid mode a purely specular path of order up to ImageSourceOrder is already in the image sum,
            // so it is skipped here; scattered paths, and specular paths of higher order, are counted.
            if (segment > 0 && (scattered || segment > options.ImageSourceOrder))
            {
                // with many receivers (a map) only those in the grid cells the segment crosses are tested
                var tested = receivers.Count;
                if (index is not null) { index.Query(position, direction, length, scratch!); tested = scratch!.Count; }
                for (var k = 0; k < tested; k++)
                {
                    var r = index is null ? k : scratch!.Found[k];
                    var entry = SphereEntry(position, direction, length, receivers[r], radius);
                    if (entry < 0) continue;
                    var bin = (int)((pathM + entry) / speed / options.BinWidthS);
                    if (bin < binCount)
                        bins[r].Add(bin, energy * Math.Pow(airFactorPerM, entry), direction, axes[r]);
                }
            }

            energy *= Math.Pow(airFactorPerM, length);
            pathM += length;
            position += direction * length;

            // a face sound passes through (a partition): the ray goes on through it, or is reflected, chosen by their shares, carrying the two together, so
            // that on average τ goes through and 1 - max(α, τ) comes back. Only these faces take the extra draw, so a room without them traces as before.
            var normal = FacingNormal(room.Faces[face], direction);
            var loss = angles is null ? alpha[face] : angles.Of(face, Math.Abs(direction.Dot(normal)));
            if (through[face] > 0)
            {
                var kept = 1 - loss + through[face];
                energy *= kept;
                if (rng.NextDouble() * kept < through[face])
                {
                    // through it, the same way: not a reflection, so the count of reflections (and the image sources' share) is unchanged
                    lastFace = face;
                    continue;
                }
            }
            else energy *= 1 - loss;
            var s = scatter[face];
            if (edges is not null)
            {
                // the edges' diffraction needs the distance the specular reflection would go on; it is looked for only when it could matter
                var cos = Math.Abs(direction.Dot(normal));
                if (!edges.NoneWhatever(face, cos, length))
                {
                    var (onward, _) = room.NearestHit(position, direction - normal * (2 * direction.Dot(normal)), face);
                    s = edges.Combined(face, s, cos, length, onward > 0 && double.IsFinite(onward) ? onward : length);
                }
            }
            direction = Reflect(direction, normal, s, options.ScatteringModel, ref rng, out var sendDiffusely);
            scattered |= sendDiffusely;

            lastFace = face;
            segment++;
        }
    }

    /// <summary>The face's normal on the side a ray arrives from: a wall's inward normal, or whichever side of a screen the ray hits.</summary>
    internal static Vec3 FacingNormal(RoomFace face, Vec3 direction) => face.InwardNormal.Dot(direction) > 0 ? -face.InwardNormal : face.InwardNormal;

    /// <summary>
    /// Distance along a segment (from its start, unit direction, length) at
    /// which it first enters the receiver sphere, or -1 if it does not enter it.
    /// </summary>
    internal static double SphereEntry(Vec3 start, Vec3 direction, double length, Vec3 centre, double radius)
    {
        var toCentre = centre - start;
        var along = toCentre.Dot(direction);
        var perpendicularSq = toCentre.Dot(toCentre) - along * along;
        var radiusSq = radius * radius;
        if (perpendicularSq >= radiusSq) return -1;
        var entry = along - Math.Sqrt(radiusSq - perpendicularSq);
        return entry > 0 && entry <= length ? entry : -1;
    }

    internal static Vec3 RandomDirection(ref SplitMix64 rng)
    {
        var z = 2 * rng.NextDouble() - 1;
        var phi = 2 * Math.PI * rng.NextDouble();
        var r = Math.Sqrt(1 - z * z);
        return new Vec3(r * Math.Cos(phi), r * Math.Sin(phi), z);
    }

    /// <summary>
    /// The direction a ray leaves a surface by (see <see cref="ScatteringModel"/>). One draw is always taken first, whether or not the model
    /// uses it, and the Lambert draws follow, so that the ray tracer and the billiard rays keep to the same stream and a surface with no
    /// scattering gives the same ray in either model. <paramref name="scattered"/> is whether the ray was turned from the specular direction.
    /// </summary>
    internal static Vec3 Reflect(Vec3 direction, Vec3 normal, double scatter, ScatteringModel model, ref SplitMix64 rng, out bool scattered)
    {
        var specular = direction - normal * (2 * direction.Dot(normal));
        var draw = rng.NextDouble();
        if (model == ScatteringModel.VectorBased)
        {
            scattered = scatter > 0;
            if (scatter <= 0) return specular;
            if (scatter >= 1) return LambertDirection(normal, ref rng);
            // Both vectors lie on the room side of the surface, so the sum cannot vanish.
            return (specular * (1 - scatter) + LambertDirection(normal, ref rng) * scatter).Normalized();
        }

        scattered = draw < scatter;
        return scattered ? LambertDirection(normal, ref rng) : specular;
    }

    /// <summary>A cosine-weighted random direction in the hemisphere around <paramref name="normal"/> (Lambert's law).</summary>
    internal static Vec3 LambertDirection(Vec3 normal, ref SplitMix64 rng)
    {
        var helper = Math.Abs(normal.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        var tangent = normal.Cross(helper).Normalized();
        var bitangent = normal.Cross(tangent);
        var cosTheta = Math.Sqrt(rng.NextDouble());
        var sinTheta = Math.Sqrt(1 - cosTheta * cosTheta);
        var phi = 2 * Math.PI * rng.NextDouble();
        return tangent * (sinTheta * Math.Cos(phi)) + bitangent * (sinTheta * Math.Sin(phi)) + normal * cosTheta;
    }

    private static void ValidateOptions(RayTraceOptions o)
    {
        if (o.RayCount <= 0) throw new ArgumentOutOfRangeException(nameof(o), "The ray count must be positive.");
        if (o.ReceiverRadiusM <= 0) throw new ArgumentOutOfRangeException(nameof(o), "The receiver radius must be positive.");
        if (o.MaxTimeS <= 0 || o.BinWidthS <= 0) throw new ArgumentOutOfRangeException(nameof(o), "The time settings must be positive.");
        if (o.CutoffDb <= 0) throw new ArgumentOutOfRangeException(nameof(o), "The energy cutoff must be positive.");
        if (o.BandCentresHz is not null && (o.BandCentresHz.Count != 8 || o.BandCentresHz.Any(f => !(f > 0) || !double.IsFinite(f))))
            throw new ArgumentOutOfRangeException(nameof(o), "There must be eight positive band centre frequencies.");
        if (o.ImageSourceOrder < 0 || o.ImageSourceOrder > ImageSourceEnumerator.MaxSupportedOrder)
            throw new ArgumentOutOfRangeException(nameof(o), $"The image-source order must be between 0 and {ImageSourceEnumerator.MaxSupportedOrder}.");
    }

    internal static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>SplitMix64: a small, deterministic generator, so each ray gets its own reproducible stream.</summary>
    internal struct SplitMix64
    {
        private ulong _state;

        public SplitMix64(ulong seed) => _state = seed;

        public double NextDouble()
        {
            _state += 0x9E3779B97F4A7C15UL;
            return (Mix(_state) >> 11) * (1.0 / (1UL << 53));
        }
    }
}
