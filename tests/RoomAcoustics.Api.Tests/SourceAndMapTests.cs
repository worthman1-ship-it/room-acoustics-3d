using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RoomAcoustics.Api.Tests;

/// <summary>Several sources, line arrays, measured balloons, sections, height matrices and third-octave maps.</summary>
public sealed class SourceAndMapTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _http;

    public SourceAndMapTests(WebApplicationFactory<Program> factory) => _http = factory.CreateClient();

    private static object P(double x, double y, double z) => new { x, y, z };

    private static object Box() => new
    {
        lengthM = 12.0, widthM = 8.0, heightM = 4.0,
        floor = new { materialId = 1, scattering = 0.1 }, walls = new { materialId = 0, scattering = 0.2 }, ceiling = new { materialId = 0, scattering = 0.1 },
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> Run(object request)
    {
        var response = await _http.PostAsJsonAsync("/api/simulate", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    private static double Mid(JsonElement metrics, string name) => metrics.GetProperty(name)[4].GetDouble(); // the 1 kHz band

    [Fact]
    public async Task ASecondSource_AddsItsPower_AndAFarAwayOneIsHeardLaterAndAnyBadOneIsRefused()
    {
        var one = await Run(new { room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 20_000 });
        var two = await Run(new
        {
            room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 20_000,
            extraSources = new[] { new { position = P(2, 4, 1.6), gainDb = 0.0, delayMs = 0.0 } },
        });
        // the sources share one source's power (the gains only set the balance between them), so two equal ones in one place change nothing
        Assert.Equal(0.0, Mid(two.GetProperty("metrics"), "strengthGDb") - Mid(one.GetProperty("metrics"), "strengthGDb"), 0.5);

        var farAndLate = await _http.PostAsJsonAsync("/api/simulate", new
        {
            room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 5_000,
            extraSources = new[] { new { position = P(11, 7, 1.6), gainDb = -6.0, delayMs = 80.0 } },
        });
        Assert.Equal(HttpStatusCode.OK, farAndLate.StatusCode);

        foreach (var bad in new object[]
        {
            new { position = P(50, 4, 1.6), gainDb = 0.0, delayMs = 0.0 },   // outside the room
            new { position = P(3, 4, 1.6), gainDb = 30.0, delayMs = 0.0 },   // too loud
            new { position = P(3, 4, 1.6), gainDb = 0.0, delayMs = 900.0 },  // too late
        })
        {
            var refused = await _http.PostAsJsonAsync("/api/simulate", new { room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 5_000, extraSources = new[] { bad } });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
        var tooMany = Enumerable.Range(0, 8).Select(i => new { position = P(3 + i * 0.5, 4, 1.6), gainDb = 0.0, delayMs = 0.0 }).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PostAsJsonAsync("/api/simulate", new { room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 5_000, extraSources = tooMany })).StatusCode);
    }

    [Fact]
    public async Task ExtraReceivers_GetTheirOwnResults_FirstIsThePrimary_AndBadOnesAreRefused()
    {
        var response = await Run(new
        {
            room = Box(), source = P(2, 4, 1.6), receiver = P(4, 4, 1.2), rayCount = 20_000, soundPowerDb = Enumerable.Repeat(90.0, 8).ToArray(),
            extraReceivers = new[] { P(10, 4, 1.2), P(8, 6, 1.2) },
        });
        var receivers = response.GetProperty("receivers");
        Assert.Equal(3, receivers.GetArrayLength());
        // the first is the receiver of the main results
        Assert.Equal(response.GetProperty("directDistanceM").GetDouble(), receivers[0].GetProperty("directDistanceM").GetDouble(), 6);
        Assert.Equal(response.GetProperty("broadband").GetProperty("c80Db").GetDouble(), receivers[0].GetProperty("broadband").GetProperty("c80Db").GetDouble(), 6);
        // a receiver farther from the source has the longer direct path and the lower level
        Assert.True(receivers[1].GetProperty("directDistanceM").GetDouble() > receivers[0].GetProperty("directDistanceM").GetDouble());
        Assert.True(receivers[1].GetProperty("broadband").GetProperty("splDb").GetDouble() < receivers[0].GetProperty("broadband").GetProperty("splDb").GetDouble());
        Assert.Equal(8, receivers[2].GetProperty("metrics").GetProperty("t30S").GetArrayLength());
        Assert.True(receivers[2].GetProperty("sti").GetProperty("sti").GetDouble() > 0);

        // each receiver has its own decay curves and impulse response, the first being the main ones
        foreach (var r in receivers.EnumerateArray())
        {
            Assert.Equal(8, r.GetProperty("decay").GetProperty("schroederDb").GetArrayLength());
            Assert.Equal(8, r.GetProperty("impulse").GetProperty("levelDb").GetArrayLength());
        }
        Assert.Equal(response.GetProperty("decay").GetProperty("overallSchroederDb").GetRawText(), receivers[0].GetProperty("decay").GetProperty("overallSchroederDb").GetRawText());
        Assert.Equal(response.GetProperty("impulse").GetProperty("overallDb").GetRawText(), receivers[0].GetProperty("impulse").GetProperty("overallDb").GetRawText());
        // the farther receiver hears the direct sound later: its impulse response starts with more empty bins
        int Empty(JsonElement r) => r.GetProperty("impulse").GetProperty("overallDb").EnumerateArray().TakeWhile(v => v.ValueKind == JsonValueKind.Null).Count();
        Assert.True(Empty(receivers[1]) > Empty(receivers[0]));

        // without any, the list is the one receiver
        var single = await Run(new { room = Box(), source = P(2, 4, 1.6), receiver = P(4, 4, 1.2), rayCount = 5_000 });
        Assert.Equal(1, single.GetProperty("receivers").GetArrayLength());

        var outside = await _http.PostAsJsonAsync("/api/simulate", new { room = Box(), source = P(2, 4, 1.6), receiver = P(4, 4, 1.2), rayCount = 5_000, extraReceivers = new[] { P(50, 4, 1.2) } });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
        var tooMany = Enumerable.Range(0, 8).Select(i => P(3 + i * 0.5, 5, 1.2)).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PostAsJsonAsync("/api/simulate", new { room = Box(), source = P(2, 4, 1.6), receiver = P(4, 4, 1.2), rayCount = 5_000, extraReceivers = tooMany })).StatusCode);
    }

    [Fact]
    public async Task ALineOfReceiversFromOneSource_GivesTheIso3382_3OpenPlanOfficeResults()
    {
        var office = new
        {
            lengthM = 24.0, widthM = 8.0, heightM = 3.0,
            floor = new { materialId = 1, scattering = 0.1 }, walls = new { materialId = 0, scattering = 0.2 }, ceiling = new { materialId = 1, scattering = 0.1 },
        };
        var line = new[] { 4.0, 6, 8, 11, 14, 18, 21 }.Select(x => P(x, 4, 1.2)).ToArray();
        var response = await Run(new { room = office, source = P(2, 4, 1.2), receiver = P(5, 4, 1.2), rayCount = 20_000, extraReceivers = line });
        var open = response.GetProperty("openPlan");
        Assert.Equal(JsonValueKind.Null, open.GetProperty("notWorkedOut").ValueKind);
        Assert.True(open.GetProperty("omnidirectional").GetBoolean());
        Assert.InRange(open.GetProperty("backgroundDbA").GetDouble(), 39, 40); // NC 30, A-weighted over 125 Hz to 8 kHz

        // a position for each receiver; its speech is the standard's 1 m speech taken there by G, A-weighted over 125 Hz to 8 kHz
        var positions = open.GetProperty("positions");
        Assert.Equal(8, positions.GetArrayLength());
        double[] at1m = { 49.9, 54.3, 58.0, 52.0, 44.8, 38.8, 33.5 }, a = { -16.1, -8.6, -3.2, 0, 1.2, 1.0, -1.1 };
        for (var i = 0; i < 8; i++)
        {
            var g = response.GetProperty("receivers")[i].GetProperty("metrics").GetProperty("strengthGDb");
            var speech = 10 * Math.Log10(Enumerable.Range(0, 7).Sum(b => Math.Pow(10, (at1m[b] + g[b + 1].GetDouble() - 20 + a[b]) / 10)));
            Assert.InRange(positions[i].GetProperty("speechDbA").GetDouble(), speech - 0.01, speech + 0.01);
            Assert.Equal(response.GetProperty("receivers")[i].GetProperty("directDistanceM").GetDouble(), positions[i].GetProperty("distanceM").GetDouble(), 3);
        }

        // the receivers are 3, 2, 4, 6, 9, 12, 16 and 19 m away: the decay is taken over the seven within 2 to 16 m, the STI line over all of them
        Assert.Equal(7, open.GetProperty("decayPositions").GetInt32());
        Assert.Equal(8, open.GetProperty("stiPositions").GetInt32());
        Assert.InRange(open.GetProperty("spatialDecayDb").GetDouble(), 0.5, 6.1);
        Assert.InRange(open.GetProperty("speechAt4mDbA").GetDouble(), 40, 57.4);
        // the nearest workstation is the second receiver, 2 m away; the farthest hears less clearly
        Assert.Equal(positions[1].GetProperty("sti").GetDouble(), open.GetProperty("nearestSti").GetDouble(), 6);
        Assert.True(positions[1].GetProperty("sti").GetDouble() > positions[7].GetProperty("sti").GetDouble());

        // with a second source, or too few receivers, there are no single numbers, and it says why
        var two = await Run(new { room = office, source = P(2, 4, 1.2), receiver = P(5, 4, 1.2), rayCount = 5_000, extraReceivers = line, extraSources = new[] { new { position = P(20, 4, 1.2) } } });
        Assert.Contains("one source", two.GetProperty("openPlan").GetProperty("notWorkedOut").GetString());
        var few = await Run(new { room = office, source = P(2, 4, 1.2), receiver = P(5, 4, 1.2), rayCount = 5_000, extraReceivers = line.Take(2).ToArray() });
        Assert.Contains("at least 4", few.GetProperty("openPlan").GetProperty("notWorkedOut").GetString());
        Assert.Equal(3, few.GetProperty("openPlan").GetProperty("positions").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, few.GetProperty("openPlan").GetProperty("spatialDecayDb").ValueKind);
    }

    [Fact]
    public async Task ALineOfReceiversFromOneSource_GivesTheIso14257WorkroomResults()
    {
        var hall = new
        {
            lengthM = 24.0, widthM = 8.0, heightM = 3.0,
            floor = new { materialId = 1, scattering = 0.1 }, walls = new { materialId = 0, scattering = 0.2 }, ceiling = new { materialId = 1, scattering = 0.1 },
        };
        var line = new[] { 4.0, 6, 8, 11, 14, 18, 21 }.Select(x => P(x, 4, 1.2)).ToArray();
        double[] lw = { 85, 90, 92, 94, 93, 90, 86, 80 };
        var response = await Run(new { room = hall, source = P(2, 4, 1.2), receiver = P(5, 4, 1.2), rayCount = 20_000, extraReceivers = line, soundPowerDb = lw });
        var work = response.GetProperty("workroom");
        Assert.Equal(JsonValueKind.Null, work.GetProperty("notWorkedOut").ValueKind);
        Assert.Equal(5, work.GetProperty("d1M").GetDouble());
        Assert.Equal(16, work.GetProperty("d2M").GetDouble());

        // each position's distribution value is Lp - LW: G - 31 in each band 125 Hz to 4 kHz, and the source's spectrum's whole level less its whole power
        var positions = work.GetProperty("positions");
        Assert.Equal(8, positions.GetArrayLength());
        for (var i = 0; i < 8; i++)
        {
            var receiver = response.GetProperty("receivers")[i];
            var g = receiver.GetProperty("metrics").GetProperty("strengthGDb");
            var spl = receiver.GetProperty("metrics").GetProperty("splDb");
            var r = receiver.GetProperty("directDistanceM").GetDouble();
            for (var b = 0; b < 6; b++)
                Assert.InRange(positions[i].GetProperty("bandsDb")[b].GetDouble() - (g[b + 1].GetDouble() - 31), -0.01, 0.01);
            Assert.Equal(-20 * Math.Log10(r) - 11, positions[i].GetProperty("referenceDb").GetDouble(), 2);
            double Sum(IEnumerable<double> levels) => 10 * Math.Log10(levels.Sum(l => Math.Pow(10, l / 10)));
            var own = Sum(Enumerable.Range(1, 6).Select(b => spl[b].GetDouble())) - Sum(lw.Skip(1).Take(6));
            Assert.InRange(positions[i].GetProperty("sourceDb").GetDouble(), own - 0.02, own + 0.02);
        }

        // the receivers are 3, 2, 4, 6, 9, 12, 16 and 19 m away: three in the near region (1 to 5 m), four in the middle (5 to 16 m) and two in the far (from
        // 16 m), too few there. The hall is reverberant, so in the middle the curves fall more slowly than a free field's 6 dB per doubling and lie above it
        var curves = work.GetProperty("curves");
        Assert.Equal(new[] { "normalised", "source", "125", "250", "500", "1000", "2000", "4000" }, curves.EnumerateArray().Select(c => c.GetProperty("curve").GetString()));
        foreach (var c in curves.EnumerateArray())
        {
            var regions = c.GetProperty("regions");
            Assert.Equal(new[] { "near", "middle", "far" }, regions.EnumerateArray().Select(g => g.GetProperty("region").GetString()));
            Assert.Equal(new[] { 3, 4, 2 }, regions.EnumerateArray().Select(g => g.GetProperty("positions").GetInt32()));
            Assert.Equal(new[] { 4.0, 10, 30 }, regions.EnumerateArray().Select(g => g.GetProperty("atM").GetDouble()));
            Assert.Equal(JsonValueKind.Null, regions[2].GetProperty("toM").ValueKind);
            var middle = regions[1];
            Assert.InRange(middle.GetProperty("decayPerDoublingDb").GetDouble(), 0, 6.1);
            Assert.True(middle.GetProperty("excessDb").GetDouble() > 0);
            Assert.True(middle.GetProperty("excessAtDb").GetDouble() > 0);
            Assert.True(double.IsFinite(regions[0].GetProperty("decayPerDoublingDb").GetDouble()));
            foreach (var key in new[] { "decayPerDoublingDb", "excessDb", "excessAtDb" }) Assert.Equal(JsonValueKind.Null, regions[2].GetProperty(key).ValueKind);
        }

        // with d1 and d2 set to 8 m and 20 m the receivers fall into the regions afresh: 2, 3, 4 and 6 m near, 9, 12, 16 and 19 m middle, none far
        var moved = (await Run(new { room = hall, source = P(2, 4, 1.2), receiver = P(5, 4, 1.2), rayCount = 5_000, extraReceivers = line, workroomD1M = 8.0, workroomD2M = 20.0 }))
            .GetProperty("workroom");
        Assert.Equal((8.0, 20.0), (moved.GetProperty("d1M").GetDouble(), moved.GetProperty("d2M").GetDouble()));
        Assert.Equal(new[] { 4, 4, 0 }, moved.GetProperty("curves")[0].GetProperty("regions").EnumerateArray().Select(g => g.GetProperty("positions").GetInt32()));
        var bad = await _http.PostAsJsonAsync("/api/simulate", new { room = hall, source = P(2, 4, 1.2), receiver = P(5, 4, 1.2), rayCount = 5_000, workroomD1M = 20.0, workroomD2M = 8.0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // with a second source, or too few receivers in every region, there are no DL2 or DLf, and it says why
        var two = await Run(new { room = hall, source = P(2, 4, 1.2), receiver = P(5, 4, 1.2), rayCount = 5_000, extraReceivers = line, extraSources = new[] { new { position = P(20, 4, 1.2) } } });
        Assert.Contains("one source", two.GetProperty("workroom").GetProperty("notWorkedOut").GetString());
        var few = await Run(new { room = hall, source = P(2, 4, 1.2), receiver = P(5, 4, 1.2), rayCount = 5_000, extraReceivers = line.Take(1).ToArray() });
        Assert.Contains("at least 3", few.GetProperty("workroom").GetProperty("notWorkedOut").GetString());
        Assert.Equal(2, few.GetProperty("workroom").GetProperty("positions").GetArrayLength());
        Assert.Equal(0, few.GetProperty("workroom").GetProperty("curves").GetArrayLength());
        // without a sound power there is no curve for the source's own spectrum
        Assert.Equal(JsonValueKind.Null, few.GetProperty("workroom").GetProperty("positions")[0].GetProperty("sourceDb").ValueKind);
    }

    [Fact]
    public async Task EachSourceHasItsOwnSoundPowerSpectrum_ThatSharesTheResponseBandByBand()
    {
        object Request(object? extra, double[]? first = null) => new
        {
            room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 20_000, imageSourceOrder = 1,
            soundPowerDb = first ?? Enumerable.Repeat(90.0, 8).ToArray(), extraSources = extra is null ? Array.Empty<object>() : new[] { extra },
        };
        double Spl(JsonElement r, int band) => r.GetProperty("metrics").GetProperty("splDb")[band].GetDouble();

        // a power of its own is the same as the first source's moved by a gain
        var byGain = await Run(Request(new { position = P(10, 6, 1.6), gainDb = -10.0 }));
        var byPower = await Run(Request(new { position = P(10, 6, 1.6), soundPowerDb = Enumerable.Repeat(80.0, 8).ToArray() }));
        Assert.Equal(byGain.GetProperty("broadband").GetProperty("splDb").GetDouble(), byPower.GetProperty("broadband").GetProperty("splDb").GetDouble(), 6);

        // a source that is far louder at 1 kHz only raises that band, and the others hardly
        var one = await Run(Request(null));
        var loud1k = await Run(Request(new { position = P(10, 6, 1.6), soundPowerDb = new[] { 60.0, 60, 60, 60, 120, 60, 60, 60 } }));
        Assert.True(Spl(loud1k, 4) > Spl(one, 4) + 15, "1 kHz is dominated by the loud source");
        Assert.InRange(Spl(loud1k, 0) - Spl(one, 0), -6, 6);

        // and each is refused when it makes no sense
        var noFirst = await _http.PostAsJsonAsync("/api/simulate", new { room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 5_000,
            extraSources = new[] { new { position = P(10, 6, 1.6), soundPowerDb = Enumerable.Repeat(80.0, 8).ToArray() } } });
        Assert.Equal(HttpStatusCode.BadRequest, noFirst.StatusCode);
        var wrongLength = await _http.PostAsJsonAsync("/api/simulate", Request(new { position = P(10, 6, 1.6), soundPowerDb = new[] { 80.0, 80 } }));
        Assert.Equal(HttpStatusCode.BadRequest, wrongLength.StatusCode);
    }

    [Fact]
    public async Task StipaAndRasti_AreGivenForEveryReceiver_AndTheQuickEstimate_AndTrackTheFullSti()
    {
        var response = await Run(new { room = Box(), source = P(2, 4, 1.6), receiver = P(5, 4, 1.2), rayCount = 20_000, extraReceivers = new[] { P(11, 4, 1.2) } });
        foreach (var r in response.GetProperty("receivers").EnumerateArray())
        {
            var sti = r.GetProperty("sti");
            double sti0 = sti.GetProperty("sti").GetDouble(), stipa = sti.GetProperty("stipa").GetDouble(), rasti = sti.GetProperty("rasti").GetDouble();
            Assert.InRange(stipa, 0, 1);
            Assert.InRange(rasti, 0, 1);
            Assert.InRange(stipa, sti0 - 0.1, sti0 + 0.1);
            Assert.InRange(rasti, sti0 - 0.2, sti0 + 0.2);
        }
        Assert.Equal(response.GetProperty("sti").GetProperty("stipa").GetDouble(), response.GetProperty("receivers")[0].GetProperty("sti").GetProperty("stipa").GetDouble());

        var quick = await Json(await _http.PostAsJsonAsync("/api/statistical", new { room = Box(), source = P(2, 4, 1.6), receiver = P(5, 4, 1.2) }));
        var quickSti = quick.GetProperty("sti");
        Assert.InRange(quickSti.GetProperty("stipa").GetDouble(), quickSti.GetProperty("sti").GetDouble() - 0.1, quickSti.GetProperty("sti").GetDouble() + 0.1);
        Assert.InRange(quickSti.GetProperty("rasti").GetDouble(), 0, 1);
    }

    [Fact]
    public async Task TheOtherGlobalEstimates_AreGiven_InTheSimulationAndTheQuickEstimate()
    {
        var request = new { room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 5_000 };
        var simulated = (await Run(request)).GetProperty("metrics");
        var quickResponse = await _http.PostAsJsonAsync("/api/statistical", request);
        Assert.Equal(HttpStatusCode.OK, quickResponse.StatusCode);
        var quick = (await Json(quickResponse)).GetProperty("metrics");
        foreach (var metrics in new[] { simulated, quick })
        {
            for (var b = 0; b < 8; b++)
            {
                double At(string name) => metrics.GetProperty(name)[b].GetDouble();
                // the walls and ceiling are alike but the floor is not, so the estimates differ; the orderings below always hold
                Assert.True(At("millingtonT60S") <= At("eyringT60S") + 1e-9, "Millington is never longer than Eyring");
                Assert.True(At("fitzroyT60S") >= At("arauPuchadesT60S") - 1e-9, "an arithmetic mean is never below the geometric one");
                Assert.True(At("arauPuchadesT60S") > 0);
            }
        }
    }

    private static object Flat(double alpha, double? audienceEdgeM = null, bool open = false) => new
    {
        materialId = 0, scattering = 0.1, customAlpha = Enumerable.Repeat(alpha, 8).ToArray(), audienceEdgeM, open,
    };

    private async Task<double> SabineAt1k(object floor, object walls)
    {
        var room = new { lengthM = 12.0, widthM = 8.0, heightM = 4.0, floor, walls, ceiling = Flat(0.1) };
        var response = await _http.PostAsJsonAsync("/api/statistical", new { room, source = P(2, 4, 1.6), receiver = P(9, 4, 1.2) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response)).GetProperty("metrics").GetProperty("sabineT60S")[4].GetDouble();
    }

    [Fact]
    public async Task AnAudienceArea_AbsorbsMoreByItsExposedEdge_AsIfItsAreaWereLargerByAHalfMetreStrip()
    {
        // the 12 x 8 m floor is 96 m2: 48 m of exposed edge adds a strip 0.5 m wide, 24 m2, so its absorption is 1.25 times as much
        var audience = await SabineAt1k(Flat(0.3, 48), Flat(0.1));
        var larger = await SabineAt1k(Flat(0.375), Flat(0.1));
        Assert.Equal(larger, audience, 3);
        // no exposed edge (or no audience area) changes nothing
        var plain = await SabineAt1k(Flat(0.3), Flat(0.1));
        Assert.Equal(plain, await SabineAt1k(Flat(0.3, 0), Flat(0.1)), 6);
        Assert.True(audience < plain);
        // the effect never takes a coefficient past 1
        Assert.Equal(await SabineAt1k(Flat(1.0), Flat(0.1)), await SabineAt1k(Flat(0.9, 500), Flat(0.1)), 3);
        var bad = await _http.PostAsJsonAsync("/api/statistical", new { room = new { lengthM = 12.0, widthM = 8.0, heightM = 4.0, floor = Flat(0.3, -1.0), walls = Flat(0.1), ceiling = Flat(0.1) }, source = P(2, 4, 1.6), receiver = P(9, 4, 1.2) });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task AnOpenSurface_LetsAllTheSoundOut_LikeAnAbsorptionOfOne()
    {
        var open = await SabineAt1k(Flat(0.3), Flat(0.1, null, true));
        Assert.Equal(await SabineAt1k(Flat(0.3), Flat(1.0)), open, 6);
        Assert.True(open < await SabineAt1k(Flat(0.3), Flat(0.1)));
    }

    [Fact]
    public async Task AirThatMakesNoSense_IsRefusedEverywhere_AndTheLimitsThemselvesAreAccepted()
    {
        foreach (var path in new[] { "/api/simulate", "/api/statistical", "/api/reflections" })
            foreach (var air in new object[] { new { temperatureC = -300.0 }, new { temperatureC = 200.0 }, new { humidityPercent = 150.0 }, new { humidityPercent = -5.0 } })
            {
                var body = JsonSerializer.SerializeToNode(new { room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 2000, imageSourceOrder = 1, maxOrder = 1 })!.AsObject();
                foreach (var (k, v) in JsonSerializer.SerializeToNode(air)!.AsObject()) body[k] = v!.DeepClone();
                Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync(path, body)).StatusCode);
            }
        foreach (var air in new object[] { new { temperatureC = -40.0, humidityPercent = 0.0 }, new { temperatureC = 60.0, humidityPercent = 100.0 } })
        {
            var body = JsonSerializer.SerializeToNode(new { room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 2000, imageSourceOrder = 1 })!.AsObject();
            foreach (var (k, v) in JsonSerializer.SerializeToNode(air)!.AsObject()) body[k] = v!.DeepClone();
            Assert.Equal(HttpStatusCode.OK, (await _http.PostAsJsonAsync("/api/simulate", body)).StatusCode);
        }
    }

    [Fact]
    public async Task TheReflectionList_GivesEachPathItsSurfacesDelayAndLevel_AndBadRequestsAreRefused()
    {
        var response = await _http.PostAsJsonAsync("/api/reflections", new { room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), maxOrder = 1 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await Json(response);
        var reflections = list.GetProperty("reflections");
        Assert.Equal(6, reflections.GetArrayLength());
        Assert.Equal(6, list.GetProperty("total").GetInt32());
        Assert.Equal(7.0, list.GetProperty("directDistanceM").GetDouble(), 1); // 7 m along x, 0.4 m of height
        var floor = reflections.EnumerateArray().Single(r => r.GetProperty("surfaces")[0].GetString() == "Floor");
        Assert.Equal(1, floor.GetProperty("order").GetInt32());
        Assert.True(floor.GetProperty("delayMs").GetDouble() > 0);
        Assert.Equal(8, floor.GetProperty("levelsDb").GetArrayLength());
        Assert.True(floor.GetProperty("overallDb").GetDouble() < 0, "a reflection is quieter than the direct sound");
        Assert.Equal(3, floor.GetProperty("path").GetArrayLength());
        var delays = reflections.EnumerateArray().Select(r => r.GetProperty("delayMs").GetDouble()).ToArray();
        Assert.Equal(delays.Order().ToArray(), delays);

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/reflections", new { room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), maxOrder = 9 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/reflections", new { room = Box(), source = P(2, 4, 1.6), receiver = P(90, 4, 1.2), maxOrder = 1 })).StatusCode);
    }

    [Fact]
    public async Task AReflectorCoverageMap_ShowsWhereAFirstReflectionFromTheSurfaceArrivesInTheWindow()
    {
        async Task<JsonElement> Coverage(object extra)
        {
            var request = new Dictionary<string, object?>
            {
                ["room"] = Box(), ["source"] = P(2, 4, 1.6), ["metric"] = "Reflector", ["heightM"] = 1.2, ["spacingM"] = 3.0, ["rayCount"] = 5_000, ["imageSourceOrder"] = 1,
            };
            foreach (var kv in (IDictionary<string, object?>)extra) request[kv.Key] = kv.Value;
            var response = await _http.PostAsJsonAsync("/api/map", request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await Json(response)).GetProperty("parameters").GetProperty("Reflector");
        }

        var ceiling = await Coverage(new Dictionary<string, object?> { ["reflectorGroup"] = "Ceiling", ["reflectorFromMs"] = 5.0, ["reflectorToMs"] = 80.0 });
        Assert.Equal("dB", ceiling.GetProperty("unit").GetString());
        var levels = ceiling.GetProperty("broadband").EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetDouble()).ToArray();
        Assert.NotEmpty(levels);
        Assert.All(levels, v => Assert.True(v < 0, "a reflection is quieter than the direct sound"));

        // a window that closes before any reflection can arrive covers nothing, and the walls are a surface of a box too
        var none = await Coverage(new Dictionary<string, object?> { ["reflectorGroup"] = "Ceiling", ["reflectorFromMs"] = 0.0, ["reflectorToMs"] = 0.1 });
        Assert.All(none.GetProperty("broadband").EnumerateArray(), v => Assert.Equal(JsonValueKind.Null, v.ValueKind));
        Assert.NotEmpty((await Coverage(new Dictionary<string, object?> { ["reflectorGroup"] = "Walls" })).GetProperty("broadband").EnumerateArray());

        var unknown = await _http.PostAsJsonAsync("/api/map", new { room = Box(), source = P(2, 4, 1.6), metric = "Reflector", rayCount = 5_000, reflectorGroup = "Nowhere" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var missing = await _http.PostAsJsonAsync("/api/map", new { room = Box(), source = P(2, 4, 1.6), metric = "Reflector", rayCount = 5_000 });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task TheRaysAreShared_BetweenTheSources()
    {
        var response = await _http.PostAsJsonAsync("/api/rays", new
        {
            room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 100, band = 4,
            extraSources = new[] { new { position = P(6, 2, 1.6), gainDb = 0.0, delayMs = 0.0 } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(100, (await Json(response)).GetArrayLength()); // 50 from each
    }

    [Fact]
    public async Task ALineArray_NarrowsTheBeam_AndItsSettingsAreChecked()
    {
        object Array(int elements, string shading = "uniform") => new
        {
            horizontalDeg = 120.0, verticalDeg = 120.0, aimAzimuthDeg = 0.0, aimElevationDeg = 0.0, kind = "lineArray",
            lineArray = new { elements, spacingM = 0.2, orientation = "horizontal", delayStepMs = 0.0, splayDegPerElement = 0.0, shading, sidelobeDb = 30.0 },
        };
        // a long horizontal array aimed along the room throws more sound at a receiver on its axis than one far off it
        async Task<double> StrengthAt(object receiver, object directivity) =>
            Mid((await Run(new { room = Box(), source = P(2, 4, 1.6), receiver, rayCount = 30_000, directivity })).GetProperty("metrics"), "strengthGDb");
        var onAxis = await StrengthAt(P(9, 4, 1.6), Array(16));
        var offAxis = await StrengthAt(P(2, 7, 1.6), Array(16));
        Assert.True(onAxis > offAxis, $"on axis {onAxis} dB, off axis {offAxis} dB");

        foreach (var bad in new object[]
        {
            new { horizontalDeg = 120.0, verticalDeg = 120.0, kind = "lineArray", lineArray = new { elements = 1, spacingM = 0.2, orientation = "vertical" } },
            new { horizontalDeg = 120.0, verticalDeg = 120.0, kind = "lineArray", lineArray = new { elements = 8, spacingM = 0.2, orientation = "sideways" } },
            new { horizontalDeg = 120.0, verticalDeg = 120.0, kind = "lineArray", lineArray = new { elements = 8, spacingM = 0.2, orientation = "vertical", shading = "bogus" } },
            new { horizontalDeg = 120.0, verticalDeg = 120.0, kind = "lineArray" },
            new { horizontalDeg = 120.0, verticalDeg = 120.0, kind = "trumpet" },
        })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await _http.PostAsJsonAsync("/api/simulate", new { room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 5_000, directivity = bad })).StatusCode);
    }

    /// <summary>A measured balloon as CSV: a front-facing lobe, louder towards azimuth 0, at every 30 degrees.</summary>
    private static string BalloonCsv()
    {
        var lines = new List<string> { "azimuth_deg,elevation_deg,level_db" };
        for (var el = -90; el <= 90; el += 30)
            for (var az = 0; az < 360; az += 30)
                lines.Add($"{az},{el},{(-12 * (1 - Math.Cos(az * Math.PI / 180) * Math.Cos(el * Math.PI / 180))).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return string.Join("\n", lines);
    }

    /// <summary>A CLF1 text file, TAB separated, with the loudspeaker's level falling 0.1 dB a degree from the front in the horizontal plane and 0.2 in the vertical.</summary>
    private static string ClfText()
    {
        var lines = new List<string>
        {
            "<CLF1>", "<VERSION>\t1", "<MODELNAME>\tHorn 9050", "<MANUFACTURER>\tTest Audio", "<MINBAND>\t125", "<MAXBAND>\t8000", "<TYPE>\t<Passive>",
            "<RADIATION>\t<Fullsphere>", "<BALLOON-SYMMETRY>\t<full>", "<BALLOON-ARC-ORDER>\t<default>", "<BALLOON-REF>\t<relative>\t<SIGN>\t<reversed>",
        };
        foreach (var band in new[] { 125, 250, 500, 1000, 2000, 4000, 8000 })
        {
            lines.Add($"<BAND>\t{band}");
            for (var arc = 0; arc <= 90; arc += 10)
                lines.Add(string.Join("\t", Enumerable.Range(0, 19).Select(c => (c * 10 * (0.1 + 0.1 * Math.Pow(Math.Sin(arc * Math.PI / 180), 2))).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        lines.Add("<CLF1END>");
        return string.Join("\r\n", lines);
    }

    [Fact]
    public async Task ACifBalloonFromByusArchive_IsUploadedAsASourcePattern_NamedAfterItsFile()
    {
        // BYU's "ASCII format" beside each .CI2 file: no header, a <BAND> line for each third-octave band, then 72 arcs of 37 levels, comma separated
        var text = string.Concat(new[] { 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000 }.Select(hz => $"<BAND>\t{hz}\r\n"
            + string.Concat(Enumerable.Range(0, 72).Select(arc => string.Concat(Enumerable.Range(0, 37).Select(k => (-0.2 * k * 5).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ",")) + "\r\n"))));
        var upload = await _http.PostAsync("/api/balloon/import?name=Flute.csv", new StringContent(text, Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var balloon = await Json(upload);
        Assert.Equal("Flute", balloon.GetProperty("name").GetString());
        // 0.2 dB down a degree from the front, whichever way: 40 degrees to the left is 8 dB down
        var horizontal = balloon.GetProperty("horizontal")[4];
        var angles = horizontal.GetProperty("anglesDeg").EnumerateArray().Select(a => a.GetDouble()).ToList();
        Assert.Equal(-8, horizontal.GetProperty("levelsDb")[angles.IndexOf(40)].GetDouble(), 1);
    }

    [Fact]
    public async Task ACommonLoudspeakerFormatTextFile_IsUploadedAsASourcePattern_ByWhatItContains()
    {
        var upload = await _http.PostAsync("/api/balloon/import?name=horn.tab", new StringContent(ClfText(), Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var balloon = await Json(upload);
        Assert.Equal("Test Audio Horn 9050", balloon.GetProperty("name").GetString());   // named by its model, not its file
        Assert.Equal(72, balloon.GetProperty("azimuths").GetInt32());
        Assert.Equal(37, balloon.GetProperty("elevations").GetInt32());
        // arcs turn from the top (0) to the left (90), so 40 degrees along the 90 degree arc is 40 degrees to the left: 8 dB down; 40 degrees up is on the 0 degree arc: 4 dB down
        var horizontal = balloon.GetProperty("horizontal")[4];
        var angles = horizontal.GetProperty("anglesDeg").EnumerateArray().Select(a => a.GetDouble()).ToList();
        Assert.Equal(-8, horizontal.GetProperty("levelsDb")[angles.IndexOf(40)].GetDouble(), 1);
        var vertical = balloon.GetProperty("vertical")[4];
        var elevations = vertical.GetProperty("anglesDeg").EnumerateArray().Select(a => a.GetDouble()).ToList();
        Assert.Equal(-4, vertical.GetProperty("levelsDb")[elevations.IndexOf(40)].GetDouble(), 1);

        // a file that is not understood says why, with its name
        var bad = await _http.PostAsync("/api/balloon/import?name=bad.tab", new StringContent(ClfText().Replace("<full>", "<diagonal>"), Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var message = (await Json(bad)).GetProperty("error").GetString();
        Assert.Contains("bad.tab", message);
        Assert.Contains("diagonal", message);

        // the extension does not matter: a CLF text file with an odd one is still read by what it holds
        var odd = await _http.PostAsync("/api/balloon/import?name=horn.dat", new StringContent(ClfText(), Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, odd.StatusCode);
        Assert.Equal("Test Audio Horn 9050", (await Json(odd)).GetProperty("name").GetString());
    }

    /// <summary>A CF2 as the CLF authoring tool lays one out (see Core's ClfBinaryTests): a pattern 6 dB down at 90 degrees off axis in every direction.</summary>
    private static byte[] Cf2()
    {
        var bytes = new byte[334_040];
        new byte[] { 0x41, 0xBD, 0x0A, 0x00, 0x01 }.CopyTo(bytes, 0);
        Encoding.Latin1.GetBytes("Test maker").CopyTo(bytes, 568);
        Encoding.Latin1.GetBytes("TM-2").CopyTo(bytes, 312);
        for (var i = 0; i < 23 * 72 * 37; i++)
        {
            var angle = i % 37 * 5.0;
            BitConverter.GetBytes(i % 37 == 36 ? 0f : (float)(-6 * (1 - Math.Cos(angle * Math.PI / 180)))).CopyTo(bytes, 78_172 + i * 4);
        }
        return bytes;
    }

    [Fact]
    public async Task AClfDistributionFile_IsRead_AndAnyOtherBinaryFileIsExplained()
    {
        // a .CF2 is read by its first bytes, whatever it is called, and named by its maker and model
        foreach (var name in new[] { "Speaker.CF2", "speaker.bin" })
        {
            var upload = await _http.PostAsync($"/api/balloon/import?name={name}", new ByteArrayContent(Cf2()));
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            var balloon = await Json(upload);
            Assert.Equal("Test maker TM-2", balloon.GetProperty("name").GetString());
            var horizontal = balloon.GetProperty("horizontal")[4];
            var angles = horizontal.GetProperty("anglesDeg").EnumerateArray().Select(a => a.GetDouble()).ToList();
            Assert.Equal(-6, horizontal.GetProperty("levelsDb")[angles.IndexOf(90)].GetDouble() - horizontal.GetProperty("levelsDb")[angles.IndexOf(0)].GetDouble(), 1);
        }

        // an EASE 3 balloon (.lob) is read too, named by its file, and an EASE file with no balloon (a .spk) says where the balloon is
        var lob = new List<byte>(Encoding.ASCII.GetBytes("EASE"));
        lob.AddRange(new byte[14]);
        lob.AddRange(BitConverter.GetBytes(21));
        lob.AddRange(new byte[5394 - lob.Count]);
        for (var band = 0; band < 21; band++)
        {
            lob.AddRange(new byte[30]);
            lob.AddRange(new byte[] { 0x88, 0x78, 0x00, 0x00, 0x00, 0x00 });
            for (var i = 0; i < 72 * 37; i++) lob.AddRange(BitConverter.GetBytes((short)Math.Round(600 * (1 - Math.Cos(i % 37 * 5.0 * Math.PI / 180)))));
        }
        var ease = await _http.PostAsync("/api/balloon/import?name=genelec-8030a.lob", new ByteArrayContent(lob.ToArray()));
        Assert.Equal(HttpStatusCode.OK, ease.StatusCode);
        var easeBalloon = await Json(ease);
        Assert.Equal("genelec-8030a", easeBalloon.GetProperty("name").GetString());
        var cut = easeBalloon.GetProperty("horizontal")[4];
        var at = cut.GetProperty("anglesDeg").EnumerateArray().Select(a => a.GetDouble()).ToList();
        Assert.Equal(-6, cut.GetProperty("levelsDb")[at.IndexOf(90)].GetDouble() - cut.GetProperty("levelsDb")[at.IndexOf(0)].GetDouble(), 1);
        var spk = await _http.PostAsync("/api/balloon/import?name=genelec-8030a.spk", new ByteArrayContent(Encoding.ASCII.GetBytes("EASE").Concat(new byte[4000]).ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, spk.StatusCode);
        Assert.Contains(".lob", (await Json(spk)).GetProperty("error").GetString());

        // another binary file (a .CF1, whose layout is not known) says what to do instead
        var other = new byte[] { 0x43, 0x46, 0x31, 0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0x7F, 0x00, 0x10, 0x02, 0x00, 0x00, 0x05 }
            .Concat(Enumerable.Range(0, 2000).Select(i => (byte)(i * 37 % 256))).ToArray();
        var refused = await _http.PostAsync("/api/balloon/import?name=Speaker.CF1", new ByteArrayContent(other));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var message = (await Json(refused)).GetProperty("error").GetString()!;
        Assert.Contains("Speaker.CF1", message);
        Assert.Contains("binary", message);
        Assert.Contains("CLF Viewer", message);
    }

    [Fact]
    public async Task AMeasuredBalloon_IsUploaded_ShownAsPolarCuts_AndUsedAsASourcePattern()
    {
        var upload = await _http.PostAsync("/api/balloon/import?name=Lobe.csv", new StringContent(BalloonCsv(), Encoding.UTF8, "text/csv"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var balloon = await Json(upload);
        Assert.Equal(12, balloon.GetProperty("azimuths").GetInt32());
        Assert.Equal(7, balloon.GetProperty("elevations").GetInt32());
        Assert.Equal(8, balloon.GetProperty("horizontal").GetArrayLength());
        Assert.Equal(73, balloon.GetProperty("horizontal")[0].GetProperty("anglesDeg").GetArrayLength());
        Assert.True(balloon.GetProperty("maxDb").GetDouble() > balloon.GetProperty("minDb").GetDouble() + 10);

        var id = balloon.GetProperty("id").GetString();
        async Task<double> StrengthAt(double aim) => Mid((await Run(new
        {
            room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.6), rayCount = 30_000,
            directivity = new { horizontalDeg = 90.0, verticalDeg = 40.0, aimAzimuthDeg = aim, kind = "balloon", balloonId = id },
        })).GetProperty("metrics"), "strengthGDb");
        Assert.True(await StrengthAt(0) > await StrengthAt(180), "the lobe aimed at the receiver should be louder than the same lobe aimed away");

        // a missing or unknown pattern, and a file that is not a pattern, are refused with a message
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/simulate", new
        {
            room = Box(), source = P(2, 4, 1.6), receiver = P(9, 4, 1.2), rayCount = 5_000,
            directivity = new { horizontalDeg = 90.0, verticalDeg = 40.0, kind = "balloon", balloonId = "nonexistent" },
        })).StatusCode);
        var bad = await _http.PostAsync("/api/balloon/import?name=Junk.csv", new StringContent("this is not a table", Encoding.UTF8, "text/csv"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Junk.csv", (await Json(bad)).GetProperty("error").GetString());
    }

    private async Task<JsonElement> MapOf(object request)
    {
        var response = await _http.PostAsJsonAsync("/api/map", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json(response);
    }

    [Fact]
    public async Task ASection_IsAVerticalPlane_WithTheRoomsOutlineOnIt()
    {
        var map = await MapOf(new { room = Box(), source = P(2, 4, 1.6), metric = "C80", mode = "section", axis = "x", positionM = 6.0, spacingM = 0.5, rayCount = 5_000 });
        Assert.Equal("section", map.GetProperty("mode").GetString());
        Assert.Equal("x", map.GetProperty("axis").GetString());
        var points = map.GetProperty("points").EnumerateArray().ToList();
        Assert.NotEmpty(points);
        Assert.All(points, p => Assert.Equal(6.0, p.GetProperty("x").GetDouble(), 6));
        Assert.True(points.Select(p => p.GetProperty("z").GetDouble()).Distinct().Count() >= 3, "points at several heights");
        Assert.True(map.GetProperty("outline").GetArrayLength() >= 4); // a rectangle: floor, ceiling and two walls

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/map", new { room = Box(), source = P(2, 4, 1.6), mode = "section", axis = "z", positionM = 6.0 })).StatusCode);
    }

    [Fact]
    public async Task AHeightMatrix_RepeatsThePlanGrid_AtEachHeight()
    {
        var map = await MapOf(new { room = Box(), source = P(2, 4, 1.6), metric = "T30", mode = "volume", heightsM = new[] { 1.2, 2.4 }, spacingM = 3.0, rayCount = 5_000 });
        Assert.Equal("volume", map.GetProperty("mode").GetString());
        var heights = map.GetProperty("points").EnumerateArray().Select(p => Math.Round(p.GetProperty("z").GetDouble(), 3)).Distinct().Order().ToList();
        Assert.Equal(new[] { 1.2, 2.4 }, heights);
        Assert.Equal(new[] { 1.2, 2.4 }, map.GetProperty("heights").EnumerateArray().Select(h => h.GetDouble()).ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/map", new { room = Box(), source = P(2, 4, 1.6), mode = "volume" })).StatusCode);
    }

    [Fact]
    public async Task AMap_GivesEveryOctaveBandAtEachPoint()
    {
        var map = await MapOf(new { room = Box(), source = P(2, 4, 1.6), metric = "T30", heightM = 1.2, spacingM = 4.0, rayCount = 20_000 });
        var points = map.GetProperty("points").EnumerateArray().ToList();
        Assert.All(map.GetProperty("parameters").GetProperty("T30").GetProperty("values").EnumerateArray(), v => Assert.Equal(8, v.GetArrayLength()));
        Assert.Equal(points.Count, map.GetProperty("parameters").GetProperty("T30").GetProperty("values").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/map", new { room = Box(), source = P(2, 4, 1.6), mode = "bogus" })).StatusCode);
    }

    [Fact]
    public async Task ASurfaceMap_SitsAFixedHeightAboveTheChosenSurface_AndRefusesOnesThatCannotBeStoodOn()
    {
        object Req(string? group, double height = 1.2) => new { room = Box(), source = P(2, 4, 1.6), metric = "C80", mode = "surface", surfaceGroup = group, heightM = height, spacingM = 3.0, rayCount = 5_000 };
        var map = await MapOf(Req("Floor", 1.5));
        Assert.Equal("surface", map.GetProperty("mode").GetString());
        Assert.Equal("Floor", map.GetProperty("surfaceGroup").GetString());
        var points = map.GetProperty("points").EnumerateArray().ToList();
        Assert.NotEmpty(points);
        Assert.All(points, p => Assert.Equal(1.5, p.GetProperty("z").GetDouble(), 6)); // a flat floor gives a flat map

        foreach (var bad in new[] { Req(null), Req(""), Req("Ceiling"), Req("Nonsense") })
            Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/map", bad)).StatusCode);
    }

    [Fact]
    public async Task AMap_HasEveryIso3382ParameterAndTheSti_EachWithABroadbandValue()
    {
        object Req(double level = 60, int nc = 30, string voice = "male") =>
            new { room = Box(), source = P(2, 4, 1.6), metric = "T30", heightM = 1.2, spacingM = 4.0, rayCount = 20_000, speechLevelDbA = level, backgroundNc = nc, voice };
        var map = await MapOf(Req());
        var parameters = map.GetProperty("parameters");
        var count = map.GetProperty("points").GetArrayLength();
        foreach (var name in new[] { "T30", "T20", "EDT", "Ts", "C80", "C50", "D50", "G", "Gearly", "Glate", "LF", "LFC", "LJ", "STearly", "STlate" })
        {
            var parameter = parameters.GetProperty(name);
            Assert.Equal(count, parameter.GetProperty("values").GetArrayLength());
            Assert.Equal(count, parameter.GetProperty("broadband").GetArrayLength());
        }

        // the broadband T30 is the mean of the 500 and 1000 Hz bands
        var t30 = parameters.GetProperty("T30");
        for (var i = 0; i < count; i++)
        {
            var bands = t30.GetProperty("values")[i];
            Assert.InRange(t30.GetProperty("broadband")[i].GetDouble(), (bands[3].GetDouble() + bands[4].GetDouble()) / 2 - 0.001, (bands[3].GetDouble() + bands[4].GetDouble()) / 2 + 0.001);
        }

        // the Speech Transmission Index is one number at each point, and follows the noise
        var sti = parameters.GetProperty("STI");
        Assert.Equal(JsonValueKind.Null, sti.GetProperty("values").ValueKind);
        Assert.All(sti.GetProperty("broadband").EnumerateArray(), v => Assert.InRange(v.GetDouble(), 0.0, 1.0));
        var noisy = (await MapOf(Req(nc: 60))).GetProperty("parameters").GetProperty("STI").GetProperty("broadband");
        Assert.True(noisy[0].GetDouble() < sti.GetProperty("broadband")[0].GetDouble(), "a louder background is less intelligible");

        // so is the Speech Intelligibility Index
        var sii = parameters.GetProperty("SII");
        Assert.Equal(JsonValueKind.Null, sii.GetProperty("values").ValueKind);
        Assert.All(sii.GetProperty("broadband").EnumerateArray(), v => Assert.InRange(v.GetDouble(), 0.0, 1.0));
        var noisySii = (await MapOf(Req(nc: 60))).GetProperty("parameters").GetProperty("SII").GetProperty("broadband");
        Assert.True(noisySii[0].GetDouble() < sii.GetProperty("broadband")[0].GetDouble(), "a louder background is less intelligible");

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/map", Req(voice: "child"))).StatusCode);
        Assert.Equal("STI", (await MapOf(new { room = Box(), source = P(2, 4, 1.6), metric = "sti", spacingM = 4.0, rayCount = 5_000 })).GetProperty("metric").GetString());
    }

    private static readonly double[] A = { -26.2, -16.1, -8.6, -3.2, 0, 1.2, 1.0, -1.1 };
    private static double Energy(IEnumerable<double> levels) => 10 * Math.Log10(levels.Sum(l => Math.Pow(10, l / 10)));

    [Fact]
    public async Task ASourceSoundPower_GivesTheSoundPressureLevelAtTheReceiver_LwPlusGMinus31_UnweightedAndAWeighted()
    {
        var power = new[] { 80.0, 85, 88, 90, 90, 88, 84, 78 };
        var result = await Run(new { room = Box(), source = P(2, 4, 1.6), receiver = P(8, 4, 1.2), rayCount = 20_000, soundPowerDb = power });
        var m = result.GetProperty("metrics");
        var spl = Enumerable.Range(0, 8).Select(b => m.GetProperty("splDb")[b].GetDouble()).ToArray();
        for (var b = 0; b < 8; b++)
        {
            Assert.InRange(spl[b], power[b] + m.GetProperty("strengthGDb")[b].GetDouble() - 31 - 0.001, power[b] + m.GetProperty("strengthGDb")[b].GetDouble() - 31 + 0.001);
            Assert.InRange(m.GetProperty("splADb")[b].GetDouble(), spl[b] + A[b] - 0.001, spl[b] + A[b] + 0.001);
        }
        var broadband = result.GetProperty("broadband");
        Assert.InRange(broadband.GetProperty("splDb").GetDouble(), Energy(spl) - 0.001, Energy(spl) + 0.001);
        Assert.InRange(broadband.GetProperty("splADb").GetDouble(), Energy(spl.Select((v, b) => v + A[b])) - 0.001, Energy(spl.Select((v, b) => v + A[b])) + 0.001);

        // without a power there is no level; a bad power is refused
        var none = await Run(new { room = Box(), source = P(2, 4, 1.6), receiver = P(8, 4, 1.2), rayCount = 5_000 });
        Assert.Equal(JsonValueKind.Null, none.GetProperty("metrics").GetProperty("splDb")[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("broadband").GetProperty("splDb").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/simulate", new { room = Box(), source = P(2, 4, 1.6), receiver = P(8, 4, 1.2), soundPowerDb = new[] { 90.0, 90 } })).StatusCode);

        // a second source of the same level doubles the power: 3 dB more for the same strength
        var two = await Run(new { room = Box(), source = P(2, 4, 1.6), receiver = P(8, 4, 1.2), rayCount = 20_000, soundPowerDb = power, extraSources = new[] { new { position = P(2, 4, 1.6), gainDb = 0.0 } } });
        var g = two.GetProperty("metrics").GetProperty("strengthGDb")[4].GetDouble();
        Assert.InRange(two.GetProperty("metrics").GetProperty("splDb")[4].GetDouble(), power[4] + 10 * Math.Log10(2) + g - 31 - 0.001, power[4] + 10 * Math.Log10(2) + g - 31 + 0.001);

        // the quick estimate gives it too
        var quick = await _http.PostAsJsonAsync("/api/statistical", new { room = Box(), source = P(2, 4, 1.6), receiver = P(8, 4, 1.2), soundPowerDb = power });
        Assert.Equal(HttpStatusCode.OK, quick.StatusCode);
        Assert.Equal(JsonValueKind.Number, (await Json(quick)).GetProperty("broadband").GetProperty("splADb").ValueKind);
    }

    [Fact]
    public async Task TheResults_HaveTheEchoCriterion_TheBassAndBrillianceRatios_AndTheAlcons()
    {
        var result = await Run(new { room = Box(), source = P(2, 4, 1.6), receiver = P(8, 4, 1.2), rayCount = 20_000 });
        Assert.Equal(8, result.GetProperty("metrics").GetProperty("echoCriterionSpeech").GetArrayLength());
        Assert.Equal(8, result.GetProperty("metrics").GetProperty("echoCriterionMusic").GetArrayLength());
        var t = result.GetProperty("metrics").GetProperty("t30S");
        var bass = (t[1].GetDouble() + t[2].GetDouble()) / (t[3].GetDouble() + t[4].GetDouble());
        Assert.InRange(result.GetProperty("broadband").GetProperty("bassRatio").GetDouble(), bass - 0.001, bass + 0.001);
        var brilliance = (t[5].GetDouble() + t[6].GetDouble()) / (t[3].GetDouble() + t[4].GetDouble());
        Assert.InRange(result.GetProperty("broadband").GetProperty("brilliance").GetDouble(), brilliance - 0.001, brilliance + 0.001);
        var sti = result.GetProperty("sti");
        var alcons = Math.Min(100, 170.5405 * Math.Exp(-5.419 * sti.GetProperty("sti").GetDouble()));
        Assert.InRange(sti.GetProperty("alconsPercent").GetDouble(), alcons - 0.05, alcons + 0.05);
    }

    [Fact]
    public async Task AMap_CanShowTheSoundPressureLevel_TheAlcons_TheRatiosAndTheEchoCriterion()
    {
        var power = new[] { 80.0, 85, 88, 90, 90, 88, 84, 78 };
        var map = await MapOf(new { room = Box(), source = P(2, 4, 1.6), metric = "SPLA", heightM = 1.2, spacingM = 4.0, rayCount = 20_000, soundPowerDb = power });
        Assert.Equal("SPLA", map.GetProperty("metric").GetString());
        Assert.Equal("dB(A)", map.GetProperty("unit").GetString());
        var parameters = map.GetProperty("parameters");
        var count = map.GetProperty("points").GetArrayLength();
        foreach (var name in new[] { "SPL", "SPLA", "Alcons", "BassRatio", "Brilliance", "EKspeech", "EKmusic", "STI", "SII", "STtotal", "EEL", "IACCearly", "IACClate", "IACCall", "BQI" })
            Assert.Equal(count, parameters.GetProperty(name).GetProperty("broadband").GetArrayLength());
        // a head at each point: IACC between 0 and 1, and BQI 1 less the early one
        for (var i = 0; i < count; i++)
        {
            var early = parameters.GetProperty("IACCearly").GetProperty("broadband")[i].GetDouble();
            Assert.InRange(early, 0, 1);
            Assert.Equal(1 - early, parameters.GetProperty("BQI").GetProperty("broadband")[i].GetDouble(), 3);
        }
        // the A-weighted number is below the unweighted one for this spectrum, and nearer the source is louder
        for (var i = 0; i < count; i++) Assert.True(parameters.GetProperty("SPLA").GetProperty("broadband")[i].GetDouble() <= parameters.GetProperty("SPL").GetProperty("broadband")[i].GetDouble() + 3);
        var sti = parameters.GetProperty("STI").GetProperty("broadband")[0].GetDouble();
        Assert.InRange(parameters.GetProperty("Alcons").GetProperty("broadband")[0].GetDouble(), Math.Min(100, 170.5405 * Math.Exp(-5.419 * sti)) - 0.05, Math.Min(100, 170.5405 * Math.Exp(-5.419 * sti)) + 0.05);

        // with no source power the level maps cannot be made, and the others are still there
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/map", new { room = Box(), source = P(2, 4, 1.6), metric = "SPL", spacingM = 4.0, rayCount = 5_000 })).StatusCode);
        var without = await MapOf(new { room = Box(), source = P(2, 4, 1.6), metric = "C80", spacingM = 4.0, rayCount = 5_000 });
        Assert.False(without.GetProperty("parameters").TryGetProperty("SPL", out _));
    }
}
