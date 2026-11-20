import type { MapRaw, Point, ReceiverResult, ReflectionsResult, SimulateResponse } from './types';
import { BANDS_HZ, MAP_METRICS, bandLabel, totalDb } from './types';
import { ISO_ROWS } from './iso';
import { OPEN_PLAN_ROWS, extrapolated } from './openPlan';
import { TYPICAL_DL2, curveLabel, regionLabel, regionsNote } from './workroom';
import { xlsxBytes } from './xlsx';

/** What a printed report says about the setup, besides the results: the room, the sources (how their level was entered, whether each was heard) and how the calculation was run. */
export type SourceInfo = { name: string; position: Point; pattern: string; delayMs: number; spectrum: string; lwBandsDb: number[]; levelNote: string; heard: boolean };
export type ReportInfo = { title: string; roomLine: string; sources: SourceInfo[]; calculation: string };

/** One row of the sources table every export shows: a source, or (`position` null) the sources heard added together. */
type SourceRow = { name: string; position: Point | null; pattern: string; spectrum: string; delayMs: number; levelNote: string; heard: boolean; lwBandsDb: number[]; overallDb: number };

/** The sources as one table for every export: a row for each, then, when more than one is heard, the ones heard with their powers added as energies. */
export function sourceTable(sources: SourceInfo[]): SourceRow[] {
  const rows: SourceRow[] = sources.map((x) => ({ ...x, overallDb: totalDb(x.lwBandsDb) }));
  const heard = sources.filter((x) => x.heard);
  if (heard.length > 1) {
    rows.push({ name: 'All sources', position: null, pattern: '', spectrum: 'The ones heard, added as energies', delayMs: 0, levelNote: '', heard: true,
      lwBandsDb: BANDS_HZ.map((_, b) => totalDb(heard.map((x) => x.lwBandsDb[b]))), overallDb: totalDb(heard.flatMap((x) => x.lwBandsDb)) });
  }
  return rows;
}

/** The receivers of a result: the quick estimate has none listed, so it is the one point it was worked out for. */
const receiversOf = (result: SimulateResponse): (Omit<ReceiverResult, 'position'> & { position: Point | null })[] =>
  result.receivers ?? [{ position: null, directDistanceM: result.directDistanceM, metrics: result.metrics, broadband: result.broadband, sti: result.sti, decay: result.decay, impulse: result.impulse }];

const cell = (v: unknown): string => {
  const text = v === null || v === undefined ? '' : String(v);
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
};
const line = (...values: unknown[]) => values.map(cell).join(',');
const num = (v: number | null | undefined, digits = 4) => (v === null || v === undefined || !Number.isFinite(v) ? '' : +v.toFixed(digits));

/** What an STI was worked out for: the voice, where its speech came from, and the background noise. */
const stiFor = (sti: SimulateResponse['sti']) =>
  `${sti.voice} weighting, ${sti.signalFrom === 'sources' ? "the sources' own levels" : `a talker at ${sti.speechLevelDbA} dBA at 1 m`}, ${sti.backgroundNc === 0 ? 'no background noise' : `NC ${sti.backgroundNc}`}`;

