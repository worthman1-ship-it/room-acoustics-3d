import type { OpenPlan, Point } from './types';
import type { Frame } from './frame';

/**
 * ISO 3382-3 receiver positions: `count` points evenly spaced in a level line away from the source, at the receiver's height, from 2 m out to 16 m or the
 * room's edge (less its margin), whichever is nearer. The line heads from the source towards `toward` across the floor; when that is straight above or
 * below the source, it heads along the axis with the most room. The room's edge is its bounding box, so in a model that is not a box some points can
 * land outside the room.
 */
export function receiverLine(from: Point, toward: Point, frame: Frame, count: number): Point[] {
  const m = frame.margin;
  // how far a step of (dx, dy) can go from the source before leaving the box
  const reach = (dx: number, dy: number) => Math.min(
    dx > 0 ? (frame.max.x - m - from.x) / dx : dx < 0 ? (frame.min.x + m - from.x) / dx : Infinity,
    dy > 0 ? (frame.max.y - m - from.y) / dy : dy < 0 ? (frame.min.y + m - from.y) / dy : Infinity);
  let dx = toward.x - from.x, dy = toward.y - from.y;
  const length = Math.hypot(dx, dy);
  if (length < 0.1) [dx, dy] = [[1, 0], [-1, 0], [0, 1], [0, -1]].reduce((a, b) => (reach(b[0], b[1]) > reach(a[0], a[1]) ? b : a));
  else { dx /= length; dy /= length; }
  const z = Math.min(Math.max(toward.z, frame.min.z + m), frame.max.z - m);
  const far = Math.max(0, Math.min(16, reach(dx, dy)));
  const near = Math.min(2, far / 2);
  return Array.from({ length: count }, (_, i) => {
    const r = count === 1 ? near : near + ((far - near) * i) / (count - 1);
    return { x: from.x + dx * r, y: from.y + dy * r, z };
  });
}

/** What ISO 3382-3:2022 Annex C calls a value typical of good or of poor open-plan office acoustics (null: in between, or no range given). */
export type Verdict = 'good' | 'poor' | null;

type NumberKey = 'distractionDistanceM' | 'comfortDistanceM' | 'spatialDecayDb' | 'speechAt4mDbA' | 'backgroundDbA' | 'nearestSti' | 'privacyDistanceM';

/** The ISO 3382-3 single numbers in the order the standard reports them, each with Annex C's (2022) typical good and poor values where it gives them. */
export const OPEN_PLAN_ROWS: { key: NumberKey; label: string; unit: string; digits: number; typical: string; verdict: (v: number) => Verdict }[] = [
  { key: 'distractionDistanceM', label: 'Distraction distance rD (STI below 0.50)', unit: 'm', digits: 1, typical: 'good < 5, poor > 11', verdict: (v) => (v < 5 ? 'good' : v > 11 ? 'poor' : null) },
  { key: 'comfortDistanceM', label: 'Comfort distance rC (speech below 45 dB(A))', unit: 'm', digits: 1, typical: 'good < 5, poor > 11', verdict: (v) => (v < 5 ? 'good' : v > 11 ? 'poor' : null) },
  { key: 'spatialDecayDb', label: 'Spatial decay rate of speech D2,S', unit: 'dB', digits: 1, typical: 'good > 8, poor < 5', verdict: (v) => (v > 8 ? 'good' : v < 5 ? 'poor' : null) },
  { key: 'speechAt4mDbA', label: 'Speech level at 4 m Lp,A,S,4m', unit: 'dB(A)', digits: 1, typical: 'good < 48, poor > 52', verdict: (v) => (v < 48 ? 'good' : v > 52 ? 'poor' : null) },
  { key: 'backgroundDbA', label: 'Background noise Lp,A,B', unit: 'dB(A)', digits: 1, typical: 'good 40 to 45, poor < 35 or > 48', verdict: (v) => (v >= 40 && v <= 45 ? 'good' : v < 35 || v > 48 ? 'poor' : null) },
  { key: 'nearestSti', label: 'STI at the nearest workstation', unit: '', digits: 2, typical: '', verdict: () => null },
  { key: 'privacyDistanceM', label: 'Privacy distance rP (STI below 0.20; 2012 edition only)', unit: 'm', digits: 1, typical: '', verdict: () => null },
];

/**
 * Whether a distance (rD, rC or rP) lies outside the positions it was worked out from, so is extrapolated, as the standard says to report it: nearer than every
 * receiver more than 1 m from the source, or beyond them all. Empty when it lies among them.
 */
export function extrapolated(op: OpenPlan, key: NumberKey, value: number): string {
  if (!key.endsWith('DistanceM')) return '';
  const used = op.positions.filter((p) => p.distanceM > (key === 'comfortDistanceM' ? 2 - 1e-9 : 1) && (key !== 'comfortDistanceM' || p.distanceM <= 16)).map((p) => p.distanceM);
  if (used.length === 0) return '';
  if (value < Math.min(...used)) return 'extrapolated: nearer than every receiver';
  if (value > Math.max(...used)) return 'extrapolated: beyond every receiver';
  return '';
}
