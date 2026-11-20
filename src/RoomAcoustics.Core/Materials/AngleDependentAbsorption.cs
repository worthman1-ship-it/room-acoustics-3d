namespace RoomAcoustics.Core;

/// <summary>
/// Absorption that changes with the angle sound meets a surface at, from its random-incidence coefficient, by the model of a locally reacting surface
/// with a real normalised impedance ξ (Kuttruff, Room Acoustics, 6th ed., 2.5 and 2.6): at angle θ from the normal the reflection factor is
/// R = (ξ cos θ - 1) / (ξ cos θ + 1), so α(θ) = 4 ξ cos θ / (ξ cos θ + 1)², and Paris's average over a diffuse field is
/// α_random = (8 / ξ²) [1 + ξ - 1 / (1 + ξ) - 2 ln(1 + ξ)].
/// </summary>
/// <remarks>
/// <para>
/// Each surface's ξ is the one whose random-incidence average is its coefficient, taken on the side of high impedance (ξ above about 1.56, where the average
/// is largest, 0.951), as walls, ceilings and porous linings have. The average cannot pass 0.951, so a coefficient above it takes that ξ and its reflection
/// is scaled down to match. Either way, a diffuse field meets each surface with the absorption it was given: only how it is shared between the angles changes.
/// Sound meeting the surface head on is absorbed less than the average; the absorption rises with the angle to a maximum where cos θ = 1/ξ, close to grazing
/// for a hard surface, and only right at grazing falls to nothing. So oblique reflections, which long, low rooms are full of, lose more than the coefficient.
/// </para>
/// <para>
/// It is a model of a plain locally reacting surface. Seating is not one: sound skimming the seat tops is attenuated at low frequencies (the seat dip), which
/// this does not give.
/// </para>
/// </remarks>
public static class AngleDependentAbsorption
{
    /// <summary>The impedance at which the random-incidence average is largest, and that largest average.</summary>
    public static readonly (double Xi, double Alpha) Peak = FindPeak();

    /// <summary>Paris's random-incidence average of a locally reacting surface of real normalised impedance ξ.</summary>
    public static double RandomIncidence(double xi) =>
        xi <= 0 ? 0 : 8 / (xi * xi) * (1 + xi - 1 / (1 + xi) - 2 * Math.Log(1 + xi));

    /// <summary>α(θ) of a locally reacting surface of real normalised impedance ξ, at <paramref name="cosIncidence"/> = cos θ.</summary>
    public static double AtAngle(double xi, double cosIncidence)
    {
        var c = Math.Abs(cosIncidence) * xi;
        return 4 * c / ((c + 1) * (c + 1));
    }

    /// <summary>The impedance whose random-incidence average is <paramref name="alpha"/>, on the high-impedance side; infinite for no absorption.</summary>
    public static double ImpedanceFor(double alpha)
    {
        if (!(alpha > 0)) return double.PositiveInfinity;
        if (alpha >= Peak.Alpha) return Peak.Xi;
        // the average falls as ξ grows beyond the peak: bisect on the logarithm of ξ
        double low = Math.Log(Peak.Xi), high = Math.Log(1e9);
        for (var i = 0; i < 100; i++)
        {
            var mid = (low + high) / 2;
            if (RandomIncidence(Math.Exp(mid)) > alpha) low = mid; else high = mid;
        }
        return Math.Exp((low + high) / 2);
    }

    /// <summary>
    /// The absorption at an angle of a surface whose random-incidence coefficient is <paramref name="alpha"/> and whose impedance is <paramref name="xi"/>
    /// (from <see cref="ImpedanceFor"/>): α(θ), with what it reflects, 1 - α(θ), scaled so that its diffuse-field average is <paramref name="alpha"/> (a factor
    /// of 1 unless the coefficient is above the largest average).
    /// </summary>
    public static double Alpha(double alpha, double xi, double cosIncidence)
    {
        if (!(alpha > 0) || double.IsInfinity(xi)) return 0;
        if (alpha >= 1) return 1;
        return 1 - (1 - AtAngle(xi, cosIncidence)) * (1 - alpha) / (1 - RandomIncidence(xi));
    }

    private static (double, double) FindPeak()
    {
        // golden-section search for the largest average, which is near ξ = 1.56
        double a = 0.5, b = 5;
        var g = (Math.Sqrt(5) - 1) / 2;
        for (var i = 0; i < 200; i++)
        {
            double c = b - g * (b - a), d = a + g * (b - a);
            if (RandomIncidence(c) > RandomIncidence(d)) b = d; else a = c;
        }
        var xi = (a + b) / 2;
        return (xi, RandomIncidence(xi));
    }
}