/** Every receiver's results as rows: one per parameter with its eight octave bands and its single number, then the STI, after a row for each source when they are given. Empty cells are values that cannot be worked out. */
export function resultsRows(result: SimulateResponse, sources: SourceInfo[] = []): unknown[][] {
  const rows: unknown[][] = [['Receiver', 'x (m)', 'y (m)', 'z (m)', 'Parameter', 'Unit', ...BANDS_HZ.map((hz) => `${bandLabel(hz)} Hz`), 'Broadband']];
  // what the results are for: each source, where it is and its own sound power level by band (the first column says which source)
  for (const src of sourceTable(sources)) {
    const where = src.position ? [num(src.position.x, 3), num(src.position.y, 3), num(src.position.z, 3)] : ['', '', ''];
    const about = [src.spectrum, src.pattern, src.levelNote, src.heard ? '' : 'muted'].filter(Boolean).join('; ');
    rows.push([src.name, ...where, `Sound power level Lw (${about})`, 'dB re 1 pW', ...src.lwBandsDb.map((l) => num(l, 1)), num(src.overallDb, 1)]);
    if (src.delayMs > 0) rows.push([src.name, ...where, 'Delay', 'ms', ...BANDS_HZ.map(() => ''), num(src.delayMs, 1)]);
  }
  receiversOf(result).forEach((r, i) => {
    const where = [r.position ? num(r.position.x, 3) : '', r.position ? num(r.position.y, 3) : '', r.position ? num(r.position.z, 3) : ''];
    const metrics = r.metrics as unknown as Record<string, (number | null)[] | undefined>;
    for (const row of ISO_ROWS) rows.push([i + 1, ...where, row.label, row.unit, ...BANDS_HZ.map((_, b) => num(metrics[row.key]?.[b], 4)), num(r.broadband[row.key], 4)]);
    rows.push([i + 1, ...where, 'Sabine T60', 's', ...BANDS_HZ.map((_, b) => num(r.metrics.sabineT60S[b], 4)), '']);
    rows.push([i + 1, ...where, 'Eyring T60', 's', ...BANDS_HZ.map((_, b) => num(r.metrics.eyringT60S[b], 4)), '']);
    rows.push([i + 1, ...where, 'Millington T60', 's', ...BANDS_HZ.map((_, b) => num(r.metrics.millingtonT60S[b], 4)), '']);
    rows.push([i + 1, ...where, 'Arau-Puchades T60', 's', ...BANDS_HZ.map((_, b) => num(r.metrics.arauPuchadesT60S[b], 4)), '']);
    rows.push([i + 1, ...where, 'Fitzroy T60', 's', ...BANDS_HZ.map((_, b) => num(r.metrics.fitzroyT60S[b], 4)), '']);
    rows.push([i + 1, ...where, 'Direct distance', 'm', ...BANDS_HZ.map(() => ''), num(r.directDistanceM, 3)]);
    rows.push([i + 1, ...where, `STI (${stiFor(r.sti)})`, '', ...BANDS_HZ.map(() => ''), num(r.sti.sti, 3)]);
    rows.push([i + 1, ...where, 'Speech level at the receiver', 'dB(A)', ...BANDS_HZ.map(() => ''), num(r.sti.signalDbA, 1)]);
    rows.push([i + 1, ...where, '%ALcons', '%', ...BANDS_HZ.map(() => ''), num(r.sti.alconsPercent, 2)]);
    rows.push([i + 1, ...where, 'STIPA', '', ...BANDS_HZ.map(() => ''), num(r.sti.stipa, 3)]);
    rows.push([i + 1, ...where, 'RaSTI', '', ...BANDS_HZ.map(() => ''), num(r.sti.rasti, 3)]);
    rows.push([i + 1, ...where, 'SII', '', ...BANDS_HZ.map(() => ''), num(r.sti.sii ?? null, 3)]);
  });
  // ISO 3382-3: each receiver's speech level and STI as the standard works them out, then its single numbers (or why there are none)
  const op = result.openPlan;
  if (op) {
    const blank = BANDS_HZ.map(() => '');
    for (const p of op.positions) {
      rows.push([p.receiver + 1, '', '', '', 'ISO 3382-3 speech level Lp,A,S (normal unisex speech)', 'dB(A)', ...blank, num(p.speechDbA, 2)]);
      rows.push([p.receiver + 1, '', '', '', 'ISO 3382-3 STI (unisex speech, male weighting)', '', ...blank, num(p.sti, 3)]);
    }
    if (op.notWorkedOut) rows.push(['ISO 3382-3', '', '', '', op.notWorkedOut, '', ...blank, '']);
    for (const row of OPEN_PLAN_ROWS) {
      const v = op[row.key];
      const about = [row.typical && `typical ${row.typical}`, v !== null && extrapolated(op, row.key, v)].filter(Boolean).join('; ');
      rows.push(['ISO 3382-3', '', '', '', about ? `${row.label} (${about})` : row.label, row.unit, ...blank, num(v, row.digits + 1)]);
    }
  }
  // ISO 14257: each receiver's Lp - LW in the bands it measures (125 Hz to 4 kHz, the 63 Hz and 8 kHz columns left blank), then DL2, DLf and DL'fr of each curve in each region
  const w = result.workroom;
  if (w) {
    const blank = BANDS_HZ.map(() => '');
    for (const p of w.positions) {
      rows.push([p.receiver + 1, '', '', '', 'ISO 14257 sound distribution Lp - LW (Broadband column: A-weighted pink noise)', 'dB', '', ...p.bandsDb.map((v) => num(v, 2)), '', num(p.normalisedDb, 2)]);
      if (p.sourceDb !== null) rows.push([p.receiver + 1, '', '', '', "ISO 14257 sound distribution Lp - LW for this source's spectrum", 'dB', ...blank, num(p.sourceDb, 2)]);
      rows.push([p.receiver + 1, '', '', '', 'ISO 14257 free-field reference curve', 'dB', ...blank, num(p.referenceDb, 2)]);
    }
    if (w.notWorkedOut) rows.push(['ISO 14257', '', '', '', w.notWorkedOut, '', ...blank, '']);
    for (const c of w.curves)
      for (const g of c.regions) {
        const where = `${curveLabel(c.curve)}, ${regionLabel(g)}, ${g.positions} positions`;
        rows.push(['ISO 14257', '', '', '', `DL2, ${where}`, 'dB', ...blank, num(g.decayPerDoublingDb, 2)]);
        rows.push(['ISO 14257', '', '', '', `DLf, ${where}`, 'dB', ...blank, num(g.excessDb, 2)]);
        rows.push(['ISO 14257', '', '', '', `DL'f at ${g.atM} m, ${where}`, 'dB', ...blank, num(g.excessAtDb, 2)]);
      }
  }
  return rows;
}

