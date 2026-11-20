namespace RoomAcoustics.Core;

/// <summary>
/// An area of a given material within a room, for reverberation time
/// calculation. For most materials <see cref="AreaSqm"/> is a physical area
/// in square metres; for "per fitting"/"per person" materials (see
/// <see cref="AbsorptionMaterial"/>'s remarks) it should instead be a count
/// (of fittings, people, etc.) - the Sabine arithmetic is identical either
/// way, only the physical meaning of the number differs.
/// </summary>
/// <param name="Axis">
/// Which pair of room faces this surface is part of, 0, 1 or 2 for faces perpendicular to x, y or z (-1 when not given). Only the Arau-Puchades and
/// Fitzroy reverberation times use it.
/// </param>
public sealed record Surface(string Name, double AreaSqm, AbsorptionMaterial Material, int Axis = -1);
