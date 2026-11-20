using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoomAcoustics.Api.Tests;

/// <summary>The engine as an MCP server at /mcp, as an AI assistant's MCP client sees it.</summary>
public sealed class McpTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public McpTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private async Task<McpClient> Connect()
    {
        var http = _factory.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http);
        return await McpClient.CreateAsync(transport);
    }

    private static JsonElement Result(CallToolResult result)
    {
        Assert.False(result.IsError == true, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        return JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
    }

    [Fact]
    public async Task ItListsItsTools()
    {
        await using var client = await Connect();
        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "list_examples", "load_example", "import_model", "find_materials", "simulate", "quick_estimate" }, names);
    }

    [Fact]
    public async Task AnAssistantCanLoadAnExample_FindMaterials_AndSimulateIt()
    {
        await using var client = await Connect();
        var examples = Result(await client.CallToolAsync("list_examples"));
        var name = examples.EnumerateArray().First().GetString()!;

        var model = Result(await client.CallToolAsync("load_example", new Dictionary<string, object?> { ["name"] = name }));
        var groups = model.GetProperty("groups").EnumerateArray().ToList();
        Assert.NotEmpty(groups);
        Assert.All(groups, g => Assert.Equal(0, g.GetProperty("positions").GetArrayLength())); // no triangles: an assistant can't use them

        var carpet = Result(await client.CallToolAsync("find_materials", new Dictionary<string, object?> { ["search"] = "carpet", ["limit"] = 3 }));
        Assert.InRange(carpet.GetArrayLength(), 1, 3);
        Assert.All(carpet.EnumerateArray(), m => Assert.Contains("carpet", m.GetProperty("name").GetString()! + m.GetProperty("category").GetString(), StringComparison.OrdinalIgnoreCase));
        var carpetId = carpet[0].GetProperty("id").GetInt32();

        var request = new
        {
            model = new
            {
                modelId = model.GetProperty("id").GetString(),
                surfaces = groups.ToDictionary(g => g.GetProperty("name").GetString()!, g => new { materialId = carpetId, scattering = 0.1 }),
            },
            source = model.GetProperty("suggestedSource"),
            receiver = model.GetProperty("suggestedReceiver"),
            rayCount = 5000,
        };
        var result = Result(await client.CallToolAsync("simulate", new Dictionary<string, object?> { ["request"] = request }));
        Assert.True(result.GetProperty("broadband").GetProperty("t30S").GetDouble() > 0);
        Assert.True(result.GetProperty("sti").GetProperty("sti").GetDouble() is > 0 and <= 1);
        Assert.False(result.TryGetProperty("decay", out _)); // left out: curves of numbers
        Assert.False(result.TryGetProperty("impulse", out _));

        var estimate = Result(await client.CallToolAsync("quick_estimate", new Dictionary<string, object?> { ["request"] = request }));
        Assert.True(estimate.GetProperty("broadband").GetProperty("t30S").GetDouble() > 0);
    }

    [Fact]
    public async Task AMistake_IsToldWhy_AndAPageFromElsewhereMayNotCallIt()
    {
        await using var client = await Connect();
        var missing = await client.CallToolAsync("load_example", new Dictionary<string, object?> { ["name"] = "No such room" });
        Assert.True(missing.IsError);
        Assert.NotEmpty(missing.Content.OfType<TextContentBlock>().Single().Text);

        var outside = await client.CallToolAsync("simulate", new Dictionary<string, object?>
        {
            ["request"] = new
            {
                room = new { lengthM = 8, widthM = 6, heightM = 3, floor = new { materialId = 1 }, walls = new { materialId = 0 }, ceiling = new { materialId = 0 } },
                source = new { x = 20, y = 3, z = 1.5 }, receiver = new { x = 6, y = 3, z = 1.2 }, rayCount = 1000,
            },
        });
        Assert.True(outside.IsError);
        Assert.Contains("source", outside.Content.OfType<TextContentBlock>().Single().Text, StringComparison.OrdinalIgnoreCase);

        var http = _factory.CreateClient();
        using var call = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        call.Headers.Add("Origin", "https://example.com");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(call)).StatusCode);
    }
}
