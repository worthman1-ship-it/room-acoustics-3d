using System.Numerics;

namespace RoomAcoustics.Core;

/// <summary>Which way a line array runs.</summary>
public enum ArrayOrientation
{
    /// <summary>Up and down: the elements are stacked in height, so it shapes the sound in elevation.</summary>
    Vertical,

    /// <summary>Side to side: the elements are in a row across the front, so it shapes the sound in azimuth.</summary>
    Horizontal,
}

/// <summary>How the elements of a line array are weighted in level from the middle to the ends ("shading"), which trades a wider main beam for lower side lobes.</summary>
public enum ArrayShading
{
    /// <summary>Every element at the same level: the narrowest main beam, the highest side lobes.</summary>
    Uniform,

    /// <summary>Hamming weights, 0.54 − 0.46 cos(2π i/(N−1)): the end elements at 0.08 of the middle ones, side lobes about 43 dB down.</summary>
    Hamming,

    /// <summary>Hann weights, 0.5 (1 − cos(2π (i+1)/(N+1))): every element active but the ends small, lower side lobes than Hamming and a wider beam.</summary>
    Hann,

    /// <summary>A half-sine across the line, sin(π (i+½)/N): a gentle taper, between uniform and Hamming.</summary>
    Sine,

    /// <summary>Dolph-Chebyshev weights: every side lobe at the same chosen level below the main beam, the narrowest main beam that level allows.</summary>
    Chebyshev,
}

/// <summary>
/// The far-field directivity of a line array whose elements radiate coherently, with the interference between them: the main beam, its
/// narrowing with frequency, the side lobes and grating lobes, and the steering that a delay step or a splay gives.
/// </summary>
/// <remarks>
/// <para>
/// The elements sit on a line at spacing d, numbered from 0 at the bottom (vertical) or the right, looking the way the array faces (horizontal), and
/// element i is at s<sub>i</sub> = i − (N−1)/2 spacings from the middle. The array faces +x with the world's up as +z and the left as +y. In the direction
/// u the pressure of element i is its own pattern a<sub>i</sub>(u), seen from the way it is aimed, times e<sup>j(k u·p<sub>i</sub> − ωτ<sub>i</sub>)</sup>:
/// the path advantage of an element nearer the listener and the delay τ<sub>i</sub> = i·Δτ it is given. The array pressure is the sum, and its
/// level the square of the magnitude. A positive delay step makes the beam steer toward the later end (up for a vertical array, left for a
/// horizontal one) by sin θ = cΔτ/d; a splay of ψ per element turns element i by ψ·s<sub>i</sub> (positive up, or to the left), as in the incoherent
/// line source of this app.
/// </para>
/// <para>
/// A single frequency has nulls so deep that an octave band, which holds many wavelengths of the array, is represented by the energy average over seven
/// frequencies a sixth of an octave apart around its centre, then limited to 60 dB under its peak so that the interpolation between grid points stays
/// sensible. The result is a <see cref="BalloonDirectivity"/> and is used like a measured one: only its shape matters.
/// </para>
/// <para>
/// This is the <b>far field</b> of the array: it is exact as the listener goes to infinity and a fair description beyond about L²/(2λ) from an array of
/// length L (for 3 m at 1 kHz, 13 m). Nearer than that the elements are at different distances and angles from the listener and the real pattern differs, so for
/// a listener close to a long array the incoherent line source of this app is the better model, though it has no interference. Elements are taken to
/// have equal strength unless a shading is chosen (<see cref="ArrayShading"/>, weights on the amplitude of each element, the same in every band), and to be in a
/// baffle of their own: no diffraction round the cabinets.
/// </para>
/// </remarks>
public static class CoherentLineArray
{
    /// <summary>The fewest elements: 2.</summary>
    public const int MinElements = 2;

    /// <summary>The most elements: 128.</summary>
    public const int MaxElements = 128;

    /// <summary>How far under its peak the level of a band is limited, dB.</summary>
    public const double FloorBelowPeakDb = 60;

    /// <summary>The shallowest and deepest side-lobe level, dB below the main beam, that <see cref="ArrayShading.Chebyshev"/> accepts.</summary>
    public const double MinSidelobeDb = 10, MaxSidelobeDb = 80;

