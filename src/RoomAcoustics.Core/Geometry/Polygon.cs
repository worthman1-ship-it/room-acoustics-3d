namespace RoomAcoustics.Core;

/// <summary>Cutting a planar polygon in 3-D into triangles, for the readers and the model repairs.</summary>
internal static class Polygon
{
    /// <summary>
    /// Triangles of a planar polygon by ear clipping, so a concave outline (an L-shaped room) is covered exactly. Holes (a face with a window cut in it) are
    /// joined to the outline by a cut from each hole to a corner of the outline it can see, so the polygon becomes one outline that goes round them.
    /// </summary>
    internal static List<Vec3[]> Triangulate(IReadOnlyList<Vec3> polygon, IReadOnlyList<List<Vec3>>? holes = null)
    {
        var result = new List<Vec3[]>();
        if (polygon.Count < 3) return result;
        // the polygon's normal (Newell), and the two axes of the plane it is flattened onto
        var normal = Vec3.Zero;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            normal += new Vec3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        if (normal.Length < 1e-12) return result;
        var u = Math.Abs(normal.X) < 0.9 * normal.Length ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        u = (u - normal * (u.Dot(normal) / normal.Dot(normal))).Normalized();
        var v = normal.Normalized().Cross(u);
        (double X, double Y) Flat(Vec3 p) => (p.Dot(u), p.Dot(v));

        // the outline goes anticlockwise in the plane; each hole is joined to it, going clockwise
        var points = polygon.ToList();
        var waiting = (holes ?? Array.Empty<List<Vec3>>()).Where(h => h.Count >= 3).OrderByDescending(h => h.Max(p => Flat(p).X)).ToList();
        while (waiting.Count > 0)
        {
            var hole = waiting[0];
            waiting.RemoveAt(0);
            var ring = hole.ToList();
            double twiceArea = 0;
            for (var i = 0; i < ring.Count; i++) { var a = Flat(ring[i]); var b = Flat(ring[(i + 1) % ring.Count]); twiceArea += a.X * b.Y - b.X * a.Y; }
            if (twiceArea > 0) ring.Reverse();
            var m = ring.IndexOf(ring.MaxBy(p => Flat(p).X)!);
            var from = Flat(ring[m]);
            // the nearest corner of the outline whose cut to the hole crosses no edge of the outline or of any hole, and runs through no corner of them
            var edges = Edges(points).Concat(Edges(ring)).Concat(waiting.SelectMany(Edges)).ToList();
            var corners = points.Concat(ring).Concat(waiting.SelectMany(h => h)).Select(Flat).ToList();
            bool Clear(int i)
            {
                var to = Flat(points[i]);
                // the cut leaves the corner into the polygon, between its two edges (a corner an earlier cut has doubled is left from the right copy)
                var (before, after) = (Flat(points[(i + points.Count - 1) % points.Count]), Flat(points[(i + 1) % points.Count]));
                bool Left((double X, double Y) a, (double X, double Y) b) => (b.X - a.X) * (from.Y - a.Y) - (b.Y - a.Y) * (from.X - a.X) > 0;
                var convex = (to.X - before.X) * (after.Y - before.Y) - (to.Y - before.Y) * (after.X - before.X) > 0;
                if (convex ? !(Left(before, to) && Left(to, after)) : !(Left(before, to) || Left(to, after))) return false;
                return !edges.Any(e => Crosses(to, from, Flat(e.A), Flat(e.B))) && !corners.Any(c => c != to && c != from && OnSegment(c, to, from));
            }
            var bridge = Enumerable.Range(0, points.Count).OrderBy(i => Distance(Flat(points[i]), from)).FirstOrDefault(Clear, -1);
            if (bridge < 0) continue;
            var joined = points.Take(bridge + 1).ToList();
            for (var k = 0; k <= ring.Count; k++) joined.Add(ring[(m + k) % ring.Count]);
            joined.Add(points[bridge]);
            joined.AddRange(points.Skip(bridge + 1));
            points = joined;
        }

        var flat = points.Select(Flat).ToArray();
        double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        var left = Enumerable.Range(0, points.Count).ToList();
        while (left.Count > 3)
        {
            var ear = -1;
            for (var k = 0; k < left.Count && ear < 0; k++)
            {
                int prev = left[(k + left.Count - 1) % left.Count], here = left[k], next = left[(k + 1) % left.Count];
                if (Cross(flat[prev], flat[here], flat[next]) <= 1e-12) continue; // a reflex corner is not an ear
                // a corner standing on one of the triangle's own (as the two ends of a cut to a hole do) does not stop it being an ear
                var empty = left.Where(i => flat[i] != flat[prev] && flat[i] != flat[here] && flat[i] != flat[next]).All(i =>
                    Cross(flat[prev], flat[here], flat[i]) < 0 || Cross(flat[here], flat[next], flat[i]) < 0 || Cross(flat[next], flat[prev], flat[i]) < 0);
                if (empty) ear = k;
            }
            if (ear < 0) ear = 0; // a degenerate outline: clip anyway so it ends
            result.Add(new[] { points[left[(ear + left.Count - 1) % left.Count]], points[left[ear]], points[left[(ear + 1) % left.Count]] });
            left.RemoveAt(ear);
        }
        result.Add(new[] { points[left[0]], points[left[1]], points[left[2]] });
        return result;
    }

    private static IEnumerable<(Vec3 A, Vec3 B)> Edges(List<Vec3> ring) => ring.Select((p, i) => (p, ring[(i + 1) % ring.Count]));

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Whether point c lies on segment ab, short of its ends.</summary>
    private static bool OnSegment((double X, double Y) c, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length2 = dx * dx + dy * dy;
        if (length2 < 1e-24) return false;
        var at = ((c.X - a.X) * dx + (c.Y - a.Y) * dy) / length2;
        return at > 1e-9 && at < 1 - 1e-9 && Math.Abs((c.X - a.X) * dy - (c.Y - a.Y) * dx) / Math.Sqrt(length2) < 1e-9;
    }

    /// <summary>Whether segments pq and ab cross at a point inside both (touching at an end does not count).</summary>
    private static bool Crosses((double X, double Y) p, (double X, double Y) q, (double X, double Y) a, (double X, double Y) b)
    {
        double Side((double X, double Y) o, (double X, double Y) s, (double X, double Y) t) => (s.X - o.X) * (t.Y - o.Y) - (s.Y - o.Y) * (t.X - o.X);
        double d1 = Side(p, q, a), d2 = Side(p, q, b), d3 = Side(a, b, p), d4 = Side(a, b, q);
        const double e = 1e-12;
        return ((d1 > e && d2 < -e) || (d1 < -e && d2 > e)) && ((d3 > e && d4 < -e) || (d3 < -e && d4 > e));
    }
}
