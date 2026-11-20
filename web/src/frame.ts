import type { MapScale, Point, Room } from './types';

/** The space the room occupies, in the engine's frame, and how far inside it the source and receiver are kept (a box needs the 0.5 m receiver sphere clear of its walls). */
export type Frame = { min: Point; max: Point; margin: number };

export const boxFrame = (r: Room): Frame => ({ min: { x: 0, y: 0, z: 0 }, max: { x: r.lengthM, y: r.widthM, z: r.heightM }, margin: 0.6 });

export const size = (f: Frame): Point => ({ x: f.max.x - f.min.x, y: f.max.y - f.min.y, z: f.max.z - f.min.z });

const centre = (f: Frame) => ({ x: (f.min.x + f.max.x) / 2, y: (f.min.y + f.max.y) / 2 });

// The engine is z-up; Three.js is y-up. The room is centred on the origin in the floor plane and z becomes y.
export const toThree = (p: Point, f: Frame): [number, number, number] => {
  const c = centre(f);
  return [p.x - c.x, p.z, -(p.y - c.y)];
};

export const fromThree = (v: { x: number; y: number; z: number }, f: Frame): Point => {
  const c = centre(f);
  return { x: v.x + c.x, y: -v.z + c.y, z: v.y };
};

export const clampTo = (p: Point, f: Frame): Point => {
  const m = f.margin;
  const c = (v: number, lo: number, hi: number) => Math.min(Math.max(v, lo), Math.max(lo, hi));
  return { x: c(p.x, f.min.x + m, f.max.x - m), y: c(p.y, f.min.y + m, f.max.y - m), z: c(p.z, f.min.z + m, f.max.z - m) };
};

const PALETTE = ['#a78bfa', '#34d399', '#f472b6', '#fbbf24', '#e2e8f0', '#2dd4bf', '#94a3b8', '#f87171', '#a3e635', '#e879f9'];
export const groupColour = (index: number) => PALETTE[index % PALETTE.length];

/** The lowest and highest value of a colour scale: the values' own range, widened about its middle when narrower than a tenth of the middle or than a floor for the unit, so ray noise in an even room is not drawn as a big difference. */
export const colourRange = (values: number[], unit: string): [number, number] | null => {
  if (values.length === 0) return null;
  const lo = Math.min(...values), hi = Math.max(...values), mid = (lo + hi) / 2;
  const floor = unit === 'dB' ? 1 : unit === '%' ? 5 : 0.05;
  const span = Math.max(Math.abs(mid) * 0.1, floor, hi - lo);
  return [mid - span / 2, mid + span / 2];
};

/** The colour scale of a map: the ends the user set, the values' own (as colourRange gives them) for an end not set, and the values' own again if that leaves the bottom at or above the top. */
export const mapRange = (values: number[], unit: string, scale?: MapScale): [number, number] | null => {
  const own = colourRange(values, unit);
  if (!own || !scale) return own;
  const lo = scale.min ?? own[0], hi = scale.max ?? own[1];
  return lo < hi ? [lo, hi] : own;
};

/** How much a surface absorbs, on a fixed 0 to 1 scale: red where it reflects nearly everything, through yellow and green, to blue where it soaks nearly all up. Grey when the material has no figure for the band. */
export const absorptionColour = (alpha: number | null): string => (alpha === null ? '#64748b' : rainbow(1 - alpha));

/** Blue for the lowest value through green and yellow to red for the highest. */
export const rainbow = (t: number): string => `hsl(${Math.round(240 * (1 - Math.min(1, Math.max(0, t))))}, 85%, 52%)`;
