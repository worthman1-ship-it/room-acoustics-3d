using System.Collections.Concurrent;
using RoomAcoustics.Core;

namespace RoomAcoustics.Api;

/// <summary>Imports 3-D models (DXF, DWG, OBJ, STL, COLLADA, IFC) and keeps the few most recent in memory, so a simulation request can refer to one by id.</summary>
public static class ModelService
{
    private const int MaxModels = 8;
    private static readonly ConcurrentDictionary<string, SurfaceMesh> Store = new();
    private static readonly ConcurrentQueue<string> Order = new();
    private static readonly ConcurrentDictionary<string, string> Names = new();

    public static SurfaceMesh Get(string id) =>
        Store.TryGetValue(id, out var mesh) ? mesh : throw new ArgumentException("That model is no longer loaded; import it again.");

    /// <summary>The folder of example models: <c>examples</c> beside the repository's solution file, found by walking up from the program.</summary>
    public static string? ExamplesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "examples");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static IReadOnlyList<string> Examples() =>
        ExamplesDirectory() is { } dir
            ? Directory.GetFiles(dir).Where(f => Supported(f)).Select(Path.GetFileName).OfType<string>().Order().ToList()
            : Array.Empty<string>();

    public static ModelDto ImportExample(string name, int? space = null)
    {
        var dir = ExamplesDirectory() ?? throw new ArgumentException("There is no examples folder.");
        // only a plain file name: no path pieces that could leave the examples folder
        if (name != Path.GetFileName(name) || !Supported(name)) throw new ArgumentException("Unknown example.");
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) throw new ArgumentException("Unknown example.");
        using var stream = File.OpenRead(path);
        return Import(stream, name, 1, false, space);
    }

    public static ModelDto Import(Stream data, string fileName, double scale, bool yUp, int? space = null)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentException("The scale must be a positive number.");
        using var buffer = new MemoryStream();
        data.CopyTo(buffer);
        buffer.Position = 0;

        var skipped = new Dictionary<string, int>();
        IfcContent? ifc = null;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!Supported(fileName)) throw new ArgumentException("This reads DXF, DWG, OBJ, STL, COLLADA (.dae), IFC and 3D Studio (.3ds) models.");
        SurfaceMesh mesh;
        try
        {
            switch (extension)
            {
                case ".dxf":
                    var result = CadMeshReader.ReadDxf(buffer, scale);
                    mesh = result.Mesh;
                    foreach (var kv in result.Skipped) skipped[kv.Key] = kv.Value;
                    break;
                case ".dwg":
                    var dwg = FromTemporaryFile(".dwg", buffer, path => CadMeshReader.ReadDwg(path, scale));
                    mesh = dwg.Mesh;
                    foreach (var kv in dwg.Skipped) skipped[kv.Key] = kv.Value;
                    break;
                case ".ifc": // its units are in the file
                    ifc = FromTemporaryFile(".ifc", buffer, path => IfcMeshReader.Read(path, space));
                    mesh = ifc.Mesh;
                    break;
                case ".dae": mesh = ColladaMeshReader.Read(buffer, scale); break; // so are COLLADA's, and its up axis
                case ".obj": mesh = ObjMeshReader.Read(buffer, scale, yUp); break;
                case ".3ds": mesh = ThreeDsMeshReader.Read(buffer, scale); break;
                default: mesh = StlMeshReader.Read(buffer, scale); break;
            }
        }
        catch (Exception ex) when (ex is not ArgumentException and not OutOfMemoryException)
        {
            // the file readers throw their own exception types for a damaged or mislabelled file
            throw new ArgumentException($"Could not read '{fileName}' as a {extension[1..].ToUpperInvariant()} model: {ex.Message}");
        }
        if (mesh.Triangles.Count == 0)
            throw new ArgumentException(skipped.Count > 0
                ? "The file has no 3DFACE surfaces. It holds " + string.Join(", ", skipped.Select(kv => $"{kv.Value} {kv.Key}")) + "; EXPLODE them to faces first."
                : "The file has no surfaces this app can read.");

        var id = Keep(mesh, fileName);
        var described = Describe(id, fileName, mesh, skipped);
        return ifc is { Spaces.Count: > 0 }
            ? described with { Spaces = ifc.Spaces.Select(s => new ModelSpaceDto(s.Name, Math.Round(s.VolumeM3, 1), s.IsArea)).ToList(), Space = ifc.Space }
            : described;
    }

    // Building a room for a big model (its spatial index above all) takes seconds, and the page asks for rays again after every small change, so the last few
    // rooms are kept, keyed by the model and the finish of each of its groups. A Room3D is read-only once built, so sharing one between requests is safe.
    private const int MaxRooms = 4;
    private static readonly ConcurrentDictionary<string, Room3D> Rooms = new();
    private static readonly ConcurrentQueue<string> RoomOrder = new();

    public static Room3D BuildRoom(ModelRoomDto model)
    {
        var mesh = Get(model.ModelId);
        var key = model.ModelId + "|" + string.Join(";", model.Surfaces.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => FormattableString.Invariant($"{kv.Key}={kv.Value.MaterialId}/{kv.Value.Scattering:R}/{string.Join(",", kv.Value.CustomAlpha ?? Array.Empty<double>())}/{kv.Value.AudienceEdgeM:R}/{kv.Value.Open}/{kv.Value.Screen}/{string.Join(",", kv.Value.TransmissionLossDb ?? Array.Empty<double>())}")))
            + "|" + System.Text.Json.JsonSerializer.Serialize(model.Screens) + "|" + model.Outdoors;
        if (Rooms.TryGetValue(key, out var kept)) return kept;

        var areas = mesh.GroupAreasM2();
        SurfaceFinish FinishOf(string group) => model.Surfaces.TryGetValue(group, out var surface)
            ? SimulationService.Finish(surface, areas.GetValueOrDefault(group))
            : throw new ArgumentException($"Surface group '{group}' has no material.");
        var room = model.Outdoors
            ? mesh.ToOpenAir(FinishOf, SimulationService.ScreenFaces(model.Screens))
            : mesh.ToRoom(FinishOf, group => model.Surfaces.TryGetValue(group, out var surface) && surface.Screen, SimulationService.ScreenFaces(model.Screens));
        if (Rooms.TryAdd(key, room))
        {
            RoomOrder.Enqueue(key);
            while (RoomOrder.Count > MaxRooms && RoomOrder.TryDequeue(out var old)) Rooms.TryRemove(old, out _);
        }
        return room;
    }

    /// <summary>The DWG and IFC readers want a file, so the upload goes to a temporary one for the moment it takes to read.</summary>
    private static T FromTemporaryFile<T>(string extension, MemoryStream data, Func<string, T> read)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"acousticcalc-{Guid.NewGuid():N}{extension}");
        try
        {
            File.WriteAllBytes(temporary, data.ToArray());
            return read(temporary);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    private static bool Supported(string file) => Path.GetExtension(file).ToLowerInvariant() is ".dxf" or ".dwg" or ".obj" or ".stl" or ".dae" or ".ifc" or ".3ds";

    private static ModelDto Describe(string id, string name, SurfaceMesh mesh, IReadOnlyDictionary<string, int> skipped)
    {
        var areas = mesh.GroupAreasM2();
        var groups = mesh.Groups.Select(g => new ModelGroupDto(g, areas[g], Positions(mesh, g), SurfaceSplit.Count(mesh, g), Math.Round(AudienceEdge.ExposedLengthM(mesh, g), 1))).ToList();
        var min = new Vec3(mesh.Vertices.Min(v => v.X), mesh.Vertices.Min(v => v.Y), mesh.Vertices.Min(v => v.Z));
        var max = new Vec3(mesh.Vertices.Max(v => v.X), mesh.Vertices.Max(v => v.Y), mesh.Vertices.Max(v => v.Z));

        double? volume = null;
        string? closedError = null;
        GeometryCheckDto? check = null;
        PointDto? source = null, receiver = null;
        try
        {
            // a group named as screens is taken to be free-standing screens, as the page sets it to begin with
            var room = mesh.ToRoom(_ => new SurfaceFinish(AbsorptionMaterial.Presets.HardReflective), SurfaceMesh.LooksLikeScreen);
            volume = room.VolumeM3;
            (source, receiver) = Suggest(room, min, max);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            closedError = ex.Message;
            check = Check(mesh);
            // a model that is not closed can still be taken as outdoors, so a source and receiver above its lowest point are suggested for that
            // (taking at least 3 m of air above it, so that ground alone gets them 1.5 m up)
            try { (source, receiver) = Suggest(mesh.ToOpenAir(_ => new SurfaceFinish(AbsorptionMaterial.Presets.HardReflective)), min, max with { Z = Math.Max(max.Z, min.Z + 3) }); }
            catch (Exception open) when (open is ArgumentException or InvalidOperationException) { }
        }

        return new ModelDto(id, name, mesh.Triangles.Count, groups,
            new BoundsDto(new PointDto(min.X, min.Y, min.Z), new PointDto(max.X, max.Y, max.Z)), volume, closedError, source, receiver, skipped,
            mesh.Groups.Where(SurfaceMesh.LooksLikeScreen).ToList(), check);
    }

    /// <summary>Where the model fails to be a closed surface, with the groups named as screens left out, as the room is built.</summary>
    private static GeometryCheckDto Check(SurfaceMesh mesh)
    {
        var check = MeshCheck.Check(mesh, SurfaceMesh.LooksLikeScreen);
        var filler = check.Faults.Any(f => f.Kind == MeshFaultKind.Hole) ? new HoleFill(mesh, SurfaceMesh.LooksLikeScreen) : null;
        return new GeometryCheckDto(check.OpenEdges, check.CrowdedEdges, check.Parts, check.Faults.Select(f => new GeometryFaultDto(
            f.Kind switch { MeshFaultKind.Hole => "hole", MeshFaultKind.Crowded => "crowded", _ => "separate" },
            f.Edges.Select(e => new[] { e.A.X, e.A.Y, e.A.Z, e.B.X, e.B.Y, e.B.Z }).ToList(), f.Groups, f.LengthM, f.Hint,
            f.Kind == MeshFaultKind.Hole && filler!.Plan(f.Edges) is not null)).ToList());
    }

    /// <summary>
    /// The model with the holes asked for closed (their places in its geometry check's faults), kept as a new model under the same name. Each
    /// surface that closes a hole (a hole that is not flat is closed in flat parts) goes in a group of its own, named for where it lies: Filled floor
    /// or Filled ceiling for a level one in the bottom or top tenth of the model's height, Filled surface for any other level one, Filled wall for
    /// the rest, numbered when there are more.
    /// </summary>
    public static ModelDto Fill(FillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mesh = Get(request.ModelId);
        if (request.Holes is not { Count: > 0 }) throw new ArgumentException("Say which holes to fill.");
        var faults = MeshCheck.Check(mesh, SurfaceMesh.LooksLikeScreen).Faults;
        var filler = new HoleFill(mesh, SurfaceMesh.LooksLikeScreen);
        var caps = new List<HoleCap>();
        foreach (var index in request.Holes.Distinct())
        {
            if (index < 0 || index >= faults.Count || faults[index].Kind != MeshFaultKind.Hole) throw new ArgumentException($"Fault {index} is not a hole in this model.");
            caps.AddRange(filler.Plan(faults[index].Edges) ?? throw new ArgumentException($"Hole {index} cannot be filled: its edges are not loops in planes round an area."));
        }
        var filled = HoleFill.Fill(mesh, HoleFill.Named(mesh, caps));
        var modelName = Names.GetValueOrDefault(request.ModelId) ?? "Model";
        return Describe(Keep(filled, modelName), modelName, filled, new Dictionary<string, int>());
    }

    /// <summary>The model with one surface group split into its flat surfaces (<see cref="SurfaceSplit"/>), kept as a new model under the same name.</summary>
    public static ModelDto Split(SplitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mesh = Get(request.ModelId);
        if (string.IsNullOrEmpty(request.Group) || !mesh.Groups.Contains(request.Group)) throw new ArgumentException($"The model has no surface group \"{request.Group}\".");
        if (SurfaceSplit.Count(mesh, request.Group) < 2) throw new ArgumentException($"\"{request.Group}\" is one flat surface already.");
        var split = SurfaceSplit.Split(mesh, request.Group);
        var modelName = Names.GetValueOrDefault(request.ModelId) ?? "Model";
        return Describe(Keep(split, modelName), modelName, split, new Dictionary<string, int>());
    }

    /// <summary>The model with one surface group renamed, merged into another or deleted (see <see cref="GroupEditRequest"/>), kept as a new model under the same name.</summary>
    public static ModelDto EditGroup(GroupEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mesh = Get(request.ModelId);
        var groups = mesh.Groups;
        if (string.IsNullOrEmpty(request.Group) || !groups.Contains(request.Group)) throw new ArgumentException($"The model has no surface group \"{request.Group}\".");
        var to = request.To?.Trim() ?? "";
        var edited = (request.Action ?? "").ToLowerInvariant() switch
        {
            "rename" when to.Length == 0 => throw new ArgumentException("Give the group a name."),
            "rename" when groups.Contains(to) => throw new ArgumentException($"There is a group \"{to}\" already: merge into it instead."),
            "rename" => mesh.WithGroupRenamed(request.Group, to),
            "merge" when !groups.Contains(to) || to == request.Group => throw new ArgumentException($"Merge into another group of the model, not \"{to}\"."),
            "merge" => mesh.WithGroupRenamed(request.Group, to),
            "delete" when groups.Count == 1 => throw new ArgumentException("The model's only group cannot be deleted."),
            "delete" => mesh.WithoutGroup(request.Group),
            _ => throw new ArgumentException("The action must be rename, merge or delete."),
        };
        var modelName = Names.GetValueOrDefault(request.ModelId) ?? "Model";
        return Describe(Keep(edited, modelName), modelName, edited, new Dictionary<string, int>());
    }

    /// <summary>The model with a corner moved, a face drawn or a flat surface deleted (see <see cref="GeometryEditRequest"/>), kept as a new model under the same name.</summary>
    public static ModelDto EditGeometry(GeometryEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mesh = Get(request.ModelId);
        var p = (request.Points ?? Array.Empty<PointDto>()).Select(q => new Vec3(q.X, q.Y, q.Z)).ToList();
        var edited = (request.Action ?? "").ToLowerInvariant() switch
        {
            "move" when p.Count != 2 => throw new ArgumentException("Give the corner and where it goes."),
            "move" => MeshEdit.MoveCorner(mesh, p[0], p[1]),
            "face" => MeshEdit.AddFace(mesh, p, request.Group ?? ""),
            "delete" when p.Count != 3 => throw new ArgumentException("Give the three corners of a face of the surface."),
            "delete" => MeshEdit.RemoveSurface(mesh, p[0], p[1], p[2]),
            _ => throw new ArgumentException("The action must be move, face or delete."),
        };
        var modelName = Names.GetValueOrDefault(request.ModelId) ?? "Model";
        return Describe(Keep(edited, modelName), modelName, edited, new Dictionary<string, int>());
    }

    /// <summary>The model repaired as far as it can be (<see cref="ModelRepair"/>), kept as a new model under the same name, with a line for each step taken.</summary>
    public static ModelDto Repair(RepairRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mesh = Get(request.ModelId);
        var screens = request.Screens?.ToHashSet();
        var (repaired, steps) = ModelRepair.Repair(mesh, screens is null ? null : screens.Contains, request.Boxes);
        var modelName = Names.GetValueOrDefault(request.ModelId) ?? "Model";
        return Describe(Keep(repaired, modelName), modelName, repaired, new Dictionary<string, int>()) with { Repairs = steps };
    }

    /// <summary>Keeps a model under a new id, forgetting the oldest beyond the last few.</summary>
    private static string Keep(SurfaceMesh mesh, string name)
    {
        var id = Guid.NewGuid().ToString("N");
        Store[id] = mesh;
        Names[id] = name;
        Order.Enqueue(id);
        while (Order.Count > MaxModels && Order.TryDequeue(out var old)) { Store.TryRemove(old, out _); Names.TryRemove(old, out _); }
        return id;
    }

    /// <summary>A source and a receiver inside the room, a quarter and three quarters of the way along its longer side, 1.5 m above the lowest point.</summary>
    private static (PointDto? Source, PointDto? Receiver) Suggest(Room3D room, Vec3 min, Vec3 max)
    {
        var height = Math.Min(min.Z + 1.5, (min.Z + max.Z) / 2);
        var points = ReceiverGrid.Horizontal(room, height, 1.0, 0.6);
        if (points.Count < 2) return (null, null);
        var alongX = max.X - min.X >= max.Y - min.Y;
        double Along(Vec3 p) => alongX ? p.X : p.Y;
        var lo = alongX ? min.X : min.Y;
        var span = alongX ? max.X - min.X : max.Y - min.Y;
        Vec3 Nearest(double fraction) => points.OrderBy(p => Math.Abs(Along(p) - (lo + span * fraction))).First();
        var a = Nearest(0.25);
        var b = Nearest(0.75);
        return (new PointDto(a.X, a.Y, a.Z), new PointDto(b.X, b.Y, b.Z));
    }

    private static double[] Positions(SurfaceMesh mesh, string group)
    {
        var result = new List<double>();
        foreach (var t in mesh.Triangles.Where(t => t.Group == group))
            foreach (var index in new[] { t.A, t.B, t.C })
            {
                var v = mesh.Vertices[index];
                result.Add(Math.Round(v.X, 4));
                result.Add(Math.Round(v.Y, 4));
                result.Add(Math.Round(v.Z, 4));
            }
        return result.ToArray();
    }
}