/** The same rows as CSV. */
export const resultsCsv = (result: SimulateResponse, sources: SourceInfo[] = []): string => resultsRows(result, sources).map((r) => line(...r)).join('\r\n') + '\r\n';

/** A receiver's curves (the decay or the impulse response) as rows: time, then each receiver's eight bands and overall, side by side. */
function curveRows(result: SimulateResponse, pick: (r: ReturnType<typeof receiversOf>[number]) => { stepS: number; bands: (number | null)[][]; overall: (number | null)[] } | undefined): unknown[][] {
  const curves = receiversOf(result).map(pick);
  const head: unknown[] = ['Time (s)'];
  curves.forEach((cv, i) => { if (cv) head.push(...BANDS_HZ.map((hz) => `Receiver ${i + 1} ${bandLabel(hz)} Hz (dB)`), `Receiver ${i + 1} overall (dB)`); });
  const step = curves.find((cv) => cv)?.stepS ?? 0;
  const length = Math.max(0, ...curves.map((cv) => cv?.overall.length ?? 0));
  const rows: unknown[][] = [head];
  for (let t = 0; t < length; t++) {
    const row: unknown[] = [num(t * step, 4)];
    for (const cv of curves) if (cv) row.push(...cv.bands.map((band) => num(band[t], 2)), num(cv.overall[t], 2));
    rows.push(row);
  }
  return rows;
}

/** The local date and time, to the minute, for a file's "made" line. */
function madeAt(when = new Date()): string {
  const two = (n: number) => String(n).padStart(2, '0');
  return `${when.getFullYear()}-${two(when.getMonth() + 1)}-${two(when.getDate())} ${two(when.getHours())}:${two(when.getMinutes())} (local time)`;
}

/**
 * The results as an Excel workbook: the results by receiver, the sources with their power spectra, the setup, each receiver's decay curves and impulse
 * responses, and, when they have been worked out, the map and the reflection list.
 */
