namespace RoomAcoustics.Core;

/// <summary>
/// How a listener's head changes the level at an ear with the direction the sound comes from: the ear facing the sound hears it
/// a little louder, the far ear hears high frequencies much quieter because the head is in the way.
/// </summary>
/// <remarks>
/// This is the single pole and zero model of a rigid sphere by Brown and Duda (1998), which approximates Rayleigh's solution:
/// H(ω, θ) = (1 + j α(θ) ω / 2ω0) / (1 + j ω / 2ω0), with ω0 = c / a for a sphere of radius a, and
/// α(θ) = 1.05 + 0.95 cos(θ · 180° / 150°), where θ is the angle between the direction of the sound and the ear's outward axis
/// (0° when the sound arrives straight into the ear). Only the magnitude is used here, evaluated at each octave band's centre
/// frequency; the model is a smooth stand-in for a measured head-related transfer function, which also has the pinna's
/// notches and a source-height dependence that this does not. <see cref="PathAroundHeadM"/> gives the delay round the head.
/// </remarks>
public static class HeadShadow
{
    /// <summary>The head radius of the model, in metres (the Brown and Duda value, 8.75 cm).</summary>
    public const double HeadRadiusM = 0.0875;

    private const double SpeedOfSoundMs = 343;

    /// <summary>
    /// The change in sound power at the ear, as a ratio (1 is no change), in octave band <paramref name="band"/> (0 is 63 Hz) for sound
    /// arriving from a direction at angle θ to the ear's axis, where <paramref name="cosIncidence"/> is cos θ (+1 straight into the ear,
    /// -1 from the far side of the head).
    /// </summary>
    public static double PowerGain(int band, double cosIncidence)
    {
        if (band < 0 || band >= 8) throw new ArgumentOutOfRangeException(nameof(band), "The band must be between 0 and 7.");
        var theta = Math.Acos(Math.Clamp(cosIncidence, -1, 1)) * 180 / Math.PI;
        var alpha = 1.05 + 0.95 * Math.Cos(theta * Math.PI / 150);
        var x = Math.PI * OctaveSpectrum.Frequencies[band] * HeadRadiusM / SpeedOfSoundMs;
        return (1 + alpha * alpha * x * x) / (1 + x * x);
    }

    /// <summary>
    /// How much farther than the wavefront through the head's centre a sound has to travel to reach the ear, in metres, by Woodworth's
    /// rigid-sphere model: straight to the ear when it faces the sound (negative, the ear being ahead of the centre), and otherwise to
    /// the point where the path just touches the sphere and then round the surface. <paramref name="cosIncidence"/> is as for
    /// <see cref="PowerGain"/>. The difference between the two ears of a source at azimuth φ is a (φ + sin φ), the largest being about
    /// 0.66 ms for a head of this size.
    /// </summary>
    public static double PathAroundHeadM(double cosIncidence)
    {
        var theta = Math.Acos(Math.Clamp(cosIncidence, -1, 1));
        return theta <= Math.PI / 2 ? -HeadRadiusM * Math.Cos(theta) : HeadRadiusM * (theta - Math.PI / 2);
    }

    /// <summary>The same gain in decibels.</summary>
    public static double GainDb(int band, double cosIncidence) => 10 * Math.Log10(PowerGain(band, cosIncidence));
}
