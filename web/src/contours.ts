import type { MapPoint, Point } from './types';

/** The most contour lines a map is drawn with; a finer step draws none. */
export const MAX_CONTOURS = 50;

/** The contour levels between two values: every whole multiple of the step from lo to hi. None for a step that is not positive or would give more than MAX_CONTOURS. */
export const contourLevels = (lo: number, hi: number, step: number): number[] => {
  if (!(step > 0) || !(hi >= lo)) return [];
  const first = Math.ceil(lo / step), last = Math.floor(hi / step);
  if (last - first + 1 > MAX_CONTOURS) return [];
  const levels: number[] = [];
  for (let k = first; k <= last; k++) levels.push(Number((k * step).toFixed(10)));
  return levels;
};

type Axis = 'x' | 'y' | 'z';
/** A piece of a contour line: its two ends, and its level. */
export type Segment = [Point, Point, number];

/**
 * The contour lines of one layer of a map (a plan, a section, or one height of a height matrix), by marching squares over its regular grid:
 * each square of four neighbouring points that all have a value gives a line wherever a level falls between its corners, placed by linear interpolation.
 * `axes` are the two directions the grid runs in; the line ends carry all three coordinates, so a map above a sloping surface keeps its slope.
 * A square with two opposite corners above a level and two below is settled by the mean of its corners.
 */
export function contourSegments(points: MapPoint[], spacingM: number, axes: [Axis, Axis], levels: number[]): Segment[] {
  const valued = points.filter((p): p is MapPoint & { value: number } => p.value !== null);
  if (valued.length === 0 || levels.length === 0) return [];
  const [a, b] = axes;
  const a0 = Math.min(...valued.map((p) => p[a])), b0 = Math.min(...valued.map((p) => p[b]));
  const index = (p: MapPoint) => [Math.round((p[a] - a0) / spacingM), Math.round((p[b] - b0) / spacingM)];
  const grid = new Map<string, MapPoint & { value: number }>();
  for (const p of valued) grid.set(index(p).join(','), p);

  const at = (p: MapPoint & { value: number }, q: MapPoint & { value: number }, level: number): Point => {
    const t = (level - p.value) / (q.value - p.value);
    return { x: p.x + t * (q.x - p.x), y: p.y + t * (q.y - p.y), z: p.z + t * (q.z - p.z) };
  };
  const segments: Segment[] = [];
  for (const p of valued) {
    const [i, j] = index(p);
    // the corners anticlockwise from this one, and the edges between them: edge k runs from corner k to corner k + 1
    const corners = [grid.get(`${i},${j}`), grid.get(`${i + 1},${j}`), grid.get(`${i + 1},${j + 1}`), grid.get(`${i},${j + 1}`)];
    if (corners.some((c) => c === undefined)) continue;
    const c = corners as (MapPoint & { value: number })[];
    for (const level of levels) {
      const above = c.map((corner) => corner.value >= level);
      const crossings: (Point | null)[] = [0, 1, 2, 3].map((k) => (above[k] !== above[(k + 1) % 4] ? at(c[k], c[(k + 1) % 4], level) : null));
      const cut = crossings.filter((x): x is Point => x !== null);
      if (cut.length === 2) segments.push([cut[0], cut[1], level]);
      else if (cut.length === 4) {
        // a saddle: each corner on the other side from the square's middle is cut off by its own line, across the two edges that meet at it
        const middle = c.reduce((s, corner) => s + corner.value, 0) / 4 >= level;
        for (let k = 0; k < 4; k++) if (above[k] !== middle) segments.push([crossings[(k + 3) % 4]!, crossings[k]!, level]);
      }
    }
  }
  return segments;
}

/** The number of decimals a contour step is written with: 1 for 0.5, 0 for 2. */
export const stepDecimals = (step: number) => {
  const written = String(step), point = written.indexOf('.');
  return point < 0 ? 0 : Math.min(3, written.length - point - 1);
};

/**
 * Where to write each level's value, once on its line: the middle of the piece of the line nearest the middle of all its pieces (the median of their
 * midpoints), or the next nearest that is at least `gap` from every value already placed, so that where the lines crowd together the figures do not
 * cover one another; a level with no such piece goes without.
 */
export function contourLabels(segments: Segment[], gap = 0): { at: Point; level: number }[] {
  const byLevel = new Map<number, Point[]>();
  for (const [p, q, level] of segments) {
    const list = byLevel.get(level) ?? [];
    list.push({ x: (p.x + q.x) / 2, y: (p.y + q.y) / 2, z: (p.z + q.z) / 2 });
    byLevel.set(level, list);
  }
  const median = (values: number[]) => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
  const distance = (a: Point, b: Point) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);
  const placed: { at: Point; level: number }[] = [];
  for (const [level, mids] of [...byLevel].sort((a, b) => a[0] - b[0])) {
    const centre = { x: median(mids.map((m) => m.x)), y: median(mids.map((m) => m.y)), z: median(mids.map((m) => m.z)) };
    const at = [...mids].sort((a, b) => distance(a, centre) - distance(b, centre)).find((m) => placed.every((other) => distance(m, other.at) >= gap));
    if (at) placed.push({ at, level });
  }
  return placed;
}
