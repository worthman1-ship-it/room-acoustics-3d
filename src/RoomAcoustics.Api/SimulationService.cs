using System.Diagnostics;
using RoomAcoustics.Core;

namespace RoomAcoustics.Api;

/// <summary>Translates the web front end's requests into calls on RoomAcoustics.Core and the results back into plain JSON shapes.</summary>
public static class SimulationService
{
    public const int MaxRayCount = 500_000;
    public const int MaxDrawnRays = 2_000;
    private const double MinSizeM = 1, MaxSizeM = 200;

    public static IReadOnlyList<int> Bands => OctaveSpectrum.Frequencies;

    public static IReadOnlyList<MaterialDto> Materials() =>
        AbsorptionMaterial.Presets.All.Select((m, i) => new MaterialDto(i, m.Name, m.Category, m.Alpha)).ToList();

    /// <summary>
    /// Image sources multiply with every reflection: about (faces to the power of the order) paths are tried. A box has six faces, but an imported model can have
    /// thousands of triangles (25 000 of them took four minutes at order 2), so a model with many faces gets the highest order that keeps that count to about
    /// twenty million. A room of 200 faces or fewer is never limited. The ray tracing itself, which has a spatial index, is not affected.
    /// </summary>
    public static int EffectiveImageSourceOrder(Room3D room, int requested)
    {
        var faces = room.Faces.Count;
        if (faces <= 200 || requested <= 1) return requested;
        var affordable = (int)Math.Floor(Math.Log(20_000_000) / Math.Log(faces));
        return Math.Clamp(affordable, 1, requested);
    }

    /// <summary>The air the sound travels in: -40 to 60 degrees C and 0 to 100 % relative humidity (outside that the sound speed and air absorption mean nothing).</summary>
    private static void CheckAir(double temperatureC, double humidityPercent)
    {
        if (!double.IsFinite(temperatureC) || temperatureC < -40 || temperatureC > 60)
            throw new ArgumentException("The air temperature must be from -40 to 60 degrees C.");
        if (!double.IsFinite(humidityPercent) || humidityPercent < 0 || humidityPercent > 100)
            throw new ArgumentException("The relative humidity must be from 0 to 100 %.");
    }

    /// <summary>The program's version (Directory.Build.props), with the first seven characters of the commit it was built from when the build knows it.</summary>
    public static readonly string Version = VersionOf(typeof(SimulationService).Assembly);

    private static string VersionOf(System.Reflection.Assembly assembly)
    {
        var text = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
        var plus = text.IndexOf('+');
        return plus < 0 ? text : text[..Math.Min(text.Length, plus + 8)];
    }

    public static SimulateResponse Simulate(SimulateRequest request, CancellationToken cancel = default, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckAir(request.TemperatureC, request.HumidityPercent);
        if (request.RayCount < 1_000 || request.RayCount > MaxRayCount)
            throw new ArgumentException($"The ray count must be between 1000 and {MaxRayCount}.");
        if (request.ImageSourceOrder < 0 || request.ImageSourceOrder > ImageSourceEnumerator.MaxSupportedOrder)
            throw new ArgumentException($"The image-source order must be between 0 and {ImageSourceEnumerator.MaxSupportedOrder}.");

        var room = BuildRoom(request.Room, request.Model);
        var source = ToVec(request.Source);
        var receiver = ToVec(request.Receiver);
        var extraReceivers = request.ExtraReceivers ?? Array.Empty<PointDto>();
        if (extraReceivers.Count + 1 > MaxReceivers) throw new ArgumentException($"There can be at most {MaxReceivers} receivers.");
        if (extraReceivers.Any(r => r is null)) throw new ArgumentException("A receiver is missing.");
        var sources = BuildSource(request.Source, request.Directivity, request.ExtraSources, request.SoundPowerDb);
        var options = new RayTraceOptions(
            RayCount: request.RayCount, TemperatureC: request.TemperatureC, RelativeHumidityPercent: request.HumidityPercent,
            Seed: request.Seed, ImageSourceOrder: EffectiveImageSourceOrder(room, request.ImageSourceOrder),
            Cancellation: cancel, Progress: progress, Interaural: true, AngleDependentAbsorption: request.AngleDependentAbsorption);

        var voice = Voice(request.SpeechLevelDbA, request.Voice);
        var power = ValidPower(request.SoundPowerDb);
        CheckPowers(power, request.ExtraSources);
        var fromSources = StiFromSources(request.StiSignal, power);
        var total = power is null ? null : TotalPower(power, request.ExtraSources);
        var clock = Stopwatch.StartNew();
        var regions = Workroom.RegionsFor(request.WorkroomD1M, request.WorkroomD2M);
        var irs = ExtendedSourceTracer.TraceReceivers(room, sources, new[] { receiver }.Concat(extraReceivers.Select(ToVec)).ToList(), options);
        var ir = irs[0];
        var metrics = RoomAcousticsCalculator.Calculate(ir);
        var levels = LevelsAt(metrics.StrengthGDb, power, request.ExtraSources);
        StiDto StiOf(RoomAcousticMetrics m, ImpulseResponse response)
        {
            var speech = SpeechAt(fromSources, m.StrengthGDb, request.SpeechLevelDbA, total);
            var full = SpeechTransmissionIndex.Calculate(speech, NoiseCriterion.Curve(request.BackgroundNc), (band, fm) => m.ModulationTransfer[band][ModulationIndex(fm)], voice);
            // STIPA and RaSTI take other modulation frequencies than the full STI's, so their transfer is worked out from the impulse response itself
            Func<int, double, double> transfer = (band, fm) => RoomLevels.ModulationTransfer(response.Energy[band], response.BinWidthS, fm);
            var (stipa, rasti) = QuickSti(speech, request.BackgroundNc, voice, transfer);
            var sii = SpeechIntelligibilityIndex.InRoom(speech, NoiseCriterion.Curve(request.BackgroundNc), transfer);
            return Sti(full, request.SpeechLevelDbA, request.BackgroundNc, voice, fromSources, speech, stipa, rasti) with { Sii = Number(Math.Round(sii, 2)) };
        }
        clock.Stop();

        var globals = GlobalTimes(room, request.TemperatureC, request.HumidityPercent);

        // Decay curves every 10 ms and the impulse response in 5 ms bins, so the browser gets a few hundred points per band, not thousands.
        const int decayEvery = 10, impulseEvery = 5;
        (DecayDto, ImpulseDto) Curves(ImpulseResponse response)
        {
            var decayCurves = response.Energy.Select(h => Thin(DecayAnalysis.SchroederDb(h), decayEvery)).ToArray();
            var impulseCurves = response.Energy.Select(h => Thin(Db(Rebin(h, impulseEvery)), 1)).ToArray();
            // the overall response is the eight bands' energies added together
            var overall = SumBands(response.Energy);
            return (new DecayDto(response.BinWidthS * decayEvery, decayCurves, Thin(DecayAnalysis.SchroederDb(overall), decayEvery)),
                new ImpulseDto(response.BinWidthS * impulseEvery, impulseCurves, Thin(Db(Rebin(overall, impulseEvery)), 1)));
        }

        ReceiverResultDto ResultAt(PointDto position, RoomAcousticMetrics m, StiDto stiDto, Levels lv, ImpulseResponse response)
        {
            var (decayCurves, impulseCurves) = Curves(response);
            return new(position, (ToVec(position) - source).Length, BandMetrics(m, globals, lv), Broadband(BroadbandMetrics.From(m), lv), stiDto, decayCurves, impulseCurves);
        }
        var receivers = new List<ReceiverResultDto> { ResultAt(request.Receiver, metrics, StiOf(metrics, ir), levels, ir) };
        var allMetrics = new List<RoomAcousticMetrics> { metrics };
        for (var i = 0; i < extraReceivers.Count; i++)
        {
            var m = RoomAcousticsCalculator.Calculate(irs[i + 1]);
            allMetrics.Add(m);
            receivers.Add(ResultAt(extraReceivers[i], m, StiOf(m, irs[i + 1]), LevelsAt(m.StrengthGDb, power, request.ExtraSources), irs[i + 1]));
        }

        return new SimulateResponse(
            room.VolumeM3, room.TotalAreaM2, EnergyRayTracer.SpeedOfSound(request.TemperatureC), (receiver - source).Length, clock.Elapsed.TotalSeconds,
            receivers[0].Metrics, receivers[0].Decay!, receivers[0].Impulse!,
            options.ImageSourceOrder, receivers[0].Broadband, receivers[0].Sti, receivers,
            OpenPlan(request, receivers.Select(r => r.DirectDistanceM).ToList(), allMetrics),
            WorkroomOf(request, power, regions, receivers.Select(r => r.DirectDistanceM).ToList(), allMetrics));
    }

    /// <summary>The fewest positions in a region that DL2, DLf and DL'fr are taken from: a regression needs more than two to mean anything.</summary>
    public const int MinWorkroomPositions = 3;

