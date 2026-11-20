namespace RoomAcoustics.Core;

/// <summary>
/// The scattering a reflection gets because the surface is finite: below a limit frequency a surface is too small, seen from where the sound comes
/// from and goes to, to reflect specularly, and the sound it does not reflect specularly is spread by diffraction at its edges. This adds to the
/// surface's own scattering coefficient s (from its roughness), as s' = 1 - (1 - s)(1 - s_d), in both the rays and the image sources.
/// </summary>
/// <remarks>
/// <para>
/// After Rindel (1986, "Attenuation of sound reflections due to diffraction") as ODEON's reflection-based scattering uses it (Christensen and
/// Rindel 2005): a surface of characteristic size a (<see cref="Room3D.SurfaceSizesM"/>), met at an angle θ from its normal by sound that has come
/// d₁ to it and goes d₂ on, reflects specularly above f_g = c a* / (2 (a cos θ)²), where a* = 2 d₁ d₂ / (d₁ + d₂). Below f_g the specular energy falls
/// in proportion to f / f_g, 3 dB an octave, and the rest is scattered: s_d = 1 - f / f_g. Small surfaces, low frequencies, long paths and grazing
/// sound are scattered most; a large wall met head on is not scattered at all.
/// </para>
/// <para>
/// In the rays, d₁ is the path to the surface from the source or the last reflection, and d₂ the distance the specular reflection would go to the
/// next surface. In the image sources they are the legs of the path, the last running to the receiver.
/// </para>
/// </remarks>
public sealed class EdgeScattering
{
    private readonly IReadOnlyList<double> _sizes;
    private readonly double _frequencyHz;
    private readonly double _speedMs;

    public EdgeScattering(Room3D room, double frequencyHz, double speedMs)
    {
        ArgumentNullException.ThrowIfNull(room);
        _sizes = room.SurfaceSizesM;
        _frequencyHz = frequencyHz;
        _speedMs = speedMs;
    }

    /// <summary>The scattering of a reflection off <paramref name="face"/>: its surface's own, <paramref name="surfaceScattering"/>, with the diffraction's added.</summary>
    public double Combined(int face, double surfaceScattering, double cosIncidence, double d1M, double d2M) =>
        1 - (1 - surfaceScattering) * (1 - Coefficient(_sizes[face], cosIncidence, d1M, d2M, _frequencyHz, _speedMs));

    /// <summary>
    /// Whether a reflection off <paramref name="face"/> is sure to get no diffraction scattering, whatever the distance on: f_g is largest when
    /// d₂ is endless, at c d₁ / (a cos θ)². Lets the ray tracer skip looking for the next surface.
    /// </summary>
    public bool NoneWhatever(int face, double cosIncidence, double d1M)
    {
        var projected = _sizes[face] * cosIncidence;
        return projected > 0 && _frequencyHz * projected * projected >= _speedMs * d1M;
    }

    /// <summary>s_d = 1 - f / f_g below f_g = c a* / (2 (a cos θ)²), a* = 2 d₁ d₂ / (d₁ + d₂); 0 above it, and 0 for a surface of no size.</summary>
    public static double Coefficient(double sizeM, double cosIncidence, double d1M, double d2M, double frequencyHz, double speedMs)
    {
        if (!(sizeM > 0) || !(d1M > 0) || !(d2M > 0)) return 0;
        var projected = sizeM * Math.Abs(cosIncidence);
        if (projected < 1e-9) return 1;
        var aStar = 2 * d1M * d2M / (d1M + d2M);
        var limitHz = speedMs * aStar / (2 * projected * projected);
        return frequencyHz >= limitHz ? 0 : 1 - frequencyHz / limitHz;
    }
}
