"""Writes examples/LectureTheatre.dxf: a small lecture theatre as 3DFACE entities, one layer per kind of surface, for trying the DXF import of
Room Acoustics 3D.  python tools/make_example_dxf.py

The room is a side profile (x along the room, z up) extruded across 12 m: a raised stage at the front, an empty well, raked seating rising to the back
wall, and a ceiling that slopes up toward the back. Each edge of the profile becomes a strip across the room on its own layer, and the two side walls
are the profile itself, cut into triangles. Units are metres. Every edge is shared by exactly two faces, as the importer needs.
"""
import os

WIDTH = 12.0
# (x, z) going round the profile, counter-clockwise; the layer of the edge that starts at each point
PROFILE = [
    ((0.0, 0.9), "STAGE"),            # stage floor
    ((4.0, 0.9), "STAGE_FRONT"),      # the step down from the stage
    ((4.0, 0.0), "SEATING"),          # raked seating floor
    ((16.0, 2.4), "BACK_WALL"),
    ((16.0, 6.5), "CEILING"),
    ((9.0, 5.8), "CEILING"),
    ((0.0, 5.5), "FRONT_WALL"),
]


def area(poly):
    return 0.5 * sum(poly[i][0] * poly[(i + 1) % len(poly)][1] - poly[(i + 1) % len(poly)][0] * poly[i][1] for i in range(len(poly)))


def inside(p, a, b, c):
    def cross(o, u, v):
        return (u[0] - o[0]) * (v[1] - o[1]) - (u[1] - o[1]) * (v[0] - o[0])
    d1, d2, d3 = cross(a, b, p), cross(b, c, p), cross(c, a, p)
    return d1 >= 0 and d2 >= 0 and d3 >= 0


def triangulate(poly):
    """Ear clipping of a simple counter-clockwise polygon; returns index triples."""
    idx = list(range(len(poly)))
    out = []
    while len(idx) > 3:
        for k in range(len(idx)):
            i0, i1, i2 = idx[k - 1], idx[k], idx[(k + 1) % len(idx)]
            a, b, c = poly[i0], poly[i1], poly[i2]
            if (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]) <= 0:
                continue   # a reflex corner is not an ear
            if any(inside(poly[j], a, b, c) for j in idx if j not in (i0, i1, i2)):
                continue
            out.append((i0, i1, i2))
            idx.pop(k)
            break
        else:
            raise RuntimeError("could not triangulate")
    out.append(tuple(idx))
    return out


def face(layer, pts):
    pts = list(pts)
    if len(pts) == 3:
        pts.append(pts[2])
    lines = ["0", "3DFACE", "8", layer]
    for n, (x, y, z) in enumerate(pts):
        lines += [str(10 + n), f"{x:.6f}", str(20 + n), f"{y:.6f}", str(30 + n), f"{z:.6f}"]
    return lines


def main():
    pts = [p for p, _ in PROFILE]
    assert area(pts) > 0, "the profile must run counter-clockwise"
    entities = []
    for i, (p, layer) in enumerate(PROFILE):
        q = pts[(i + 1) % len(pts)]
        entities.append(face(layer, [(p[0], 0, p[1]), (q[0], 0, q[1]), (q[0], WIDTH, q[1]), (p[0], WIDTH, p[1])]))
    for a, b, c in triangulate(pts):
        entities.append(face("SIDE_WALLS", [(pts[a][0], 0, pts[a][1]), (pts[b][0], 0, pts[b][1]), (pts[c][0], 0, pts[c][1])]))
        entities.append(face("SIDE_WALLS", [(pts[a][0], WIDTH, pts[a][1]), (pts[c][0], WIDTH, pts[c][1]), (pts[b][0], WIDTH, pts[b][1])]))

    layers = sorted({layer for _, layer in PROFILE} | {"SIDE_WALLS"})
    lines = ["0", "SECTION", "2", "HEADER", "9", "$ACADVER", "1", "AC1009", "9", "$INSUNITS", "70", "6", "0", "ENDSEC",
             "0", "SECTION", "2", "TABLES", "0", "TABLE", "2", "LAYER", "70", str(len(layers))]
    for n, name in enumerate(layers):
        lines += ["0", "LAYER", "2", name, "70", "0", "62", str(n + 1), "6", "CONTINUOUS"]
    lines += ["0", "ENDTAB", "0", "ENDSEC", "0", "SECTION", "2", "ENTITIES"]
    for e in entities:
        lines += e
    lines += ["0", "ENDSEC", "0", "EOF"]

    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "examples")
    os.makedirs(out, exist_ok=True)
    path = os.path.join(out, "LectureTheatre.dxf")
    with open(path, "w", newline="\r\n") as f:
        f.write("\n".join(lines) + "\n")
    print(f"wrote {os.path.normpath(path)}: {len(entities)} faces, layers {', '.join(layers)}; volume {area(pts) * WIDTH:.2f} m3")


if __name__ == "__main__":
    main()
