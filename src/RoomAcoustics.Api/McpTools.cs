using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RoomAcoustics.Api;

/// <summary>
/// The engine as a Model Context Protocol server (at /mcp), so that an AI assistant can load a room, give its surfaces materials and simulate it. Each tool
/// calls the same service the page's API does; a model loaded here is kept in the engine as one the page uploads is, but the page does not show it. Results
/// leave out the triangles and the decay curves and impulse responses, which would fill an assistant's context without telling it anything it can read.
/// </summary>
[McpServerToolType]
public static class McpTools
{
    private const string Frame = "Positions are in metres, x along the room's length, y across it, z up.";

    [McpServerTool(Name = "list_examples", ReadOnly = true), Description("The example rooms that come with the app, by name, for load_example.")]
    public static IReadOnlyList<string> ListExamples() => ModelService.Examples();

    [McpServerTool(Name = "load_example"), Description("Loads an example room. Returns its id (for simulate's model.modelId), its surface groups (each to be given a material), its volume, and a suggested source and receiver. " + Frame)]
    public static ModelDto LoadExample([Description("The example's name, from list_examples.")] string name) => Run(() => Summary(ModelService.ImportExample(name)));

    [McpServerTool(Name = "import_model"), Description("Loads a room model from a file on this computer (DXF, OBJ, STL, 3DS, IFC, SketchUp COLLADA .dae and the other formats the app reads). Returns as load_example does. A model that is not a closed surface says why in closedError and cannot be simulated until repaired in the app.")]
    public static ModelDto ImportModel(
        [Description("The full path of the model file.")] string path,
        [Description("Metres per drawing unit: 1 for metres, 0.001 for millimetres.")] double scale = 1,
        [Description("True for an OBJ from a tool whose up is y rather than z.")] bool yUp = false) =>
        Run(() =>
        {
            using var file = File.OpenRead(path);
            return Summary(ModelService.Import(file, Path.GetFileName(path), scale, yUp));
        });

    [McpServerTool(Name = "find_materials", ReadOnly = true), Description("Searches the app's material library by name or category. Each has an id (for a surface's materialId) and its absorption coefficients in the eight octave bands 63 Hz to 8 kHz (null where the source gave none).")]
    public static IReadOnlyList<MaterialDto> FindMaterials(
        [Description("Words that must all appear in the name or category, e.g. \"carpet\" or \"plaster brick\".")] string search,
        [Description("The most to return.")] int limit = 25)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return SimulationService.Materials()
            .Where(m => words.All(w => m.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || m.Category.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Max(1, limit)).ToList();
    }

    [McpServerTool(Name = "simulate", ReadOnly = true), Description(
        "Simulates the room by ray tracing and image sources and returns, at the receiver, the ISO 3382 parameters in each octave band (63 Hz to 8 kHz) and as single numbers, and the Speech Transmission Index. " +
        "Give either room (a box: lengthM, widthM, heightM and a surface for floor, walls and ceiling) or model (modelId from load_example or import_model, and surfaces: every group's name mapped to a surface). " +
        "A surface is {materialId (from find_materials), scattering 0 to 1}. extraReceivers adds receivers, whose results are in receivers; with several along a line from one source, openPlan (ISO 3382-3) and workroom (ISO 14257) are worked out too. " +
        "rayCount 20 000 is quick, 60 000 the usual and 200 000 thorough. " + Frame)]
    public static SimulateResponse Simulate(SimulateRequest request, CancellationToken cancel) => Run(() => Trim(SimulationService.Simulate(request, cancel)));

    [McpServerTool(Name = "quick_estimate", ReadOnly = true), Description("Estimates the same values as simulate from the room's volume and absorption alone (Sabine, Eyring and the diffuse field), without tracing rays: instant, but blind to the room's shape. Takes simulate's request.")]
    public static SimulateResponse QuickEstimate(SimulateRequest request) => Run(() => Trim(SimulationService.Statistical(request)));

    /// <summary>A model without its triangles, or the edges of its faults: their names, sizes and why it is not closed are what an assistant can use.</summary>
    private static ModelDto Summary(ModelDto model) => model with
    {
        Groups = model.Groups.Select(g => g with { Positions = [] }).ToList(),
        Check = model.Check is { } check ? check with { Faults = check.Faults.Select(f => f with { Edges = [] }).ToList() } : null,
    };

    /// <summary>A simulation without its decay curves and impulse responses (null here, though the page's results always have them).</summary>
    private static SimulateResponse Trim(SimulateResponse result) => result with
    {
        Decay = null!,
        Impulse = null!,
        Receivers = result.Receivers?.Select(r => r with { Decay = null, Impulse = null }).ToList(),
    };

    /// <summary>A request the engine cannot do (a source outside the room, an unknown example) is the caller's mistake: the assistant is told why.</summary>
    private static T Run<T>(Func<T> work)
    {
        try { return work(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new McpException(ex.Message);
        }
    }
}