export function resultsXlsx(result: SimulateResponse, info: ReportInfo, more: { map?: MapRaw | null; reflections?: ReflectionsResult | null } = {}): Uint8Array<ArrayBuffer> {
  const sources: unknown[][] = [['Source', 'x (m)', 'y (m)', 'z (m)', 'Pattern', 'Spectrum', 'Level entered', 'Heard', 'Delay (ms)', ...BANDS_HZ.map((hz) => `Lw ${bandLabel(hz)} Hz (dB re 1 pW)`), 'Lw overall (dB re 1 pW)']];
  for (const x of sourceTable(info.sources)) {
    const where = x.position ? [num(x.position.x, 3), num(x.position.y, 3), num(x.position.z, 3)] : ['', '', ''];
    sources.push([x.name, ...where, x.pattern, x.spectrum, x.levelNote, x.position ? (x.heard ? 'yes' : 'muted') : '', x.position ? x.delayMs : '', ...x.lwBandsDb.map((l) => num(l, 1)), num(x.overallDb, 1)]);
  }
  return xlsxBytes([
    { name: 'Results', rows: resultsRows(result) },
    { name: 'Sources', rows: sources },
    { name: 'Setup', rows: [['Item', 'Value'], ['Project', info.title], ['Room', info.roomLine], ['Calculation', info.calculation], ['Made', madeAt()]] },
    { name: 'Decay', rows: curveRows(result, (r) => r.decay && { stepS: r.decay.stepS, bands: r.decay.schroederDb, overall: r.decay.overallSchroederDb }) },
    { name: 'Impulse', rows: curveRows(result, (r) => r.impulse && { stepS: r.impulse.stepS, bands: r.impulse.levelDb, overall: r.impulse.overallDb }) },
    ...(more.map ? [{ name: 'Map', rows: mapRows(more.map) }] : []),
    ...(more.reflections ? [{ name: 'Reflections', rows: reflectionsRows(more.reflections) }] : []),
  ]);
}

/** The reflection list as rows: each path's order, surfaces, length, delay after the direct sound and level against it, per octave band and overall. */
function reflectionsRows(list: ReflectionsResult): unknown[][] {
  const rows: unknown[][] = [['Order', 'Surfaces', 'Path length (m)', 'Delay after direct (ms)', ...BANDS_HZ.map((hz) => `${bandLabel(hz)} Hz (dB re direct)`), 'Overall (dB re direct)']];
  for (const r of list.reflections) rows.push([r.order, r.surfaces.join(' > '), num(r.distanceM, 3), num(r.delayMs, 3), ...BANDS_HZ.map((_, b) => num(r.levelsDb[b], 2)), num(r.overallDb, 2)]);
  return rows;
}

/** The reflection list as CSV. */
export const reflectionsCsv = (list: ReflectionsResult): string => reflectionsRows(list).map((r) => line(...r)).join('\r\n') + '\r\n';

/** A map as rows: a row per point with every parameter's single number and, where it has them, its eight octave bands. */
function mapRows(map: MapRaw): unknown[][] {
  const keys = Object.keys(map.parameters);
  const label = (key: string) => MAP_METRICS.find((m) => m.key === key)?.label ?? key;
  const head = ['x (m)', 'y (m)', 'z (m)'];
  for (const key of keys) {
    const p = map.parameters[key];
    head.push(`${label(key)} broadband (${p.unit})`);
    if (p.values) for (const hz of BANDS_HZ) head.push(`${label(key)} ${bandLabel(hz)} Hz (${p.unit})`);
  }
  const rows: unknown[][] = [head];
  map.points.forEach((pt, i) => {
    const values: unknown[] = [num(pt.x, 3), num(pt.y, 3), num(pt.z, 3)];
    for (const key of keys) {
      const p = map.parameters[key];
      values.push(num(p.broadband[i], 4));
      if (p.values) for (let b = 0; b < BANDS_HZ.length; b++) values.push(num(p.values[i][b], 4));
    }
    rows.push(values);
  });
  return rows;
}

/** A map as CSV. */
export const mapCsv = (map: MapRaw): string => mapRows(map).map((r) => line(...r)).join('\r\n') + '\r\n';

/** A 16-bit PCM WAV file of one or two channels; samples beyond full scale are clipped. */
export function wavBytes(channels: Float32Array[], sampleRateHz: number): Uint8Array<ArrayBuffer> {
  const n = channels[0].length, count = channels.length;
  const out = new Uint8Array(new ArrayBuffer(44 + n * count * 2));
  const view = new DataView(out.buffer);
  const text = (at: number, s: string) => { for (let i = 0; i < s.length; i++) view.setUint8(at + i, s.charCodeAt(i)); };
  text(0, 'RIFF'); view.setUint32(4, 36 + n * count * 2, true); text(8, 'WAVE'); text(12, 'fmt ');
  view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, count, true);
  view.setUint32(24, sampleRateHz, true); view.setUint32(28, sampleRateHz * count * 2, true); view.setUint16(32, count * 2, true); view.setUint16(34, 16, true);
  text(36, 'data'); view.setUint32(40, n * count * 2, true);
  let at = 44;
  for (let i = 0; i < n; i++) for (let c = 0; c < count; c++) { view.setInt16(at, Math.round(Math.max(-1, Math.min(1, channels[c][i])) * 32767), true); at += 2; }
  return out;
}

