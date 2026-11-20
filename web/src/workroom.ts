import type { Workroom, WorkroomRegion } from './types';

/** What each ISO 14257 curve is: the A-weighted pink noise of Table 1, the source's own sound power spectrum (Formula 3) or one octave band. */
export const curveLabel = (curve: string) =>
  curve === 'normalised' ? 'A-weighted pink noise (frequency-normalized)' : curve === 'source' ? "This source's sound power spectrum" : `${Number(curve) >= 1000 ? `${Number(curve) / 1000} k` : `${curve} `}Hz octave`;

/** ISO 11690's typical DL2 in the middle region (as the FESI acoustics guide tables it) and its recommendation for workrooms over 1000 m³. */
export const TYPICAL_DL2 = [
  'Small or intermediate room (V < 10 000 m³, h < 5 m), no absorbing ceiling, empty: 1 to 3 dB',
  'Large room (V > 10 000 m³, h > 5 m), no absorbing ceiling, with fittings: 2.5 to 4 dB',
  'Any room with an absorbing ceiling, with fittings: 3.5 to 5 dB',
  'Recommended for a workroom over 1000 m³: more than 3 to 4 dB',
];

/** A region's name and its distances: "Near (1 to 5 m)", "Middle (5 to 16 m)", "Far (from 16 m)". */
export const regionLabel = (g: WorkroomRegion) =>
  `${g.region[0].toUpperCase()}${g.region.slice(1)} (${g.toM === null ? `from ${g.fromM}` : `${g.fromM} to ${g.toM}`} m)`;

/** A line for results worked out with d1 and d2 other than the standard's typical 5 m and 16 m, which it wants reported; null for the typical ones. */
export const regionsNote = (w: Workroom) =>
  w.d1M === 5 && w.d2M === 16 ? null : `The regions are divided at d1 = ${w.d1M} m and d2 = ${w.d2M} m, not the standard's typical 5 m and 16 m.`;

/** Whether the receivers in the middle region span less than a doubling of distance, too little for a decay per doubling to mean much. */
export function narrowSpread(w: Workroom): boolean {
  const inRange = w.positions.filter((p) => p.distanceM >= w.d1M && p.distanceM <= w.d2M).map((p) => p.distanceM);
  return inRange.length < 2 || Math.max(...inRange) < 2 * Math.min(...inRange);
}
