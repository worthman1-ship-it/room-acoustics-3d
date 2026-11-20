namespace RoomAcoustics.Core;

/// <summary>
/// Dry (anechoic) test signals to put through a room: sounds that show the room's response clearly, for when no
/// recording is to hand. All are mono, between -1 and +1.
/// </summary>
public static class DrySignals
{
    /// <summary>A single full-scale click every <paramref name="intervalS"/>, starting at 0.1 s. The room's response is heard directly.</summary>
    public static double[] ClickTrain(int sampleRateHz, double seconds, double intervalS = 1.0)
    {
        Check(sampleRateHz, seconds);
        if (intervalS <= 0) throw new ArgumentOutOfRangeException(nameof(intervalS), "The click interval must be greater than zero.");
        var signal = new double[(int)Math.Round(seconds * sampleRateHz)];
        for (var t = 0.1; t < seconds; t += intervalS)
            signal[(int)Math.Round(t * sampleRateHz)] = 1;
        return signal;
    }

    /// <summary>
    /// Bursts of pink noise (equal energy per octave, as in acoustic test signals), <paramref name="onS"/> long every
    /// <paramref name="periodS"/>, with 5 ms ramps so they do not click. The gaps let the reverberant tail be heard on its own.
    /// </summary>
    public static double[] NoiseBursts(int sampleRateHz, double seconds, double onS = 0.4, double periodS = 2.0, int seed = 1)
    {
        Check(sampleRateHz, seconds);
        if (onS <= 0 || periodS < onS) throw new ArgumentOutOfRangeException(nameof(periodS), "The burst length must be positive and no longer than the period.");

        var random = new Random(seed);
        var count = (int)Math.Round(seconds * sampleRateHz);
        var signal = new double[count];
        var ramp = Math.Max(1, (int)(0.005 * sampleRateHz));

        // Paul Kellet's economical pink-noise filter on white noise.
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (var i = 0; i < count; i++)
        {
            var white = random.NextDouble() * 2 - 1;
            b0 = 0.99886 * b0 + white * 0.0555179;
            b1 = 0.99332 * b1 + white * 0.0750759;
            b2 = 0.96900 * b2 + white * 0.1538520;
            b3 = 0.86650 * b3 + white * 0.3104856;
            b4 = 0.55000 * b4 + white * 0.5329522;
            b5 = -0.7616 * b5 - white * 0.0168980;
            var pink = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362) * 0.11;
            b6 = white * 0.115926;

            var t = i / (double)sampleRateHz;
            var into = (t - 0.1) % periodS;
            if (t < 0.1 || into >= onS) continue;
            var burstSample = (int)(into * sampleRateHz);
            var burstLength = (int)(onS * sampleRateHz);
            var envelope = Math.Min(1.0, Math.Min(burstSample, burstLength - burstSample) / (double)ramp);
            signal[i] = pink * envelope;
        }

        var largest = signal.Max(Math.Abs);
        if (largest > 0)
            for (var i = 0; i < count; i++) signal[i] /= largest;
        return signal;
    }

    private static void Check(int sampleRateHz, double seconds)
    {
        if (sampleRateHz < 8000 || sampleRateHz > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "The sample rate must be between 8000 and 192000 Hz.");
        if (seconds <= 0 || seconds > RoomAuralisation.MaxSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), $"The length must be between 0 and {RoomAuralisation.MaxSeconds} s.");
    }
}