type Series = { name: string; colour: string; dash?: boolean; values: (number | null)[] };

/** A small line chart as an SVG string (no libraries, so it prints and saves with the page): the x positions, labelled ticks on x, and the series drawn over them. */
function lineChartSvg(title: string, xs: number[], xTicks: { at: number; label: string }[], series: Series[], yUnit: string, xUnit: string, yRange?: [number, number]): string {
  const w = 560, h = 230, left = 46, right = 12, top = 24, bottom = 34;
  const finite = series.flatMap((s) => s.values).filter((v): v is number => v !== null && Number.isFinite(v));
  if (finite.length === 0 || xs.length < 2) return '';
  let lo = yRange ? yRange[0] : Math.min(...finite), hi = yRange ? yRange[1] : Math.max(...finite);
  if (!yRange) {
    const span = hi - lo || Math.abs(hi) || 1;
    lo = lo >= 0 && lo - span * 0.1 < 0 ? 0 : lo - span * 0.1;
    hi += span * 0.1;
  }
  const x0 = xs[0], x1 = xs[xs.length - 1];
  const px = (x: number) => left + ((x - x0) / (x1 - x0 || 1)) * (w - left - right);
  const py = (y: number) => top + (1 - (Math.min(Math.max(y, lo), hi) - lo) / (hi - lo || 1)) * (h - top - bottom);
  const yTicks = Array.from({ length: 5 }, (_, i) => lo + ((hi - lo) * i) / 4);
  const grid = yTicks.map((t) => `<line x1="${left}" x2="${w - right}" y1="${py(t).toFixed(1)}" y2="${py(t).toFixed(1)}" stroke="#e5e7eb"/><text x="${left - 5}" y="${(py(t) + 3).toFixed(1)}" text-anchor="end">${Math.abs(t) >= 10 ? t.toFixed(0) : t.toFixed(1)}</text>`).join('');
  const xlabels = xTicks.map((t) => `<text x="${px(t.at).toFixed(1)}" y="${h - bottom + 14}" text-anchor="middle">${esc(t.label)}</text>`).join('');
  const lines = series.map((s) => {
    // a gap in the values (a null) breaks the line
    const parts: string[] = []; let run: string[] = [];
    s.values.forEach((v, i) => {
      if (v === null || !Number.isFinite(v) || i >= xs.length) { if (run.length) parts.push(run.join(' ')); run = []; }
      else run.push(`${px(xs[i]).toFixed(1)},${py(v).toFixed(1)}`);
    });
    if (run.length) parts.push(run.join(' '));
    return parts.map((p) => `<polyline points="${p}" fill="none" stroke="${s.colour}" stroke-width="${s.dash ? 1.2 : 1.8}"${s.dash ? ' stroke-dasharray="4 3"' : ''}/>`).join('');
  }).join('');
  const legend = series.map((s, i) => `<g transform="translate(${left + i * 84},12)"><line x1="0" x2="16" y1="0" y2="0" stroke="${s.colour}" stroke-width="2"${s.dash ? ' stroke-dasharray="4 3"' : ''}/><text x="20" y="3">${esc(s.name)}</text></g>`).join('');
  return `<svg viewBox="0 0 ${w} ${h}" width="${w}" height="${h}" font-family="system-ui,Segoe UI,sans-serif" font-size="10" fill="#374151" role="img" aria-label="${esc(title)}">
<rect x="${left}" y="${top}" width="${w - left - right}" height="${h - top - bottom}" fill="none" stroke="#9ca3af"/>${grid}${xlabels}${lines}${legend}
<text x="${left + (w - left - right) / 2}" y="${h - 4}" text-anchor="middle">${esc(xUnit)}</text><text x="10" y="${top + (h - top - bottom) / 2}" text-anchor="middle" transform="rotate(-90 10 ${top + (h - top - bottom) / 2})">${esc(yUnit)}</text></svg>`;
}