    /// <summary>
    /// The ISO 14257 results of a run (see <see cref="WorkroomDto"/>): the standard measures one source at a time, so only a run with one source has them; each
    /// receiver is a position, and a region's DL2, DLf and DL'fr need at least <see cref="MinWorkroomPositions"/> of them in it. The regions are divided at the
    /// request's d1 and d2.
    /// </summary>
    private static WorkroomDto WorkroomOf(SimulateRequest request, IReadOnlyList<double>? power, IReadOnlyList<Workroom.Region> regions, IReadOnlyList<double> distances,
        IReadOnlyList<RoomAcousticMetrics> metrics)
    {
        double d1 = regions[1].FromM, d2 = regions[1].ToM;
        var omni = request.Directivity is null;
        if (request.ExtraSources is { Count: > 0 })
            return new WorkroomDto("ISO 14257 measures one source at a time: solo one source to get its results.", omni, d1, d2,
                Array.Empty<WorkroomPositionDto>(), Array.Empty<WorkroomCurveDto>());

        var d = metrics.Select(m => Workroom.Distribution(m.StrengthGDb)).ToList();
        var bands = Enumerable.Range(Workroom.FirstBand, Workroom.LastBand - Workroom.FirstBand + 1).ToList();
        var normalised = d.Select(Workroom.Normalised).ToList();
        var own = power is null ? null : d.Select(x => Workroom.ForSpectrum(x, power)).ToList();
        var positions = d.Select((x, i) => new WorkroomPositionDto(i, Math.Round(distances[i], 4), Math.Round(Workroom.Reference(distances[i]), 4),
            bands.Select(b => Number(x[b])).ToList(), Number(normalised[i]), own is null ? null : Number(own[i]))).ToList();

        var counts = regions.Select(g => distances.Count(r => r >= g.FromM && r <= g.ToM)).ToList();
        if (counts.All(n => n < MinWorkroomPositions))
            return new WorkroomDto($"ISO 14257 takes DL2 and DLf from receivers in a line away from the source, at least {MinWorkroomPositions} in a region: near " +
                $"(1 to {d1:0.##} m) has {counts[0]}, middle ({d1:0.##} to {d2:0.##} m) {counts[1]} and far (beyond {d2:0.##} m) {counts[2]}.",
                omni, d1, d2, positions, Array.Empty<WorkroomCurveDto>());

        WorkroomCurveDto Curve(string name, IReadOnlyList<double> values) => new(name, regions.Select(g =>
        {
            var r = Workroom.Evaluate(distances, values, g);
            var enough = r.Positions >= MinWorkroomPositions;
            return new WorkroomRegionDto(g.Name, g.FromM, double.IsFinite(g.ToM) ? g.ToM : null, g.AtM, enough ? Number(r.DecayPerDoublingDb) : null,
                enough ? Number(r.ExcessDb) : null, enough ? Number(r.ExcessAtDb) : null, r.Positions);
        }).ToList());
        var curves = new List<WorkroomCurveDto> { Curve("normalised", normalised) };
        if (own is not null) curves.Add(Curve("source", own));
        curves.AddRange(bands.Select(b => Curve(BandName(b), d.Select(x => x[b]).ToList())));
        return new WorkroomDto(null, omni, d1, d2, positions, curves);
    }

    private static string BandName(int band) => (125 * (1 << (band - 1))).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The fewest positions more than 1 m from the source that ISO 3382-3 takes a line of measurements from (5.2.2 of 2012, 4.2.3 of 2022).</summary>
    public const int MinOpenPlanPositions = 4;

    /// <summary>
    /// The ISO 3382-3 results of a run (see <see cref="OpenPlanDto"/>): the standard measures one source at a time, so only a run with one source has them; each
    /// receiver is a position, and the single numbers need at least <see cref="MinOpenPlanPositions"/> of them more than 1 m from the source.
    /// </summary>
    private static OpenPlanDto OpenPlan(SimulateRequest request, IReadOnlyList<double> distances, IReadOnlyList<RoomAcousticMetrics> metrics)
    {
        var omni = request.Directivity is null;
        var noise = NoiseCriterion.Curve(request.BackgroundNc);
        double? background = NoiseCriterion.Ratings.Contains(request.BackgroundNc) ? Number(OpenPlanOffice.AWeightedDb(noise)) : null;
        if (request.ExtraSources is { Count: > 0 })
            return new OpenPlanDto("ISO 3382-3 measures one source at a time: solo one source to get its results.", omni, background, Array.Empty<OpenPlanPositionDto>(),
                null, null, null, null, null, null, 0, 0);

        var positions = metrics.Select((m, i) => new OpenPlanOffice.Position(distances[i], OpenPlanOffice.AWeightedDb(OpenPlanOffice.SpeechAt(m.StrengthGDb)),
            OpenPlanOffice.Sti(m.StrengthGDb, noise, (band, fm) => m.ModulationTransfer[band][ModulationIndex(fm)]))).ToList();
        var listed = positions.Select((p, i) => new OpenPlanPositionDto(i, Math.Round(p.DistanceM, 4), Number(p.SpeechDbA), Number(p.Sti))).ToList();
        if (positions.Count(p => p.DistanceM > 1) < MinOpenPlanPositions)
            return new OpenPlanDto($"ISO 3382-3 needs at least {MinOpenPlanPositions} receivers more than 1 m from the source, in a line away from it (6 to 10 are preferred).",
                omni, background, listed, null, null, null, null, null, null, 0, 0);

        var r = OpenPlanOffice.Evaluate(positions);
        return new OpenPlanDto(null, omni, background, listed, Number(r.SpatialDecayDb), Number(r.SpeechAt4mDbA), Number(r.ComfortDistanceM),
            Number(r.DistractionDistanceM), Number(r.PrivacyDistanceM), Number(r.NearestSti), r.DecayPositions, r.StiPositions);
    }

    private static BandMetricsDto BandMetrics(RoomAcousticMetrics metrics, GlobalT60 g, Levels levels) => new(
        Json(metrics.T30S), Json(metrics.T20S), Json(metrics.EarlyDecayTimeS), Json(metrics.C50Db), Json(metrics.C80Db),
        Json(metrics.D50), Json(metrics.CentreTimeS), Json(metrics.StrengthGDb), Json(g.Sabine), Json(g.Eyring),
        Json(metrics.EarlyStrengthGDb), Json(metrics.LateStrengthGDb), Json(metrics.LateralFraction80), Json(metrics.LateralFractionCoefficient80),
        Json(metrics.LateLateralLevelLJDb), Json(metrics.StageSupportEarlyDb), Json(metrics.StageSupportLateDb),
        levels.Spl, levels.SplA, Json(metrics.EchoCriterionSpeech), Json(metrics.EchoCriterionMusic),
        Json(g.Millington), Json(g.ArauPuchades), Json(g.Fitzroy),
        metrics.DirectStrengthGDb is { } direct ? Json(direct) : null, metrics.DirectToReverberantDb is { } ratio ? Json(ratio) : null,
        metrics.StageSupportTotalDb is { } total ? Json(total) : null, metrics.EarlyEnsembleLevelDb is { } ensemble ? Json(ensemble) : null,
        metrics.IaccEarly is { } early ? Json(early) : null, metrics.IaccLate is { } late ? Json(late) : null, metrics.IaccAll is { } all ? Json(all) : null);

    /// <summary>The global reverberation-time estimates, with air absorption, per octave band.</summary>
    private sealed record GlobalT60(OctaveSpectrum Sabine, OctaveSpectrum Eyring, OctaveSpectrum Millington, OctaveSpectrum ArauPuchades, OctaveSpectrum Fitzroy);

    /// <summary>The axis a face belongs to for the Arau-Puchades and Fitzroy estimates: the one its normal is most nearly along (exact for a box; a sloping face of a model goes with its nearest axis).</summary>
    private static int AxisOf(Vec3 n) => Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? 0 : Math.Abs(n.Y) >= Math.Abs(n.Z) ? 1 : 2;

    /// <summary>The room's surfaces for a statistical estimate: each face, and a screen twice, as both its sides take sound in.</summary>
    private static List<Surface> Surfaces(Room3D room) =>
        room.Faces.SelectMany(f => Enumerable.Repeat(new Surface(f.Name, f.AreaM2, f.Finish.Material, AxisOf(f.InwardNormal)), f.IsScreen ? 2 : 1)).ToList();

    private static GlobalT60 GlobalTimes(Room3D room, double temperatureC, double humidityPercent)
    {
        var surfaces = Surfaces(room);
        OctaveSpectrum At(ReverberationMethod m) => ReverberationCalculator.Calculate(room.VolumeM3, surfaces, new ReverberationOptions(m, true, temperatureC, humidityPercent)).T60;
        return new GlobalT60(At(ReverberationMethod.Sabine), At(ReverberationMethod.EyringNorris), At(ReverberationMethod.Millington), At(ReverberationMethod.ArauPuchades), At(ReverberationMethod.Fitzroy));
    }

    private static SpeechWeighting Voice(double speechLevelDbA, string? voice)
    {
        if (!double.IsFinite(speechLevelDbA) || speechLevelDbA < 30 || speechLevelDbA > 100)
            throw new ArgumentException("The speech level at 1 m must be from 30 to 100 dBA.");
        return voice?.ToLowerInvariant() switch
        {
            "male" => SpeechWeighting.Male,
            "female" => SpeechWeighting.Female,
            _ => throw new ArgumentException("The voice must be \"male\" or \"female\"."),
        };
    }

    private static int ModulationIndex(double fm) => SpeechTransmissionIndex.Modulations.ToList().IndexOf(fm);

    private static StiDto Sti(double value, double speechLevelDbA, int backgroundNc, SpeechWeighting voice, bool fromSources, OctaveSpectrum speech,
        double stipa = double.NaN, double rasti = double.NaN) =>
        new(Number(value), double.IsFinite(value) ? SpeechTransmissionIndex.Rating(value) : "", voice == SpeechWeighting.Male ? "male" : "female", speechLevelDbA, backgroundNc,
            Number(SpeechTransmissionIndex.Alcons(value)), Number(stipa), Number(rasti), fromSources ? "sources" : "talker",
            Number(SumLevels(Enumerable.Range(1, 7).Select(b => speech[b] + AWeightingDb[b]))));

    /// <summary>STIPA and RaSTI at a receiver, from the speech there and a modulation transfer function that can be asked for any modulation frequency.</summary>
    private static (double Stipa, double Rasti) QuickSti(OctaveSpectrum speech, int backgroundNc, SpeechWeighting voice, Func<int, double, double> modulationTransfer)
    {
        var noise = NoiseCriterion.Curve(backgroundNc);
        return (SpeechTransmissionIndex.Stipa(speech, noise, modulationTransfer, voice), SpeechTransmissionIndex.Rasti(speech, noise, modulationTransfer));
    }

