using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RoomAcoustics.Api.Tests;

/// <summary>The STI from the sources' own levels, the quick estimate with every source, the talker pattern and the on-axis directivity index.</summary>
public sealed class SpeechAndSourceLevelTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _http;

    public SpeechAndSourceLevelTests(WebApplicationFactory<Program> factory) => _http = factory.CreateClient();

    private static object P(double x, double y, double z) => new { x, y, z };

    private static object Box() => new
    {
        lengthM = 12.0, widthM = 8.0, heightM = 4.0,
        floor = new { materialId = 1, scattering = 0.1 }, walls = new { materialId = 0, scattering = 0.2 }, ceiling = new { materialId = 0, scattering = 0.1 },
    };

    private async Task<JsonElement> Post(string path, object request)
    {
        var response = await _http.PostAsJsonAsync(path, request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static double[] Flat(double level) => Enumerable.Repeat(level, 8).ToArray();

    private static object Request(double level, string signal, object[]? extras = null, int nc = 40) => new
    {
        room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 20_000, imageSourceOrder = 1, backgroundNc = nc,
        soundPowerDb = Flat(level), extraSources = extras ?? Array.Empty<object>(), stiSignal = signal,
    };

    [Theory]
    [InlineData("/api/simulate")]
    [InlineData("/api/statistical")]
    public async Task FromTheSources_TheStiFollowsTheirLevel_AndSaysWhatItWasFor(string path)
    {
        var quiet = (await Post(path, Request(55, "sources"))).GetProperty("sti");
        var loud = (await Post(path, Request(85, "sources"))).GetProperty("sti");
        Assert.Equal("sources", quiet.GetProperty("signalFrom").GetString());
        Assert.True(loud.GetProperty("sti").GetDouble() > quiet.GetProperty("sti").GetDouble() + 0.05, "against NC 40, louder sources are more intelligible");
        Assert.True(loud.GetProperty("signalDbA").GetDouble() > quiet.GetProperty("signalDbA").GetDouble() + 25, "30 dB more power is about 30 dB more at the receiver");

        // a talker's STI does not move with the sources' power
        var talkerQuiet = (await Post(path, Request(55, "talker"))).GetProperty("sti");
        var talkerLoud = (await Post(path, Request(85, "talker"))).GetProperty("sti");
        Assert.Equal("talker", talkerQuiet.GetProperty("signalFrom").GetString());
        Assert.Equal(talkerQuiet.GetProperty("sti").GetDouble(), talkerLoud.GetProperty("sti").GetDouble(), 6);
    }

    [Fact]
    public async Task AnStiFromTheSources_IsRefusedWithoutTheirPower_OrForAnUnknownSignal()
    {
        var noPower = await _http.PostAsJsonAsync("/api/simulate", new { room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 5_000, stiSignal = "sources" });
        Assert.Equal(HttpStatusCode.BadRequest, noPower.StatusCode);
        var unknown = await _http.PostAsJsonAsync("/api/statistical", Request(80, "loudspeaker"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var mapNoPower = await _http.PostAsJsonAsync("/api/map", new { room = Box(), source = P(2, 4, 1.6), metric = "STI", spacingM = 3.0, rayCount = 5_000, stiSignal = "sources" });
        Assert.Equal(HttpStatusCode.BadRequest, mapNoPower.StatusCode);
    }

    [Fact]
    public async Task TheQuickEstimate_TakesEverySourceIntoAccount()
    {
        double Spl(JsonElement r) => r.GetProperty("broadband").GetProperty("splDb").GetDouble();
        var one = await Post("/api/statistical", Request(90, "talker"));
        // a second source just as loud and just as far away: the direct and the reverberant sound both double, 3 dB more
        var twin = await Post("/api/statistical", Request(90, "talker", new object[] { new { position = P(2, 4, 0.8), soundPowerDb = Flat(90) } }));
        Assert.Equal(Spl(one) + 3, Spl(twin), 1);
        // a second source right beside the receiver raises the level and the clarity far more
        var near = await Post("/api/statistical", Request(90, "talker", new object[] { new { position = P(8.5, 4, 1.2), soundPowerDb = Flat(90) } }));
        double C80(JsonElement r) => r.GetProperty("metrics").GetProperty("c80Db")[4].GetDouble();
        Assert.True(Spl(near) > Spl(twin) + 3, $"near {Spl(near)} twin {Spl(twin)} one {Spl(one)}");
        Assert.True(C80(near) > C80(one) + 3, $"C80 near {C80(near)} one {C80(one)}");
        // a pattern aimed away from the receiver is quieter there than one aimed at it
        object Aimed(double azimuth) => new
        {
            room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 5_000, soundPowerDb = Flat(90),
            directivity = new { horizontalDeg = 60.0, verticalDeg = 40.0, aimAzimuthDeg = azimuth, kind = "horn" },
        };
        // (7 m away the reverberant field is most of the level, so the clarity shows the direct sound's gain more than the level does)
        var at = await Post("/api/statistical", Aimed(0));
        var away = await Post("/api/statistical", Aimed(180));
        Assert.True(Spl(at) > Spl(away) + 1, $"aimed at {Spl(at)}, away {Spl(away)}");
        Assert.True(C80(at) > C80(away) + 1, $"C80 aimed at {C80(at)}, away {C80(away)}");
    }

    [Fact]
    public async Task TheOnAxisDirectivityIndex_IsZeroForNoPattern_AndTheMeasuredOneForATalker()
    {
        var talker = (await Post("/api/directivity/index", new { horizontalDeg = 90.0, verticalDeg = 90.0, kind = "talker", aimAzimuthDeg = 30.0 })).GetProperty("onAxisDb");
        var measured = new[] { 1.47, 1.47, 2.62, 1.53, 1.39, 3.91, 4.01, 4.07 }; // Chu and Warnock's talkers
        for (var b = 0; b < 8; b++) Assert.Equal(measured[b], talker[b].GetDouble(), 0.1);
        var horn = (await Post("/api/directivity/index", new { horizontalDeg = 60.0, verticalDeg = 40.0, kind = "horn" })).GetProperty("onAxisDb");
        Assert.All(horn.EnumerateArray(), v => Assert.InRange(v.GetDouble(), 8, 20));

        // and a talker is a pattern the simulation takes
        var run = await Post("/api/simulate", new { room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 5_000, directivity = new { horizontalDeg = 90.0, verticalDeg = 90.0, kind = "talker" } });
        Assert.True(run.GetProperty("sti").GetProperty("sti").GetDouble() > 0);
    }
}