    /// <summary>
    /// Dolph-Chebyshev weights by the standard construction: the Chebyshev polynomial of degree N−1, scaled so that its ripple is the side-lobe level,
    /// sampled at N points round the unit circle and transformed back to the element weights. One or two elements get equal weights.
    /// </summary>
    private static double[] Chebyshev(int n, double sidelobeDb)
    {
        var w = new double[n];
        if (n <= 2) { Array.Fill(w, 1.0); return w; }
        var order = n - 1;
        var beta = Math.Cosh(Math.Acosh(Math.Pow(10, sidelobeDb / 20)) / order);
        var p = new System.Numerics.Complex[n];
        for (var k = 0; k < n; k++)
        {
            var x = beta * Math.Cos(Math.PI * k / n);
            var t = x > 1 ? Math.Cosh(order * Math.Acosh(x))
                : x < -1 ? ((n % 2) * 2 - 1) * Math.Cosh(order * Math.Acosh(-x))
                : Math.Cos(order * Math.Acos(x));
            p[k] = n % 2 == 1 ? t : t * System.Numerics.Complex.FromPolarCoordinates(1, Math.PI * k / n);
        }
        // direct transform: n is at most 128
        var spectrum = new double[n];
        for (var m = 0; m < n; m++)
        {
            var sum = System.Numerics.Complex.Zero;
            for (var k = 0; k < n; k++) sum += p[k] * System.Numerics.Complex.FromPolarCoordinates(1, -2 * Math.PI * k * m / n);
            spectrum[m] = sum.Real;
        }
        // the transform is symmetric about the middle element; unfold its first half
        if (n % 2 == 1)
        {
            var half = (n + 1) / 2;
            for (var i = 0; i < n; i++) w[i] = spectrum[Math.Abs(i - (half - 1))];
        }
        else
        {
            var half = n / 2 + 1;
            for (var i = 0; i < n; i++)
            {
                // [half-1 .. 1] then [1 .. half-1]
                var j = i < half - 1 ? half - 1 - i : i - (half - 1) + 1;
                w[i] = spectrum[j];
            }
        }
        var largest = w.Max();
        for (var i = 0; i < n; i++) w[i] = Math.Max(w[i], 0) / largest;
        return w;
    }