    /// <summary>Whether the STI's speech is what the sources give ("sources") or a talker's standard speech ("talker"); the sources' needs their sound power.</summary>
    private static bool StiFromSources(string? signal, double[]? power)
    {
        var fromSources = (signal ?? "talker").ToLowerInvariant() switch
        {
            "talker" => false,
            "sources" => true,
            _ => throw new ArgumentException("The STI's speech comes from \"talker\" or \"sources\"."),
        };
        if (fromSources && power is null) throw new ArgumentException("An STI from the sources' own levels needs their sound power.");
        return fromSources;
    }

    /// <summary>
    /// The speech at a receiver that the STI is worked out for: what the sources give there (Lp = Lw + G - 31 in each band, every source together), or a
    /// talker's standard speech at <paramref name="speechLevelDbA"/> at 1 m taken there by G.
    /// </summary>
    private static OctaveSpectrum SpeechAt(bool fromSources, OctaveSpectrum strengthG, double speechLevelDbA, double[]? totalPower) =>
        fromSources ? new OctaveSpectrum(PressureLevels(strengthG, totalPower!)) : SpeechTransmissionIndex.SpeechAtReceiver(strengthG, speechLevelDbA);

    private static BroadbandDto Broadband(BroadbandMetrics b, Levels levels) => new(
        Number(b.T30S), Number(b.T20S), Number(b.EdtS), Number(b.C50Db), Number(b.C80Db), Number(b.D50), Number(b.CentreTimeS), Number(b.StrengthGDb), Number(b.EarlyStrengthGDb),
        Number(b.LateStrengthGDb), Number(b.LateralFraction80), Number(b.LateralFractionCoefficient80), Number(b.LateLateralLevelDb), Number(b.StageSupportEarlyDb), Number(b.StageSupportLateDb),
        levels.Total, levels.TotalA, Number(b.EchoCriterionSpeech), Number(b.EchoCriterionMusic), Number(b.BassRatio), Number(b.Brilliance),
        Number(b.DirectStrengthGDb), Number(b.DirectToReverberantDb), Number(b.StageSupportTotalDb), Number(b.EarlyEnsembleLevelDb),
        Number(b.IaccE3), Number(b.IaccL3), Number(b.BinauralQualityIndex), Number(b.IaccA3));

    /// <summary>The sound pressure levels at a receiver, by octave band, un-weighted and A-weighted, and as one number each (the bands' energies added); null where no source power was given.</summary>
    private sealed record Levels(double?[] Spl, double?[] SplA, double? Total, double? TotalA);

    private static readonly double[] AWeightingDb = { -26.2, -16.1, -8.6, -3.2, 0, 1.2, 1.0, -1.1 };

    /// <summary>The first source's sound power level in each octave band (63 Hz to 8 kHz): eight finite levels, or none.</summary>
    private static double[]? ValidPower(IReadOnlyList<double>? power)
    {
        if (power is null) return null;
        if (power.Count != 8 || power.Any(v => !double.IsFinite(v) || v < -20 || v > 200))
            throw new ArgumentException("The source sound power needs eight levels, 63 Hz to 8 kHz, each between -20 and 200 dB.");
        return power.ToArray();
    }

    /// <summary>
    /// Every source's sound power level in each octave band: the first source's, then each further one's own (<see cref="SourceDto.SoundPowerDb"/>) or, without one, the
    /// first's moved by its gain. Null when the first source's power is not given, which leaves the levels unworked (and the sources shared by their gains alone).
    /// </summary>
    private static double[][]? SourcePowers(double[]? first, IReadOnlyList<SourceDto>? extras)
    {
        if (first is null) return null;
        var powers = new List<double[]> { first };
        foreach (var extra in extras ?? Array.Empty<SourceDto>())
            powers.Add(ValidPower(extra?.SoundPowerDb) ?? first.Select(v => v + (extra?.GainDb ?? 0)).ToArray());
        return powers.ToArray();
    }

    /// <summary>Sources with a sound power of their own are meaningless without the first source's: a request that gives them without it is refused.</summary>
    private static void CheckPowers(double[]? first, IReadOnlyList<SourceDto>? extras)
    {
        if (first is null && extras?.Any(e => e?.SoundPowerDb is not null) == true)
            throw new ArgumentException("Sources with a sound power of their own need the first source's sound power too.");
    }

    /// <summary>The sound power of every source together in each octave band: the powers added as energies.</summary>
    private static double[] TotalPower(double[]? first, IReadOnlyList<SourceDto>? extras)
    {
        var powers = SourcePowers(first, extras) ?? throw new InvalidOperationException("No source power was given.");
        return Enumerable.Range(0, 8).Select(b => 10 * Math.Log10(powers.Sum(p => Math.Pow(10, p[b] / 10)))).ToArray();
    }

    private static double SumLevels(IEnumerable<double> levels)
    {
        var finite = levels.Where(double.IsFinite).ToList();
        return finite.Count == 0 ? double.NaN : 10 * Math.Log10(finite.Sum(l => Math.Pow(10, l / 10)));
    }

    /// <summary>Lp = Lw + G - 31 in each band (see <see cref="RoomLevels"/>), where Lw is the power of all the sources together.</summary>
    private static double[] PressureLevels(OctaveSpectrum strengthG, double[] totalPower) =>
        Enumerable.Range(0, 8).Select(b => totalPower[b] + strengthG[b] - RoomLevels.FreeFieldLevelOffsetAt10mDb).ToArray();

    private static Levels LevelsAt(OctaveSpectrum strengthG, double[]? power, IReadOnlyList<SourceDto>? extras)
    {
        if (power is null) return new Levels(NotWorkedOut, NotWorkedOut, null, null);
        var spl = PressureLevels(strengthG, TotalPower(power, extras));
        var splA = spl.Select((v, b) => v + AWeightingDb[b]).ToArray();
        return new Levels(spl.Select(Number).ToArray(), splA.Select(Number).ToArray(), Number(SumLevels(spl)), Number(SumLevels(splA)));
    }

    /// <summary>
    /// The quick run: the same kind of result as <see cref="Simulate"/> from the standard statistical equations alone (see <see cref="StatisticalEstimate"/>),
    /// in no time. It knows the room's volume and its surfaces' absorption, and of the sources their places, patterns (each one's gain towards the receiver),
    /// delays and powers; not the room's shape. Every source's direct sound is added to one reverberant field; the early and late windows are counted from
    /// the first direct sound to arrive.
    /// </summary>
    public static SimulateResponse Statistical(SimulateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckAir(request.TemperatureC, request.HumidityPercent);
        var voice = Voice(request.SpeechLevelDbA, request.Voice);
        var power = ValidPower(request.SoundPowerDb);
        CheckPowers(power, request.ExtraSources);
        var fromSources = StiFromSources(request.StiSignal, power);
        var total = power is null ? null : TotalPower(power, request.ExtraSources);
        var clock = Stopwatch.StartNew();
        var room = BuildRoom(request.Room, request.Model);
        var receiver = ToVec(request.Receiver);
        var distance = Math.Max(0.1, (receiver - ToVec(request.Source)).Length);
        var surfaces = Surfaces(room);
        var globals = GlobalTimes(room, request.TemperatureC, request.HumidityPercent);
        var c = EnergyRayTracer.SpeedOfSound(request.TemperatureC);

        // each source's direct sound at the receiver: its share of the power in the band, its pattern's gain towards the receiver, its own distance and delay
        var sources = BuildSource(request.Source, request.Directivity, request.ExtraSources, request.SoundPowerDb);
        var shares = sources.SharesByBand();
        double Gain(SourcePoint p, int band) =>
            p.Directivity is null || (receiver - p.Position).Length < 1e-9 ? 1 : p.Directivity.ForBand(band).Gain((receiver - p.Position).Normalized());
        var arrivals = sources.Points.Select((p, k) =>
        {
            var r = Math.Max(0.1, (receiver - p.Position).Length);
            return (TimeS: r / c + p.DelayS, Energy: Enumerable.Range(0, 8).Select(b => shares[k][b] * 100 * Gain(p, b) / (r * r)).ToArray());
        }).ToList();
        var direct = Enumerable.Range(0, 8).Select(b => arrivals.Sum(x => x.Energy[b])).ToArray();
        var firstQ = Enumerable.Range(0, 8).Select(b => Gain(sources.Points[0], b)).ToArray(); // the critical distance is the first source's
        var estimate = StatisticalEstimate.Calculate(room.VolumeM3, surfaces, direct, firstQ, new ReverberationOptions(ReverberationMethod.EyringNorris, true, request.TemperatureC, request.HumidityPercent));
        var levels = LevelsAt(estimate.StrengthGDb, power, request.ExtraSources);

        // a diffuse field decays at one rate: 60 dB in Eyring's time, in every measure of it
        var t = estimate.EyringT60S;
        const double decayStep = 0.01, impulseStep = 0.005;
        var length = Math.Min(3000, (int)Math.Ceiling(1.3 * t.Bands.Max() / decayStep) + 1);
        var decay = Enumerable.Range(0, 8).Select(b => Enumerable.Range(0, length).Select(i => Number(-60 * i * decayStep / t[b])).ToArray()).ToArray();

        // each direct sound arrives at its own time, and the reverberant energy falls away from the first of them in 5 ms bins; energies are relative to all the
        // direct sound together
        var first = (int)Math.Floor(arrivals.Min(x => x.TimeS) / impulseStep);
        var bins = Math.Min(3000, first + (int)Math.Ceiling(1.3 * t.Bands.Max() / impulseStep) + 1);
        var energies = Enumerable.Range(0, 8).Select(b =>
        {
            var reverberant = 31200 * t[b] / room.VolumeM3;
            var perBin = 1 - Math.Exp(-13.82 * impulseStep / t[b]);
            var e = Enumerable.Range(0, bins).Select(i => i < first ? 0 : reverberant * Math.Exp(-13.82 * (i - first) * impulseStep / t[b]) * perBin).ToArray();
            foreach (var x in arrivals)
            {
                var at = (int)Math.Floor(x.TimeS / impulseStep);
                if (at < bins) e[at] += x.Energy[b];
            }
            return e;
        }).ToArray();
        double?[] Relative(double[] energy, double reference) => energy.Select((e, i) => i < first ? (double?)null : Number(10 * Math.Log10(e / reference))).ToArray();
        var impulse = energies.Select((e, b) => Relative(e, direct[b])).ToArray();
        var overallImpulse = Relative(SumBands(energies), direct.Sum());

        // the overall decay: the bands' reverberant energies added, then integrated backwards
        var overallDecay = Thin(DecayAnalysis.SchroederDb(SumBands(Enumerable.Range(0, 8).Select(b =>
            Enumerable.Range(0, length).Select(i => 31200 * t[b] / room.VolumeM3 * Math.Exp(-13.82 * i * decayStep / t[b]) * (1 - Math.Exp(-13.82 * decayStep / t[b]))).ToArray()).ToArray())), 1);
        clock.Stop();

        var speech = SpeechAt(fromSources, estimate.StrengthGDb, request.SpeechLevelDbA, total);
        var sti = SpeechTransmissionIndex.Calculate(speech, NoiseCriterion.Curve(request.BackgroundNc), (band, fm) => estimate.ModulationTransfer[band][ModulationIndex(fm)], voice);
        Func<int, double, double> transfer = (band, fm) =>
            StatisticalEstimate.ModulationTransfer(fm, direct[band], StatisticalEstimate.Energies(1, room.VolumeM3, t[band]).Reverberant, t[band]);
        var (stipa, rasti) = QuickSti(speech, request.BackgroundNc, voice, transfer);
        var sii = SpeechIntelligibilityIndex.InRoom(speech, NoiseCriterion.Curve(request.BackgroundNc), transfer);

        return new SimulateResponse(
            room.VolumeM3, room.TotalAreaM2, c, distance, clock.Elapsed.TotalSeconds,
            new BandMetricsDto(Json(t), Json(t), Json(t), Json(estimate.C50Db), Json(estimate.C80Db), Json(estimate.D50), Json(estimate.CentreTimeS),
                Json(estimate.StrengthGDb), Json(estimate.SabineT60S), Json(t),
                Json(estimate.EarlyStrengthGDb), Json(estimate.LateStrengthGDb), NotWorkedOut, NotWorkedOut, NotWorkedOut, NotWorkedOut, NotWorkedOut,
                levels.Spl, levels.SplA, NotWorkedOut, NotWorkedOut, Json(globals.Millington), Json(globals.ArauPuchades), Json(globals.Fitzroy)),
            new DecayDto(decayStep, decay, overallDecay), new ImpulseDto(impulseStep, impulse, overallImpulse), 0, Broadband(BroadbandMetrics.From(estimate), levels),
            Sti(sti, request.SpeechLevelDbA, request.BackgroundNc, voice, fromSources, speech, stipa, rasti) with { Sii = Number(Math.Round(sii, 2)) });
    }

