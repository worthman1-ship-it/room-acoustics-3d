// BRAS CR2 (seminar room, RWTH Aachen): the app's engine against the measured T20, EDT, C80 and D50, averaged over 2 sources x 5 receivers.
using System.Globalization;
using RoomAcoustics.Core;

// the folder the BRAS files were unpacked into: work (CR2.obj from export.rb), materials and additional (see README.md)
var root = Environment.GetEnvironmentVariable("BRAS_ROOT") ?? throw new InvalidOperationException("Set BRAS_ROOT to the folder holding work, materials and additional.");
var set = args.Length > 0 ? args[0] : "fitted";          // fitted or initial surface data
var rays = args.Length > 1 ? int.Parse(args[1]) : 60_000;
var order = args.Length > 2 ? int.Parse(args[2]) : 2;
var scatter = args.Length > 3 ? args[3] : "bands";        // bands, or a fixed value such as 0.1
var maxTime = args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 2.0; // the app's own length of response

double[] Csv(string line) => line.Split(',').Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
// an octave band's value: the mean of its three third octaves
double[] ToOctaves(double[] freqs, double[] values) => OctaveSpectrum.Frequencies.Select(fc =>
{
    var near = freqs.Select((f, i) => (f, i)).Where(p => p.f > fc / 1.5 && p.f < fc * 1.5).Select(p => values[p.i]).ToArray();
    return near.Average();
}).ToArray();

var tolerance = args.Length > 5 ? double.Parse(args[5], CultureInfo.InvariantCulture) : 2e-3;
var read = ObjMeshReader.ReadFile(Path.Combine(root, "work", "CR2.obj"));
// the three radiators under the windows are closed boxes standing in the room: they go in as free-standing screens of the concrete finish
bool Radiator(Vec3 p) => p.Y > 4.08 && p.Y < 4.25 && p.Z > 0.14 && p.Z < 0.60;
var regrouped = new SurfaceMeshBuilder();
foreach (var t in read.Triangles)
{
    var v = new[] { read.Vertices[t.A], read.Vertices[t.B], read.Vertices[t.C] };
    regrouped.AddTriangle(v[0], v[1], v[2], v.All(Radiator) ? "radiator" : t.Group);
}
var mesh = regrouped.Build();
var finishes = new Dictionary<string, SurfaceFinish>();
foreach (var group in mesh.Groups)
{
    var lines = File.ReadAllLines(Path.Combine(root, "materials", $"{set}_{(group == "radiator" ? "mat_CR2_concrete" : group)}.csv"));
    var f = Csv(lines[0]);
    var alpha = ToOctaves(f, Csv(lines[1]));
    var s = scatter == "bands" ? ToOctaves(f, Csv(lines[2])) : Enumerable.Repeat(double.Parse(scatter, CultureInfo.InvariantCulture), 8).ToArray();
    finishes[group] = new SurfaceFinish(new AbsorptionMaterial(group, "BRAS", alpha.Select(a => (double?)a).ToArray()), new OctaveSpectrum(s));
    Console.WriteLine($"{group,-18} {mesh.GroupAreasM2()[group],6:0.00} m²  α {string.Join(" ", alpha.Select(a => a.ToString("0.000")))}  s {string.Join(" ", s.Select(a => a.ToString("0.00")))}");
}
var check = MeshCheck.Check(mesh, g => g == "radiator");
foreach (var fault in check.Faults) Console.WriteLine($"{fault.Kind} {fault.LengthM:0.000} m [{string.Join(",", fault.Groups)}] {fault.Hint} {string.Join(" ", fault.Edges.Select(e => $"({e.A.X:0.000},{e.A.Y:0.000},{e.A.Z:0.000})-({e.B.X:0.000},{e.B.Y:0.000},{e.B.Z:0.000})"))}");
var room = mesh.ToRoom(g => finishes[g], g => g == "radiator");
Console.WriteLine($"volume {room.VolumeM3:0.0} m³, area {room.TotalAreaM2:0.00} m²");

var sources = new[] { new Vec3(0.931, -2.547, 0.723), new Vec3(0.119, 2.880, 0.723) };
var receivers = new[] { new Vec3(-0.993, 1.426, 1.230), new Vec3(0.439, -0.147, 1.230), new Vec3(1.361, -0.603, 1.230), new Vec3(-1.110, -0.256, 1.230), new Vec3(-0.998, -1.409, 1.230) };
var options = new RayTraceOptions(RayCount: rays, TemperatureC: 19.5, RelativeHumidityPercent: 41.7, ImageSourceOrder: order, MaxTimeS: maxTime);
var all = new List<RoomAcousticMetrics>();
var clock = System.Diagnostics.Stopwatch.StartNew();
foreach (var s in sources)
    foreach (var ir in ExtendedSourceTracer.TraceReceivers(room, new ExtendedSource(new[] { new SourcePoint(s) }), receivers, options))
        all.Add(RoomAcousticsCalculator.Calculate(ir));
Console.WriteLine($"{all.Count} responses in {clock.Elapsed.TotalSeconds:0.0} s ({rays} rays, image sources to order {order}, scattering {scatter}, {set} data)");

double Mean(Func<RoomAcousticMetrics, OctaveSpectrum> get, int band) => all.Select(m => get(m)[band]).Where(double.IsFinite).Average();
using var csv = new StreamWriter(Path.Combine(root, "work", $"sim_{set}_{scatter}_{rays}_{order}_{maxTime}.csv"));
csv.WriteLine("band,T20,EDT,C80,D50");
for (var b = 0; b < 8; b++)
    csv.WriteLine(string.Join(",", OctaveSpectrum.Frequencies[b], Mean(m => m.T20S, b).ToString("0.000", CultureInfo.InvariantCulture), Mean(m => m.EarlyDecayTimeS, b).ToString("0.000", CultureInfo.InvariantCulture),
        Mean(m => m.C80Db, b).ToString("0.00", CultureInfo.InvariantCulture), (100 * Mean(m => m.D50, b)).ToString("0.0", CultureInfo.InvariantCulture)));
