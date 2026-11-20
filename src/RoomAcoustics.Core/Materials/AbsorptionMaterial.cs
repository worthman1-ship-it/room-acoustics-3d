using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoomAcoustics.Core;

/// <summary>
/// A named surface finish with an octave-band absorption coefficient (alpha)
/// spectrum, 63 Hz - 8 kHz. <see cref="Alpha"/> is intentionally nullable
/// per band: real material data is frequently reported for only some
/// octave bands (manufacturers very often omit 63 Hz and/or 8 kHz), and
/// silently defaulting a missing measurement to 0 would misrepresent it as
/// "measured, no absorption" rather than "not reported". Use
/// <see cref="ToOctaveSpectrum"/> to get a complete spectrum for a
/// calculation, which fills any gaps from the nearest reported band.
/// </summary>
/// <remarks>
/// Most entries are a true per-m^2 coefficient (multiply by a <see
/// cref="Surface"/>'s floor/wall/ceiling area). A minority - anything
/// named/categorised "per fitting", "per person" or "per light" (audience
/// seating, acoustic luminaires, some baffles) - are instead an absolute
/// equivalent absorption area for one unit of that thing, so values above
/// 1.0 are expected and correct (e.g. a large integrated light-fitting
/// absorber panel spanning several m^2). The <see cref="Surface"/> model
/// still works for these: enter the *count* of fittings/people as the
/// "area", not a physical area in m^2 - the arithmetic (quantity x per-unit
/// value) is identical either way, only the label differs. This is called
/// out here rather than silently excluding those ~16 entries, since
/// excluding real reference data because it uses a different multiplier
/// convention would lose genuine information a user might want.
/// </remarks>
public sealed record AbsorptionMaterial(
    string Name,
    string Category,
    double?[] Alpha,
    double? Nrc = null,
    string? Notes = null,
    string? Reference = null)
{
    public double? this[int bandIndex] => Alpha[bandIndex];

    /// <summary>
    /// A complete 8-band spectrum, filling any band with no reported value
    /// from its nearest reported neighbour (ties broken towards the
    /// lower-frequency band) - a standard, transparent approximation for
    /// missing extreme-band data, not a fabricated measurement. Throws if
    /// the material has no data in any band at all.
    /// </summary>
    public OctaveSpectrum ToOctaveSpectrum()
    {
        var result = new double[8];
        for (var i = 0; i < 8; i++)
        {
            if (Alpha[i] is { } value)
            {
                result[i] = value;
                continue;
            }

            var bestDistance = int.MaxValue;
            int? nearest = null;
            for (var j = 0; j < 8; j++)
            {
                if (Alpha[j] is null) continue;
                var distance = Math.Abs(j - i);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearest = j;
                }
            }

            if (nearest is null)
                throw new InvalidOperationException($"Material '{Name}' has no absorption data in any octave band.");
            result[i] = Alpha[nearest.Value]!.Value;
        }
        return new OctaveSpectrum(result);
    }

    /// <summary>
    /// Absorption coefficient reference data. <see cref="All"/> is the two
    /// presets (<see cref="HardReflective"/>/<see cref="SoftAbsorptive"/>),
    /// then the user's own library if there is one (<see cref="UserLibraryPath"/>,
    /// the same JSON layout as the built-in file), then a small built-in
    /// library of materials from published tables (BB93, EN 12354-6,
    /// Bies and Hansen, Egan), each citing its source in <see cref="Reference"/>.
    /// The user's library comes first so that its materials keep their ids.
    /// </summary>
    public static class Presets
    {
        /// <summary>The user's own material library, read if it exists: %APPDATA%\RoomAcoustics3D\materials.json.</summary>
        public static string UserLibraryPath { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoomAcoustics3D", "materials.json");

        public static readonly AbsorptionMaterial HardReflective = new(
            "Hard/reflective (e.g. painted concrete)", "Quick presets",
            new double?[] { 0.01, 0.01, 0.01, 0.01, 0.02, 0.02, 0.02, 0.02 });

        public static readonly AbsorptionMaterial SoftAbsorptive = new(
            "Soft/absorptive (e.g. heavy carpet)", "Quick presets",
            new double?[] { 0.2, 0.3, 0.5, 0.7, 0.8, 0.9, 1.0, 0.9 });

        public static IReadOnlyList<AbsorptionMaterial> All { get; } = Load(UserLibraryPath);

        /// <summary>The presets, the materials in <paramref name="userLibrary"/> if that file exists, then the built-in library.</summary>
        public static IReadOnlyList<AbsorptionMaterial> Load(string? userLibrary)
        {
            var assembly = typeof(AbsorptionMaterial).Assembly;
            const string resourceName = "RoomAcoustics.Core.Materials.Data.AbsorptionMaterials.json";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

            var entries = new List<MaterialEntry>();
            if (userLibrary is not null && File.Exists(userLibrary))
            {
                using var own = File.OpenRead(userLibrary);
                entries.AddRange(JsonSerializer.Deserialize<List<MaterialEntry>>(own)
                    ?? throw new InvalidOperationException($"The material library {userLibrary} could not be read."));
            }
            entries.AddRange(JsonSerializer.Deserialize<List<MaterialEntry>>(stream)
                ?? throw new InvalidOperationException("Absorption material database failed to deserialise."));

            var materials = entries
                // measured values a little over 1 are real in published tables, but an absorption coefficient is at most 1, so they are held there
                .Select(e => new AbsorptionMaterial(e.Name, e.Category, e.Alpha.Select(a => a is > 1.0 ? 1.0 : a).ToArray(), e.Nrc, e.Notes, e.Reference))
                .ToList();

            return new[] { HardReflective, SoftAbsorptive }.Concat(materials).ToList();
        }

        private sealed class MaterialEntry
        {
            [JsonPropertyName("Name")] public string Name { get; set; } = "";
            [JsonPropertyName("Category")] public string Category { get; set; } = "";
            [JsonPropertyName("Alpha")] public double?[] Alpha { get; set; } = new double?[8];
            [JsonPropertyName("Nrc")] public double? Nrc { get; set; }
            [JsonPropertyName("Notes")] public string? Notes { get; set; }
            [JsonPropertyName("Reference")] public string? Reference { get; set; }
        }
    }
}
