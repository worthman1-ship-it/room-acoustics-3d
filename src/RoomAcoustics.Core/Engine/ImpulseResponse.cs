namespace RoomAcoustics.Core;

/// <summary>
/// Energy impulse response at one receiver, per octave band: the energy
/// arriving in each time bin, for a unit-energy impulse emitted at the source.
/// Only ratios of these energies are meaningful (the metrics are all ratios),
/// except <see cref="ReferenceEnergy10m"/>, which is the free-field energy at
/// 10 m in the same units and is used for the strength index G.
/// </summary>
/// <param name="BinWidthS">Width of each time bin, seconds.</param>
/// <param name="Energy">Eight arrays (one per octave band, 63 Hz - 8 kHz), each holding the energy per bin.</param>
/// <param name="ReferenceEnergy10m">Free-field energy at 10 m, per band, for the same receiver.</param>
/// <param name="LateralEnergy">
/// Optional. The same bins with each arrival's energy multiplied by cos² of the angle between its direction and the
/// receiver's lateral axis (a figure-of-eight microphone pointing sideways). Gives LF and LJ.
/// </param>
/// <param name="LateralAmplitudeEnergy">
/// Optional. The same bins with each arrival's energy multiplied by |cos| of that angle, for LFC.
/// </param>
/// <param name="ShadowedEnergy">
/// Optional. The same bins with each arrival's energy multiplied by the <see cref="HeadShadow"/> power gain for its direction at
/// an ear, band by band. Only traced when ear axes are given; it is what an ear of a head would hear.
/// </param>
/// <param name="DirectEnergy">
/// Optional. How much of <see cref="Energy"/> is the direct sound, per band, in the same units (0 where a wall blocks it). Known to a traced response,
/// which adds the direct sound on its own; a measured one cannot say. Gives the direct sound's strength and the direct-to-reverberant ratio.
/// </param>
/// <param name="ArrivalFrom">
/// Optional (traced when <see cref="RayTraceOptions.ArrivalDirections"/> is set). Three lots of bins, for x, y and z: each arrival's energy times that
/// component of the unit vector it comes from, summed, band by band. Over the bin's energy it is the mean direction its sound comes from; its length is
/// 1 for a single arrival and nearly 0 in a diffuse field (one minus it is DirAC's diffuseness).
/// </param>
/// <param name="Interaural">
/// Optional (traced when <see cref="RayTraceOptions.Interaural"/> is set). The energy of the early and late windows gathered by the lateral angle it comes
/// from, for the IACC of a head facing the source (<see cref="InterauralCoherence"/>).
/// </param>
public sealed record ImpulseResponse(double BinWidthS, IReadOnlyList<double[]> Energy, OctaveSpectrum ReferenceEnergy10m,
    IReadOnlyList<double[]>? LateralEnergy = null, IReadOnlyList<double[]>? LateralAmplitudeEnergy = null,
    IReadOnlyList<double[]>? ShadowedEnergy = null, OctaveSpectrum? DirectEnergy = null, IReadOnlyList<IReadOnlyList<double[]>>? ArrivalFrom = null,
    InterauralEnergy? Interaural = null);