/// <summary>A stereo rendering: the dry sound as a listener would hear it in the room.</summary>
/// <param name="Left">The left ear's signal, scaled so the larger channel peaks at the requested level.</param>
/// <param name="Right">The right ear's signal; the same length as <paramref name="Left"/>.</param>
/// <param name="SampleRateHz">The sample rate of both channels.</param>
/// <param name="LeftEar">Where the left ear was.</param>
/// <param name="RightEar">Where the right ear was.</param>
/// <param name="InterauralLevelDifferenceDb">Left energy against right energy over the whole signal, in dB: positive when the left ear hears more.</param>
/// <param name="InterauralTimeDifferenceMs">
/// When the direct sound reaches the right ear minus when it reaches the left, in milliseconds, by the head model: negative for a source
/// on the listener's right. Zero if the head delay was off or the direct sound was blocked.
/// </param>
/// <param name="Scale">The factor the channels were scaled by to peak at the requested level: divide by it for the response at the tracer's own level (a unit source).</param>
/// <param name="Head">
/// The energy at the head's centre by the lateral angle it comes from, for the head as it faces, in the early and late windows: what its IACC is worked out
/// from (<see cref="InterauralCoherence"/>). Null for a measured room.
/// </param>
public sealed record AuralisationResult(double[] Left, double[] Right, int SampleRateHz, Vec3 LeftEar, Vec3 RightEar,
    double InterauralLevelDifferenceDb, double InterauralTimeDifferenceMs = 0, double Scale = 1, InterauralEnergy? Head = null)
{
    public double DurationS => Left.Length / (double)SampleRateHz;
}

/// <summary>
/// Puts a dry sound into a room as heard by a listener: the two ears are traced from the (possibly extended) source,
/// an impulse response is synthesised for each, and the signal is convolved with both. See <see cref="Auralisation"/>
/// for the method and its limits.
/// </summary>
public static class RoomAuralisation
{
    /// <summary>The longest dry signal accepted, in seconds. Rendering time grows with length.</summary>
    public const double MaxSeconds = 120;

    /// <summary>The ear receivers are small spheres so that the two ears stay distinct; a larger sphere would blur them.</summary>
    public const double EarReceiverRadiusM = 0.25;