const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c] as string));

/** A self-contained, printable page: the setup, then for each receiver the single-number results, the octave-band table and its charts (reverberation, clarity, decay and impulse response). */
function receiverCharts(r: ReturnType<typeof receiversOf>[number]): string {
  const bandX = BANDS_HZ.map((_, i) => i), bandTicks = BANDS_HZ.map((hz, i) => ({ at: i, label: bandLabel(hz) }));
  const m = r.metrics;
  const rt = lineChartSvg('Reverberation time by octave band', bandX, bandTicks, [
    { name: 'T30', colour: '#7c3aed', values: m.t30S }, { name: 'EDT', colour: '#334155', values: m.edtS },
    { name: 'Sabine', colour: '#6b7280', dash: true, values: m.sabineT60S }, { name: 'Eyring', colour: '#d97706', dash: true, values: m.eyringT60S },
    { name: 'Millington', colour: '#059669', dash: true, values: m.millingtonT60S }, { name: 'Fitzroy', colour: '#dc2626', dash: true, values: m.fitzroyT60S },
  ], 's', 'Octave band (Hz)');
  const clarity = lineChartSvg('Clarity by octave band', bandX, bandTicks, [{ name: 'C80', colour: '#7c3aed', values: m.c80Db }, { name: 'C50', colour: '#059669', values: m.c50Db }], 'dB', 'Octave band (Hz)');
  const timeTicks = (stepS: number, n: number) => { const end = stepS * (n - 1), every = end > 2 ? 0.5 : end > 1 ? 0.25 : 0.1; return Array.from({ length: Math.floor(end / every) + 1 }, (_, i) => ({ at: i * every / stepS, label: (i * every).toFixed(2) })); };
  const decay = r.decay && lineChartSvg('Decay curve (Schroeder), overall', r.decay.overallSchroederDb.map((_, i) => i), timeTicks(r.decay.stepS, r.decay.overallSchroederDb.length), [{ name: 'Overall', colour: '#7c3aed', values: r.decay.overallSchroederDb }], 'dB', 'Time (s)', [-60, 0]);
  const top = r.impulse ? Math.ceil(Math.max(...r.impulse.overallDb.filter((v): v is number => v !== null), -200) / 10) * 10 : 0;
  const impulse = r.impulse && lineChartSvg('Impulse response, overall', r.impulse.overallDb.map((_, i) => i), timeTicks(r.impulse.stepS, r.impulse.overallDb.length), [{ name: 'Overall', colour: '#059669', values: r.impulse.overallDb }], 'dB', 'Time (s)', [top - 80, top]);
  return `<div class="charts">${rt}${clarity}${decay ?? ''}${impulse ?? ''}</div>`;
}