    /// <summary>
    /// The amplitude weight of each of <paramref name="elements"/> elements for a shading, numbered from one end, each between 0 and 1 and the largest 1.
    /// Symmetric about the middle. Throws <see cref="ArgumentOutOfRangeException"/> for fewer than 1 element.
    /// </summary>
    public static double[] ShadingWeights(ArrayShading shading, int elements, double chebyshevSidelobeDb = 30)
    {
        if (elements < 1) throw new ArgumentOutOfRangeException(nameof(elements), elements, "There must be at least one element.");
        if (!(chebyshevSidelobeDb >= MinSidelobeDb && chebyshevSidelobeDb <= MaxSidelobeDb))
            throw new ArgumentOutOfRangeException(nameof(chebyshevSidelobeDb), chebyshevSidelobeDb, $"The side-lobe level must be {MinSidelobeDb} to {MaxSidelobeDb} dB below the main beam.");
        if (shading == ArrayShading.Chebyshev) return Chebyshev(elements, chebyshevSidelobeDb);
        var w = new double[elements];
        for (var i = 0; i < elements; i++)
            w[i] = shading switch
            {
                ArrayShading.Uniform => 1,
                ArrayShading.Hamming => elements == 1 ? 1 : 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (elements - 1)),
                ArrayShading.Hann => 0.5 * (1 - Math.Cos(2 * Math.PI * (i + 1) / (elements + 1))),
                ArrayShading.Sine => Math.Sin(Math.PI * (i + 0.5) / elements),
                _ => throw new ArgumentOutOfRangeException(nameof(shading), shading, "Unknown shading."),
            };
        var largest = w.Max();
        for (var i = 0; i < elements; i++) w[i] /= largest;
        return w;
    }

    private static readonly int[] SixthOctaveSteps = { -3, -2, -1, 0, 1, 2, 3 };

    /// <summary>
    /// The directivity balloon of the array in each octave band. Throws <see cref="ArgumentOutOfRangeException"/> for a number of elements outside
    /// <see cref="MinElements"/> to <see cref="MaxElements"/>, a spacing that is not positive, a delay or splay that is not a finite number or turns the end
    /// elements by more than 90°, or a grid step outside 1° to 30°.
    /// </summary>
    /// <param name="elements">N, the number of elements.</param>
    /// <param name="spacingM">d, the distance between neighbouring elements, m.</param>
    /// <param name="orientation">Whether the line runs up and down or across.</param>
    /// <param name="delayStepS">Δτ, the delay of each element after the one before it, s (negative steers the other way).</param>
    /// <param name="splayDegPerElement">ψ, the angle each element is turned from the one before it, degrees.</param>
    /// <param name="element">The pattern of one element, aimed along the front; null for an omnidirectional element.</param>
    /// <param name="stepDeg">The spacing of the azimuth and elevation grid of the result, degrees.</param>
    /// <param name="shading">How the elements are weighted in level from the middle to the ends.</param>
    /// <param name="chebyshevSidelobeDb">For <see cref="ArrayShading.Chebyshev"/>: how far below the main beam every side lobe is held, dB (10 to 80).</param>
    public static BalloonDirectivity ToBalloon(int elements, double spacingM, ArrayOrientation orientation, double delayStepS = 0,
        double splayDegPerElement = 0, BalloonDirectivity? element = null, double stepDeg = 5, ArrayShading shading = ArrayShading.Uniform, double chebyshevSidelobeDb = 30)
    {
        if (elements < MinElements || elements > MaxElements)
            throw new ArgumentOutOfRangeException(nameof(elements), elements, $"An array has {MinElements} to {MaxElements} elements.");
        if (!(spacingM > 0) || !double.IsFinite(spacingM))
            throw new ArgumentOutOfRangeException(nameof(spacingM), spacingM, "The spacing must be a positive number of metres.");
        if (!double.IsFinite(delayStepS))
            throw new ArgumentOutOfRangeException(nameof(delayStepS), delayStepS, "The delay step must be a number.");
        if (!double.IsFinite(splayDegPerElement) || Math.Abs(splayDegPerElement) * (elements - 1) / 2 > 90)
            throw new ArgumentOutOfRangeException(nameof(splayDegPerElement), splayDegPerElement, "The splay must be a number that turns the end elements by no more than 90°.");
        if (!Enum.IsDefined(shading))
            throw new ArgumentOutOfRangeException(nameof(shading), shading, "Unknown shading.");
        if (!(stepDeg >= 1 && stepDeg <= 30))
            throw new ArgumentOutOfRangeException(nameof(stepDeg), stepDeg, "The grid step must be 1° to 30°.");

        var azimuths = Enumerable.Range(0, (int)Math.Floor(359.999 / stepDeg) + 1).Select(i => i * stepDeg).ToArray();
        var elevationCount = (int)Math.Floor(180 / stepDeg + 1e-9) + 1;
        var elevations = Enumerable.Range(0, elevationCount).Select(i => -90 + i * stepDeg).ToList();
        if (elevations[^1] < 90) elevations.Add(90);

        var weights = ShadingWeights(shading, elements, chebyshevSidelobeDb);
        var levels = new double[8][][];
        Parallel.For(0, 8, band =>
        {
            var grid = new double[elevations.Count][];
            var peak = 0.0;
            for (var e = 0; e < elevations.Count; e++)
            {
                grid[e] = new double[azimuths.Length];
                for (var a = 0; a < azimuths.Length; a++)
                {
                    var power = BandPower(band, azimuths[a], elevations[e], elements, spacingM, orientation, delayStepS, splayDegPerElement, element, weights);
                    grid[e][a] = power;
                    if (power > peak) peak = power;
                }
            }
            var floor = peak * Math.Pow(10, -FloorBelowPeakDb / 10);
            for (var e = 0; e < grid.Length; e++)
                for (var a = 0; a < azimuths.Length; a++)
                    grid[e][a] = 10 * Math.Log10(Math.Max(grid[e][a], Math.Max(floor, double.Epsilon)));
            levels[band] = grid;
        });
        return new BalloonDirectivity(azimuths, elevations, levels);
    }

    /// <summary>The energy-average power of the array in one direction over the octave band, relative (the power of one element at that direction is 1).</summary>
    private static double BandPower(int band, double azimuthDeg, double elevationDeg, int elements, double spacingM, ArrayOrientation orientation,
        double delayStepS, double splayDeg, BalloonDirectivity? element, double[] weights)
    {
        var az = azimuthDeg * Math.PI / 180;
        var el = elevationDeg * Math.PI / 180;
        var ux = Math.Cos(el) * Math.Cos(az);
        var uy = Math.Cos(el) * Math.Sin(az);
        var uz = Math.Sin(el);
        var along = orientation == ArrayOrientation.Vertical ? uz : uy;     // the component of the direction along the line

        // each element's amplitude does not depend on frequency within the band (the pattern is per band), so find them once
        var amplitude = new double[elements];
        for (var i = 0; i < elements; i++)
        {
            if (element is null) { amplitude[i] = weights[i]; continue; }
            var tilt = splayDeg * (i - (elements - 1) / 2.0) * Math.PI / 180;
            double x, y, z;
            if (orientation == ArrayOrientation.Vertical)
            {
                (x, y, z) = (ux * Math.Cos(tilt) + uz * Math.Sin(tilt), uy, -ux * Math.Sin(tilt) + uz * Math.Cos(tilt));
            }
            else
            {
                (x, y, z) = (ux * Math.Cos(tilt) + uy * Math.Sin(tilt), -ux * Math.Sin(tilt) + uy * Math.Cos(tilt), uz);
            }
            var elementAz = Math.Atan2(y, x) * 180 / Math.PI;
            if (elementAz < 0) elementAz += 360;
            var elementEl = Math.Asin(Math.Clamp(z, -1, 1)) * 180 / Math.PI;
            amplitude[i] = weights[i] * Math.Pow(10, element.LevelDb(band, elementAz, elementEl) / 20);
        }

        var total = 0.0;
        foreach (var step in SixthOctaveSteps)
        {
            var f = OctaveSpectrum.Frequencies[band] * Math.Pow(2, step / 6.0);
            var k = 2 * Math.PI * f / SpeedOfSoundMs;
            var sum = Complex.Zero;
            for (var i = 0; i < elements; i++)
            {
                var position = (i - (elements - 1) / 2.0) * spacingM;
                var phase = k * along * position - 2 * Math.PI * f * delayStepS * i;
                sum += amplitude[i] * new Complex(Math.Cos(phase), Math.Sin(phase));
            }
            total += sum.Real * sum.Real + sum.Imaginary * sum.Imaginary;
        }
        return total / SixthOctaveSteps.Length;
    }

    private const double SpeedOfSoundMs = 343;   // air at 20 °C, m/s
}
