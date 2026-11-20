using RoomAcoustics.Api;

var builder = WebApplication.CreateBuilder(args);

// The Vite dev server (and a built copy served from anywhere on this machine) may call the API.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Host == "localhost" || u.Host == "127.0.0.1"))
    .AllowAnyHeader()
    .AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
// The engine as an MCP server at /mcp (McpTools), for an AI assistant; stateless, as every tool call stands alone.
builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "Room Acoustics 3D", Version = SimulationService.Version })
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();

var app = builder.Build();
app.UseCors();

// The MCP endpoint can read files on this machine (import_model), so a web page from anywhere but this machine may not call it.
app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin.ToString();
    if (context.Request.Path.StartsWithSegments("/mcp") && origin.Length > 0
        && !(Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Host == "localhost" || u.Host == "127.0.0.1")))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

// A bad request (a source outside the room, a ray count beyond the limit) is the caller's mistake: 400 with the Core message.
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

app.MapGet("/api/health", () => new { status = "ok", engine = "RoomAcoustics.Core", version = SimulationService.Version });
app.MapGet("/api/bands", () => SimulationService.Bands);
app.MapGet("/api/materials", () => SimulationService.Materials());
app.MapGet("/api/examples", () => ModelService.Examples());
app.MapPost("/api/model/example", (string name, int? space) => ModelService.ImportExample(name, space));
// The file's bytes are the request body: POST /api/model/import?name=Hall.dxf&scale=1 (scale 0.001 for millimetres, yUp for an OBJ from a y-up tool).
// space picks one room of an IFC file (an index into the model's list of spaces); the largest room when not given.
app.MapPost("/api/model/import", async (HttpRequest request, string name, double? scale, bool? yUp, int? space) =>
{
    // a whole building's IFC model runs to tens of megabytes, above the server's usual 30 MB
    if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 512L * 1024 * 1024;
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body);
    body.Position = 0;
    return ModelService.Import(body, name, scale ?? 1, yUp ?? false, space);
});
// Closes holes in a model with faces across them: a new model, its new groups (Filled floor, Filled ceiling, Filled wall) to be given finishes.
app.MapPost("/api/model/fill", (FillRequest request) => ModelService.Fill(request));
app.MapPost("/api/model/split", (SplitRequest request) => ModelService.Split(request));
app.MapPost("/api/model/group", (GroupEditRequest request) => ModelService.EditGroup(request));
app.MapPost("/api/model/geometry", (GeometryEditRequest request) => ModelService.EditGeometry(request));
app.MapPost("/api/model/repair", (RepairRequest request) => ModelService.Repair(request));
// A measured source pattern (a balloon CSV: azimuth, elevation, then the eight octave bands, or one column for all): the file's bytes are the body.
app.MapPost("/api/balloon/import", async (HttpRequest request, string name) =>
{
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body);
    body.Position = 0;
    return BalloonService.Import(body, name);
});
// A measured head-related transfer function, as a SOFA file: the file's bytes are the body.
app.MapPost("/api/hrtf/import", async (HttpRequest request, string name) =>
{
    // a SOFA file can be larger than the server's usual limit on a request
    if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 64 * 1024 * 1024;
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body);
    body.Position = 0;
    return HrtfService.Import(body, name);
});
// A measured impulse response, as a WAV file (the bytes are the body): its parameters per octave band, to compare with the simulation's.
app.MapPost("/api/measured", async (HttpRequest request, string name) =>
{
    // a long multichannel recording at a high rate can be larger than the server's usual limit on a request
    if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 256L * 1024 * 1024;
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body);
    body.Position = 0;
    return MeasuredService.Analyse(body, name);
});
app.MapPost("/api/simulate", (SimulateRequest request) => SimulationService.Simulate(request));
app.MapPost("/api/statistical", (SimulateRequest request) => SimulationService.Statistical(request));
// Long calculations as jobs: start one, poll it for progress, cancel it, collect the result. (The plain /api/simulate and /api/map calls still work and wait for the answer.)
app.MapPost("/api/jobs/simulate", (SimulateRequest request) => JobView(JobService.Start((cancel, progress) => SimulationService.Simulate(request, cancel, progress))));
app.MapPost("/api/jobs/auralise", (AuraliseRequest request) => JobView(JobService.Start((cancel, progress) => SimulationService.Auralise(request, cancel, progress))));
app.MapPost("/api/jobs/ambisonic", (AuraliseRequest request) => JobView(JobService.Start((cancel, progress) => SimulationService.Ambisonic(request, cancel, progress))));
app.MapPost("/api/jobs/thirdoctave", (SimulateRequest request) => JobView(JobService.Start((cancel, progress) => SimulationService.ThirdOctaves(request, cancel, progress))));
app.MapPost("/api/jobs/map", (MapRequest request) => JobView(JobService.Start((cancel, progress) => SimulationService.Map(request, cancel, progress))));
app.MapGet("/api/jobs/{id}", (string id) => JobService.Get(id) is { } job ? Results.Ok(JobView(job)) : Results.NotFound());
app.MapDelete("/api/jobs/{id}", (string id) => JobService.Cancel(id) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/map/metrics", () => SimulationService.MapMetricNames);
app.MapPost("/api/map", (MapRequest request) => SimulationService.Map(request));
app.MapPost("/api/rays", (RaysRequest request) => SimulationService.Rays(request));
app.MapPost("/api/reflections", (ReflectionsRequest request) => SimulationService.Reflections(request));
// A pattern's directivity index on its axis, by octave band, so a level at 1 m on axis can be turned into a sound power.
app.MapPost("/api/directivity/index", (DirectivityDto directivity) => SimulationService.DirectivityIndex(directivity));
app.MapMcp("/mcp");

// The built web app, when there is one: a wwwroot folder beside the program (a published copy), or web/dist in the repository. Then the API and the page
// are one program on one port, and http://localhost:5080 is the whole application.
var webRoot = FindWebRoot();
if (webRoot is not null)
{
    var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    app.MapFallback(async context =>
    {
        // anything that is not the API or a file is the page itself (and an unknown /api path stays a 404)
        if (context.Request.Path.StartsWithSegments("/api")) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(files.GetFileInfo("index.html"));
    });
}

app.Run();

static object JobView(Job job) => new { id = job.Id, status = job.Status, progress = job.Progress, elapsedSeconds = job.ElapsedSeconds, error = job.Error, result = job.Status == "done" ? job.Result : null };

static string? FindWebRoot()
{
    var beside = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    if (File.Exists(Path.Combine(beside, "index.html"))) return beside;
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var dist = Path.Combine(dir.FullName, "web", "dist");
        if (File.Exists(Path.Combine(dist, "index.html"))) return dist;
    }
    return null;
}

// the tests start the API in memory (WebApplicationFactory<Program>)
public partial class Program;
