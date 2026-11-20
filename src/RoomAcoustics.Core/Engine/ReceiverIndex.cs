namespace RoomAcoustics.Core;

/// <summary>
/// A uniform grid over many receiver spheres, so that a ray segment is tested only against the receivers in the cells it passes through
/// instead of against every receiver. A plan map or section has hundreds or thousands of receivers and each ray segment used to be tested against
/// all of them; this finds the same receivers (every sphere the segment touches lies in a cell the segment crosses) and finds each once.
/// </summary>
internal sealed class ReceiverIndex
{
    /// <summary>Below this many receivers a plain loop is as fast as the grid.</summary>
    public const int WorthwhileCount = 24;

    private const int MaxCellsPerAxis = 64;

    private readonly double _minX, _minY, _minZ, _maxX, _maxY, _maxZ, _cell;
    private readonly int _nx, _ny, _nz;
    private readonly int[][] _cells;
    private readonly int _count;

    public ReceiverIndex(IReadOnlyList<Vec3> receivers, double radius)
    {
        _count = receivers.Count;
        _minX = receivers.Min(r => r.X) - radius; _maxX = receivers.Max(r => r.X) + radius;
        _minY = receivers.Min(r => r.Y) - radius; _maxY = receivers.Max(r => r.Y) + radius;
        _minZ = receivers.Min(r => r.Z) - radius; _maxZ = receivers.Max(r => r.Z) + radius;
        var longest = Math.Max(_maxX - _minX, Math.Max(_maxY - _minY, _maxZ - _minZ));
        // a cell is at least a sphere across, so a sphere touches at most eight of them, and there are at most 64 cells along the longest side
        _cell = Math.Max(2 * radius, longest / MaxCellsPerAxis);
        _nx = Math.Max(1, (int)Math.Ceiling((_maxX - _minX) / _cell));
        _ny = Math.Max(1, (int)Math.Ceiling((_maxY - _minY) / _cell));
        _nz = Math.Max(1, (int)Math.Ceiling((_maxZ - _minZ) / _cell));

        var lists = new List<int>?[_nx * _ny * _nz];
        for (var i = 0; i < receivers.Count; i++)
        {
            var p = receivers[i];
            int x0 = Cell(p.X - radius, _minX, _nx), x1 = Cell(p.X + radius, _minX, _nx);
            int y0 = Cell(p.Y - radius, _minY, _ny), y1 = Cell(p.Y + radius, _minY, _ny);
            int z0 = Cell(p.Z - radius, _minZ, _nz), z1 = Cell(p.Z + radius, _minZ, _nz);
            for (var z = z0; z <= z1; z++)
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                        (lists[(z * _ny + y) * _nx + x] ??= new List<int>()).Add(i);
        }
        _cells = lists.Select(l => l?.ToArray() ?? Array.Empty<int>()).ToArray();
    }

    private int Cell(double v, double min, int n) => Math.Clamp((int)Math.Floor((v - min) / _cell), 0, n - 1);

    /// <summary>The working space of one thread: which receivers were already listed for this query, and the list itself.</summary>
    public sealed class Scratch
    {
        internal readonly int[] Seen;
        internal int Stamp;
        public readonly int[] Found;
        public int Count;
        public Scratch(int receivers) { Seen = new int[receivers]; Found = new int[receivers]; }
    }

    public Scratch NewScratch() => new(_count);

    /// <summary>
    /// Lists in <paramref name="scratch"/> the receivers whose cells the segment passes through (each once): every receiver whose sphere the
    /// segment touches is among them, along with some it only passes near.
    /// </summary>
    public void Query(Vec3 start, Vec3 direction, double length, Scratch scratch)
    {
        scratch.Count = 0;
        if (++scratch.Stamp == int.MaxValue) { Array.Clear(scratch.Seen); scratch.Stamp = 1; }

        // clip the segment to the grid's box
        double t0 = 0, t1 = length;
        if (!Clip(start.X, direction.X, _minX, _maxX, ref t0, ref t1)) return;
        if (!Clip(start.Y, direction.Y, _minY, _maxY, ref t0, ref t1)) return;
        if (!Clip(start.Z, direction.Z, _minZ, _maxZ, ref t0, ref t1)) return;

        var px = start.X + direction.X * t0; var py = start.Y + direction.Y * t0; var pz = start.Z + direction.Z * t0;
        int ix = Cell(px, _minX, _nx), iy = Cell(py, _minY, _ny), iz = Cell(pz, _minZ, _nz);
        Setup(direction.X, px, _minX, ix, out var stepX, out var nextX, out var deltaX);
        Setup(direction.Y, py, _minY, iy, out var stepY, out var nextY, out var deltaY);
        Setup(direction.Z, pz, _minZ, iz, out var stepZ, out var nextZ, out var deltaZ);
        // the times above are measured from the clipped start; the segment's end is that far along
        var end = t1 - t0;

        while (true)
        {
            foreach (var r in _cells[(iz * _ny + iy) * _nx + ix])
            {
                if (scratch.Seen[r] == scratch.Stamp) continue;
                scratch.Seen[r] = scratch.Stamp;
                scratch.Found[scratch.Count++] = r;
            }
            if (nextX <= nextY && nextX <= nextZ)
            {
                if (nextX > end) return;
                ix += stepX; nextX += deltaX;
                if (ix < 0 || ix >= _nx) return;
            }
            else if (nextY <= nextZ)
            {
                if (nextY > end) return;
                iy += stepY; nextY += deltaY;
                if (iy < 0 || iy >= _ny) return;
            }
            else
            {
                if (nextZ > end) return;
                iz += stepZ; nextZ += deltaZ;
                if (iz < 0 || iz >= _nz) return;
            }
        }
    }

    private static bool Clip(double start, double direction, double min, double max, ref double t0, ref double t1)
    {
        if (Math.Abs(direction) < 1e-12) return start >= min && start <= max;
        var a = (min - start) / direction;
        var b = (max - start) / direction;
        if (a > b) (a, b) = (b, a);
        if (a > t0) t0 = a;
        if (b < t1) t1 = b;
        return t0 <= t1;
    }

    private void Setup(double direction, double position, double min, int cell, out int step, out double next, out double delta)
    {
        if (Math.Abs(direction) < 1e-12) { step = 0; next = double.PositiveInfinity; delta = double.PositiveInfinity; return; }
        step = direction > 0 ? 1 : -1;
        var boundary = min + (direction > 0 ? cell + 1 : cell) * _cell;
        next = (boundary - position) / direction;
        delta = _cell / Math.Abs(direction);
    }
}
