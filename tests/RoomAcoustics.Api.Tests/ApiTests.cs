using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RoomAcoustics.Api.Tests;

/// <summary>The web front end's API, started in memory: what the browser sends and gets back.</summary>
public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _http;

    public ApiTests(WebApplicationFactory<Program> factory) => _http = factory.CreateClient();

    private static object BoxRoom(double l = 8, double w = 6, double h = 3) => new
    {
        lengthM = l, widthM = w, heightM = h,
        floor = new { materialId = 1, scattering = 0.1 }, walls = new { materialId = 0, scattering = 0.2 }, ceiling = new { materialId = 0, scattering = 0.1 },
    };

    private static object Simulation(object roomOrModel, bool model, object source, object receiver, int rays = 5000) => model
        ? new { model = roomOrModel, source, receiver, rayCount = rays }
        : new { room = roomOrModel, source, receiver, rayCount = rays };

    private static object P(double x, double y, double z) => new { x, y, z };

    private async Task<JsonElement> Json(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>());

    /// <summary>A closed box of 3DFACE entities, as a DXF file: the floor on its own layer, the ceiling on another, the four walls on a third.</summary>
    private static string BoxDxf(double l, double w, double h, bool leaveOutACeiling = false)
    {
        string Quad(string layer, (double, double, double) a, (double, double, double) b, (double, double, double) c, (double, double, double) d)
        {
            var lines = new List<string> { "0", "3DFACE", "8", layer };
            var corners = new[] { a, b, c, d };
            for (var i = 0; i < 4; i++)
            {
                lines.Add((10 + i).ToString()); lines.Add(corners[i].Item1.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add((20 + i).ToString()); lines.Add(corners[i].Item2.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add((30 + i).ToString()); lines.Add(corners[i].Item3.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return string.Join("\n", lines);
        }
        var faces = new List<string>
        {
            Quad("FLOOR", (0, 0, 0), (l, 0, 0), (l, w, 0), (0, w, 0)),
            Quad("WALLS", (0, 0, 0), (l, 0, 0), (l, 0, h), (0, 0, h)),
            Quad("WALLS", (l, 0, 0), (l, w, 0), (l, w, h), (l, 0, h)),
            Quad("WALLS", (l, w, 0), (0, w, 0), (0, w, h), (l, w, h)),
            Quad("WALLS", (0, w, 0), (0, 0, 0), (0, 0, h), (0, w, h)),
        };
        if (!leaveOutACeiling) faces.Add(Quad("CEILING", (0, 0, h), (l, 0, h), (l, w, h), (0, w, h)));
        return string.Join("\n", new[] { "0", "SECTION", "2", "ENTITIES" }.Concat(faces).Concat(new[] { "0", "ENDSEC", "0", "EOF", "" }));
    }

    [Fact]
    public async Task AStatisticalQuickRun_GivesTheSameKindOfResultAtOnce_FromTheStandardEquations()
    {
        var response = await _http.PostAsJsonAsync("/api/statistical", Simulation(BoxRoom(), false, P(1, 3, 1.5), P(6, 3, 1.2)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Json(response);
        var metrics = result.GetProperty("metrics");
        Assert.Equal(144, result.GetProperty("volumeM3").GetDouble(), 6);
        Assert.Equal(8, metrics.GetProperty("t30S").GetArrayLength());
        for (var b = 0; b < 8; b++)
        {
            // a diffuse field has one decay rate: every reverberation time is Eyring's
            var eyring = metrics.GetProperty("eyringT60S")[b].GetDouble();
            Assert.True(eyring < metrics.GetProperty("sabineT60S")[b].GetDouble());
            Assert.Equal(eyring, metrics.GetProperty("t30S")[b].GetDouble(), 6);
            Assert.Equal(eyring, metrics.GetProperty("edtS")[b].GetDouble(), 6);
            var d50 = metrics.GetProperty("d50")[b].GetDouble();
            Assert.InRange(d50, 0, 1);
        }
        Assert.Equal(8, result.GetProperty("decay").GetProperty("schroederDb").GetArrayLength());
        Assert.Equal(8, result.GetProperty("impulse").GetProperty("levelDb").GetArrayLength());
        Assert.True(result.GetProperty("computeSeconds").GetDouble() < 1, "no rays are traced");

        // the decay falls 60 dB in the reverberation time
        var decay = result.GetProperty("decay");
        var step = decay.GetProperty("stepS").GetDouble();
        var curve = decay.GetProperty("schroederDb")[4];
        var t = metrics.GetProperty("eyringT60S")[4].GetDouble();
        var at = (int)Math.Round(t / step);
        Assert.Equal(-60, curve[at].GetDouble(), 0);
    }

    private static void AssertBroadbandIsTheBandMeans(JsonElement result)
    {
        var m = result.GetProperty("metrics");
        double Mean(string name, params int[] bands) => bands.Average(b => m.GetProperty(name)[b].GetDouble());
        var broadband = result.GetProperty("broadband");
        Assert.InRange(broadband.GetProperty("t30S").GetDouble(), Mean("t30S", 3, 4) - 0.001, Mean("t30S", 3, 4) + 0.001);
        Assert.InRange(broadband.GetProperty("edtS").GetDouble(), Mean("edtS", 3, 4) - 0.001, Mean("edtS", 3, 4) + 0.001);
        // C80 and C50 over 500 and 1000 Hz, as ISO 3382-1:2009 Table A.1 averages clarity
        Assert.InRange(broadband.GetProperty("c80Db").GetDouble(), Mean("c80Db", 3, 4) - 0.001, Mean("c80Db", 3, 4) + 0.001);
        Assert.InRange(broadband.GetProperty("c50Db").GetDouble(), Mean("c50Db", 3, 4) - 0.001, Mean("c50Db", 3, 4) + 0.001);
        Assert.InRange(broadband.GetProperty("strengthGDb").GetDouble(), Mean("strengthGDb", 3, 4) - 0.001, Mean("strengthGDb", 3, 4) + 0.001);
        Assert.InRange(broadband.GetProperty("earlyStrengthGDb").GetDouble(), Mean("earlyStrengthGDb", 3, 4) - 0.001, Mean("earlyStrengthGDb", 3, 4) + 0.001);
        Assert.InRange(broadband.GetProperty("lateStrengthGDb").GetDouble(), Mean("lateStrengthGDb", 3, 4) - 0.001, Mean("lateStrengthGDb", 3, 4) + 0.001);
    }

    [Fact]
    public async Task AStatisticalQuickRun_GivesTheBroadbandValuesAndTheSti_WithoutTheDirectionalMeasures()
    {
        var response = await _http.PostAsJsonAsync("/api/statistical", Simulation(BoxRoom(), false, P(1, 3, 1.5), P(6, 3, 1.2)));
        var result = await Json(response);
        AssertBroadbandIsTheBandMeans(result);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("broadband").GetProperty("lf80").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("metrics").GetProperty("lf80")[3].ValueKind);
        var sti = result.GetProperty("sti");
        Assert.InRange(sti.GetProperty("sti").GetDouble(), 0, 1);
        Assert.NotEmpty(sti.GetProperty("rating").GetString()!);
        Assert.Equal("male", sti.GetProperty("voice").GetString());
        Assert.Equal(60, sti.GetProperty("speechLevelDbA").GetDouble());
        Assert.Equal(30, sti.GetProperty("backgroundNc").GetInt32());
        Assert.InRange(sti.GetProperty("sii").GetDouble(), 0, 1);
    }

    [Fact]
    public async Task TheSii_FollowsTheBackgroundNoise_AndTheSpeechLevel()
    {
        async Task<double> Sii(int nc, double speech)
        {
            var body = JsonSerializer.SerializeToNode(Simulation(BoxRoom(), false, P(1, 3, 1.5), P(6, 3, 1.2)))!.AsObject();
            body["backgroundNc"] = nc;
            body["speechLevelDbA"] = speech;
            return (await Json(await _http.PostAsJsonAsync("/api/statistical", body))).GetProperty("sti").GetProperty("sii").GetDouble();
        }
        Assert.True(await Sii(20, 60) > await Sii(55, 60), "quieter is more intelligible");
        Assert.True(await Sii(50, 72) > await Sii(50, 55), "louder speech is more intelligible in noise");
    }

    [Fact]
    public async Task TheSti_FollowsTheSpeechLevel_TheBackgroundNoise_AndTheVoiceThatWereAskedFor()
    {
        async Task<double> Sti(object extra)
        {
            var body = JsonSerializer.SerializeToNode(Simulation(BoxRoom(), false, P(1, 3, 1.5), P(6, 3, 1.2)))!.AsObject();
            foreach (var (k, v) in JsonSerializer.SerializeToNode(extra)!.AsObject()) body[k] = v!.DeepClone();
            var response = await _http.PostAsJsonAsync("/api/statistical", body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await Json(response)).GetProperty("sti").GetProperty("sti").GetDouble();
        }
        Assert.True(await Sti(new { backgroundNc = 20 }) > await Sti(new { backgroundNc = 55 }), "quieter is more intelligible");
        Assert.True(await Sti(new { backgroundNc = 50, speechLevelDbA = 72 }) > await Sti(new { backgroundNc = 50, speechLevelDbA = 55 }), "louder speech is more intelligible in noise");
        Assert.NotEqual(await Sti(new { voice = "male" }), await Sti(new { voice = "female" }));
    }

    [Fact]
    public async Task ASimulation_GivesTheIso3382ParametersPerBand_TheirBroadbandValues_AndTheSti()
    {
        var response = await _http.PostAsJsonAsync("/api/simulate", Simulation(BoxRoom(), false, P(2, 3, 1.5), P(6, 3, 1.2), 20_000));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Json(response);
        var m = result.GetProperty("metrics");
        foreach (var name in new[] { "earlyStrengthGDb", "lateStrengthGDb", "lf80", "lfc80", "lateLateralLevelDb", "stageSupportEarlyDb", "stageSupportLateDb", "stageSupportTotalDb", "earlyEnsembleLevelDb", "iaccEarly", "iaccLate" })
            Assert.Equal(8, m.GetProperty(name).GetArrayLength());
        // IACC E3 is the early coefficient's mean over 500 Hz to 2 kHz, and BQI is 1 less it
        var broadband = result.GetProperty("broadband");
        var e3 = Enumerable.Range(3, 3).Average(b => m.GetProperty("iaccEarly")[b].GetDouble());
        Assert.Equal(e3, broadband.GetProperty("iaccEarly").GetDouble(), 3);
        Assert.Equal(1 - e3, broadband.GetProperty("binauralQualityIndex").GetDouble(), 3);
        Assert.InRange(broadband.GetProperty("iaccLate").GetDouble(), 0, 1);
        // Gade's early ensemble level is the early strength against the direct sound at 1 m rather than 10 m: 20 dB less in every band
        for (var b = 0; b < 8; b++)
            Assert.Equal(m.GetProperty("earlyStrengthGDb")[b].GetDouble() - 20, m.GetProperty("earlyEnsembleLevelDb")[b].GetDouble(), 3);
        Assert.Equal(JsonValueKind.Number, result.GetProperty("broadband").GetProperty("stageSupportTotalDb").ValueKind);
        AssertBroadbandIsTheBandMeans(result);
        // a real response has arrival directions, so the lateral fraction is a number in 0..1
        Assert.InRange(result.GetProperty("broadband").GetProperty("lf80").GetDouble(), 0, 1);
        Assert.InRange(result.GetProperty("sti").GetProperty("sti").GetDouble(), 0, 1);
        Assert.NotEmpty(result.GetProperty("sti").GetProperty("rating").GetString()!);
        Assert.InRange(result.GetProperty("sti").GetProperty("sii").GetDouble(), 0, 1);
    }

    [Fact]
    public async Task ASpeechLevelOrVoiceThatMakesNoSense_IsRefused()
    {
        foreach (var extra in new object[] { new { speechLevelDbA = 5 }, new { speechLevelDbA = 140 }, new { voice = "child" } })
        {
            var body = JsonSerializer.SerializeToNode(Simulation(BoxRoom(), false, P(1, 3, 1.5), P(6, 3, 1.2)))!.AsObject();
            foreach (var (k, v) in JsonSerializer.SerializeToNode(extra)!.AsObject()) body[k] = v!.DeepClone();
            Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/statistical", body)).StatusCode);
        }
    }

    [Fact]
    public async Task AStatisticalQuickRun_ComesWithAnImportedModelToo()
    {
        var model = await Json(await Import("box.dxf", BoxDxf(8, 6, 3)));
        var id = model.GetProperty("id").GetString();
        var surfaces = new Dictionary<string, object>
        {
            ["FLOOR"] = new { materialId = 1, scattering = 0.1 }, ["WALLS"] = new { materialId = 0, scattering = 0.2 }, ["CEILING"] = new { materialId = 0, scattering = 0.1 },
        };
        var response = await _http.PostAsJsonAsync("/api/statistical", Simulation(new { modelId = id, surfaces }, true, P(1, 3, 1.5), P(6, 3, 1.2)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(144, (await Json(response)).GetProperty("volumeM3").GetDouble(), 3);
    }

    [Fact]
    public async Task AScreenBetweenSourceAndReceiver_CutsTheLevelBehindIt_DrawnOrFromAModelLayer()
    {
        // a 1.5 m screen standing on the floor across the room, 1 m short of each side wall, between a seated talker and listener
        object Room(bool screen) => new
        {
            lengthM = 10.0, widthM = 6.0, heightM = 3.0,
            floor = new { materialId = 1, scattering = 0.1 }, walls = new { materialId = 1, scattering = 0.1 }, ceiling = new { materialId = 1, scattering = 0.1 },
            screens = screen ? new[] { new { x = 5.0, y = 3.0, baseZ = 0.0, lengthM = 4.0, heightM = 1.5, angleDeg = 90.0, finish = new { materialId = 1, scattering = 0.1 } } } : null,
        };
        async Task<JsonElement> Simulate(object room) => await Json(await _http.PostAsJsonAsync("/api/simulate", Simulation(room, false, P(2, 3, 1.2), P(8, 3, 1.2))));
        var open = await Simulate(Room(false));
        var screened = await Simulate(Room(true));
        double G(JsonElement r, int band) => r.GetProperty("metrics").GetProperty("strengthGDb")[band].GetDouble();
        Assert.True(G(screened, 6) < G(open, 6) - 1, "the screen shields the listener at 4 kHz");
        Assert.Equal(open.GetProperty("volumeM3").GetDouble(), screened.GetProperty("volumeM3").GetDouble(), 6);

        // a receiver within 0.5 m of a screen is refused, as one near a wall is
        var near = await _http.PostAsJsonAsync("/api/simulate", Simulation(Room(true), false, P(2, 3, 1.2), P(5.3, 3, 1.2)));
        Assert.Equal(HttpStatusCode.BadRequest, near.StatusCode);

        // the same screen as a layer of an imported model: named as screens, it is taken for one, and the room is closed without it
        var dxf = BoxDxf(10, 6, 3).Replace("0\nENDSEC", string.Join("\n", new[] { "0", "3DFACE", "8", "SCREENS", "10", "5", "20", "1", "30", "0", "11", "5", "21", "5", "31", "0",
            "12", "5", "22", "5", "32", "1.5", "13", "5", "23", "1", "33", "1.5" }) + "\n0\nENDSEC");
        var model = await Json(await Import("office.dxf", dxf));
        Assert.Equal(JsonValueKind.Null, model.GetProperty("closedError").ValueKind);
        Assert.Equal(new[] { "SCREENS" }, model.GetProperty("screenGroups").EnumerateArray().Select(g => g.GetString()));
        var surfaces = new Dictionary<string, object>
        {
            ["FLOOR"] = new { materialId = 1, scattering = 0.1 }, ["WALLS"] = new { materialId = 1, scattering = 0.1 }, ["CEILING"] = new { materialId = 1, scattering = 0.1 },
            ["SCREENS"] = new { materialId = 1, scattering = 0.1, screen = true },
        };
        var fromModel = await Json(await _http.PostAsJsonAsync("/api/simulate", Simulation(new { modelId = model.GetProperty("id").GetString(), surfaces }, true, P(2, 3, 1.2), P(8, 3, 1.2))));
        Assert.True(G(fromModel, 6) < G(open, 6) - 1, "the modelled screen shields the listener too");
    }

    [Fact]
    public async Task APartitionAcrossTheRoom_CouplesTwoRooms_ThroughItsReductionAndItsDoor()
    {
        // a 10 x 6 x 3 m room split at x = 5 by a wall from side to side and floor to ceiling: the source in one half, the receiver in the other
        object Room(double[] reduction, object? door) => new
        {
            lengthM = 10.0, widthM = 6.0, heightM = 3.0,
            floor = new { materialId = 1, scattering = 0.1 }, walls = new { materialId = 1, scattering = 0.1 }, ceiling = new { materialId = 1, scattering = 0.1 },
            screens = new[] { new { x = 5.0, y = 3.0, baseZ = 0.0, lengthM = 6.0, heightM = 3.0, angleDeg = 90.0,
                                    finish = new { materialId = 1, scattering = 0.1, transmissionLossDb = reduction }, door } },
        };
        async Task<JsonElement> Simulate(object room) => await Json(await _http.PostAsJsonAsync("/api/simulate", Simulation(room, false, P(2, 3, 1.2), P(8, 2, 1.2))));
        double G(JsonElement r) => r.GetProperty("broadband").GetProperty("strengthGDb").GetDouble();
        var light = await Simulate(Room(Enumerable.Repeat(20.0, 8).ToArray(), null));
        var heavy = await Simulate(Room(Enumerable.Repeat(50.0, 8).ToArray(), null));
        var door = await Simulate(Room(Enumerable.Repeat(50.0, 8).ToArray(), new { offsetM = 2.5, widthM = 1.0, heightM = 2.1 }));
        Assert.InRange(G(light) - G(heavy), 25, 35); // 30 dB more reduction, 30 dB less level
        Assert.True(G(door) > G(heavy) + 15, "the door lets far more through than the wall");
        // the direct sound only passes through the wall, cut by its reduction
        Assert.Equal(heavy.GetProperty("broadband").GetProperty("directStrengthGDb").GetDouble() + 30, light.GetProperty("broadband").GetProperty("directStrengthGDb").GetDouble(), 6);

        var bad = await _http.PostAsJsonAsync("/api/simulate", Simulation(Room(new[] { 20.0 }, null), false, P(2, 3, 1.2), P(8, 2, 1.2)));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("eight values", await bad.Content.ReadAsStringAsync());
        var wide = await _http.PostAsJsonAsync("/api/simulate", Simulation(Room(Enumerable.Repeat(20.0, 8).ToArray(), new { offsetM = 5.5, widthM = 1.0, heightM = 2.1 }), false, P(2, 3, 1.2), P(8, 2, 1.2)));
        Assert.Equal(HttpStatusCode.BadRequest, wide.StatusCode);
        Assert.Contains("door", await wide.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ASurfaceSource_RadiatesFromItsWholeArea_ToTheSideItFaces()
    {
        object Room() => new
        {
            lengthM = 20.0, widthM = 12.0, heightM = 6.0,
            floor = new { materialId = 1, scattering = 0.1 }, walls = new { materialId = 1, scattering = 0.1 }, ceiling = new { materialId = 1, scattering = 0.1 },
        };
        // a 4 x 3 m wall radiating into the room, standing 1 m in front of the end wall; the receiver 8 m in front of it
        async Task<HttpResponseMessage> Run(double aimDeg, double widthM = 4, bool oneSided = true) => await _http.PostAsJsonAsync("/api/simulate", new
        {
            room = Room(), source = P(1, 6, 3), receiver = P(9, 6, 1.5), rayCount = 5000,
            directivity = new { horizontalDeg = 90, verticalDeg = 40, aimAzimuthDeg = aimDeg, aimElevationDeg = 0, kind = "surface", surface = new { widthM, heightM = 3.0, oneSided } },
        });
        var facing = await Json(await Run(0));
        var away = await Json(await Run(180));
        double? Direct(JsonElement r) => r.GetProperty("broadband").GetProperty("directStrengthGDb") is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;
        // the receiver is in front: the direct sound arrives from every part of the surface; turned round, none does
        // 20 lg(10 / 8) = 1.9 dB for an omnidirectional point 8 m away; a cosine radiator has 4 times (6 dB) on its axis, a little less off it
        Assert.InRange(Direct(facing)!.Value, 6.5, 8.2);
        Assert.Null(Direct(away));
        Assert.True(facing.GetProperty("broadband").GetProperty("strengthGDb").GetDouble() > away.GetProperty("broadband").GetProperty("strengthGDb").GetDouble());

        var bad = await Run(0, widthM: 0);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("surface source", await bad.Content.ReadAsStringAsync());
        // a surface poking through the room's wall is refused, as a source outside it is
        var outside = await Run(90, widthM: 6);
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
    }

    [Fact]
    public async Task AModelThatIsNotClosed_CanBeTakenAsOutdoors()
    {
        // ground alone: a 40 x 40 m hard surface, nothing else
        var ground = string.Join("\n", "0", "SECTION", "2", "ENTITIES", "0", "3DFACE", "8", "GROUND",
            "10", "0", "20", "0", "30", "0", "11", "40", "21", "0", "31", "0", "12", "40", "22", "40", "32", "0", "13", "0", "23", "40", "33", "0", "0", "ENDSEC", "0", "EOF", "");
        var model = await Json(await Import("ground.dxf", ground));
        Assert.Equal(JsonValueKind.String, model.GetProperty("closedError").ValueKind);
        Assert.Equal(JsonValueKind.Object, model.GetProperty("suggestedSource").ValueKind); // above the ground, for taking it outdoors
        object Spec(bool outdoors) => new { modelId = model.GetProperty("id").GetString(), surfaces = new Dictionary<string, object> { ["GROUND"] = new { materialId = 1, scattering = 0.0, customAlpha = Enumerable.Repeat(0.02, 8).ToArray() } }, outdoors };

        var shut = await _http.PostAsJsonAsync("/api/simulate", Simulation(Spec(false), true, P(15, 20, 1.5), P(25, 20, 1.5)));
        Assert.Equal(HttpStatusCode.BadRequest, shut.StatusCode);
        var run = await _http.PostAsJsonAsync("/api/simulate", Simulation(Spec(true), true, P(15, 20, 1.5), P(25, 20, 1.5)));
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var r = await Json(run);
        // 10 m away the direct sound is G = 0 dB; the hard ground's reflection, over 10.4 m, nearly doubles the energy: 10 lg(1 + (10 / 10.44)²) = 2.8 dB
        Assert.InRange(r.GetProperty("broadband").GetProperty("directStrengthGDb").GetDouble(), -0.1, 0.1);
        Assert.InRange(r.GetProperty("broadband").GetProperty("strengthGDb").GetDouble(), 2.3, 3.3);
    }

    [Fact]
    public async Task A3DStudioFile_IsImportedWithAGroupForEachMaterial()
    {
        static byte[] Chunk(ushort id, params byte[][] contents)
        {
            var body = contents.SelectMany(c => c).ToArray();
            return BitConverter.GetBytes(id).Concat(BitConverter.GetBytes((uint)(body.Length + 6))).Concat(body).ToArray();
        }
        static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);
        // a 6 x 5 x 3 box: the floor's faces "Carpet", the rest the object's own name
        var v = new[] { (0, 0, 0), (6, 0, 0), (6, 5, 0), (0, 5, 0), (0, 0, 3), (6, 0, 3), (6, 5, 3), (0, 5, 3) };
        var faces = new[] { (0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7) }
            .SelectMany(q => new[] { (q.Item1, q.Item2, q.Item3), (q.Item1, q.Item3, q.Item4) }).ToArray();
        var file = Chunk(0x4D4D, Chunk(0x3D3D, Chunk(0x4000, Encoding.Latin1.GetBytes("Hall\0"), Chunk(0x4100,
            Chunk(0x4110, U16(8).Concat(v.SelectMany(p => new[] { (float)p.Item1, p.Item2, p.Item3 }.SelectMany(BitConverter.GetBytes))).ToArray()),
            Chunk(0x4120, U16(faces.Length).Concat(faces.SelectMany(f => U16(f.Item1).Concat(U16(f.Item2)).Concat(U16(f.Item3)).Concat(U16(0)))).ToArray(),
                Chunk(0x4130, Encoding.Latin1.GetBytes("Carpet\0"), U16(2), U16(0), U16(1)))))));
        var model = await Json(await _http.PostAsync("/api/model/import?name=Hall.3ds", new ByteArrayContent(file)));
        Assert.Equal(90, model.GetProperty("volumeM3").GetDouble(), 6);
        Assert.Equal(new[] { "Carpet", "Hall" }, model.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("name").GetString()).OrderBy(n => n));

        // a group can be renamed, merged into another, or deleted; each edit is a new model
        async Task<JsonElement> Edit(JsonElement m, string group, string action, string? to = null) =>
            await Json(await _http.PostAsJsonAsync("/api/model/group", new { modelId = m.GetProperty("id").GetString(), group, action, to }));
        string[] Names(JsonElement m) => m.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("name").GetString()!).Order().ToArray();
        var renamed = await Edit(model, "Carpet", "rename", "Floor");
        Assert.Equal(new[] { "Floor", "Hall" }, Names(renamed));
        Assert.Equal(90, renamed.GetProperty("volumeM3").GetDouble(), 6);
        var merged = await Edit(renamed, "Floor", "merge", "Hall");
        Assert.Equal(new[] { "Hall" }, Names(merged));
        Assert.Equal(90, merged.GetProperty("volumeM3").GetDouble(), 6);
        var deleted = await Edit(renamed, "Floor", "delete");
        Assert.Equal(new[] { "Hall" }, Names(deleted));
        Assert.Equal(JsonValueKind.String, deleted.GetProperty("closedError").ValueKind); // no floor: not closed any more
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/model/group",
            new { modelId = renamed.GetProperty("id").GetString(), group = "Floor", action = "rename", to = "Hall" })).StatusCode);
    }

    [Fact]
    public async Task AModelsShape_CanBeEdited_ASurfaceDeletedAndDrawnAgain_AndACornerMoved()
    {
        // a 6 x 5 x 3 box, its ceiling a group of its own
        var obj = string.Join('\n', "v 0 0 0", "v 6 0 0", "v 6 5 0", "v 0 5 0", "v 0 0 3", "v 6 0 3", "v 6 5 3", "v 0 5 3",
            "usemtl Walls", "f 1 2 3 4", "f 1 2 6 5", "f 2 3 7 6", "f 3 4 8 7", "f 4 1 5 8", "usemtl Ceiling", "f 5 6 7 8");
        var model = await Json(await Import("box.obj", obj));
        Assert.Equal(90, model.GetProperty("volumeM3").GetDouble(), 6);
        async Task<HttpResponseMessage> Edit(JsonElement m, string action, double[][] points, string? group = null) =>
            await _http.PostAsJsonAsync("/api/model/geometry", new { modelId = m.GetProperty("id").GetString(), action, group,
                points = points.Select(q => new { x = q[0], y = q[1], z = q[2] }) });

        var open = await Json(await Edit(model, "delete", new[] { new double[] { 0, 0, 3 }, new double[] { 6, 0, 3 }, new double[] { 6, 5, 3 } }));
        Assert.Equal(JsonValueKind.String, open.GetProperty("closedError").ValueKind);
        Assert.DoesNotContain(open.GetProperty("groups").EnumerateArray(), g => g.GetProperty("name").GetString() == "Ceiling");

        var closed = await Json(await Edit(open, "face", new[] { new double[] { 0, 0, 3 }, new double[] { 6, 0, 3 }, new double[] { 6, 5, 3 }, new double[] { 0, 5, 3 } }, "New ceiling"));
        Assert.Equal(90, closed.GetProperty("volumeM3").GetDouble(), 6);
        Assert.Contains(closed.GetProperty("groups").EnumerateArray(), g => g.GetProperty("name").GetString() == "New ceiling");

        var raised = await Json(await Edit(closed, "move", new[] { new double[] { 6, 5, 3 }, new double[] { 6, 5, 4 } }));
        Assert.True(raised.GetProperty("volumeM3").GetDouble() > 90);
        Assert.Equal(4, raised.GetProperty("bounds").GetProperty("max").GetProperty("z").GetDouble(), 9);

        Assert.Equal(HttpStatusCode.BadRequest, (await Edit(closed, "move", new[] { new double[] { 3, 3, 3 }, new double[] { 3, 3, 4 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Edit(closed, "twist", new[] { new double[] { 0, 0, 0 } })).StatusCode);
    }

    private async Task<HttpResponseMessage> Import(string name, string text, string query = "") =>
        await _http.PostAsync($"/api/model/import?name={name}{query}", new StringContent(text, Encoding.UTF8, "application/octet-stream"));

    [Fact]
    public async Task Health_AndTheMaterialList_AreServed()
    {
        var health = await Json(await _http.GetAsync("/api/health"));
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+(\+[0-9a-f]{7})?$", health.GetProperty("version").GetString()); // Directory.Build.props, and the commit when the build knows it

        var materials = await Json(await _http.GetAsync("/api/materials"));
        Assert.True(materials.GetArrayLength() > 30);
        var first = materials[0];
        Assert.Equal(0, first.GetProperty("id").GetInt32());
        Assert.Equal(8, first.GetProperty("alpha").GetArrayLength());
    }

    [Fact]
    public async Task ABoxRoom_IsSimulated_WithMetricsForEveryBand()
    {
        var response = await _http.PostAsJsonAsync("/api/simulate", Simulation(BoxRoom(), false, P(2, 3, 1.5), P(6, 3, 1.2)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Json(response);

        Assert.Equal(144, result.GetProperty("volumeM3").GetDouble(), 6);
        var t30 = result.GetProperty("metrics").GetProperty("t30S");
        Assert.Equal(8, t30.GetArrayLength());
        var sabine = result.GetProperty("metrics").GetProperty("sabineT60S");
        Assert.All(Enumerable.Range(0, 8), i => Assert.True(sabine[i].GetDouble() > 0));
        Assert.True(t30[4].GetDouble() > 0.2 && t30[4].GetDouble() < 10, "T30 at 1 kHz should be a sensible reverberation time");
        Assert.True(result.GetProperty("decay").GetProperty("schroederDb").GetArrayLength() == 8);
        Assert.True(result.GetProperty("impulse").GetProperty("levelDb")[0].GetArrayLength() > 100);
    }

    [Fact]
    public async Task ASourceOutsideTheRoom_IsA400_WithTheEnginesMessage()
    {
        var response = await _http.PostAsJsonAsync("/api/simulate", Simulation(BoxRoom(), false, P(20, 3, 1.5), P(6, 3, 1.2)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("inside the room", (await Json(response)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(500)]      // fewer rays than the minimum
    [InlineData(2_000_000)] // more than the limit
    public async Task ARayCountOutsideTheLimits_IsRefused(int rays)
    {
        var response = await _http.PostAsJsonAsync("/api/simulate", Simulation(BoxRoom(), false, P(2, 3, 1.5), P(6, 3, 1.2), rays));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ARoomMustBeEitherABoxOrAModel_NotNeitherNorBoth()
    {
        var neither = await _http.PostAsJsonAsync("/api/simulate", new { source = P(2, 3, 1.5), receiver = P(6, 3, 1.2) });
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);

        var both = await _http.PostAsJsonAsync("/api/simulate", new { room = BoxRoom(), model = new { modelId = "x", surfaces = new { } }, source = P(2, 3, 1.5), receiver = P(6, 3, 1.2) });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task TheRays_AreReturnedAsPolylinesFromTheSource_AndSomeReachTheReceiver()
    {
        var response = await _http.PostAsJsonAsync("/api/rays", new { room = BoxRoom(), source = P(2, 3, 1.5), receiver = P(6, 3, 1.2), rayCount = 200, maxReflections = 8, band = 4 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rays = await Json(response);
        Assert.Equal(200, rays.GetArrayLength());
        var start = rays[0].GetProperty("points")[0];
        Assert.Equal(2, start[0].GetDouble(), 6);
        Assert.Equal(3, start[1].GetDouble(), 6);
        Assert.Contains(rays.EnumerateArray(), r => r.GetProperty("reachesReceiver").GetBoolean());
        // each point carries the level the ray leaves it with: 0 dB at the source, never rising
        Assert.All(rays.EnumerateArray(), r =>
        {
            var levels = r.GetProperty("levelsDb").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            Assert.Equal(r.GetProperty("points").GetArrayLength(), levels.Length);
            Assert.Equal(0, levels[0], 6);
            Assert.True(levels.Zip(levels.Skip(1)).All(pair => pair.Second <= pair.First + 1e-9));
        });
    }

    [Fact]
    public async Task TheOverallResponse_IsTheBandsAddedTogether_InTheDecayAndTheImpulseResponse_AndForTheRays()
    {
        foreach (var path in new[] { "/api/simulate", "/api/statistical" })
        {
            var result = await Json(await _http.PostAsJsonAsync(path, Simulation(BoxRoom(), false, P(2, 3, 1.5), P(6, 3, 1.2), 20_000)));
            var impulse = result.GetProperty("impulse");
            var overall = impulse.GetProperty("overallDb");
            Assert.Equal(impulse.GetProperty("levelDb")[0].GetArrayLength(), overall.GetArrayLength());
            // adding energies can only raise the level of a bin: the overall response is above every band in the bins that have sound
            for (var i = 0; i < overall.GetArrayLength(); i++)
            {
                if (overall[i].ValueKind != JsonValueKind.Number) continue;
                for (var b = 0; b < 8; b++)
                    if (impulse.GetProperty("levelDb")[b][i].ValueKind == JsonValueKind.Number && path == "/api/simulate")
                        Assert.True(overall[i].GetDouble() >= impulse.GetProperty("levelDb")[b][i].GetDouble() - 0.001, $"bin {i}, band {b}");
            }
            var decay = result.GetProperty("decay");
            Assert.Equal(decay.GetProperty("schroederDb")[0].GetArrayLength(), decay.GetProperty("overallSchroederDb").GetArrayLength());
            Assert.Equal(0.0, decay.GetProperty("overallSchroederDb")[0].GetDouble(), 1); // a Schroeder curve starts at 0 dB
        }

        var rays = new { room = BoxRoom(), source = P(2, 3, 1.5), receiver = P(6, 3, 1.2), rayCount = 50, maxReflections = 4 };
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsJsonAsync("/api/rays", new { rays.room, rays.source, rays.receiver, rays.rayCount, rays.maxReflections, band = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/rays", new { rays.room, rays.source, rays.receiver, rays.rayCount, rays.maxReflections, band = -2 })).StatusCode);
    }

    [Fact]
    public async Task TheExampleModel_IsListed_AndImportedWithItsLayers()
    {
        var examples = await Json(await _http.GetAsync("/api/examples"));
        Assert.Contains(examples.EnumerateArray(), e => e.GetString() == "LectureTheatre.dxf");

        var response = await _http.PostAsync("/api/model/example?name=LectureTheatre.dxf", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var model = await Json(response);
        Assert.Equal(7, model.GetProperty("groups").GetArrayLength());
        Assert.Equal(910.8, model.GetProperty("volumeM3").GetDouble(), 1);
        Assert.Equal(JsonValueKind.Null, model.GetProperty("closedError").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, model.GetProperty("suggestedSource").ValueKind);
    }

    [Theory]
    [InlineData("../README.md")]
    [InlineData("..\\README.md")]
    [InlineData("nothing.dxf")]
    public async Task AnExampleName_CannotReachOutsideTheExamplesFolder(string name)
    {
        var response = await _http.PostAsync($"/api/model/example?name={Uri.EscapeDataString(name)}", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ADwgModel_IsImported_LikeTheSameModelAsDxf_AndNoTemporaryFileIsLeftBehind()
    {
        // a DWG of the same 6 x 5 x 3 box, written with the DWG library: floor, four walls and ceiling as 3DFACEs on three layers
        var l = 6.0; var w = 5.0; var h = 3.0;
        var document = new ACadSharp.CadDocument();
        void Quad(string layerName, (double, double, double) a, (double, double, double) b, (double, double, double) c, (double, double, double) d)
        {
            if (!document.Layers.TryGetValue(layerName, out var layer)) { layer = new ACadSharp.Tables.Layer(layerName); document.Layers.Add(layer); }
            CSMath.XYZ P(double x, double y, double z) => new(x, y, z);
            document.Entities.Add(new ACadSharp.Entities.Face3D
            {
                Layer = layer,
                FirstCorner = P(a.Item1, a.Item2, a.Item3), SecondCorner = P(b.Item1, b.Item2, b.Item3),
                ThirdCorner = P(c.Item1, c.Item2, c.Item3), FourthCorner = P(d.Item1, d.Item2, d.Item3),
            });
        }
        Quad("FLOOR", (0, 0, 0), (l, 0, 0), (l, w, 0), (0, w, 0));
        Quad("WALLS", (0, 0, 0), (l, 0, 0), (l, 0, h), (0, 0, h));
        Quad("WALLS", (l, 0, 0), (l, w, 0), (l, w, h), (l, 0, h));
        Quad("WALLS", (l, w, 0), (0, w, 0), (0, w, h), (l, w, h));
        Quad("WALLS", (0, w, 0), (0, 0, 0), (0, 0, h), (0, w, h));
        Quad("CEILING", (0, 0, h), (l, 0, h), (l, w, h), (0, w, h));
        byte[] dwg;
        using (var output = new MemoryStream())
        {
            using (var writer = new ACadSharp.IO.DwgWriter(output, document)) writer.Write();
            dwg = output.ToArray();
        }
        var before = Directory.GetFiles(Path.GetTempPath(), "acousticcalc-*.dwg").Length;
        var response = await _http.PostAsync("/api/model/import?name=Box.dwg", new ByteArrayContent(dwg));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var model = await Json(response);
        Assert.Equal(90, model.GetProperty("volumeM3").GetDouble(), 6);
        Assert.Equal(3, model.GetProperty("groups").GetArrayLength());
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "acousticcalc-*.dwg").Length);

        var damaged = await _http.PostAsync("/api/model/import?name=Bad.dwg", new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Equal(HttpStatusCode.BadRequest, damaged.StatusCode);
        Assert.Contains("Bad.dwg", (await Json(damaged)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AnIfcRoomAndAColladaBox_AreImported_AndTheTemporaryFileIsRemoved()
    {
        const string ifc = """
            ISO-10303-21;
            HEADER;
            FILE_DESCRIPTION(('ViewDefinition [ReferenceView]'),'2;1');
            FILE_NAME('r.ifc','2026-01-01T00:00:00',(''),(''),'','','');
            FILE_SCHEMA(('IFC4'));
            ENDSEC;
            DATA;
            #1=IFCPROJECT('0YvctVUKr0kugbFTf53O9L',$,'P',$,$,$,$,(#20),#10);
            #10=IFCUNITASSIGNMENT((#11));
            #11=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);
            #20=IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-05,#21,$);
            #21=IFCAXIS2PLACEMENT3D(#22,$,$);
            #22=IFCCARTESIANPOINT((0.,0.,0.));
            #9=IFCDIRECTION((0.,0.,1.));
            #56=IFCCARTESIANPOINT((5.,3.));
            #57=IFCAXIS2PLACEMENT2D(#56,$);
            #58=IFCRECTANGLEPROFILEDEF(.AREA.,$,#57,10.,6.);
            #59=IFCEXTRUDEDAREASOLID(#58,#21,#9,3.);
            #34=IFCPRODUCTDEFINITIONSHAPE($,$,(#35));
            #35=IFCSHAPEREPRESENTATION(#20,'Body','SweptSolid',(#59));
            #30=IFCSPACE('1b9Z1X8xT1B8bnRvlaZx03',$,'Hall',$,$,$,#34,$,.ELEMENT.,.INTERNAL.,$);
            #76=IFCCARTESIANPOINT((1.,1.));
            #77=IFCAXIS2PLACEMENT2D(#76,$);
            #78=IFCRECTANGLEPROFILEDEF(.AREA.,$,#77,2.,2.);
            #79=IFCEXTRUDEDAREASOLID(#78,#21,#9,2.);
            #44=IFCPRODUCTDEFINITIONSHAPE($,$,(#45));
            #45=IFCSHAPEREPRESENTATION(#20,'Body','SweptSolid',(#79));
            #40=IFCSPACE('1b9Z1X8xT1B8bnRvlaZx04',$,'Closet',$,$,$,#44,$,.ELEMENT.,.INTERNAL.,$);
            ENDSEC;
            END-ISO-10303-21;
            """;
        var before = Directory.GetFiles(Path.GetTempPath(), "acousticcalc-*.ifc").Length;
        var response = await Import("Hall.ifc", ifc);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var model = await Json(response);
        Assert.Equal(180, model.GetProperty("volumeM3").GetDouble(), 6);
        Assert.Equal(new[] { "Ceiling", "Floor", "Walls" }, model.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("name").GetString()).Order().ToArray());
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "acousticcalc-*.ifc").Length);
        // the floor is against the walls all round, so none of its edge is open to the room
        Assert.Equal(0, model.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("name").GetString() == "Floor").GetProperty("exposedEdgeM").GetDouble());
        // the file's rooms, by name, and the one read; another is read when asked for
        Assert.Equal(new[] { "Closet", "Hall" }, model.GetProperty("spaces").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray());
        Assert.Equal(1, model.GetProperty("space").GetInt32());
        var closet = await Json(await Import("Hall.ifc", ifc, "&space=0"));
        Assert.Equal(8, closet.GetProperty("volumeM3").GetDouble(), 6);
        Assert.Equal(0, closet.GetProperty("space").GetInt32());

        var damaged = await Import("Bad.ifc", "not an ifc file");
        Assert.Equal(HttpStatusCode.BadRequest, damaged.StatusCode);
        Assert.Contains("Bad.ifc", (await Json(damaged)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AColladaBox_IsImported_InTheMetresItsFileSays()
    {
        const string dae = """
            <?xml version="1.0"?>
            <COLLADA xmlns="http://www.collada.org/2005/11/COLLADASchema" version="1.4.1">
              <asset><unit name="inch" meter="0.5"/><up_axis>Z_UP</up_axis></asset>
              <library_geometries><geometry id="g" name="Box"><mesh>
                <source id="p"><float_array id="pa" count="24">0 0 0 20 0 0 20 12 0 0 12 0 0 0 6 20 0 6 20 12 6 0 12 6</float_array></source>
                <vertices id="v"><input semantic="POSITION" source="#p"/></vertices>
                <polylist count="6"><input semantic="VERTEX" source="#v" offset="0"/><vcount>4 4 4 4 4 4</vcount><p>0 3 2 1 4 5 6 7 0 1 5 4 1 2 6 5 2 3 7 6 3 0 4 7</p></polylist>
              </mesh></geometry></library_geometries>
              <library_visual_scenes><visual_scene id="s"><node id="n"><instance_geometry url="#g"/></node></visual_scene></library_visual_scenes>
            </COLLADA>
            """;
        var response = await Import("Box.dae", dae);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(180, (await Json(response)).GetProperty("volumeM3").GetDouble(), 6);
    }

    [Fact]
    public async Task AModelWithManyFaces_HasItsImageSourceOrderLimited_ABoxKeepsWhatWasAsked()
    {
        // a dome of 6 240 triangles: image sources grow as faces^order, so order 2 (39 million paths) is cut to 1
        var vertices = new List<string> { "v 0 0 -10" };
        const int rings = 40, segs = 80;
        for (var i = 1; i < rings; i++)
            for (var j = 0; j < segs; j++)
            {
                double th = Math.PI * i / rings, ph = 2 * Math.PI * j / segs;
                var f = (Func<double, string>)(v => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture));
                vertices.Add($"v {f(10 * Math.Sin(th) * Math.Cos(ph))} {f(10 * Math.Sin(th) * Math.Sin(ph))} {f(-10 * Math.Cos(th))}");
            }
        vertices.Add("v 0 0 10");
        int Idx(int i, int j) => 1 + (i - 1) * segs + (j % segs) + 1;
        var faces = new List<string> { "g DOME" };
        for (var j = 0; j < segs; j++) faces.Add($"f 1 {Idx(1, j + 1)} {Idx(1, j)}");
        for (var i = 1; i < rings - 1; i++)
            for (var j = 0; j < segs; j++)
            {
                faces.Add($"f {Idx(i, j)} {Idx(i, j + 1)} {Idx(i + 1, j + 1)}");
                faces.Add($"f {Idx(i, j)} {Idx(i + 1, j + 1)} {Idx(i + 1, j)}");
            }
        for (var j = 0; j < segs; j++) faces.Add($"f {vertices.Count} {Idx(rings - 1, j)} {Idx(rings - 1, j + 1)}");
        var dome = await Json(await Import("Dome.obj", string.Join(Environment.NewLine, vertices.Concat(faces)), "&yUp=false"));
        Assert.Equal(JsonValueKind.Null, dome.GetProperty("closedError").ValueKind);

        var spec = new { modelId = dome.GetProperty("id").GetString(), surfaces = new Dictionary<string, object> { ["DOME"] = new { materialId = 0, scattering = 0.2 } } };
        var run = await Json(await _http.PostAsJsonAsync("/api/simulate", Simulation(spec, true, dome.GetProperty("suggestedSource"), dome.GetProperty("suggestedReceiver"), rays: 1_000)));
        Assert.Equal(1, run.GetProperty("imageSourceOrderUsed").GetInt32());

        var box = await Json(await _http.PostAsJsonAsync("/api/simulate", Simulation(BoxRoom(), false, P(2, 3, 1.5), P(6, 3, 1.2), rays: 1_000)));
        Assert.Equal(2, box.GetProperty("imageSourceOrderUsed").GetInt32());
    }

    [Fact]
    public async Task AnImportedModel_IsSimulated_ThroughItsGroupSurfaces()
    {
        var model = await Json(await Import("Box.dxf", BoxDxf(6, 5, 3)));
        Assert.Equal(90, model.GetProperty("volumeM3").GetDouble(), 6);
        var groups = model.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("name").GetString()!).ToList();
        Assert.Equal(new[] { "FLOOR", "WALLS", "CEILING" }.Order(), groups.Order());

        var surfaces = groups.ToDictionary(g => g, g => new { materialId = g == "FLOOR" ? 1 : 0, scattering = 0.2 });
        var spec = new { modelId = model.GetProperty("id").GetString(), surfaces };
        var run = await _http.PostAsJsonAsync("/api/simulate", Simulation(spec, true, model.GetProperty("suggestedSource"), model.GetProperty("suggestedReceiver")));
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        Assert.Equal(90, (await Json(run)).GetProperty("volumeM3").GetDouble(), 6);
    }

    [Fact]
    public async Task ADrawingInMillimetres_IsScaledToMetres()
    {
        var model = await Json(await Import("Box.dxf", BoxDxf(6000, 5000, 3000), "&scale=0.001"));
        Assert.Equal(90, model.GetProperty("volumeM3").GetDouble(), 6);
        Assert.Equal(6, model.GetProperty("bounds").GetProperty("max").GetProperty("x").GetDouble(), 6);
    }

    [Fact]
    public async Task AModelThatIsNotClosed_IsReturnedForViewing_WithTheReason()
    {
        var response = await Import("Open.dxf", BoxDxf(6, 5, 3, leaveOutACeiling: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var model = await Json(response);
        Assert.Equal(JsonValueKind.String, model.GetProperty("closedError").ValueKind);
        Assert.Equal(JsonValueKind.Null, model.GetProperty("volumeM3").ValueKind);
        Assert.Equal(10, model.GetProperty("triangleCount").GetInt32());

        // the geometry check finds the hole round the top of the walls
        var check = model.GetProperty("check");
        Assert.Equal(4, check.GetProperty("openEdges").GetInt32());
        Assert.Equal(1, check.GetProperty("parts").GetInt32());
        var hole = Assert.Single(check.GetProperty("faults").EnumerateArray());
        Assert.Equal("hole", hole.GetProperty("kind").GetString());
        Assert.Equal(22, hole.GetProperty("lengthM").GetDouble(), 6);
        Assert.Equal(4, hole.GetProperty("edges").GetArrayLength());
        Assert.Equal(6, hole.GetProperty("edges")[0].GetArrayLength());
        Assert.Contains("missing", hole.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task AModelThatIsNotClosed_IsRepaired_SayingWhatWasDone()
    {
        var model = await Json(await Import("Open.dxf", BoxDxf(6, 5, 3, leaveOutACeiling: true)));
        var response = await _http.PostAsJsonAsync("/api/model/repair", new { modelId = model.GetProperty("id").GetString(), boxes = true, screens = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var repaired = await Json(response);
        Assert.Equal(JsonValueKind.Null, repaired.GetProperty("closedError").ValueKind);
        Assert.Equal(90, repaired.GetProperty("volumeM3").GetDouble(), 6);
        var step = Assert.Single(repaired.GetProperty("repairs").EnumerateArray());
        Assert.Equal("Filled a hole with \"Filled ceiling\", 30.0 m².", step.GetString());

        // a closed model has nothing to repair
        var closed = await Json(await Import("Box.dxf", BoxDxf(6, 5, 3)));
        var again = await Json(await _http.PostAsJsonAsync("/api/model/repair", new { modelId = closed.GetProperty("id").GetString() }));
        Assert.Equal(0, again.GetProperty("repairs").GetArrayLength());
    }

    [Fact]
    public async Task AGroupOfSeveralFlatSurfaces_IsSplitIntoThem()
    {
        var model = await Json(await Import("Box.dxf", BoxDxf(6, 5, 3)));
        int Surfaces(JsonElement m, string group) => m.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("name").GetString() == group).GetProperty("surfaces").GetInt32();
        Assert.Equal(4, Surfaces(model, "WALLS"));
        Assert.Equal(1, Surfaces(model, "FLOOR"));

        var response = await _http.PostAsJsonAsync("/api/model/split", new { modelId = model.GetProperty("id").GetString(), group = "WALLS" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var split = await Json(response);
        Assert.Equal(new[] { "CEILING", "FLOOR", "WALLS: Wall 1", "WALLS: Wall 2", "WALLS: Wall 3", "WALLS: Wall 4" },
            split.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("name").GetString()).OrderBy(n => n));
        Assert.Equal(90, split.GetProperty("volumeM3").GetDouble(), 6);

        // a group that is one flat surface, or one the model has not, is refused
        var one = await _http.PostAsJsonAsync("/api/model/split", new { modelId = model.GetProperty("id").GetString(), group = "FLOOR" });
        Assert.Equal(HttpStatusCode.BadRequest, one.StatusCode);
        var none = await _http.PostAsJsonAsync("/api/model/split", new { modelId = model.GetProperty("id").GetString(), group = "ROOF" });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [Fact]
    public async Task AHoleTheCheckFinds_IsFilled_AndTheRoomCloses()
    {
        var model = await Json(await Import("Open.dxf", BoxDxf(6, 5, 3, leaveOutACeiling: true)));
        var hole = Assert.Single(model.GetProperty("check").GetProperty("faults").EnumerateArray());
        Assert.True(hole.GetProperty("fillable").GetBoolean());

        var response = await _http.PostAsJsonAsync("/api/model/fill", new { modelId = model.GetProperty("id").GetString(), holes = new[] { 0 } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var filled = await Json(response);
        Assert.Equal(JsonValueKind.Null, filled.GetProperty("closedError").ValueKind);
        Assert.Equal(90, filled.GetProperty("volumeM3").GetDouble(), 6);
        Assert.Equal(JsonValueKind.Null, filled.GetProperty("check").ValueKind);
        Assert.NotEqual(model.GetProperty("id").GetString(), filled.GetProperty("id").GetString());
        var ceiling = Assert.Single(filled.GetProperty("groups").EnumerateArray(), g => g.GetProperty("name").GetString() == "Filled ceiling");
        Assert.Equal(30, ceiling.GetProperty("areaM2").GetDouble(), 6);

        // a fault that is not there is refused
        var wrong = await _http.PostAsJsonAsync("/api/model/fill", new { modelId = model.GetProperty("id").GetString(), holes = new[] { 3 } });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [Fact]
    public async Task ModelProblems_AreAnsweredWith400AndAMessage()
    {
        var model = await Json(await Import("Box.dxf", BoxDxf(6, 5, 3)));
        var id = model.GetProperty("id").GetString();
        var source = model.GetProperty("suggestedSource");
        var receiver = model.GetProperty("suggestedReceiver");

        // a group with no material, and a model that was never loaded
        var missing = await _http.PostAsJsonAsync("/api/simulate", Simulation(new { modelId = id, surfaces = new { FLOOR = new { materialId = 0, scattering = 0.1 } } }, true, source, receiver));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("no material", (await Json(missing)).GetProperty("error").GetString());

        var unknown = await _http.PostAsJsonAsync("/api/simulate", Simulation(new { modelId = "nope", surfaces = new { } }, true, source, receiver));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // a file that is not a model, and one that is not a drawing at all
        Assert.Equal(HttpStatusCode.BadRequest, (await Import("notes.txt", "hello")).StatusCode);
        var garbage = await Import("bad.dxf", "this is not a drawing");
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        Assert.Contains("bad.dxf", (await Json(garbage)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task APlanMap_HasAValueAtEveryGridPoint_InsideTheRoom()
    {
        var response = await _http.PostAsJsonAsync("/api/map", new { room = BoxRoom(8, 6, 3), source = P(2, 3, 1.5), metric = "C80", heightM = 1.2, spacingM = 2.0, rayCount = 5000 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var map = await Json(response);
        Assert.Equal("C80", map.GetProperty("metric").GetString());
        Assert.Equal("dB", map.GetProperty("unit").GetString());
        var points = map.GetProperty("points");
        Assert.Equal(12, points.GetArrayLength()); // 4 x 3 cells of 2 m
        Assert.All(points.EnumerateArray(), p =>
        {
            Assert.InRange(p.GetProperty("x").GetDouble(), 0, 8);
            Assert.InRange(p.GetProperty("y").GetDouble(), 0, 6);
            Assert.Equal(1.2, p.GetProperty("z").GetDouble(), 6);
        });
        // every octave band 63 Hz to 8 kHz is worked out at once, so the page can switch band without another run
        var c80 = map.GetProperty("parameters").GetProperty("C80");
        Assert.Equal(12, c80.GetProperty("values").GetArrayLength());
        Assert.All(c80.GetProperty("values").EnumerateArray(), values =>
        {
            Assert.Equal(8, values.GetArrayLength());
            Assert.All(values.EnumerateArray(), v => Assert.Equal(JsonValueKind.Number, v.ValueKind));
        });
    }

    [Fact]
    public async Task ASurfaceWithItsOwnAbsorption_IsUsedInPlaceOfAMaterialFromTheList_AndBadValuesAreRefused()
    {
        async Task<double> T30(object walls)
        {
            var room = new { lengthM = 8, widthM = 6, heightM = 3, floor = new { materialId = 1, scattering = 0.1 }, walls, ceiling = new { materialId = 0, scattering = 0.1 } };
            var response = await _http.PostAsJsonAsync("/api/simulate", Simulation(room, false, P(2, 3, 1.5), P(6, 3, 1.2), 20_000));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await Json(response)).GetProperty("metrics").GetProperty("t30S")[4].GetDouble();
        }

        var reflective = await T30(new { materialId = 0, scattering = 0.2, customAlpha = new[] { 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02 } });
        var absorbent = await T30(new { materialId = 0, scattering = 0.2, customAlpha = new[] { 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9, 0.9 } });
        Assert.True(absorbent < reflective * 0.6, $"walls of alpha 0.9 should decay much faster than walls of 0.02 ({absorbent:0.00} s against {reflective:0.00} s)");

        foreach (var bad in new object[] { new[] { 0.5, 0.5 }, new[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 1.5 }, new[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, -0.1 } })
        {
            var room = new { lengthM = 8, widthM = 6, heightM = 3, floor = new { materialId = 1, scattering = 0.1 }, walls = new { materialId = 0, scattering = 0.2, customAlpha = bad }, ceiling = new { materialId = 0, scattering = 0.1 } };
            Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/simulate", Simulation(room, false, P(2, 3, 1.5), P(6, 3, 1.2)))).StatusCode);
        }
    }

    [Fact]
    public async Task AMapOfAnImportedModel_Works_AndBadMapRequestsAreRefused()
    {
        var model = await Json(await Import("Box.dxf", BoxDxf(8, 6, 3)));
        // a dictionary, because the group names are layer names and must keep their case
        var surfaces = new Dictionary<string, object> { ["FLOOR"] = new { materialId = 1, scattering = 0.2 }, ["WALLS"] = new { materialId = 0, scattering = 0.2 }, ["CEILING"] = new { materialId = 0, scattering = 0.2 } };
        var spec = new { modelId = model.GetProperty("id").GetString(), surfaces };
        var ok = await _http.PostAsJsonAsync("/api/map", new { model = spec, source = P(2, 3, 1.5), metric = "T30", spacingM = 2.0, rayCount = 5000 });
        Assert.True(ok.StatusCode == HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());

        foreach (var bad in new object[]
        {
            new { room = BoxRoom(), source = P(2, 3, 1.5), metric = "nonsense" },
            new { room = BoxRoom(), source = P(2, 3, 1.5), spacingM = 0.01 },
            new { room = BoxRoom(), source = P(2, 3, 1.5), clearanceM = 0.1 },
            new { room = BoxRoom(60, 60, 4), source = P(2, 3, 1.5), spacingM = 0.25 }, // far too many points
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/map", bad)).StatusCode);
    }

    [Fact]
    public async Task ANarrowSource_AimedAwayFromTheReceiver_IsQuieterThanOneAimedAtIt()
    {
        async Task<double> Strength(object? directivity)
        {
            var body = new { room = BoxRoom(10, 6, 3), source = P(2, 3, 1.5), receiver = P(8, 3, 1.5), rayCount = 20000, directivity };
            var result = await Json(await _http.PostAsJsonAsync("/api/simulate", body));
            // clarity, not strength: the direct sound is what a beam changes at a receiver, and the reverberant level hardly moves
            return result.GetProperty("metrics").GetProperty("c80Db")[4].GetDouble();
        }

        var at = await Strength(new { horizontalDeg = 40, verticalDeg = 40, aimAzimuthDeg = 0, aimElevationDeg = 0 });
        var away = await Strength(new { horizontalDeg = 40, verticalDeg = 40, aimAzimuthDeg = 180, aimElevationDeg = 0 });
        Assert.True(at > away + 3, $"C80 aimed at the receiver {at:F1} dB, aimed away {away:F1} dB");

        var bad = await _http.PostAsJsonAsync("/api/simulate", new { room = BoxRoom(), source = P(2, 3, 1.5), receiver = P(6, 3, 1.2), rayCount = 5000, directivity = new { horizontalDeg = 0, verticalDeg = 40 } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    private async Task<JsonElement> Poll(string id, params string[] until)
    {
        for (var i = 0; i < 600; i++)
        {
            var job = await Json(await _http.GetAsync($"/api/jobs/{id}"));
            if (until.Contains(job.GetProperty("status").GetString())) return job;
            await Task.Delay(100);
        }
        throw new TimeoutException("the job did not reach " + string.Join("/", until));
    }

    [Fact]
    public async Task ASimulationJob_ReportsProgress_AndEndsWithTheResult()
    {
        var started = await Json(await _http.PostAsJsonAsync("/api/jobs/simulate", Simulation(BoxRoom(), false, P(2, 3, 1.5), P(6, 3, 1.2), 20000)));
        var id = started.GetProperty("id").GetString()!;
        Assert.Contains(started.GetProperty("status").GetString(), new[] { "running", "done" });

        var done = await Poll(id, "done", "failed");
        Assert.Equal("done", done.GetProperty("status").GetString());
        Assert.Equal(1.0, done.GetProperty("progress").GetDouble(), 6);
        Assert.Equal(8, done.GetProperty("result").GetProperty("metrics").GetProperty("t30S").GetArrayLength());
    }

    [Fact]
    public async Task AMapJob_ReturnsTheMap()
    {
        var started = await Json(await _http.PostAsJsonAsync("/api/jobs/map", new { room = BoxRoom(8, 6, 3), source = P(2, 3, 1.5), metric = "T30", spacingM = 2.0, rayCount = 5000 }));
        var done = await Poll(started.GetProperty("id").GetString()!, "done", "failed");
        Assert.Equal("done", done.GetProperty("status").GetString());
        Assert.Equal(12, done.GetProperty("result").GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task ACancelledJob_StopsLongBeforeItWouldHaveFinished()
    {
        // 500 000 rays takes tens of seconds; cancelling must end it in a couple
        var started = await Json(await _http.PostAsJsonAsync("/api/jobs/simulate", Simulation(BoxRoom(20, 15, 6), false, P(2, 3, 1.5), P(15, 9, 1.2), 500_000)));
        var id = started.GetProperty("id").GetString()!;
        await Task.Delay(500);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/jobs/{id}")).StatusCode);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var job = await Poll(id, "cancelled", "done", "failed");
        Assert.Equal("cancelled", job.GetProperty("status").GetString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed.TotalSeconds:F1} s to stop");
        Assert.Equal(JsonValueKind.Null, job.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task AJobWithABadRequest_FailsWithTheMessage_AndUnknownJobsAre404()
    {
        var started = await Json(await _http.PostAsJsonAsync("/api/jobs/simulate", Simulation(BoxRoom(), false, P(50, 3, 1.5), P(6, 3, 1.2))));
        var failed = await Poll(started.GetProperty("id").GetString()!, "failed", "done");
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Contains("inside the room", failed.GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/jobs/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.DeleteAsync("/api/jobs/nope")).StatusCode);
    }

    private static float[] Floats(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var result = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    [Fact]
    public async Task AMeasuredHrtf_CanBeUploadedAsSofa_AndTheRoomHeardThroughIt()
    {
        // a made-up head measured every 30 degrees round the horizontal: each ear 15 dB louder from its own side than from the other
        var m = 12;
        var ir = new double[m, 2, 64];
        var positions = new double[m, 3];
        for (var i = 0; i < m; i++)
        {
            var side = Math.Sin(i * 30 * Math.PI / 180);
            ir[i, 0, 5] = 1 + 0.7 * side; ir[i, 1, 5] = 1 - 0.7 * side;
            positions[i, 0] = i * 30; positions[i, 2] = 1.2;
        }
        var file = new PureHDF.H5File
        {
            ["Data.IR"] = ir, ["Data.SamplingRate"] = new double[] { 44_100 },
            ["SourcePosition"] = new PureHDF.H5Dataset(positions) { Attributes = new() { ["Type"] = "spherical" } },
        };
        using var sofa = new MemoryStream();
        file.Write(sofa);
        var uploaded = await Json(await _http.PostAsync("/api/hrtf/import?name=head.sofa", new ByteArrayContent(sofa.ToArray())));
        Assert.Equal(12, uploaded.GetProperty("measurements").GetInt32());

        // facing +x, the source to the left: heard through the made-up head, whose ears differ by 15 dB in every band, the left ear is louder by more than
        // through the spherical head, whose shadow is slight at low frequencies
        async Task<double> Ild(string? hrtfId)
        {
            var started = await Json(await _http.PostAsJsonAsync("/api/jobs/auralise", new
            {
                room = BoxRoom(8, 6, 3), source = P(4, 5, 1.2), listener = P(4, 2.5, 1.2), facingAzimuthDeg = 0, rayCount = 20_000, sampleRateHz = 22_050, hrtfId,
            }));
            var done = await Poll(started.GetProperty("id").GetString()!, "done", "failed");
            Assert.Equal("done", done.GetProperty("status").GetString());
            return done.GetProperty("result").GetProperty("interauralLevelDifferenceDb").GetDouble();
        }
        var measured = await Ild(uploaded.GetProperty("id").GetString());
        var sphere = await Ild(null);
        Assert.True(measured > 0 && measured > sphere + 0.5, $"through the measured head {measured} dB, the sphere {sphere} dB");

        var notSofa = await _http.PostAsync("/api/hrtf/import?name=x.sofa", new ByteArrayContent(Encoding.ASCII.GetBytes("not an HDF5 file")));
        Assert.Equal(HttpStatusCode.BadRequest, notSofa.StatusCode);
        Assert.Contains("HRTF", await notSofa.Content.ReadAsStringAsync());
        var gone = await Json(await _http.PostAsJsonAsync("/api/jobs/auralise", new { room = BoxRoom(), source = P(2, 3, 1.5), listener = P(6, 3, 1.2), rayCount = 20_000, hrtfId = "nothing" }));
        Assert.Contains("upload it again", (await Poll(gone.GetProperty("id").GetString()!, "failed", "done")).GetProperty("error").GetString());
    }

    [Fact]
    public async Task EachSourcesResponse_CanBeHadOnItsOwn_AtItsLevelAgainstTheOthers()
    {
        // the listener faces +x; source 1 on the left (+y), source 2 as far away on the right and 10 dB quieter
        var started = await Json(await _http.PostAsJsonAsync("/api/jobs/auralise", new
        {
            room = BoxRoom(8, 6, 3), source = P(4, 5, 1.2), listener = P(4, 3, 1.2), facingAzimuthDeg = 0, rayCount = 20_000, sampleRateHz = 22_050, separate = true,
            extraSources = new[] { new { position = P(4, 1, 1.2), gainDb = -10.0 } },
        }));
        var done = await Poll(started.GetProperty("id").GetString()!, "done", "failed");
        Assert.Equal("done", done.GetProperty("status").GetString());
        var sources = done.GetProperty("result").GetProperty("sources").EnumerateArray()
            .Select(s => (Left: Floats(s.GetProperty("leftF32").GetString()!), Right: Floats(s.GetProperty("rightF32").GetString()!))).ToArray();
        Assert.Equal(2, sources.Length);
        double E(float[] c) => c.Sum(v => (double)v * v);
        Assert.InRange(10 * Math.Log10((E(sources[0].Left) + E(sources[0].Right)) / (E(sources[1].Left) + E(sources[1].Right))), 7, 13);
        Assert.True(E(sources[0].Left) > E(sources[0].Right), "source 1 is on the left");
        Assert.True(E(sources[1].Right) > E(sources[1].Left), "source 2 is on the right");
        Assert.InRange(sources.Max(s => Math.Max(s.Left.Max(Math.Abs), s.Right.Max(Math.Abs))), 0.45f, 0.55f);
    }

    [Fact]
    public async Task AnAmbisonicResponse_HasFourAmbiXChannels_WithTheSourcesDirectionInThem()
    {
        // the source to the listener's +y side, level with it: Y carries the direct sound, Z little
        var started = await Json(await _http.PostAsJsonAsync("/api/jobs/ambisonic",
            new { room = BoxRoom(8, 6, 3), source = P(4, 5, 1.2), listener = P(4, 2, 1.2), rayCount = 20_000, sampleRateHz = 22_050 }));
        var done = await Poll(started.GetProperty("id").GetString()!, "done", "failed");
        Assert.Equal("done", done.GetProperty("status").GetString());
        var r = done.GetProperty("result");
        var channels = r.GetProperty("channelsF32").EnumerateArray().Select(c => Floats(c.GetString()!)).ToArray();
        Assert.Equal(4, channels.Length);
        Assert.All(channels, c => Assert.Equal(r.GetProperty("samples").GetInt32(), c.Length));
        Assert.InRange(channels.Max(c => c.Max(Math.Abs)), 0.45f, 0.55f);
        // the direct sound alone: it arrives from 3 m away at about 9 ms, before the floor's reflection (3.8 m, 11 ms)
        double Early(float[] c) => c.Skip(22_050 * 8 / 1000).Take(22_050 * 25 / 10_000).Sum(v => (double)v * v);
        Assert.True(Early(channels[1]) > 0.5 * Early(channels[0]), "Y, from +y, has most of W's early energy");
        Assert.True(Early(channels[2]) < 0.2 * Early(channels[1]), "Z little");
    }

    [Fact]
    public async Task Auralisation_ReturnsTwoEarsImpulseResponses_ThatDifferWhenTheSourceIsToOneSide()
    {
        var started = await Json(await _http.PostAsJsonAsync("/api/jobs/auralise",
            new { room = BoxRoom(8, 6, 3), source = P(2, 5, 1.5), listener = P(6, 3, 1.2), facingAzimuthDeg = 0, rayCount = 20_000, sampleRateHz = 22_050 }));
        var done = await Poll(started.GetProperty("id").GetString()!, "done", "failed");
        Assert.Equal("done", done.GetProperty("status").GetString());

        var r = done.GetProperty("result");
        var left = Floats(r.GetProperty("leftF32").GetString()!);
        var right = Floats(r.GetProperty("rightF32").GetString()!);
        Assert.Equal(r.GetProperty("samples").GetInt32(), left.Length);
        Assert.Equal(left.Length, right.Length);
        Assert.True(left.Length > 22_050 / 4, "at least a quarter of a second of response");
        Assert.InRange(Math.Max(left.Max(Math.Abs), right.Max(Math.Abs)), 0.45f, 0.55f); // scaled so the larger peak is 0.5
        Assert.NotEqual(left, right);
        Assert.Equal(8, r.GetProperty("iaccEarly").GetArrayLength());
        // IACC from where the sound comes from: two ears 17 cm apart hear nearly the same sound at 63 Hz, early and late; BQI is 1 - IACC E3
        Assert.True(r.GetProperty("iaccEarly")[0].GetDouble() > 0.9);
        Assert.True(r.GetProperty("iaccLate")[0].GetDouble() > 0.9);
        var e3 = Enumerable.Range(3, 3).Average(b => r.GetProperty("iaccEarly")[b].GetDouble());
        Assert.Equal(e3, r.GetProperty("iaccE3").GetDouble(), 3);
        Assert.Equal(1 - e3, r.GetProperty("binauralQualityIndex").GetDouble(), 3);
    }

    [Fact]
    public async Task AbsorptionByAngle_ChangesTheRun()
    {
        var plain = Simulation(BoxRoom(), false, P(2, 3, 1.5), P(6, 3, 1.2), 20_000);
        var body = JsonSerializer.SerializeToNode(plain)!.AsObject();
        body["angleDependentAbsorption"] = true;
        var before = await Json(await _http.PostAsJsonAsync("/api/simulate", plain));
        var after = await Json(await _http.PostAsJsonAsync("/api/simulate", body));
        Assert.NotEqual(before.GetProperty("metrics").GetProperty("c80Db")[4].GetDouble(), after.GetProperty("metrics").GetProperty("c80Db")[4].GetDouble());
    }

    [Fact]
    public async Task ThirdOctaves_AreWorkedOutAtTheReceiver_InThe23BandsFrom50HzTo8kHz()
    {
        var request = Simulation(BoxRoom(), false, P(2, 3, 1.5), P(6, 3, 1.2), 20_000);
        var started = await Json(await _http.PostAsJsonAsync("/api/jobs/thirdoctave", request));
        var done = await Poll(started.GetProperty("id").GetString()!, "done", "failed");
        Assert.Equal("done", done.GetProperty("status").GetString());
        var bands = done.GetProperty("result").GetProperty("bands").EnumerateArray().ToList();
        Assert.Equal(new[] { 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000 },
            bands.Select(b => b.GetProperty("hz").GetInt32()));

        // the 1 kHz third has the octave run's absorption at 1 kHz, so about its T30
        var octave = await Json(await _http.PostAsJsonAsync("/api/simulate", request));
        var t30 = octave.GetProperty("metrics").GetProperty("t30S")[4].GetDouble();
        Assert.InRange(bands[13].GetProperty("t30S").GetDouble(), 0.85 * t30, 1.15 * t30);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(5_000_000)]
    public async Task Auralisation_RefusesATooSmallOrTooLargeRayCount(int rays)
    {
        var started = await Json(await _http.PostAsJsonAsync("/api/jobs/auralise", new { room = BoxRoom(), source = P(2, 3, 1.5), listener = P(6, 3, 1.2), rayCount = rays }));
        var failed = await Poll(started.GetProperty("id").GetString()!, "failed", "done");
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Contains("ray count", failed.GetProperty("error").GetString());
    }

    // --- the built page (only when `npm run build` has been run: web/dist beside the repository's sources) ---

    private static string? BuiltPage()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var dist = Path.Combine(dir.FullName, "web", "dist");
            if (File.Exists(Path.Combine(dist, "index.html"))) return dist;
        }
        return null;
    }

    [Fact]
    public async Task TheBuiltPage_IsServedBesideTheApi_WithItsScript_AndUnknownApiPathsStay404()
    {
        var dist = BuiltPage();
        if (dist is null) return; // the web app has not been built on this machine

        var home = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("text/html", home.Content.Headers.ContentType?.MediaType);
        var html = await home.Content.ReadAsStringAsync();
        Assert.Contains("id=\"root\"", html);

        var script = System.Text.RegularExpressions.Regex.Match(html, @"src=""(/assets/[^""]+\.js)""");
        Assert.True(script.Success, "the page names its script");
        var js = await _http.GetAsync(script.Groups[1].Value);
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/does-not-exist")).StatusCode);
    }

    /// <summary>
    /// A measured-looking impulse response as a 16-bit mono WAV at 48 kHz: 50 ms of silence, a direct sound, then noise under an envelope that falls 60 dB
    /// in <paramref name="t60S"/>, 2 s in all, with a little steady noise under it.
    /// </summary>
    private static byte[] DecayWav(double t60S)
    {
        const int rate = 48000;
        var random = new Random(3);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        var x = new double[2 * rate];
        for (var i = 0; i < x.Length; i++)
        {
            var t = i / (double)rate - 0.05;
            x[i] = (t >= 0 ? 0.05 * Gauss() * Math.Pow(10, -3 * t / t60S) : 0) + 1e-5 * Gauss();
        }
        x[(int)(0.05 * rate)] = 0.9;
        var data = x.SelectMany(v => BitConverter.GetBytes((short)Math.Round(Math.Clamp(v, -1, 1) * 32767))).ToArray();
        using var s = new MemoryStream();
        using var w = new BinaryWriter(s);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data.Length); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data.Length); w.Write(data);
        w.Flush();
        return s.ToArray();
    }

    [Fact]
    public async Task AMeasuredImpulseResponse_IsAnalysedInOctaveBands_FromItsDirectSound()
    {
        var response = await _http.PostAsync("/api/measured?name=Hall.wav", new ByteArrayContent(DecayWav(0.8)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var m = await Json(response);
        Assert.Equal("Hall.wav", m.GetProperty("name").GetString());
        Assert.Equal(48000, m.GetProperty("sampleRateHz").GetInt32());
        Assert.Equal(1, m.GetProperty("channels").GetInt32());
        Assert.Equal(0.05, m.GetProperty("directArrivalS").GetDouble(), 3);
        Assert.Equal(1.95, m.GetProperty("durationS").GetDouble(), 2);
        foreach (var b in new[] { 3, 4, 5 })
        {
            Assert.InRange(m.GetProperty("t30S")[b].GetDouble(), 0.72, 0.88);
            Assert.InRange(m.GetProperty("edtS")[b].GetDouble(), 0.68, 0.92);
            Assert.True(m.GetProperty("dynamicRangeDb")[b].GetDouble() > 45);
            Assert.Equal(JsonValueKind.Number, m.GetProperty("c80Db")[b].ValueKind);
        }
        Assert.Equal(8, m.GetProperty("d50").GetArrayLength());

        // a file that is not a WAV, and a WAV with no sound in it, are refused with the reason
        var bad = await _http.PostAsync("/api/measured?name=Notes.txt", new ByteArrayContent(Encoding.ASCII.GetBytes("not a recording at all")));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("WAV", (await Json(bad)).GetProperty("error").GetString());
        var silent = DecayWav(0.8);
        Array.Clear(silent, 44, silent.Length - 44);
        var quiet = await _http.PostAsync("/api/measured?name=Silent.wav", new ByteArrayContent(silent));
        Assert.Equal(HttpStatusCode.BadRequest, quiet.StatusCode);
        Assert.Contains("no sound", (await Json(quiet)).GetProperty("error").GetString());
    }
}