    /// <summary>
    /// Renders <paramref name="dry"/> for a listener whose head is at <paramref name="head"/> and faces
    /// <paramref name="facingAzimuthDeg"/> (0 along +x, 90 along +y, in plan). The options' ray count, image-source order,
    /// air and seed apply; the receiver radius is cut to <see cref="EarReceiverRadiusM"/> if larger. The ears are
    /// <paramref name="earSpacingM"/> apart, so the response needs a few hundred thousand rays to be smooth. With
    /// <paramref name="headShadow"/> each ear's energy is weighted by the <see cref="HeadShadow"/> of a sphere, so sound from the
    /// far side reaches that ear weaker, more so at high frequencies; without it the ears are two bare microphones. With
    /// <paramref name="headDelay"/> each ear's response is moved by less than a millisecond so that the direct sound arrives when
    /// Woodworth's sphere model (<see cref="HeadShadow.PathAroundHeadM"/>) says: the time bins of the energy response are a millisecond
    /// wide, which on their own leave the interaural time difference to chance. With <paramref name="hrtf"/> each ear's energy is weighted by that measured
    /// head-related transfer function instead of the sphere's shadow (the head facing <paramref name="facingAzimuthDeg"/>, level).
    /// </summary>
    public static AuralisationResult Render(Room3D room, ExtendedSource source, Vec3 head, double facingAzimuthDeg, double[] dry,
        int sampleRateHz, RayTraceOptions? options = null, double earSpacingM = 0.17, double peak = 0.5, bool headShadow = true,
        bool headDelay = true, MeasuredHrtf? hrtf = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dry);
        if (dry.Length == 0) throw new ArgumentException("The dry signal is empty.", nameof(dry));
        if (sampleRateHz < 8000 || sampleRateHz > 192000) throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "The sample rate must be between 8000 and 192000 Hz.");
        if (dry.Length > MaxSeconds * sampleRateHz)
            throw new ArgumentException($"The dry signal is longer than {MaxSeconds:0} s.", nameof(dry));
        if (peak <= 0 || peak > 1) throw new ArgumentOutOfRangeException(nameof(peak), "The peak level must be above 0 and at most 1.");

        var facing = new Vec3(Math.Cos(facingAzimuthDeg * Math.PI / 180), Math.Sin(facingAzimuthDeg * Math.PI / 180), 0);
        var (leftEar, rightEar) = Auralisation.EarPositions(head, facing, earSpacingM);
        if (!room.Contains(leftEar) || !room.Contains(rightEar))
            throw new ArgumentException("The listener's ears must be inside the room.", nameof(head));

        options ??= new RayTraceOptions();
        options = options with { ReceiverRadiusM = Math.Min(options.ReceiverRadiusM, EarReceiverRadiusM) };
        if (headShadow && hrtf is not null)
        {
            // a direction in the room in the head's own axes: ahead, to the left, up
            var toLeft = new Vec3(-facing.Y, facing.X, 0);
            Vec3 InHead(Vec3 from) => new(from.Dot(facing), from.Dot(toLeft), from.Z);
            options = options with { EarGains = new Func<int, Vec3, double>[] { (band, from) => hrtf.PowerGain(0, band, InHead(from)), (band, from) => hrtf.PowerGain(1, band, InHead(from)) } };
        }

        // Each ear's outward axis points away from the other: left ear to the left, right ear to the right.
        var outward = (rightEar - leftEar).Normalized();
        // A third receiver at the head's centre gathers where the sound comes from, for the IACC of the head as it faces (its shadow and gains are not used).
        var traced = options with
        {
            Interaural = true, HeadLeft = outward * -1,
            EarGains = options.EarGains is { } gains ? new[] { gains[0], gains[1], (_, _) => 1.0 } : null,
        };
        var responses = ExtendedSourceTracer.TraceReceivers(room, source, new[] { leftEar, rightEar, head }, traced,
            headShadow ? new[] { outward * -1, outward, outward } : null);
        var atHead = responses[2].Interaural;
        responses = responses.Take(2).ToList();
        if (headShadow)
            responses = responses.Select(r => r with { Energy = r.ShadowedEnergy! }).ToList();

        // Different noise for each ear: the early arrivals differ through the geometry, and the diffuse tail is not identical
        // at two points a head apart.
        var leftImpulse = Auralisation.SynthesizeImpulseResponse(responses[0], sampleRateHz, options.Seed);
        var rightImpulse = Auralisation.SynthesizeImpulseResponse(responses[1], sampleRateHz, options.Seed + 1);

        var itdMs = 0.0;
        if (headDelay && HeadDelayShifts(room, source, head, leftEar, rightEar, outward, options, sampleRateHz) is { } shifts)
        {
            // Moving an ear's response later lengthens it; pad the other so the two channels stay the same length.
            var length = Math.Max(leftImpulse.Length + shifts.Left, rightImpulse.Length + shifts.Right);
            leftImpulse = Delayed(leftImpulse, shifts.Left, length);
            rightImpulse = Delayed(rightImpulse, shifts.Right, length);
            itdMs = shifts.ItdS * 1000;
        }

        var (left, right) = Auralisation.RenderStereo(dry, leftImpulse, rightImpulse, peak, out var scale);

        var leftEnergy = left.Sum(x => x * x);
        var rightEnergy = right.Sum(x => x * x);
        var difference = leftEnergy > 0 && rightEnergy > 0 ? 10 * Math.Log10(leftEnergy / rightEnergy) : 0;
        return new AuralisationResult(left, right, sampleRateHz, leftEar, rightEar, difference, itdMs, scale, atHead);
    }

    /// <summary>
    /// Puts a dry sound into a room that was measured: <paramref name="dry"/> is convolved with the recorded impulse response
    /// <paramref name="impulse"/>, which is first cut to start at its largest sample (the direct sound) and taken to the sound's sample
    /// rate. The result is the same in both channels (a mono recording has no ears to tell apart) and is scaled so that its largest sample is
    /// <paramref name="peak"/>, as <see cref="Render"/> does, so a measured room and the simulated one are heard at the same level. Needs no
    /// simulation. The impulse response may be at most <see cref="MeasuredImpulseResponse.MaxDurationS"/> seconds long after the direct sound.
    /// </summary>
    public static AuralisationResult RenderMeasured(double[] dry, int dryRateHz, double[] impulse, int impulseRateHz, double peak = 0.5)
    {
        ArgumentNullException.ThrowIfNull(dry);
        ArgumentNullException.ThrowIfNull(impulse);
        if (dry.Length == 0) throw new ArgumentException("The dry signal is empty.", nameof(dry));
        if (dryRateHz < 8000 || dryRateHz > 192000) throw new ArgumentOutOfRangeException(nameof(dryRateHz), "The sample rate must be between 8000 and 192000 Hz.");
        if (impulseRateHz < 8000 || impulseRateHz > 192000) throw new ArgumentOutOfRangeException(nameof(impulseRateHz), "The impulse response's sample rate must be between 8000 and 192000 Hz.");
        if (dry.Length > MaxSeconds * dryRateHz) throw new ArgumentException($"The dry signal is longer than {MaxSeconds:0} s.", nameof(dry));
        if (peak <= 0 || peak > 1) throw new ArgumentOutOfRangeException(nameof(peak), "The peak level must be above 0 and at most 1.");

        var start = 0;
        var largest = 0.0;
        for (var i = 0; i < impulse.Length; i++)
            if (Math.Abs(impulse[i]) > largest) { largest = Math.Abs(impulse[i]); start = i; }
        if (!(largest > 0)) throw new ArgumentException("The impulse response has no sound in it.", nameof(impulse));
        if (impulse.Length - start > MeasuredImpulseResponse.MaxDurationS * impulseRateHz)
            throw new ArgumentException($"The impulse response runs for more than {MeasuredImpulseResponse.MaxDurationS:0} s after its direct sound.", nameof(impulse));

        var response = Auralisation.Resample(impulse[start..], impulseRateHz, dryRateHz);
        var heard = Auralisation.Convolve(dry, response);
        var loudest = heard.Length == 0 ? 0 : heard.Max(Math.Abs);
        if (loudest > 0)
            for (var i = 0; i < heard.Length; i++) heard[i] *= peak / loudest;
        return new AuralisationResult(heard, (double[])heard.Clone(), dryRateHz, Vec3.Zero, Vec3.Zero, 0);
    }

    /// <summary>
    /// How many samples to move each ear's response so that the direct sound lands where the head model puts it. The tracer files the
    /// direct sound in the time bin that holds its straight-line arrival at the ear, and the synthesis spreads that over the bin, so the
    /// bin's middle is where it is now; the model's arrival is the distance to the head's centre plus the path round the head. The
    /// shifts are made non-negative by moving both ears by the same amount. Null if the direct sound is blocked, out of the response, or
    /// the head is at the source. The last value is the interaural time difference, right minus left, in seconds.
    /// </summary>
    internal static (int Left, int Right, double ItdS)? HeadDelayShifts(Room3D room, ExtendedSource source, Vec3 head, Vec3 leftEar, Vec3 rightEar,
        Vec3 outward, RayTraceOptions options, int sampleRateHz)
    {
        var centre = source.Centre;
        var headDistance = (head - centre).Length;
        if (headDistance < 1e-9 || !room.IsClear(centre, leftEar) || !room.IsClear(centre, rightEar)) return null;

        var speed = EnergyRayTracer.SpeedOfSound(options.TemperatureC);
        var binCount = (int)Math.Ceiling(options.MaxTimeS / options.BinWidthS);
        var travel = (head - centre).Normalized();

        (double Arrival, double Offset)? Ear(Vec3 position, Vec3 axis)
        {
            var bin = (int)((position - centre).Length / speed / options.BinWidthS);
            if (bin >= binCount) return null;
            var arrival = (headDistance + HeadShadow.PathAroundHeadM(-travel.Dot(axis))) / speed;
            return (arrival, arrival - (bin + 0.5) * options.BinWidthS);
        }

        if (Ear(leftEar, outward * -1) is not { } left || Ear(rightEar, outward) is not { } right) return null;
        var earliest = Math.Min(left.Offset, right.Offset);
        return ((int)Math.Round((left.Offset - earliest) * sampleRateHz), (int)Math.Round((right.Offset - earliest) * sampleRateHz),
            right.Arrival - left.Arrival);
    }

    private static double[] Delayed(double[] impulse, int samples, int length)
    {
        var moved = new double[length];
        Array.Copy(impulse, 0, moved, samples, impulse.Length);
        return moved;
    }
}