    /// <summary>
    /// The room at the receiver in the 23 third-octave bands 50 Hz to 8 kHz: three passes of the tracer, each with the surfaces' absorption and scattering
    /// interpolated to its eight bands (Core's <c>ThirdOctaveSimulation</c>), so it takes about three times a run.
    /// </summary>
    public static ThirdOctaveResponse ThirdOctaves(SimulateRequest request, CancellationToken cancel = default, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckAir(request.TemperatureC, request.HumidityPercent);
        if (request.RayCount < 1_000 || request.RayCount > MaxRayCount)
            throw new ArgumentException($"The ray count must be between 1000 and {MaxRayCount}.");
        if (request.ImageSourceOrder < 0 || request.ImageSourceOrder > ImageSourceEnumerator.MaxSupportedOrder)
            throw new ArgumentException($"The image-source order must be between 0 and {ImageSourceEnumerator.MaxSupportedOrder}.");

        var room = BuildRoom(request.Room, request.Model);
        CheckPowers(ValidPower(request.SoundPowerDb), request.ExtraSources);
        var sources = BuildSource(request.Source, request.Directivity, request.ExtraSources, request.SoundPowerDb);
        var options = new RayTraceOptions(
            RayCount: request.RayCount, TemperatureC: request.TemperatureC, RelativeHumidityPercent: request.HumidityPercent,
            Seed: request.Seed, ImageSourceOrder: EffectiveImageSourceOrder(room, request.ImageSourceOrder), Cancellation: cancel, Progress: progress,
            AngleDependentAbsorption: request.AngleDependentAbsorption);

        var clock = Stopwatch.StartNew();
        var bands = ThirdOctaveSimulation.Calculate(room, sources, ToVec(request.Receiver), options);
        return new ThirdOctaveResponse(bands.Select(b => new ThirdOctaveBandDto(b.NominalHz, Number(b.T30S), Number(b.T20S), Number(b.EdtS), Number(b.C80Db),
            Number(b.C50Db), Number(b.D50), Number(b.CentreTimeS), Number(b.StrengthGDb))).ToList(), clock.Elapsed.TotalSeconds);
    }

    public static IReadOnlyList<RayDto> Rays(RaysRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckAir(request.TemperatureC, request.HumidityPercent);
        if (request.RayCount < 1 || request.RayCount > MaxDrawnRays)
            throw new ArgumentException($"The number of rays to draw must be between 1 and {MaxDrawnRays}.");
        if (request.Band < BilliardRays.Overall || request.Band > 7) throw new ArgumentException("The band must be 0 (63 Hz) to 7 (8 kHz), or -1 for the overall response.");

        var room = BuildRoom(request.Room, request.Model);
        var options = new RayTraceOptions(TemperatureC: request.TemperatureC, RelativeHumidityPercent: request.HumidityPercent, Seed: request.Seed);
        var receiver = request.Receiver is { } r ? ToVec(r) : (Vec3?)null;
        var radius = options.ReceiverRadiusM;

        // the rays are shared between the sources, each drawn with its own pattern
        var sources = BuildSource(request.Source, request.Directivity, request.ExtraSources).Points;
        var share = Math.Max(1, request.RayCount / sources.Count);
        return sources
            .SelectMany(point => BilliardRays.Trace(room, point.Position, share, request.MaxReflections, request.Band, options with { Directivity = point.Directivity }))
            .Select(ray => new RayDto(
                ray.Points.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
                ray.Reflections, ray.Scattered,
                receiver is { } rx && BilliardRays.ReachesReceiver(ray, rx, radius),
                ray.LevelsDb!.ToArray()))
            .ToList();
    }

    /// <summary>
    /// A binaural impulse response of the room at a listener, by rendering a single unit click through <see cref="RoomAuralisation"/> (a click convolved
    /// with the response is the response), so the browser can convolve any sound with it.
    /// </summary>
    public static AuraliseResponse Auralise(AuraliseRequest request, CancellationToken cancel = default, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckAir(request.TemperatureC, request.HumidityPercent);
        if (request.RayCount < 20_000 || request.RayCount > MaxRayCount) throw new ArgumentException($"The ray count must be between 20000 and {MaxRayCount} (two small ears need many rays).");
        if (request.ImageSourceOrder < 0 || request.ImageSourceOrder > ImageSourceEnumerator.MaxSupportedOrder)
            throw new ArgumentException($"The image-source order must be between 0 and {ImageSourceEnumerator.MaxSupportedOrder}.");
        if (request.SampleRateHz is not (22_050 or 32_000 or 44_100 or 48_000)) throw new ArgumentException("The sample rate must be 22050, 32000, 44100 or 48000 Hz.");
        if (!double.IsFinite(request.FacingAzimuthDeg)) throw new ArgumentException("The facing direction must be a number.");

        var room = BuildRoom(request.Room, request.Model);
        var options = new RayTraceOptions(RayCount: request.RayCount, TemperatureC: request.TemperatureC, RelativeHumidityPercent: request.HumidityPercent,
            Seed: request.Seed, ImageSourceOrder: EffectiveImageSourceOrder(room, request.ImageSourceOrder), Cancellation: cancel, Progress: progress,
            AngleDependentAbsorption: request.AngleDependentAbsorption);
        CheckPowers(ValidPower(request.SoundPowerDb), request.ExtraSources);
        var source = BuildSource(request.Source, request.Directivity, request.ExtraSources, request.SoundPowerDb);

        var hrtf = request.HrtfId is { } id ? HrtfService.Get(id) : null;
        var clock = Stopwatch.StartNew();
        var heard = RoomAuralisation.Render(room, source, ToVec(request.Listener), request.FacingAzimuthDeg, new[] { 1.0 }, request.SampleRateHz, options, hrtf: hrtf);
        // IACC of the head as it faces, from where the sound comes from (the rendered ears' noise is independent, so their own correlation would say little)
        var iaccEarly = InterauralCoherence.Bands(heard.Head!.Early);
        var iaccLate = InterauralCoherence.Bands(heard.Head.Late);
        var e3 = BroadbandMetrics.Mean(iaccEarly, 3, 4, 5);
        var separate = request.Separate && request.ExtraSources is { Count: > 0 } ? Separately(request, room, source, options, hrtf) : null;
        clock.Stop();

        return new AuraliseResponse(request.SampleRateHz, heard.Left.Length, Pack(heard.Left), Pack(heard.Right), heard.DurationS,
            Math.Round(heard.InterauralLevelDifferenceDb, 2), Math.Round(heard.InterauralTimeDifferenceMs, 3),
            Number(e3), iaccEarly.Bands.Select(Number).ToArray(), iaccLate.Bands.Select(Number).ToArray(),
            Number(BroadbandMetrics.Mean(iaccLate, 3, 4, 5)), Number(1 - e3), clock.Elapsed.TotalSeconds, separate);
    }