export function reportHtml(result: SimulateResponse, info: ReportInfo): string {
  const receivers = receiversOf(result);
  const fmt = (v: number | null | undefined, digits: number) => (v === null || v === undefined ? '–' : v.toFixed(digits));
  const head = `<tr><th>Parameter</th>${BANDS_HZ.map((hz) => `<th>${bandLabel(hz)}</th>`).join('')}<th>Broadband</th></tr>`;
  const sections = receivers.map((r, i) => {
    const metrics = r.metrics as unknown as Record<string, (number | null)[] | undefined>;
    const body = ISO_ROWS.map((row) => `<tr><td>${esc(row.label)}${row.unit ? ` (${esc(row.unit)})` : ''}</td>${BANDS_HZ.map((_, b) => `<td>${fmt(metrics[row.key]?.[b], row.digits)}</td>`).join('')}<td><b>${fmt(r.broadband[row.key], row.digits)}</b></td></tr>`).join('');
    const where = r.position ? ` at ${r.position.x.toFixed(1)}, ${r.position.y.toFixed(1)}, ${r.position.z.toFixed(1)} m` : '';
    return `<h2>Receiver ${i + 1}${where}</h2>
<p>Direct distance ${r.directDistanceM.toFixed(2)} m. Speech Transmission Index <b>${fmt(r.sti.sti, 2)}</b> ${esc(r.sti.rating)} (${esc(stiFor(r.sti))}; speech ${fmt(r.sti.signalDbA, 1)} dB(A) at the receiver); %ALcons ${fmt(r.sti.alconsPercent, 1)}; STIPA ${fmt(r.sti.stipa, 2)}; RaSTI ${fmt(r.sti.rasti, 2)}; SII ${fmt(r.sti.sii ?? null, 2)}.</p>
<table>${head}${body}</table>
${receiverCharts(r)}`;
  }).join('\n');
  const sourcesTable = info.sources.length === 0 ? '' : `<h3>Sources (sound power level Lw, dB re 1 pW)</h3>
<table><tr><th>Source</th><th>Position (m)</th><th>Pattern</th><th>Spectrum</th>${BANDS_HZ.map((hz) => `<th>${bandLabel(hz)}</th>`).join('')}<th>Overall</th></tr>
${sourceTable(info.sources).map((x) => `<tr><td>${esc(x.name)}${x.delayMs > 0 ? ` (delay ${x.delayMs} ms)` : ''}${x.heard ? '' : ' (muted)'}</td><td>${x.position ? `${x.position.x.toFixed(1)}, ${x.position.y.toFixed(1)}, ${x.position.z.toFixed(1)}` : ''}</td><td>${esc(x.pattern)}</td><td>${esc(x.spectrum)}${x.levelNote && x.levelNote !== 'Entered as sound power' ? `<br><small>${esc(x.levelNote)}</small>` : ''}</td>${x.lwBandsDb.map((l) => `<td>${l.toFixed(1)}</td>`).join('')}<td><b>${x.overallDb.toFixed(1)}</b></td></tr>`).join('')}
</table>`;
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>${esc(info.title)} – acoustic report</title>
<style>
body{font:13px/1.45 system-ui,Segoe UI,sans-serif;margin:24px;color:#111}
h1{font-size:20px;margin:0 0 4px}h2{font-size:15px;margin:22px 0 4px;break-after:avoid}h3{font-size:13px;margin:14px 0 4px}p{margin:4px 0}
table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums;font-size:12px;break-inside:avoid}
th,td{border-bottom:1px solid #ddd;padding:2px 6px;text-align:right}th:first-child,td:first-child{text-align:left}th{background:#f3f4f6}
.charts{display:flex;flex-wrap:wrap;gap:8px;margin-top:10px}.charts svg{max-width:100%;height:auto;break-inside:avoid}
.note{color:#555;font-size:11px;margin-top:16px}button{margin:8px 0;padding:4px 12px}@media print{button{display:none}body{margin:12mm}}
</style></head><body>
<button onclick="window.print()">Print</button>
<h1>${esc(info.title)}</h1>
<p>${esc(info.roomLine)}</p>
<p>${esc(info.calculation)}</p>
${sourcesTable}
${sections}
${openPlanHtml(result.openPlan)}
${workroomHtml(result.workroom)}
<p class="note">Reverberation times are T30 unless named. The Broadband column is the ISO 3382-1 single number (the mean of the octave bands the standard averages each parameter over); a band that cannot be worked out is left out of the mean and shown as –. Calculated ${esc(new Date().toLocaleString())}.</p>
</body></html>`;
}

/** The ISO 3382-3 open-plan office section of the printed report: its single numbers (or why there are none) and each position's speech level and STI. */
function openPlanHtml(op: SimulateResponse['openPlan']): string {
  if (!op) return '';
  const fmt = (v: number | null, digits: number) => (v === null ? '–' : v.toFixed(digits));
  const numbers = OPEN_PLAN_ROWS.map((row) => {
    const v = op[row.key];
    const outside = v === null ? '' : extrapolated(op, row.key, v);
    return `<tr><td>${esc(row.label)}</td><td><b>${fmt(v, row.digits)}</b>${row.unit && v !== null ? ` ${esc(row.unit)}` : ''}${outside ? `<br><small>${esc(outside)}</small>` : ''}</td><td>${esc(row.typical)}</td></tr>`;
  }).join('');
  const positions = op.positions.map((p) => `<tr><td>Receiver ${p.receiver + 1}</td><td>${p.distanceM.toFixed(2)}</td><td>${fmt(p.speechDbA, 1)}</td><td>${fmt(p.sti, 2)}</td></tr>`).join('');
  return `<h2>Open-plan office (ISO 3382-3)</h2>
${op.notWorkedOut ? `<p><b>${esc(op.notWorkedOut)}</b></p>` : ''}${op.omnidirectional ? '' : '<p>The source has a pattern; the standard uses an omnidirectional one.</p>'}
<p>Normal unisex speech from the source (ISO 3382-3 Table 1, 57.4 dB(A) at 1 m), taken to each receiver by the room's strength G; the STI with male weighting and the background noise.</p>
<table><tr><th>Single number</th><th>Value</th><th>Typical (2022 Annex C)</th></tr>${numbers}</table>
${positions ? `<h3>Positions</h3><table><tr><th>Position</th><th>Distance (m)</th><th>Speech (dB(A))</th><th>STI</th></tr>${positions}</table>` : ''}`;
}

/** The ISO 14257 workroom section of the printed report: DL2, DLf and DL'fr of each curve in each region (or why there are none) and each position's Lp - LW. */
function workroomHtml(w: SimulateResponse['workroom']): string {
  if (!w) return '';
  const fmt = (v: number | null, digits: number) => (v === null ? '–' : v.toFixed(digits));
  const regions = w.curves[0]?.regions ?? [];
  const curves = w.curves.map((c) => `<tr><td>${esc(curveLabel(c.curve))}</td>${c.regions.map((g) => `<td><b>${fmt(g.decayPerDoublingDb, 1)}</b></td><td><b>${fmt(g.excessDb, 1)}</b></td><td>${fmt(g.excessAtDb, 1)}</td>`).join('')}</tr>`).join('');
  const head = `<tr><th rowspan="2">Curve</th>${regions.map((g) => `<th colspan="3">${esc(regionLabel(g))}, ${g.positions} positions</th>`).join('')}</tr><tr>${regions.map((g) => `<th>DL2 (dB)</th><th>DLf (dB)</th><th>DL'f ${g.atM} m (dB)</th>`).join('')}</tr>`;
  const source = w.positions.some((p) => p.sourceDb !== null);
  const positions = w.positions.map((p) => `<tr><td>Receiver ${p.receiver + 1}</td><td>${p.distanceM.toFixed(2)}</td>${p.bandsDb.map((v) => `<td>${fmt(v, 1)}</td>`).join('')}<td>${fmt(p.normalisedDb, 1)}</td>${source ? `<td>${fmt(p.sourceDb, 1)}</td>` : ''}<td>${p.referenceDb.toFixed(1)}</td></tr>`).join('');
  return `<h2>Workroom (ISO 14257)</h2>
${w.notWorkedOut ? `<p><b>${esc(w.notWorkedOut)}</b></p>` : ''}${regionsNote(w) ? `<p><b>${esc(regionsNote(w)!)}</b></p>` : ''}${w.omnidirectional ? '' : '<p>The source has a pattern; the standard uses an omnidirectional one.</p>'}
<p>Lp - LW at each receiver (the room's strength G less 31 dB). In each region of distance from the source (near, 1 to ${w.d1M} m; middle, ${w.d1M} to ${w.d2M} m; far, from ${w.d2M} m), DL2 is the fall per doubling of distance, DLf the excess over the free-field curve averaged over log distance, and DL'f the regression line's excess at 4, 10 or 30 m; a region needs at least three positions. Typical DL2 in the middle region (ISO 11690): ${esc(TYPICAL_DL2.join('; '))}.</p>
${curves ? `<table>${head}${curves}</table>` : ''}
${positions ? `<h3>Positions (Lp - LW, dB)</h3><table><tr><th>Position</th><th>Distance (m)</th><th>125</th><th>250</th><th>500</th><th>1k</th><th>2k</th><th>4k</th><th>Pink (A)</th>${source ? '<th>Source</th>' : ''}<th>Free field</th></tr>${positions}</table>` : ''}`;
}

/** Opens the report in a new window for printing; returns false if the browser blocked the window. */
export function openReport(html: string): boolean {
  const win = window.open('', '_blank');
  if (!win) return false;
  win.document.open(); win.document.write(html); win.document.close();
  return true;
}