    /// <summary>
    /// Each source's two ears' responses on its own, at its share of all the sources' power (the mean of its shares in the eight bands, as the responses
    /// heard together share it), scaled all by one factor so that the largest sample of any is 0.5: mixed as they are, they keep their levels.
    /// </summary>
    private static IReadOnlyList<EarsDto> Separately(AuraliseRequest request, Room3D room, ExtendedSource all, RayTraceOptions options, MeasuredHrtf? hrtf)
    {
        var groups = SourceGroups(request.Source, request.Directivity, request.ExtraSources!, request.SoundPowerDb);
        var shares = all.SharesByBand();
        var renders = new List<(double[] Left, double[] Right)>();
        var at = 0;
        foreach (var group in groups)
        {
            var share = Enumerable.Range(0, 8).Average(b => Enumerable.Range(at, group.Count).Sum(k => shares[k][b]));
            at += group.Count;
            var heard = RoomAuralisation.Render(room, new ExtendedSource(group), ToVec(request.Listener), request.FacingAzimuthDeg, new[] { 1.0 }, request.SampleRateHz,
                options with { Progress = null }, hrtf: hrtf);
            var gain = Math.Sqrt(share) / heard.Scale;
            renders.Add((heard.Left.Select(v => v * gain).ToArray(), heard.Right.Select(v => v * gain).ToArray()));
        }
        var top = renders.Max(r => Math.Max(r.Left.Max(Math.Abs), r.Right.Max(Math.Abs)));
        var common = top > 0 ? 0.5 / top : 1;
        return renders.Select(r => new EarsDto(Pack(r.Left.Select(v => v * common).ToArray()), Pack(r.Right.Select(v => v * common).ToArray()))).ToList();
    }

    /// <summary>The first-order Ambisonic (AmbiX) impulse response at the listener's position, from the same request as the binaural one.</summary>
    public static AmbisonicResponse Ambisonic(AuraliseRequest request, CancellationToken cancel = default, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckAir(request.TemperatureC, request.HumidityPercent);
        if (request.RayCount < 20_000 || request.RayCount > MaxRayCount) throw new ArgumentException($"The ray count must be between 20000 and {MaxRayCount}.");
        if (request.ImageSourceOrder < 0 || request.ImageSourceOrder > ImageSourceEnumerator.MaxSupportedOrder)
            throw new ArgumentException($"The image-source order must be between 0 and {ImageSourceEnumerator.MaxSupportedOrder}.");
        if (request.SampleRateHz is not (22_050 or 32_000 or 44_100 or 48_000)) throw new ArgumentException("The sample rate must be 22050, 32000, 44100 or 48000 Hz.");
        if (request.AmbisonicOrder < 1 || request.AmbisonicOrder > Ambisonics.MaxOrder) throw new ArgumentException($"The Ambisonic order must be 1 to {Ambisonics.MaxOrder}.");

        var room = BuildRoom(request.Room, request.Model);
        var options = new RayTraceOptions(RayCount: request.RayCount, TemperatureC: request.TemperatureC, RelativeHumidityPercent: request.HumidityPercent,
            Seed: request.Seed, ImageSourceOrder: EffectiveImageSourceOrder(room, request.ImageSourceOrder), Cancellation: cancel, Progress: progress,
            AngleDependentAbsorption: request.AngleDependentAbsorption);
        CheckPowers(ValidPower(request.SoundPowerDb), request.ExtraSources);
        var source = BuildSource(request.Source, request.Directivity, request.ExtraSources, request.SoundPowerDb);

        var clock = Stopwatch.StartNew();
        var channels = Ambisonics.Render(room, source, ToVec(request.Listener), request.SampleRateHz, options, order: request.AmbisonicOrder);
        clock.Stop();
        return new AmbisonicResponse(request.SampleRateHz, channels[0].Length, channels.Select(Pack).ToList(), channels[0].Length / (double)request.SampleRateHz, clock.Elapsed.TotalSeconds);
    }

    private static string Pack(double[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), (float)samples[i]);
        return Convert.ToBase64String(bytes);
    }

    public const int MaxMapPoints = 3_000;

    /// <summary>The metrics a map can show: the name, its unit and how to read it from the metrics of one receiver (null where it cannot be worked out).</summary>
    private static readonly Dictionary<string, (string Unit, Func<RoomAcousticMetrics, OctaveSpectrum> Of, double Scale, Func<BroadbandMetrics, double> Broadband)> MapMetrics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["T30"] = ("s", m => m.T30S, 1, b => b.T30S), ["T20"] = ("s", m => m.T20S, 1, b => b.T20S), ["EDT"] = ("s", m => m.EarlyDecayTimeS, 1, b => b.EdtS),
        ["C80"] = ("dB", m => m.C80Db, 1, b => b.C80Db), ["C50"] = ("dB", m => m.C50Db, 1, b => b.C50Db), ["D50"] = ("%", m => m.D50, 100, b => b.D50),
        ["G"] = ("dB", m => m.StrengthGDb, 1, b => b.StrengthGDb), ["Ts"] = ("s", m => m.CentreTimeS, 1, b => b.CentreTimeS),
        ["Gearly"] = ("dB", m => m.EarlyStrengthGDb, 1, b => b.EarlyStrengthGDb), ["Glate"] = ("dB", m => m.LateStrengthGDb, 1, b => b.LateStrengthGDb),
        ["LF"] = ("", m => m.LateralFraction80, 1, b => b.LateralFraction80), ["LFC"] = ("", m => m.LateralFractionCoefficient80, 1, b => b.LateralFractionCoefficient80),
        ["LJ"] = ("dB", m => m.LateLateralLevelLJDb, 1, b => b.LateLateralLevelDb),
        ["STearly"] = ("dB", m => m.StageSupportEarlyDb, 1, b => b.StageSupportEarlyDb), ["STlate"] = ("dB", m => m.StageSupportLateDb, 1, b => b.StageSupportLateDb),
        ["STtotal"] = ("dB", m => m.StageSupportTotalDb ?? OctaveSpectrum.Flat(double.NaN), 1, b => b.StageSupportTotalDb),
        ["EEL"] = ("dB", m => m.EarlyEnsembleLevelDb ?? OctaveSpectrum.Flat(double.NaN), 1, b => b.EarlyEnsembleLevelDb),
        ["EKspeech"] = ("", m => m.EchoCriterionSpeech, 1, b => b.EchoCriterionSpeech), ["EKmusic"] = ("", m => m.EchoCriterionMusic, 1, b => b.EchoCriterionMusic),
        ["Gdirect"] = ("dB", m => m.DirectStrengthGDb ?? OctaveSpectrum.Flat(double.NaN), 1, b => b.DirectStrengthGDb),
        ["DR"] = ("dB", m => m.DirectToReverberantDb ?? OctaveSpectrum.Flat(double.NaN), 1, b => b.DirectToReverberantDb),
        // a head at each point facing the source; BQI in a band is 1 less that band's early IACC
        ["IACCearly"] = ("", m => m.IaccEarly ?? OctaveSpectrum.Flat(double.NaN), 1, b => b.IaccE3),
        ["IACClate"] = ("", m => m.IaccLate ?? OctaveSpectrum.Flat(double.NaN), 1, b => b.IaccL3),
        ["IACCall"] = ("", m => m.IaccAll ?? OctaveSpectrum.Flat(double.NaN), 1, b => b.IaccA3),
        ["BQI"] = ("", m => m.IaccEarly is { } e ? new OctaveSpectrum(e.Bands.Select(v => 1 - v).ToArray()) : OctaveSpectrum.Flat(double.NaN), 1, b => b.BinauralQualityIndex),
    };

    /// <summary>The Speech Transmission Index: a map can show it as well, but it is one number at a point, not one per band.</summary>
    public const string StiMetric = "STI";

    /// <summary>What a map shows besides the table above: one number at a point (%ALcons from the STI, Beranek's bass and brilliance ratios) and, with a source sound power, the sound pressure level.</summary>
    private static readonly string[] OtherMapMetrics = { StiMetric, "SII", "Alcons", "BassRatio", "Brilliance", "SPL", "SPLA", ReflectorMetric };

    /// <summary>A map of where a first reflection from a chosen surface arrives within a time window after the direct sound, as its level against the direct sound.</summary>
    public const string ReflectorMetric = "Reflector";

    public static IReadOnlyCollection<string> MapMetricNames => MapMetrics.Keys.Concat(OtherMapMetrics).ToList();

    public static MapResponse Map(MapRequest request, CancellationToken cancel = default, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckAir(request.TemperatureC, request.HumidityPercent);
        var shown = MapMetricNames.FirstOrDefault(k => k.Equals(request.Metric, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown metric '{request.Metric}'. Use one of: {string.Join(", ", MapMetricNames)}.");
        var voice = Voice(request.SpeechLevelDbA, request.Voice);
        var power = ValidPower(request.SoundPowerDb);
        CheckPowers(power, request.ExtraSources);
        var fromSources = StiFromSources(request.StiSignal, power);
        var total = power is null ? null : TotalPower(power, request.ExtraSources);
        if (power is null && shown is "SPL" or "SPLA") throw new ArgumentException("A sound pressure level map needs the source sound power.");
        var reflector = string.IsNullOrWhiteSpace(request.ReflectorGroup) ? null : request.ReflectorGroup;
        if (shown == ReflectorMetric && reflector is null) throw new ArgumentException("A reflector coverage map needs the surface whose reflections it shows.");
        if (!double.IsFinite(request.ReflectorFromMs) || !double.IsFinite(request.ReflectorToMs) || request.ReflectorFromMs < 0 || request.ReflectorToMs <= request.ReflectorFromMs || request.ReflectorToMs > 500)
            throw new ArgumentException("The reflector window must run from 0 or more to at most 500 ms after the direct sound, and end after it starts.");
        if (request.RayCount < 1_000 || request.RayCount > MaxRayCount) throw new ArgumentException($"The ray count must be between 1000 and {MaxRayCount}.");
        if (request.ImageSourceOrder < 0 || request.ImageSourceOrder > ImageSourceEnumerator.MaxSupportedOrder)
            throw new ArgumentException($"The image-source order must be between 0 and {ImageSourceEnumerator.MaxSupportedOrder}.");
        if (!double.IsFinite(request.SpacingM) || request.SpacingM < 0.25 || request.SpacingM > 20) throw new ArgumentException("The grid spacing must be between 0.25 and 20 metres.");
        // a receiver is a 0.5 m sphere, so it must be at least that far from every surface
        if (!double.IsFinite(request.ClearanceM) || request.ClearanceM < 0.5) throw new ArgumentException("The clearance must be at least 0.5 m (the receiver sphere).");
        if (!double.IsFinite(request.HeightM)) throw new ArgumentException("The height must be a number.");
        var mode = (request.Mode ?? "plan").ToLowerInvariant();
        if (mode is not ("plan" or "section" or "volume" or "surface")) throw new ArgumentException("The map mode must be plan, section, volume or surface.");

        var room = BuildRoom(request.Room, request.Model);
        var sources = BuildSource(request.Source, request.Directivity, request.ExtraSources, request.SoundPowerDb);
        var options = new RayTraceOptions(RayCount: request.RayCount, TemperatureC: request.TemperatureC, RelativeHumidityPercent: request.HumidityPercent,
            Seed: request.Seed, ImageSourceOrder: EffectiveImageSourceOrder(room, request.ImageSourceOrder), Cancellation: cancel, Progress: progress,
            AngleDependentAbsorption: request.AngleDependentAbsorption, Interaural: true); // for the IACC parameters: a few per cent more memory per point than its bins

        IReadOnlyList<Vec3> grid;
        string? axis = null;
        double? position = null;
        IReadOnlyList<double>? heights = null;
        IReadOnlyList<IReadOnlyList<double>>? outline = null;
        switch (mode)
        {
            case "section":
                var sectionAxis = (request.Axis ?? "").ToLowerInvariant() switch
                {
                    "x" => SectionAxis.X,
                    "y" => SectionAxis.Y,
                    _ => throw new ArgumentException("The section axis must be x (the plane x = position) or y."),
                };
                if (!double.IsFinite(request.PositionM)) throw new ArgumentException("The section position must be a number.");
                grid = RoomSection.Grid(room, sectionAxis, request.PositionM, request.SpacingM, request.ClearanceM);
                outline = RoomSection.Outline(room, sectionAxis, request.PositionM).Select(seg => (IReadOnlyList<double>)new[] { seg.U1, seg.V1, seg.U2, seg.V2 }).ToList();
                axis = sectionAxis.ToString().ToLowerInvariant();
                position = request.PositionM;
                break;
            case "volume":
                if (request.HeightsM is not { Count: > 0 } wanted || wanted.Count > RoomVolume.MaxHeights || wanted.Any(h => !double.IsFinite(h)))
                    throw new ArgumentException($"A volume map needs 1 to {RoomVolume.MaxHeights} heights.");
                grid = RoomVolume.Grid(room, wanted, request.SpacingM, request.ClearanceM, out var empty);
                heights = wanted.Where(h => !empty.Contains(h)).Order().ToList();
                break;
            case "surface":
                if (string.IsNullOrWhiteSpace(request.SurfaceGroup)) throw new ArgumentException("A surface map needs the surface to map above.");
                grid = ReceiverGrid.OverSurface(room, name => IsOnSurface(name, request.SurfaceGroup), request.HeightM, request.SpacingM, request.ClearanceM);
                break;
            default:
                grid = ReceiverGrid.Horizontal(room, request.HeightM, request.SpacingM, request.ClearanceM);
                break;
        }

        // a receiver on top of a source cannot be a sphere outside it
        var points = grid.Where(p => sources.Points.All(s => (p - s.Position).Length > options.ReceiverRadiusM + 0.05)).ToList();
        if (points.Count == 0) throw new ArgumentException("No grid point is clear of the surfaces and the source: reduce the spacing or the clearance, or change the height.");
        if (points.Count > MaxMapPoints) throw new ArgumentException($"That grid has {points.Count} points; the limit is {MaxMapPoints}. Use a larger spacing.");

        var clock = Stopwatch.StartNew();
        // every receiver is traced in all eight octave bands at once, so the page picks the band to show without another run
        var responses = ExtendedSourceTracer.TraceReceivers(room, sources, points, options);
        var metrics = responses.Select(RoomAcousticsCalculator.Calculate).ToList();
        // every parameter is read from the same traced responses, so the page switches parameter (and between a band and the broadband value) without another run
        var broadband = metrics.Select(BroadbandMetrics.From).ToList();
        var parameters = MapMetrics.ToDictionary(kv => kv.Key, kv => new MapParameterDto(kv.Value.Unit,
            metrics.Select(m => Enumerable.Range(0, 8).Select(b => Number(kv.Value.Of(m)[b] * kv.Value.Scale)).ToArray()).ToArray(),
            broadband.Select(b => Number(kv.Value.Broadband(b) * kv.Value.Scale)).ToArray()));
        var stis = metrics.Select(m => SpeechTransmissionIndex.Calculate(SpeechAt(fromSources, m.StrengthGDb, request.SpeechLevelDbA, total), NoiseCriterion.Curve(request.BackgroundNc),
            (band, fm) => m.ModulationTransfer[band][ModulationIndex(fm)], voice)).ToList();
        parameters[StiMetric] = new MapParameterDto("", null, stis.Select(Number).ToArray());
        parameters["Alcons"] = new MapParameterDto("%", null, stis.Select(s => Number(SpeechTransmissionIndex.Alcons(s))).ToArray());
        // the SII's modulation frequencies are not the STI's, so its modulation transfer is worked out from each point's response itself
        var noise = NoiseCriterion.Curve(request.BackgroundNc);
        parameters["SII"] = new MapParameterDto("", null, metrics.Select((m, i) => Number(Math.Round(SpeechIntelligibilityIndex.InRoom(
            SpeechAt(fromSources, m.StrengthGDb, request.SpeechLevelDbA, total), noise,
            (band, fm) => RoomLevels.ModulationTransfer(responses[i].Energy[band], responses[i].BinWidthS, fm)), 2))).ToArray());
        parameters["BassRatio"] = new MapParameterDto("", null, broadband.Select(b => Number(b.BassRatio)).ToArray());
        parameters["Brilliance"] = new MapParameterDto("", null, broadband.Select(b => Number(b.Brilliance)).ToArray());
        if (reflector is not null)
        {
            if (!room.Faces.Any(f => IsReflectorSurface(f.Name, reflector))) throw new ArgumentException($"There is no surface '{reflector}' in this room.");
            var first = sources.Points[0];
            var radius = options.ReceiverRadiusM;
            double? Covered(Vec3 point)
            {
                var strongest = ReflectionList.For(room, first.Position, point, 1, request.TemperatureC, request.HumidityPercent, first.Directivity, radius)
                    .Where(r => IsReflectorSurface(r.FaceNames[0], reflector) && r.DelayMs >= request.ReflectorFromMs && r.DelayMs <= request.ReflectorToMs)
                    .Select(r => OverallLevel(r.LevelDb)).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max();
                return Number(strongest);
            }
            parameters[ReflectorMetric] = new MapParameterDto("dB", null, points.Select(Covered).ToArray());
        }
        if (power is not null)
        {
            var spl = metrics.Select(m => PressureLevels(m.StrengthGDb, total!)).ToList();
            parameters["SPL"] = new MapParameterDto("dB", spl.Select(l => l.Select(Number).ToArray()).ToArray(), spl.Select(l => Number(SumLevels(l))).ToArray());
            parameters["SPLA"] = new MapParameterDto("dB(A)", spl.Select(l => l.Select((v, b) => Number(v + AWeightingDb[b])).ToArray()).ToArray(),
                spl.Select(l => Number(SumLevels(l.Select((v, b) => v + AWeightingDb[b])))).ToArray());
        }
        clock.Stop();

        return new MapResponse(shown, parameters[shown].Unit,
            mode is "plan" or "surface" ? request.HeightM : 0, request.SpacingM, clock.Elapsed.TotalSeconds,
            points.Select(p => new MapPointDto(p.X, p.Y, p.Z)).ToList(), parameters,
            mode, axis, position, heights, outline, mode == "surface" ? request.SurfaceGroup : null);
    }

    /// <summary>The mean of the 500 Hz and 1 kHz bands' levels, leaving out a band with nothing in it: the single number the page gives for a reflection.</summary>
    private static double OverallLevel(IReadOnlyList<double> levelsDb)
    {
        var mid = new[] { levelsDb[3], levelsDb[4] }.Where(double.IsFinite).ToList();
        return mid.Count == 0 ? double.NaN : mid.Average();
    }

    /// <summary>As <see cref="IsOnSurface"/>, and a box's "Walls" is all four of its walls.</summary>
    private static bool IsReflectorSurface(string faceName, string group) =>
        IsOnSurface(faceName, group) || (group.Equals("Walls", StringComparison.OrdinalIgnoreCase) && faceName.StartsWith("Wall ", StringComparison.OrdinalIgnoreCase));

    public const int MaxListedReflections = 500;
    public const int MaxReflectionOrder = 4;

    /// <summary>The reflection list at a receiver from the first source (see <see cref="ReflectionList"/>).</summary>
    public static ReflectionsResponse Reflections(ReflectionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckAir(request.TemperatureC, request.HumidityPercent);
        if (request.MaxOrder < 1 || request.MaxOrder > MaxReflectionOrder) throw new ArgumentException($"The reflection order must be between 1 and {MaxReflectionOrder}.");
        var room = BuildRoom(request.Room, request.Model);
        var source = ToVec(request.Source);
        var receiver = ToVec(request.Receiver);
        if (!room.Contains(source)) throw new ArgumentException("The source must be inside the room.");
        if (!room.Contains(receiver)) throw new ArgumentException("The receiver must be inside the room.");
        var order = EffectiveImageSourceOrder(room, request.MaxOrder);
        var list = ReflectionList.For(room, source, receiver, order, request.TemperatureC, request.HumidityPercent, ToDirectivity(request.Directivity));
        return new ReflectionsResponse((receiver - source).Length, (receiver - source).Length / EnergyRayTracer.SpeedOfSound(request.TemperatureC) * 1000, order, list.Count,
            list.Take(MaxListedReflections).Select(r => new ReflectionDto(r.Order, r.FaceNames, Math.Round(r.DistanceM, 4), Math.Round(r.DelayMs, 4), Json(new OctaveSpectrum(r.LevelDb)),
                Number(OverallLevel(r.LevelDb)), r.Path.Select(p => new[] { p.X, p.Y, p.Z }).ToArray())).ToList());
    }

    /// <summary>Whether a face belongs to the named surface: a box's "Floor", or a model group, whose faces are named "group 1", "group 2" and so on.</summary>
    private static bool IsOnSurface(string faceName, string group) =>
        faceName.Equals(group, StringComparison.OrdinalIgnoreCase) ||
        (faceName.StartsWith(group + " ", StringComparison.OrdinalIgnoreCase) && faceName[(group.Length + 1)..].All(char.IsAsciiDigit));

    public const int MaxSources = 8;
    public const int MaxReceivers = 8;

    /// <summary>
    /// The sources of a request as Core's <see cref="ExtendedSource"/>: the first, then any further ones, each with its own pattern, its level (its own sound
    /// power spectrum when the powers are given, otherwise dB relative to the first) and its delay.
    /// </summary>
    private static ExtendedSource BuildSource(PointDto position, DirectivityDto? directivity, IReadOnlyList<SourceDto>? extras, IReadOnlyList<double>? firstPower = null)
    {
        extras ??= Array.Empty<SourceDto>();
        if (extras.Count + 1 > MaxSources) throw new ArgumentException($"There can be at most {MaxSources} sources.");
        foreach (var extra in extras)
        {
            if (extra is null) throw new ArgumentException("A source is missing.");
            if (!double.IsFinite(extra.GainDb) || extra.GainDb < -40 || extra.GainDb > 20) throw new ArgumentException("A source's level must be between -40 and 20 dB relative to the first.");
            if (!double.IsFinite(extra.DelayMs) || extra.DelayMs < 0 || extra.DelayMs > 500) throw new ArgumentException("A source's delay must be between 0 and 500 ms.");
        }
        // with the sources' powers known, each shares the response band by band by its own spectrum; without, by its gain alone, the same in every band
        return new ExtendedSource(SourceGroups(position, directivity, extras, firstPower).SelectMany(g => g));
    }

    /// <summary>The points of each source in turn (one, or a surface source's grid), as <see cref="BuildSource"/> takes them.</summary>
    private static List<List<SourcePoint>> SourceGroups(PointDto position, DirectivityDto? directivity, IReadOnlyList<SourceDto>? extras, IReadOnlyList<double>? firstPower)
    {
        extras ??= Array.Empty<SourceDto>();
        var powers = SourcePowers(ValidPower(firstPower), extras);
        double[]? Gains(int i) => powers?[i].Select(v => Math.Pow(10, v / 10)).ToArray();
        var groups = new List<List<SourcePoint>> { Spread(new SourcePoint(ToVec(position), 1, ToDirectivity(directivity), 0, Gains(0)), directivity).ToList() };
        for (var i = 0; i < extras.Count; i++)
        {
            var extra = extras[i];
            groups.Add(Spread(new SourcePoint(ToVec(extra.Position), powers is null ? Math.Pow(10, extra.GainDb / 10) : 1, ToDirectivity(extra.Directivity), extra.DelayMs / 1000, Gains(i + 1)),
                extra.Directivity).ToList());
        }
        return groups;
    }

    /// <summary>The most points a surface source is split into each way, and the spacing it is split at below that.</summary>
    public const int MaxSurfaceElementsEachWay = 8;
    private const double SurfaceElementSpacingM = 0.5;

    /// <summary>
    /// A source as the points it radiates from: itself, or for a surface source an even grid over its rectangle (about every 0.5 m, at most 8 each way), each
    /// with an equal share of its power, its pattern, delay and spectrum.
    /// </summary>
    private static IEnumerable<SourcePoint> Spread(SourcePoint point, DirectivityDto? d)
    {
        if (d?.Surface is not { } surface || !string.Equals(d.Kind, "surface", StringComparison.OrdinalIgnoreCase)) return new[] { point };
        var aim = AimOf(d);
        var across = Math.Abs(aim.Z) < 0.999 ? new Vec3(0, 0, 1).Cross(aim).Normalized()
            : new Vec3(-Math.Sin(d.AimAzimuthDeg * Math.PI / 180), Math.Cos(d.AimAzimuthDeg * Math.PI / 180), 0);
        var up = aim.Cross(across).Normalized();
        int along = Math.Clamp((int)Math.Ceiling(surface.WidthM / SurfaceElementSpacingM), 1, MaxSurfaceElementsEachWay);
        int high = Math.Clamp((int)Math.Ceiling(surface.HeightM / SurfaceElementSpacingM), 1, MaxSurfaceElementsEachWay);
        var share = point.Weight / (along * high);
        return ExtendedSource.Rectangle(point.Position, across, up, surface.WidthM, surface.HeightM, along, high, point.Directivity).Points
            .Select(e => point with { Position = e.Position, Weight = share });
    }

    /// <summary>
    /// A source pattern aimed along an azimuth and elevation: a horn-like one from its -6 dB coverage angles (the default), a coherent line array, or a measured
    /// balloon uploaded earlier; null for an omnidirectional source.
    /// </summary>
    private static SourceDirectivity? ToDirectivity(DirectivityDto? d)
    {
        if (d is null) return null;
        foreach (var v in new[] { d.HorizontalDeg, d.VerticalDeg, d.AimAzimuthDeg, d.AimElevationDeg, d.FloorDb })
            if (!double.IsFinite(v)) throw new ArgumentException("The source's angles must be numbers.");
        if (string.Equals(d.Kind, "surface", StringComparison.OrdinalIgnoreCase))
        {
            var s = d.Surface ?? throw new ArgumentException("A surface source needs its width and height.");
            if (!double.IsFinite(s.WidthM) || !double.IsFinite(s.HeightM) || s.WidthM < 0.1 || s.HeightM < 0.1 || s.WidthM > 50 || s.HeightM > 50)
                throw new ArgumentException("A surface source must be 0.1 to 50 m wide and high.");
            // each part of a one-sided surface radiates as cos θ to the side it faces (Lambert), nothing behind; otherwise evenly every way
            var facing = AimOf(d);
            return s.OneSided ? new SourceDirectivity(v => Math.Max(0, v.Dot(facing))) : null;
        }
        var balloon = (d.Kind ?? "horn").ToLowerInvariant() switch
        {
            "horn" => BalloonDirectivity.FromCoverage(d.HorizontalDeg, d.VerticalDeg, d.FloorDb), // validates the ranges
            "linearray" => ToLineArray(d),
            "balloon" => BalloonService.Get(d.BalloonId ?? throw new ArgumentException("A measured source needs the id of its uploaded pattern.")),
            "talker" => Talker.Value,
            var other => throw new ArgumentException($"Unknown source type '{other}': use horn, lineArray, balloon, talker or surface."),
        };
        return balloon.Aimed(AimOf(d));
    }

    /// <summary>The human talker's balloon, made once.</summary>
    private static readonly Lazy<BalloonDirectivity> Talker = new(BalloonDirectivity.Talker);

    /// <summary>The direction a source is aimed along, from its azimuth (from +x towards +y) and elevation (above horizontal).</summary>
    private static Vec3 AimOf(DirectivityDto d)
    {
        var az = d.AimAzimuthDeg * Math.PI / 180;
        var el = d.AimElevationDeg * Math.PI / 180;
        return new Vec3(Math.Cos(el) * Math.Cos(az), Math.Cos(el) * Math.Sin(az), Math.Sin(el));
    }

    /// <summary>
    /// A pattern's directivity index on its axis in each octave band (10 lg of its gain where it is aimed, the gain averaging 1 over the sphere), so the page
    /// can turn a level at 1 m on axis into a sound power and back. An omnidirectional source has 0 dB in every band.
    /// </summary>
    public static DirectivityIndexDto DirectivityIndex(DirectivityDto? d)
    {
        var pattern = ToDirectivity(d);
        if (pattern is null) return new DirectivityIndexDto(new double[8]);
        var axis = AimOf(d!);
        return new DirectivityIndexDto(Enumerable.Range(0, 8).Select(b => Math.Round(10 * Math.Log10(pattern.ForBand(b).Gain(axis)), 2)).ToArray());
    }

    private static BalloonDirectivity ToLineArray(DirectivityDto d)
    {
        var a = d.LineArray ?? throw new ArgumentException("A line array needs its settings (elements, spacing, orientation).");
        foreach (var v in new[] { a.SpacingM, a.DelayStepMs, a.SplayDegPerElement, a.SidelobeDb })
            if (!double.IsFinite(v)) throw new ArgumentException("The array's settings must be numbers.");
        var orientation = (a.Orientation ?? "").ToLowerInvariant() switch
        {
            "vertical" => ArrayOrientation.Vertical,
            "horizontal" => ArrayOrientation.Horizontal,
            _ => throw new ArgumentException("The array runs vertical or horizontal."),
        };
        if (!Enum.TryParse<ArrayShading>(a.Shading, ignoreCase: true, out var shading)) throw new ArgumentException($"Unknown shading '{a.Shading}': use uniform, hamming, hann, sine or chebyshev.");
        // each element has the horn-like pattern given by the coverage angles
        var element = BalloonDirectivity.FromCoverage(d.HorizontalDeg, d.VerticalDeg, d.FloorDb);
        return CoherentLineArray.ToBalloon(a.Elements, a.SpacingM, orientation, a.DelayStepMs / 1000, a.SplayDegPerElement, element, 5, shading, a.SidelobeDb);
    }

    private static Room3D BuildRoom(RoomDto? box, ModelRoomDto? model)
    {
        if ((box is null) == (model is null)) throw new ArgumentException("Give either a box room or an imported model.");
        if (model is not null) return ModelService.BuildRoom(model);
        var room = box!;
        foreach (var (name, value) in new[] { ("length", room.LengthM), ("width", room.WidthM), ("height", room.HeightM) })
            if (!double.IsFinite(value) || value < MinSizeM || value > MaxSizeM)
                throw new ArgumentException($"The room {name} must be between {MinSizeM} and {MaxSizeM} metres.");
        var footprintM2 = room.LengthM * room.WidthM;
        var built = Room3D.Extrude(
            new[] { (0.0, 0.0), (room.LengthM, 0.0), (room.LengthM, room.WidthM), (0.0, room.WidthM) }, room.HeightM,
            Finish(room.Floor, footprintM2), Finish(room.Walls, 2 * (room.LengthM + room.WidthM) * room.HeightM), Finish(room.Ceiling, footprintM2));
        return room.Screens is { Count: > 0 } ? new Room3D(built.Faces.Select(f => new PlanarFace(f.Name, f.Vertices, f.Finish)), ScreenFaces(room.Screens)) : built;
    }

    /// <summary>The most screens a room may have.</summary>
    public const int MaxScreens = 100;

    /// <summary>
    /// Each screen as a planar rectangle, "Screen 1", "Screen 2"..., or with a door as the five rectangles round the opening (below and above the door's
    /// head on each side, and over the door), cut so that they meet edge to edge and the door's three edges are the screen's free edges there.
    /// </summary>
    public static IReadOnlyList<PlanarFace> ScreenFaces(IReadOnlyList<ScreenDto>? screens)
    {
        if (screens is null || screens.Count == 0) return Array.Empty<PlanarFace>();
        if (screens.Count > MaxScreens) throw new ArgumentException($"A room can have at most {MaxScreens} screens.");
        return screens.SelectMany((s, i) =>
        {
            ArgumentNullException.ThrowIfNull(s);
            if (new[] { s.X, s.Y, s.BaseZ, s.AngleDeg }.Any(v => !double.IsFinite(v)))
                throw new ArgumentException($"Screen {i + 1}'s position and angle must be numbers.");
            if (!double.IsFinite(s.LengthM) || !double.IsFinite(s.HeightM) || s.LengthM < 0.1 || s.HeightM < 0.1 || s.LengthM > MaxSizeM || s.HeightM > MaxSizeM)
                throw new ArgumentException($"Screen {i + 1} must be 0.1 to {MaxSizeM} m long and high.");
            var angle = s.AngleDeg * Math.PI / 180;
            var along = new Vec3(Math.Cos(angle), Math.Sin(angle), 0);
            var start = new Vec3(s.X, s.Y, s.BaseZ) - along * (s.LengthM / 2);
            var finish = Finish(s.Finish);
            // a rectangle of the screen from u0 to u1 along it and z0 to z1 up from its base
            PlanarFace Piece(double u0, double u1, double z0, double z1) => new($"Screen {i + 1}", new[]
            {
                start + along * u0 + new Vec3(0, 0, z0), start + along * u1 + new Vec3(0, 0, z0),
                start + along * u1 + new Vec3(0, 0, z1), start + along * u0 + new Vec3(0, 0, z1),
            }, finish);
            if (s.Door is not { } door) return new[] { Piece(0, s.LengthM, 0, s.HeightM) };

            if (new[] { door.OffsetM, door.WidthM, door.HeightM }.Any(v => !double.IsFinite(v)) || door.OffsetM < 0 || door.WidthM < 0.1 || door.HeightM < 0.1
                || door.OffsetM + door.WidthM > s.LengthM + 1e-9 || door.HeightM > s.HeightM + 1e-9)
                throw new ArgumentException($"Screen {i + 1}'s door must be at least 0.1 m wide and high, and within the screen.");
            double a = door.OffsetM, b = Math.Min(s.LengthM, door.OffsetM + door.WidthM), h = Math.Min(s.HeightM, door.HeightM);
            const double gone = 1e-6; // a piece this thin is not there (a door at the screen's end, or as high as it)
            return new[] { (0.0, a, 0.0, h), (0.0, a, h, s.HeightM), (a, b, h, s.HeightM), (b, s.LengthM, h, s.HeightM), (b, s.LengthM, 0.0, h) }
                .Where(p => p.Item2 - p.Item1 > gone && p.Item4 - p.Item3 > gone)
                .Select(p => Piece(p.Item1, p.Item2, p.Item3, p.Item4)).ToArray();
        }).ToList();
    }

    /// <summary>The width of the strip an audience area's exposed edge adds to the area its absorption acts over, metres (the usual 0.5 m rule for the edge effect of seating).</summary>
    private const double AudienceEdgeStripM = 0.5;

    /// <summary>The finish of a surface group of the given total area (the area only matters for an audience area, whose absorption is scaled by its edge effect).</summary>
    public static SurfaceFinish Finish(SurfaceDto surface, double areaM2)
    {
        var finish = Finish(surface);
        if (surface.AudienceEdgeM is not { } edge || surface.Open) return finish;
        if (!double.IsFinite(edge) || edge < 0) throw new ArgumentException("An audience area's exposed edge must be a length of 0 or more metres.");
        if (areaM2 <= 0) return finish;
        var factor = (areaM2 + AudienceEdgeStripM * edge) / areaM2;
        // a coefficient above 1 (per-person and per-fitting entries, which are absorption areas) is left as it is
        var alpha = finish.Material.ToOctaveSpectrum().Bands.Select(a => (double?)(a >= 1 ? a : Math.Min(1, a * factor))).ToArray();
        return finish with { Material = new AbsorptionMaterial(finish.Material.Name + " (audience area)", finish.Material.Category, alpha) };
    }

    public static SurfaceFinish Finish(SurfaceDto surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!double.IsFinite(surface.Scattering) || surface.Scattering < 0 || surface.Scattering > 1)
            throw new ArgumentException("The scattering coefficient must be between 0 and 1.");
        if (surface.Open)
            return new SurfaceFinish(new AbsorptionMaterial("Open (sound passes out)", "Open", Enumerable.Repeat((double?)1.0, 8).ToArray()), OctaveSpectrum.Flat(0));
        if (surface.CustomAlpha is { } custom)
        {
            if (custom.Length != 8 || custom.Any(a => !double.IsFinite(a) || a < 0 || a > 1))
                throw new ArgumentException("A custom material needs eight absorption coefficients, each between 0 and 1 (63 Hz to 8 kHz).");
            return new SurfaceFinish(new AbsorptionMaterial("Custom", "Custom", custom.Select(a => (double?)a).ToArray()), OctaveSpectrum.Flat(surface.Scattering),
                TransmissionLoss(surface.TransmissionLossDb));
        }
        var all = AbsorptionMaterial.Presets.All;
        if (surface.MaterialId < 0 || surface.MaterialId >= all.Count) throw new ArgumentException($"Unknown material id {surface.MaterialId}.");
        return new SurfaceFinish(all[surface.MaterialId], OctaveSpectrum.Flat(surface.Scattering), TransmissionLoss(surface.TransmissionLossDb));
    }

    /// <summary>A sound reduction in the eight octave bands, each 0 to 200 dB (null for none).</summary>
    private static OctaveSpectrum? TransmissionLoss(double[]? reduction)
    {
        if (reduction is null) return null;
        if (reduction.Length != 8 || reduction.Any(r => !double.IsFinite(r) || r < 0 || r > 200))
            throw new ArgumentException("A sound reduction needs eight values, each 0 to 200 dB (63 Hz to 8 kHz).");
        return new OctaveSpectrum(reduction);
    }

    private static Vec3 ToVec(PointDto p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z)) throw new ArgumentException("A position must be finite numbers.");
        return new Vec3(p.X, p.Y, p.Z);
    }

    private static double?[] Json(OctaveSpectrum s) => s.Bands.Select(Number).ToArray();

    /// <summary>A per-band value a statistical estimate cannot give (the lateral measures and stage support need arrival directions and a real response).</summary>
    private static readonly double?[] NotWorkedOut = new double?[8];

    private static double? Number(double v) => double.IsFinite(v) ? Math.Round(v, 4) : null;

    private static double[] SumBands(IReadOnlyList<double[]> bands) => Enumerable.Range(0, bands.Max(b => b.Length)).Select(i => bands.Sum(b => i < b.Length ? b[i] : 0)).ToArray();

    private static double[] Rebin(double[] h, int by)
    {
        var result = new double[(h.Length + by - 1) / by];
        for (var i = 0; i < h.Length; i++) result[i / by] += h[i];
        return result;
    }

    /// <summary>10 log10 of each bin, null where there is no energy.</summary>
    private static double[] Db(double[] energy) => energy.Select(e => e > 0 ? 10 * Math.Log10(e) : double.NaN).ToArray();

    private static double?[] Thin(double[] values, int every) =>
        values.Where((_, i) => i % every == 0).Select(Number).ToArray();
}
