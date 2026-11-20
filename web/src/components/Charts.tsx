import { Fragment, useMemo, useRef, useState, type ReactNode } from 'react';
import { Area, AreaChart, Bar, BarChart, CartesianGrid, ComposedChart, Legend, Line, LineChart, ReferenceArea, ReferenceLine, ResponsiveContainer, Scatter, Tooltip, XAxis, YAxis } from 'recharts';
import type { Band, MapPoint, MapResult, Measured, OpenPlan, Point, ReceiverResult, SimulateResponse, MapRaw, ReflectionsResult, Workroom } from '../types';
import MeasuredPanel from './Measured';
import { OPEN_PLAN_ROWS, extrapolated } from '../openPlan';
import { TYPICAL_DL2, curveLabel, narrowSpread, regionLabel, regionsNote } from '../workroom';
import { BANDS_HZ, bandLabel } from '../types';
import { ISO_ROWS } from '../iso';
import { download } from '../project';
import { snapshotPng } from '../chartImage';
import { openReport, reportHtml, resultsCsv, resultsXlsx, type ReportInfo } from '../export';
import { clampTo, mapRange, rainbow, size, type Frame } from '../frame';
import { contourLabels, contourLevels, contourSegments, MAX_CONTOURS, stepDecimals, type Segment } from '../contours';

/** What the result tabs draw: the results of the receiver chosen. */
type View = Pick<ReceiverResult, 'metrics' | 'broadband' | 'sti'> & { decay: SimulateResponse['decay']; impulse: SimulateResponse['impulse'] };

type Tab = 'rt' | 'clarity' | 'response' | 'decay' | 'impulse' | 'iso' | 'thirds' | 'receivers' | 'office' | 'workroom' | 'reflections' | 'listen' | 'measured';
const TABS: { key: Tab; label: string }[] = [
  { key: 'rt', label: 'Reverberation' }, { key: 'clarity', label: 'Clarity' }, { key: 'response', label: 'Frequency response' },
  { key: 'decay', label: 'Decay curve' }, { key: 'impulse', label: 'Impulse response' }, { key: 'iso', label: 'ISO 3382 / STI' }, { key: 'thirds', label: 'Third octaves' }, { key: 'receivers', label: 'Receivers' }, { key: 'office', label: 'Open-plan office' }, { key: 'workroom', label: 'Workroom' }, { key: 'reflections', label: 'Reflections' }, { key: 'listen', label: 'Listen' }, { key: 'measured', label: 'Measured' },
];

/** The tabs with charts, which can be saved as pictures. */
const CHART_TABS: Tab[] = ['rt', 'clarity', 'response', 'decay', 'impulse', 'office', 'workroom'];

/** Every receiver side by side: the single-number values, the STI and the direct distance. */
function ReceiversTable({ result }: { result: SimulateResponse }) {
  const receivers = result.receivers ?? [];
  const text = (v: number | null, digits: number) => (v === null ? '–' : v.toFixed(digits));
  if (receivers.length === 0) return <div className="grid h-full place-items-center text-sm text-slate-500">The quick estimate is for one point; press Run simulation to get results for each receiver.</div>;
  return (
    <div className="h-full overflow-auto" data-testid="receivers">
      <table className="w-full min-w-[480px] border-collapse text-xs tabular-nums">
        <thead>
          <tr className="text-left text-slate-400">
            <th className="py-1 pr-3 font-medium">Parameter</th>
            {receivers.map((_, i) => <th key={i} className="px-2 py-1 text-right font-medium text-accent">Receiver {i + 1}</th>)}
          </tr>
        </thead>
        <tbody>
          <tr className="border-t border-white/5 text-slate-200">
            <th scope="row" className="py-1 pr-3 text-left font-normal">Position x, y, z (m)</th>
            {receivers.map((r, i) => <td key={i} className="px-2 py-1 text-right">{r.position.x.toFixed(1)}, {r.position.y.toFixed(1)}, {r.position.z.toFixed(1)}</td>)}
          </tr>
          <tr className="border-t border-white/5 text-slate-200">
            <th scope="row" className="py-1 pr-3 text-left font-normal">Direct distance (m)</th>
            {receivers.map((r, i) => <td key={i} className="px-2 py-1 text-right">{r.directDistanceM.toFixed(2)}</td>)}
          </tr>
          <tr className="border-t border-white/5 text-slate-200">
            <th scope="row" className="py-1 pr-3 text-left font-normal">STI</th>
            {receivers.map((r, i) => <td key={i} className="px-2 py-1 text-right" data-testid={`receiver-${i}-sti`}>{text(r.sti.sti, 2)}</td>)}
          </tr>
          <tr className="border-t border-white/5 text-slate-200">
            <th scope="row" className="py-1 pr-3 text-left font-normal">STIPA</th>
            {receivers.map((r, i) => <td key={i} className="px-2 py-1 text-right">{text(r.sti.stipa ?? null, 2)}</td>)}
          </tr>
          <tr className="border-t border-white/5 text-slate-200">
            <th scope="row" className="py-1 pr-3 text-left font-normal">RaSTI</th>
            {receivers.map((r, i) => <td key={i} className="px-2 py-1 text-right">{text(r.sti.rasti ?? null, 2)}</td>)}
          </tr>
          <tr className="border-t border-white/5 text-slate-200">
            <th scope="row" className="py-1 pr-3 text-left font-normal">SII</th>
            {receivers.map((r, i) => <td key={i} className="px-2 py-1 text-right">{text(r.sti.sii ?? null, 2)}</td>)}
          </tr>
          <tr className="border-t border-white/5 text-slate-200">
            <th scope="row" className="py-1 pr-3 text-left font-normal">%ALcons</th>
            {receivers.map((r, i) => <td key={i} className="px-2 py-1 text-right">{text(r.sti.alconsPercent, 1)}</td>)}
          </tr>
          {ISO_ROWS.map((row) => (
            <tr key={row.key} className="border-t border-white/5 text-slate-200">
              <th scope="row" className="py-1 pr-3 text-left font-normal">{row.label}{row.unit && <span className="text-slate-500"> ({row.unit})</span>}</th>
              {receivers.map((r, i) => <td key={i} className="px-2 py-1 text-right" data-testid={`receiver-${i}-${row.key}`}>{text(r.broadband[row.key], row.digits)}</td>)}
            </tr>
          ))}
        </tbody>
      </table>
      <p className="mt-2 text-[11px] leading-snug text-slate-500">The single-number values (ISO 3382-1 band means). The ISO 3382 / STI tab shows every octave band for the receiver you pick there.</p>
    </div>
  );
}

/**
 * ISO 3382-3 (open-plan offices): the single numbers with Annex C's typical good and poor values, the spatial decay of speech against distance (log axis, the
 * line through 2 to 16 m) and the STI against distance (the line rD and rP are read from), as the standard's Figure 3 draws them, and each position's values.
 */
function OpenPlanPanel({ op }: { op: OpenPlan | null | undefined }) {
  const text = (v: number | null, digits: number) => (v === null ? '–' : v.toFixed(digits));
  if (!op) return <div className="grid h-full place-items-center px-6 text-center text-sm text-slate-500">The quick estimate is for one point; press Run simulation with a line of receivers away from one source for the ISO 3382-3 results.</div>;
  const far = op.positions.filter((p) => p.distanceM > 1);
  const top = Math.max(16, ...op.positions.map((p) => p.distanceM)) * 1.15;
  // a fit over less than a doubling of distance says little about the decay per doubling
  const inRange = op.positions.filter((p) => p.distanceM >= 2 && p.distanceM <= 16).map((p) => p.distanceM);
  const narrow = inRange.length < 2 || Math.max(...inRange) < 2 * Math.min(...inRange);
  // the lines the single numbers come from: speech L = L4m - D2,S log2(r / 4) over 2 to 16 m, and the STI's straight line through (rD, 0.50) and (rP, 0.20)
  const d2s = op.spatialDecayDb, l4 = op.speechAt4mDbA, rD = op.distractionDistanceM, rP = op.privacyDistanceM;
  const decayLine = d2s !== null && l4 !== null ? [2, 16].map((r) => ({ r, fit: l4 - d2s * Math.log2(r / 4) })) : [];
  const stiLine = rD !== null && rP !== null && far.length > 1
    ? [Math.min(...far.map((p) => p.distanceM)), Math.max(...far.map((p) => p.distanceM))].map((r) => ({ r, fit: 0.5 + (r - rD) * (0.2 - 0.5) / (rP - rD) })) : [];
  const levels = op.positions.filter((p) => p.speechDbA !== null && p.distanceM > 0).map((p) => ({ r: p.distanceM, speech: p.speechDbA }));
  const stis = op.positions.filter((p) => p.sti !== null).map((p) => ({ r: p.distanceM, sti: p.sti }));
  const shade = { good: 'text-emerald-300', poor: 'text-rose-300' } as const;
  return (
    <div className="h-full overflow-auto" data-testid="open-plan">
      {op.notWorkedOut && <p className="mb-2 rounded-md border border-amber-400/20 bg-amber-400/5 px-3 py-2 text-xs text-amber-200" data-testid="open-plan-why">{op.notWorkedOut}</p>}
      {!op.omnidirectional && <p className="mb-2 text-[11px] text-amber-200">The source has a pattern: ISO 3382-3 uses an omnidirectional one, so these results are for this source rather than the standard's.</p>}
      {!op.notWorkedOut && narrow && <p className="mb-2 text-[11px] text-amber-200" data-testid="open-plan-narrow">The receivers 2 to 16 m away span less than a doubling of distance, so the lines through them, and every number read from them, are unreliable: spread the receivers along a line from near the source to far from it.</p>}
      <div className="flex flex-wrap gap-4">
        <table className="min-w-[340px] flex-1 border-collapse text-xs tabular-nums">
          <thead><tr className="text-left text-slate-400"><th className="py-1 pr-3 font-medium">ISO 3382-3 single number</th><th className="px-2 py-1 text-right font-medium text-accent">Value</th><th className="px-2 py-1 font-medium text-slate-500">Typical (Annex C)</th></tr></thead>
          <tbody>
            {OPEN_PLAN_ROWS.map((row) => {
              const v = op[row.key];
              const verdict = v === null ? null : row.verdict(v);
              const outside = v === null ? '' : extrapolated(op, row.key, v);
              return (
                <tr key={row.key} className="border-t border-white/5 text-slate-200">
                  <th scope="row" className="py-1 pr-3 text-left font-normal">{row.label}</th>
                  <td className={`px-2 py-1 text-right font-semibold ${verdict ? shade[verdict] : 'text-slate-100'}`} data-testid={`open-plan-${row.key}`}>
                    {text(v, row.digits)}{row.unit && v !== null && <span className="font-normal text-slate-500"> {row.unit}</span>}
                    {verdict && <span className="ml-1 font-normal">({verdict})</span>}
                    {outside && <div className="text-[10px] font-normal text-slate-500">{outside}</div>}
                  </td>
                  <td className="px-2 py-1 text-slate-500">{row.typical}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
        <table className="min-w-[260px] border-collapse text-xs tabular-nums">
          <thead><tr className="text-left text-slate-400"><th className="py-1 pr-3 font-medium">Position</th><th className="px-2 py-1 text-right font-medium">Distance (m)</th><th className="px-2 py-1 text-right font-medium">Speech (dB(A))</th><th className="px-2 py-1 text-right font-medium">STI</th></tr></thead>
          <tbody>
            {op.positions.map((p) => (
              <tr key={p.receiver} className="border-t border-white/5 text-slate-200">
                <th scope="row" className="py-1 pr-3 text-left font-normal">Receiver {p.receiver + 1}</th>
                <td className="px-2 py-1 text-right">{p.distanceM.toFixed(2)}</td>
                <td className="px-2 py-1 text-right">{text(p.speechDbA, 1)}</td>
                <td className="px-2 py-1 text-right">{text(p.sti, 2)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {op.positions.length > 0 && (
        <div className="mt-3 grid gap-3 md:grid-cols-2">
          <div className="h-56">
            <div className="label mb-1 text-[10px]">Speech level against distance (D2,S and Lp,A,S,4m from 2 to 16 m)</div>
            <ResponsiveContainer width="100%" height="90%">
              <ComposedChart margin={{ top: 4, right: 12, bottom: 4, left: 0 }}>
                <CartesianGrid stroke={GRID} />
                <XAxis dataKey="r" type="number" scale="log" domain={[1, top]} ticks={[1, 2, 4, 8, 16, 32].filter((t) => t <= top)} allowDataOverflow {...AXIS} unit=" m" />
                <YAxis {...AXIS} unit=" dB" domain={['auto', 'auto']} />
                <Tooltip {...tooltip} formatter={(v: number) => `${v.toFixed(1)} dB(A)`} labelFormatter={(r) => `${Number(r).toFixed(2)} m`} />
                <ReferenceLine y={45} stroke="#475569" strokeDasharray="4 3" label={{ value: '45 dB(A)', fill: '#64748b', fontSize: 10, position: 'insideTopRight' }} />
                <Scatter data={levels} dataKey="speech" name="Speech" fill="#a78bfa" isAnimationActive={false} />
                <Line data={decayLine} dataKey="fit" name="Line, 2 to 16 m" stroke="#fbbf24" dot={false} isAnimationActive={false} />
              </ComposedChart>
            </ResponsiveContainer>
          </div>
          <div className="h-56">
            <div className="label mb-1 text-[10px]">STI against distance (rD at 0.50, rP at 0.20)</div>
            <ResponsiveContainer width="100%" height="90%">
              <ComposedChart margin={{ top: 4, right: 12, bottom: 4, left: 0 }}>
                <CartesianGrid stroke={GRID} />
                <XAxis dataKey="r" type="number" domain={[0, Math.ceil(top)]} {...AXIS} unit=" m" />
                <YAxis {...AXIS} domain={[0, 1]} />
                <Tooltip {...tooltip} formatter={(v: number) => v.toFixed(2)} labelFormatter={(r) => `${Number(r).toFixed(2)} m`} />
                <ReferenceLine y={0.5} stroke="#475569" strokeDasharray="4 3" />
                <ReferenceLine y={0.2} stroke="#334155" strokeDasharray="2 3" />
                <Scatter data={stis} dataKey="sti" name="STI" fill="#34d399" isAnimationActive={false} />
                <Line data={stiLine} dataKey="fit" name="Line" stroke="#fbbf24" dot={false} isAnimationActive={false} />
              </ComposedChart>
            </ResponsiveContainer>
          </div>
        </div>
      )}
      <p className="mt-2 text-[11px] leading-snug text-slate-500">
        ISO 3382-3 (2012 and 2022): the source radiates normal unisex speech (Table 1, 57.4 dB(A) at 1 m) and each receiver is a workstation on a line away from it.
        The speech at a receiver is that taken there by the room's strength G; the STI uses it, the background noise set in Settings and male weighting, whatever voice Settings has.
        The standard wants at least four positions (6 to 10 preferred), seated at 1.2 m, and two source positions per zone: run each source on its own and compare.
      </p>
    </div>
  );
}

/** The octave bands ISO 14257 measures in. */
const WORKROOM_BANDS = [125, 250, 500, 1000, 2000, 4000];

/**
 * ISO 14257 (workrooms): DL2, DLf and DL'fr of each spatial sound distribution curve in the near, middle and far regions, each position's values, and the curves against distance
 * (log axis) with the free-field reference, as the standard's Figure 1 draws them.
 */
function WorkroomPanel({ w }: { w: Workroom | null | undefined }) {
  const text = (v: number | null, digits: number) => (v === null ? '–' : v.toFixed(digits));
  if (!w) return <div className="grid h-full place-items-center px-6 text-center text-sm text-slate-500">The quick estimate is for one point; press Run simulation with a line of receivers away from one source for the ISO 14257 results.</div>;
  const top = Math.max(16, ...w.positions.map((p) => p.distanceM)) * 1.15;
  const hasSource = w.positions.some((p) => p.sourceDb !== null);
  const points = [...w.positions].sort((a, b) => a.distanceM - b.distanceM).map((p) => ({ r: p.distanceM, normalised: p.normalisedDb, source: p.sourceDb }));
  const reference = [1, top].map((r) => ({ r, reference: -20 * Math.log10(r) - 11 }));
  const regions = w.curves[0]?.regions ?? [];
  return (
    <div className="h-full overflow-auto" data-testid="workroom">
      {w.notWorkedOut && <p className="mb-2 rounded-md border border-amber-400/20 bg-amber-400/5 px-3 py-2 text-xs text-amber-200" data-testid="workroom-why">{w.notWorkedOut}</p>}
      {regionsNote(w) && <p className="mb-2 text-[11px] text-violet-200" data-testid="workroom-regions">{regionsNote(w)}</p>}
      {!w.omnidirectional && <p className="mb-2 text-[11px] text-amber-200">The source has a pattern: ISO 14257 uses an omnidirectional one, so these results are for this source rather than the standard's.</p>}
      {!w.notWorkedOut && regions[1]?.decayPerDoublingDb != null && narrowSpread(w) && <p className="mb-2 text-[11px] text-amber-200" data-testid="workroom-narrow">The receivers {w.d1M} to {w.d2M} m away span less than a doubling of distance, so the middle region's DL2 says little: spread them along a line from the source.</p>}
      <div className="flex flex-wrap gap-4">
        {w.curves.length > 0 && (
          <table className="min-w-[560px] flex-[2] border-collapse text-xs tabular-nums">
            <thead>
              <tr className="text-slate-400">
                <th rowSpan={2} className="py-1 pr-3 text-left align-bottom font-medium">Curve</th>
                {regions.map((g) => (
                  <th key={g.region} colSpan={3} className="border-l border-white/10 px-2 py-1 text-center font-medium" data-testid={`workroom-region-${g.region}`}>
                    {regionLabel(g)}<span className="ml-1 font-normal text-slate-500">{g.positions} {g.positions === 1 ? 'position' : 'positions'}</span>
                  </th>
                ))}
              </tr>
              <tr className="text-right text-slate-400">
                {regions.map((g) => (
                  <Fragment key={g.region}>
                    <th className="border-l border-white/10 px-2 py-1 font-medium text-accent">DL2</th><th className="px-2 py-1 font-medium text-accent">DLf</th>
                    <th className="px-2 py-1 font-medium" title={`The regression line's excess over the free-field curve at ${g.atM} m`}>DL′f {g.atM} m</th>
                  </Fragment>
                ))}
              </tr>
            </thead>
            <tbody>
              {w.curves.map((c) => (
                <tr key={c.curve} className="border-t border-white/5 text-slate-200">
                  <th scope="row" className="py-1 pr-3 text-left font-normal">{curveLabel(c.curve)}</th>
                  {c.regions.map((g) => (
                    <Fragment key={g.region}>
                      <td className="border-l border-white/10 px-2 py-1 text-right font-semibold" data-testid={`workroom-dl2-${g.region}-${c.curve}`}>{text(g.decayPerDoublingDb, 1)}</td>
                      <td className="px-2 py-1 text-right font-semibold" data-testid={`workroom-dlf-${g.region}-${c.curve}`}>{text(g.excessDb, 1)}</td>
                      <td className="px-2 py-1 text-right" data-testid={`workroom-dlfr-${g.region}-${c.curve}`}>{text(g.excessAtDb, 1)}</td>
                    </Fragment>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        )}
        <div className="min-w-[260px] flex-1 text-[11px] leading-snug text-slate-400">
          <div className="label mb-1 text-[10px]">Typical DL2 (ISO 11690)</div>
          <ul className="list-disc space-y-0.5 pl-4">{TYPICAL_DL2.map((t) => <li key={t}>{t}</li>)}</ul>
        </div>
      </div>
      {w.positions.length > 0 && (
        <table className="mt-3 w-full min-w-[640px] border-collapse text-xs tabular-nums">
          <thead>
            <tr className="text-left text-slate-400">
              <th className="py-1 pr-3 font-medium">Position (Lp - LW, dB)</th><th className="px-2 py-1 text-right font-medium">Distance (m)</th>
              {WORKROOM_BANDS.map((hz) => <th key={hz} className="px-2 py-1 text-right font-medium">{bandLabel(hz)}</th>)}
              <th className="px-2 py-1 text-right font-medium">Pink (A)</th>{hasSource && <th className="px-2 py-1 text-right font-medium">Source</th>}<th className="px-2 py-1 text-right font-medium text-slate-500">Free field</th>
            </tr>
          </thead>
          <tbody>
            {w.positions.map((p) => (
              <tr key={p.receiver} className="border-t border-white/5 text-slate-200">
                <th scope="row" className="py-1 pr-3 text-left font-normal">Receiver {p.receiver + 1}</th>
                <td className="px-2 py-1 text-right">{p.distanceM.toFixed(2)}</td>
                {p.bandsDb.map((v, b) => <td key={b} className="px-2 py-1 text-right">{text(v, 1)}</td>)}
                <td className="px-2 py-1 text-right">{text(p.normalisedDb, 1)}</td>{hasSource && <td className="px-2 py-1 text-right">{text(p.sourceDb, 1)}</td>}
                <td className="px-2 py-1 text-right text-slate-500">{p.referenceDb.toFixed(1)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {w.positions.length > 0 && (
        <div className="mt-3 h-56">
          <div className="label mb-1 text-[10px]">Spatial sound distribution curve: Lp - LW against distance, and the free-field curve</div>
          <ResponsiveContainer width="100%" height="90%">
            <ComposedChart margin={{ top: 4, right: 12, bottom: 4, left: 0 }}>
              <CartesianGrid stroke={GRID} />
              <XAxis dataKey="r" type="number" scale="log" domain={[1, top]} ticks={[1, 2, 4, 8, 16, 32, 64].filter((t) => t <= top)} allowDataOverflow {...AXIS} unit=" m" />
              <YAxis {...AXIS} unit=" dB" domain={['auto', 'auto']} />
              <Tooltip {...tooltip} formatter={(v: number) => `${v.toFixed(1)} dB`} labelFormatter={(r) => `${Number(r).toFixed(2)} m`} />
              <ReferenceArea x1={w.d1M} x2={w.d2M} fill="#fbbf24" fillOpacity={0.05} />
              <Line data={reference} dataKey="reference" name="Free field" stroke="#64748b" strokeDasharray="4 3" dot={false} isAnimationActive={false} />
              <Line data={points} dataKey="normalised" name="A-weighted pink noise" stroke="#a78bfa" isAnimationActive={false} />
              {hasSource && <Line data={points} dataKey="source" name="This source" stroke="#34d399" isAnimationActive={false} />}
            </ComposedChart>
          </ResponsiveContainer>
        </div>
      )}
      <p className="mt-2 text-[11px] leading-snug text-slate-500">
        ISO 14257 (2001): each receiver is a position on a line away from the source, and Lp - LW there is the room's strength G less 31 dB in each octave band.
        In each region (near, 1 to {w.d1M} m; middle, {w.d1M} to {w.d2M} m, shaded; far, from {w.d2M} m) DL2 is the fall per doubling of distance of a least-squares line
        through the positions there, DLf their excess over the free-field curve averaged over log distance, and DL′f the line's excess at 4, 10 or 30 m. A region
        needs at least three positions. The standard wants the source at least 3 m from walls and the path clear of large objects by 1.5 m.
      </p>
    </div>
  );
}

/** The ISO 3382 parameters by octave band with their broadband single numbers, and the Speech Transmission Index, for one receiver. */
function IsoTable({ view }: { view: View }) {
  const { sti, broadband, metrics } = view;
  const text = (v: number | null | undefined, digits: number) => (v === null || v === undefined ? '–' : v.toFixed(digits));
  return (
    <div className="h-full overflow-auto" data-testid="iso">
      <div className="mb-3 flex flex-wrap items-center gap-3">
        <div className="rounded-lg border border-white/5 bg-ink-900/70 px-3 py-1.5" data-testid="sti">
          <div className="label text-[10px]">Speech Transmission Index</div>
          <div className="text-lg font-semibold tabular-nums text-slate-100">
            {text(sti.sti, 2)} <span className="text-sm font-normal text-accent">{sti.rating}</span>
            <span className="ml-3 text-sm font-normal text-slate-400" data-testid="alcons">%ALcons {text(sti.alconsPercent, 1)}</span>
            <span className="ml-3 text-sm font-normal text-slate-400" data-testid="stipa" title="STIPA: two modulation frequencies per octave band instead of fourteen">STIPA {text(sti.stipa, 2)}</span>
            <span className="ml-3 text-sm font-normal text-slate-400" data-testid="rasti" title="RaSTI: the 500 Hz and 2 kHz bands only">RaSTI {text(sti.rasti, 2)}</span>
            <span className="ml-3 text-sm font-normal text-slate-400" data-testid="sii"
                  title="Speech Intelligibility Index (ANSI S3.5-1997, octave bands 250 Hz to 8 kHz): the share of the speech's information that is audible, 0 to 1, with the reverberation counted as noise through the modulation transfer (clause 5.2), for listeners of normal hearing">SII {text(sti.sii ?? null, 2)}</span>
          </div>
        </div>
        <p className="min-w-0 flex-1 text-[11px] leading-snug text-slate-500">
          At the receiver, for {sti.signalFrom === 'sources' ? <>the sources' own levels and spectra ({text(sti.signalDbA ?? null, 1)} dB(A) of speech there)</> : <>a talker at {sti.speechLevelDbA} dBA at 1 m</>},
          {' '}{sti.voice} weighting and {sti.backgroundNc === 0 ? 'no background noise' : `NC ${sti.backgroundNc} background noise`}
          {' '}(IEC 60268-16, from the room's modulation transfer in each octave band). Change these in Settings.
        </p>
      </div>
      <table className="w-full min-w-[640px] border-collapse text-xs tabular-nums">
        <thead>
          <tr className="text-left text-slate-400">
            <th className="py-1 pr-3 font-medium">Parameter</th>
            {BANDS_HZ.map((hz) => <th key={hz} className="px-2 py-1 text-right font-medium">{bandLabel(hz)}</th>)}
            <th className="border-l border-white/10 px-2 py-1 text-right font-medium text-accent" title="The single-number value: the mean of the octave bands ISO 3382-1 averages it over">Broadband</th>
            <th className="px-2 py-1 text-left font-medium text-slate-500">Mean of (Hz)</th>
          </tr>
        </thead>
        <tbody>
          {ISO_ROWS.map((row) => (
            <tr key={row.key} className="border-t border-white/5 text-slate-200">
              <th scope="row" className="py-1 pr-3 text-left font-normal">{row.label}{row.unit && <span className="text-slate-500"> ({row.unit})</span>}</th>
              {BANDS_HZ.map((hz, b) => <td key={hz} className="px-2 py-1 text-right">{text((metrics as Record<string, Band | undefined>)[row.key]?.[b] ?? null, row.digits)}</td>)}
              <td className="border-l border-white/10 px-2 py-1 text-right font-semibold text-slate-100" data-testid={`broadband-${row.key}`}>{text(broadband[row.key], row.digits)}</td>
              <td className="px-2 py-1 text-slate-500">{row.over}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="mt-2 text-[11px] leading-snug text-slate-500">
        A band that cannot be worked out is left out of the mean. The lateral and stage-support values need the directions the sound arrives from, so they come from Run simulation only; the quick estimate gives –.
      </p>
    </div>
  );
}

const AXIS = { stroke: '#64748b', fontSize: 11 } as const;
const GRID = '#232329';
const tooltip = { contentStyle: { background: '#0f0f12', border: '1px solid rgba(255,255,255,0.08)', borderRadius: 8, fontSize: 12 }, labelStyle: { color: '#94a3b8' }, itemStyle: { padding: 0 } };

const bandRows = (series: Record<string, Band>) =>
  BANDS_HZ.map((hz, i) => ({ band: bandLabel(hz), ...Object.fromEntries(Object.entries(series).map(([k, v]) => [k, v[i]])) }));

/** The mean of the 500 Hz and 1 kHz bands, the usual single-figure value for a room (null if neither exists). */
const mid = (band: Band) => {
  const v = [band[3], band[4]].filter((x): x is number => x !== null);
  return v.length ? v.reduce((a, b) => a + b, 0) / v.length : null;
};

function Stat({ label, value, unit, digits = 2 }: { label: string; value: number | null; unit: string; digits?: number }) {
  return (
    <div className="rounded-lg border border-white/5 bg-ink-900/70 px-2.5 py-1">
      <div className="label text-[10px]">{label}</div>
      <div className="text-base font-semibold tabular-nums text-slate-100">
        {value === null ? '–' : value.toFixed(digits)} <span className="text-xs font-normal text-slate-500">{unit}</span>
      </div>
    </div>
  );
}

/** The colour scale beside a map, with a mark at each contour level on it. */
function Scale({ range, unit, levels, step }: { range: [number, number]; unit: string; levels: number[]; step: number | null }) {
  return (
    <div className="flex h-full shrink-0 flex-col items-center py-2 text-[11px] text-slate-400" data-testid="map-scale">
      <span>{range[1].toFixed(2)}</span>
      {/* drawn as an SVG so that a picture of the map takes it too */}
      <svg className="my-1 min-h-0 w-3 flex-1" viewBox="0 0 12 100" preserveAspectRatio="none" aria-hidden="true">
        <defs>
          <linearGradient id="map-scale-gradient" x1="0" y1="1" x2="0" y2="0">
            {[0, 0.25, 0.5, 0.75, 1].map((t) => <stop key={t} offset={t} stopColor={rainbow(t)} />)}
          </linearGradient>
        </defs>
        <rect width="12" height="100" fill="url(#map-scale-gradient)" />
        {levels.filter((l) => l >= range[0] && l <= range[1]).map((l) => {
          const y = (100 * (range[1] - l)) / (range[1] - range[0]);
          return <line key={l} x1={0} x2={12} y1={y} y2={y} stroke="#020617" strokeWidth={1} vectorEffect="non-scaling-stroke" />;
        })}
      </svg>
      <span>{range[0].toFixed(2)}</span>
      <span className="mt-1 text-slate-500">{unit}</span>
      {step !== null && <span className="mt-1 max-w-[4.5rem] text-center text-slate-500">{levels.length ? `lines every ${step}` : `a step of ${step} gives over ${MAX_CONTOURS} lines`}</span>}
    </div>
  );
}

/** Contour lines drawn over a flat map, each level's value written once on its line; `place` gives where a point of the room goes in the picture. */
function ContourLines({ segments, place, width, decimals }: { segments: Segment[]; place: (p: Point) => [number, number]; width: number; decimals: number }) {
  if (segments.length === 0) return null;
  const d = segments.map(([p, q]) => { const [x1, y1] = place(p), [x2, y2] = place(q); return `M${x1.toFixed(3)} ${y1.toFixed(3)}L${x2.toFixed(3)} ${y2.toFixed(3)}`; }).join('');
  return (
    <g pointerEvents="none">
      <path data-testid="map-contours" d={d} fill="none" stroke="#0b0b0e" strokeWidth={width} strokeLinecap="round" />
      {contourLabels(segments, width * 9 * 2.5).map(({ at, level }) => {
        const [x, y] = place(at);
        // a light halo round dark figures, so they read over any colour and across the line
        return <text key={level} data-testid="contour-label" x={x} y={y} fontSize={width * 9} textAnchor="middle" dominantBaseline="central" fill="#0b0b0e"
                     stroke="#f8fafc" strokeWidth={width * 2.5} paintOrder="stroke" fontWeight={600}>{level.toFixed(decimals)}</text>;
      })}
    </g>
  );
}

/** The lowest, mean and highest value on a map, and the band it is for. */
function MapStats({ map, values }: { map: MapResult; values: number[] }) {
  if (values.length === 0) return null;
  const mean = values.reduce((x, y) => x + y, 0) / values.length;
  const band = map.band === null ? 'broadband' : `${bandLabel(BANDS_HZ[map.band] ?? 0)} Hz`;
  return (
    <div className="pointer-events-none absolute left-3 top-2 z-10 rounded bg-black/40 px-2 py-1 text-[11px] tabular-nums text-slate-300 backdrop-blur" data-testid="map-stats">
      {map.metric} · {band} · min {Math.min(...values).toFixed(2)} · mean {mean.toFixed(2)} · max {Math.max(...values).toFixed(2)} {map.unit}
    </div>
  );
}

/** Where an SVG click landed, in the SVG's own units. */
const svgPoint = (e: React.MouseEvent<SVGSVGElement>) => {
  const matrix = e.currentTarget.getScreenCTM();
  return matrix ? new DOMPoint(e.clientX, e.clientY).matrixTransform(matrix.inverse()) : null;
};
const tenth = (v: number) => Math.round(v * 10) / 10;

type Marks = { source: Point; receiver: Point; extras: Point[] };

/** A plan (or one height of a height matrix) as a flat picture: one square per grid point. Click anywhere to put the receiver there. */
function PlanMap({ map, points, frame, marks, height, onReceiver, range, contours, decimals }: {
  map: MapResult; points: MapPoint[]; frame: Frame; marks: Marks; height: number; onReceiver: (p: Point) => void; range: [number, number] | null; contours: Segment[]; decimals: number;
}) {
  const s = size(frame);
  const pad = map.spacingM / 2;
  const w = s.x, h = s.y;
  const marker = Math.max(w, h) * 0.014;
  const place = (e: React.MouseEvent<SVGSVGElement>) => {
    const at = svgPoint(e);
    if (!at || at.x < 0 || at.x > w || at.y < 0 || at.y > h) return; // outside the room
    const x = tenth(at.x + frame.min.x), y = tenth(h - at.y + frame.min.y);
    // above a surface the receiver goes at the height of the nearest point of the map, which follows the surface
    const nearest = map.mode === 'surface' ? points.reduce<MapPoint | null>((best, p) => (!best || Math.hypot(p.x - x, p.y - y) < Math.hypot(best.x - x, best.y - y) ? p : best), null) : null;
    onReceiver(clampTo({ x, y, z: nearest ? tenth(nearest.z) : height }, frame));
  };
  const dot = (p: Point, fill: string, key: string) => <circle key={key} cx={p.x - frame.min.x} cy={h - (p.y - frame.min.y)} r={marker} fill={fill} stroke="#0b0b0e" strokeWidth={marker * 0.25} />;
  return (
    <svg viewBox={`${-pad} ${-pad} ${w + 2 * pad} ${h + 2 * pad}`} className="h-full min-w-0 flex-1 cursor-crosshair" preserveAspectRatio="xMidYMid meet" role="img" aria-label="Plan map" onClick={place}>
      <rect x={0} y={0} width={w} height={h} fill="#141417" stroke="#334155" strokeWidth={0.05} />
      {points.map((p, i) => p.value === null || !range ? null : (
        <rect key={i} x={p.x - frame.min.x - map.spacingM * 0.46} y={h - (p.y - frame.min.y) - map.spacingM * 0.46} width={map.spacingM * 0.92} height={map.spacingM * 0.92}
              fill={rainbow((p.value - range[0]) / (range[1] - range[0]))} opacity={0.9}>
          <title>{`x ${p.x.toFixed(1)}, y ${p.y.toFixed(1)}${map.mode === 'surface' ? `, z ${p.z.toFixed(1)}` : ''}: ${p.value.toFixed(2)} ${map.unit}`}</title>
        </rect>
      ))}
      <ContourLines segments={contours} place={(p) => [p.x - frame.min.x, h - (p.y - frame.min.y)]} width={Math.max(w, h) * 0.003} decimals={decimals} />
      <g pointerEvents="none">
        {marks.extras.map((p, i) => dot(p, '#f472b6', `x${i}`))}
        {dot(marks.source, '#34d399', 'source')}
        {dot(marks.receiver, '#22d3ee', 'receiver')}
      </g>
    </svg>
  );
}

/** A vertical section: the room's profile in the cutting plane, with a square for each grid point in it. Click to put the receiver at that point of the plane. */
function SectionMap({ map, points, frame, marks, onReceiver, range, contours, decimals }: {
  map: MapResult; points: MapPoint[]; frame: Frame; marks: Marks; onReceiver: (p: Point) => void; range: [number, number] | null; contours: Segment[]; decimals: number;
}) {
  const alongX = map.axis === 'y'; // a cut at constant y runs along x
  const uMin = alongX ? frame.min.x : frame.min.y, uMax = alongX ? frame.max.x : frame.max.y;
  const zMin = frame.min.z, zMax = frame.max.z;
  const w = uMax - uMin, h = zMax - zMin;
  const pad = map.spacingM / 2;
  const u = (p: Point) => (alongX ? p.x : p.y);
  const marker = Math.max(w, h) * 0.014;
  const place = (e: React.MouseEvent<SVGSVGElement>) => {
    const at = svgPoint(e);
    if (!at || at.x < 0 || at.x > w || at.y < 0 || at.y > h) return;
    const inPlane = tenth(at.x + uMin), z = tenth(zMax - at.y), fixed = map.positionM ?? 0;
    onReceiver(clampTo(alongX ? { x: inPlane, y: fixed, z } : { x: fixed, y: inPlane, z }, frame));
  };
  const near = (p: Point) => Math.abs((alongX ? p.y : p.x) - (map.positionM ?? 0)) < 0.5; // a source this close to the plane is drawn on it
  const dot = (p: Point, fill: string, key: string) => near(p) ? <circle key={key} cx={u(p) - uMin} cy={zMax - p.z} r={marker} fill={fill} stroke="#0b0b0e" strokeWidth={marker * 0.25} /> : null;
  return (
    <svg viewBox={`${-pad} ${-pad} ${w + 2 * pad} ${h + 2 * pad}`} className="h-full min-w-0 flex-1 cursor-crosshair" preserveAspectRatio="xMidYMid meet" role="img" aria-label="Section map" onClick={place}>
      <rect x={0} y={0} width={w} height={h} fill="#141417" />
      {points.map((p, i) => p.value === null || !range ? null : (
        <rect key={i} x={u(p) - uMin - map.spacingM * 0.46} y={zMax - p.z - map.spacingM * 0.46} width={map.spacingM * 0.92} height={map.spacingM * 0.92}
              fill={rainbow((p.value - range[0]) / (range[1] - range[0]))} opacity={0.9}>
          <title>{`${alongX ? 'x' : 'y'} ${u(p).toFixed(1)}, z ${p.z.toFixed(1)}: ${p.value.toFixed(2)} ${map.unit}`}</title>
        </rect>
      ))}
      <ContourLines segments={contours} place={(p) => [u(p) - uMin, zMax - p.z]} width={Math.max(w, h) * 0.003} decimals={decimals} />
      {(map.outline ?? []).map(([u1, v1, u2, v2], i) => (
        <line key={i} x1={u1 - uMin} y1={zMax - v1} x2={u2 - uMin} y2={zMax - v2} stroke="#c4b5fd" strokeWidth={0.06} />
      ))}
      <g pointerEvents="none">
        {marks.extras.map((p, i) => dot(p, '#f472b6', `x${i}`))}
        {dot(marks.source, '#34d399', 'source')}
        {dot(marks.receiver, '#22d3ee', 'receiver')}
      </g>
    </svg>
  );
}

/** A map of any kind, with its scale and its figures. A height matrix is shown one height at a time. */
export function MapView({ map, frame, source, receiver, extras, onReceiver }: { map: MapResult; frame: Frame; source: Point; receiver: Point; extras: Point[]; onReceiver: (p: Point) => void }) {
  const [height, setHeight] = useState(0);
  const heights = map.heights ?? [];
  const chosen = map.mode === 'volume' ? heights[Math.min(height, heights.length - 1)] : undefined;
  const points = chosen === undefined ? map.points : map.points.filter((p) => Math.abs(p.z - chosen) < 0.05);
  const values = points.filter((p) => p.value !== null).map((p) => p.value as number);
  // one scale for the whole matrix, so the heights can be compared by eye
  const range = mapRange(map.points.filter((p) => p.value !== null).map((p) => p.value as number), map.unit, map.scale);
  const step = map.scale?.contourStep ?? null;
  const levels = step !== null && values.length ? contourLevels(Math.min(...values), Math.max(...values), step) : [];
  // the lines of the layer shown, in the two directions its grid runs in
  const contours = contourSegments(points, map.spacingM, map.mode !== 'section' ? ['x', 'y'] : map.axis === 'x' ? ['y', 'z'] : ['x', 'z'], levels);
  const marks: Marks = { source, receiver, extras };
  return (
    <div className="relative flex h-full flex-col gap-1">
      <MapStats map={map} values={values} />
      {map.mode === 'volume' && (
        <div className="z-10 ml-auto flex flex-wrap items-center justify-end gap-1.5 text-[11px] text-slate-400" role="group" aria-label="Height shown">
          Height
          {heights.map((hz, i) => <button key={hz} type="button" aria-pressed={i === height} onClick={() => setHeight(i)} className={`chip ${i === height ? 'chip-on' : ''}`}>{hz.toFixed(1)} m</button>)}
        </div>
      )}
      <div className="flex min-h-0 flex-1 items-center gap-4 pt-5">
        {map.mode === 'section'
          ? <SectionMap map={map} points={points} frame={frame} marks={marks} onReceiver={onReceiver} range={range} contours={contours} decimals={step ? stepDecimals(step) : 0} />
          : <PlanMap map={map} points={points} frame={frame} marks={marks} height={chosen ?? receiver.z} onReceiver={onReceiver} range={range} contours={contours} decimals={step ? stepDecimals(step) : 0} />}
        {range && <Scale range={range} unit={map.unit} levels={levels} step={step} />}
      </div>
    </div>
  );
}

export default function Charts({ result, bandIndex, overall, busy, listen, reflections, thirdOctaves, report, mapRaw, reflectionList }: {
  result: SimulateResponse | null; bandIndex: number; overall: boolean; busy: boolean; listen: ReactNode; reflections: ReactNode; thirdOctaves: ReactNode; report: ReportInfo;
  /** the map and the reflection list as last worked out, for the workbook */
  mapRaw: MapRaw | null; reflectionList: ReflectionsResult | null;
}) {
  const [tab, setTab] = useState<Tab>('rt');
  const [blocked, setBlocked] = useState(false);
  const [chosen, setChosen] = useState(0);
  const shown = useRef<HTMLDivElement>(null);
  const [pictured, setPictured] = useState<string | null>(null);
  // a measured response loaded on the Measured tab, kept while other tabs are shown
  const [measured, setMeasured] = useState<Measured | null>(null);
  // every tab that shows one receiver's results shows the one chosen here; the first is the main receiver
  const receivers = result?.receivers ?? [];
  const at = receivers.length ? receivers[Math.min(chosen, receivers.length - 1)] : undefined;
  const view = useMemo<View | null>(() => result && ({
    metrics: at?.metrics ?? result.metrics, broadband: at?.broadband ?? result.broadband, sti: at?.sti ?? result.sti,
    decay: at?.decay ?? result.decay, impulse: at?.impulse ?? result.impulse,
  }), [result, at]);
  const perReceiver = tab !== 'receivers' && tab !== 'office' && tab !== 'workroom' && tab !== 'listen' && tab !== 'reflections' && tab !== 'thirds' && receivers.length > 1;

  const rt = useMemo(() => view && bandRows({ T30: view.metrics.t30S, EDT: view.metrics.edtS, Sabine: view.metrics.sabineT60S, Eyring: view.metrics.eyringT60S,
    Millington: view.metrics.millingtonT60S, 'Arau-Puchades': view.metrics.arauPuchadesT60S, Fitzroy: view.metrics.fitzroyT60S }), [view]);
  const clarity = useMemo(() => view && bandRows({ C80: view.metrics.c80Db, C50: view.metrics.c50Db }), [view]);
  const response = useMemo(() => view && bandRows({ G: view.metrics.strengthGDb }), [view]);
  const decay = useMemo(() => {
    if (!view) return null;
    const n = view.decay.schroederDb[0].length;
    return Array.from({ length: n }, (_, i) => ({ t: +(i * view.decay.stepS).toFixed(3), overall: view.decay.overallSchroederDb[i], ...Object.fromEntries(BANDS_HZ.map((hz, b) => [`b${hz}`, view.decay.schroederDb[b][i]])) }));
  }, [view]);
  const impulse = useMemo(() => {
    if (!view) return null;
    const levels = overall ? view.impulse.overallDb : view.impulse.levelDb[bandIndex];
    return levels.map((v, i) => ({ t: +(i * view.impulse.stepS).toFixed(3), level: v }));
  }, [view, bandIndex, overall]);
  const impulseTop = useMemo(() => Math.max(...(impulse ?? []).map((p) => p.level ?? -200), -200), [impulse]);

  return (
    <div className="flex h-full flex-col">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-white/5 px-3 py-1.5">
        <div className="flex min-w-0 max-w-full gap-1 overflow-x-auto [scrollbar-width:none]">
          {TABS.map((t) => (
            <button key={t.key} onClick={() => setTab(t.key)} className={`shrink-0 whitespace-nowrap rounded-md px-3 py-1.5 text-xs font-medium transition ${tab === t.key ? 'bg-accent/15 text-accent' : 'text-slate-400 hover:bg-white/5 hover:text-slate-200'}`}>{t.label}</button>
          ))}
        </div>
        <div className="ml-auto flex items-center gap-2">
          {result && (
            <div role="group" aria-label="Export results" className="flex items-center gap-1">
              <button type="button" className="chip" title="Every receiver's results by octave band and broadband, as a CSV file for a spreadsheet"
                      onClick={() => download('results.csv', resultsCsv(result, report.sources), 'text/csv')}>Results CSV</button>
              <button type="button" className="chip" title="The same results, the sources with their power spectra and the setup, as an Excel workbook with a sheet for each"
                      onClick={() => download('results.xlsx', resultsXlsx(result, report, { map: mapRaw, reflections: reflectionList }), 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet')}>Results XLSX</button>
              <button type="button" className="chip" title="A printable page with the setup and every receiver's results"
                      onClick={() => setBlocked(!openReport(reportHtml(result, report)))}>Report</button>
              {CHART_TABS.includes(tab) && (
                <button type="button" className="chip" title="The chart (or charts) of this tab as a PNG picture, for a report"
                        onClick={async () => {
                          const label = TABS.find((t) => t.key === tab)!.label;
                          const title = `${label}${perReceiver ? ` · Receiver ${Math.min(chosen, receivers.length - 1) + 1}` : ''}`;
                          const png = shown.current && await snapshotPng(shown.current, title);
                          setPictured(png ? null : 'There is no chart to save.');
                          if (png) download(`${tab}.png`, png, 'image/png');
                        }}>Chart PNG</button>
              )}
              {pictured && <span className="text-[11px] text-rose-300">{pictured}</span>}
              {blocked && <span className="text-[11px] text-rose-300">The browser blocked the report window: allow pop-ups for this page.</span>}
            </div>
          )}
          <Stat label="T30 (500–1k)" value={view ? mid(view.metrics.t30S) : null} unit="s" />
          <Stat label="EDT" value={view ? mid(view.metrics.edtS) : null} unit="s" />
          <Stat label="C80" value={view ? mid(view.metrics.c80Db) : null} unit="dB" digits={1} />
          <Stat label="G" value={view ? mid(view.metrics.strengthGDb) : null} unit="dB" digits={1} />
          <Stat label="STI" value={view ? view.sti.sti : null} unit={view ? view.sti.rating : ''} />
        </div>
      </div>

      <div className="flex min-h-0 flex-1 flex-col p-3">
      {perReceiver && (
        <div role="group" aria-label="Receiver shown" className="mb-2 flex flex-wrap items-center gap-1.5">
          {receivers.map((_, i) => <button key={i} type="button" aria-pressed={Math.min(chosen, receivers.length - 1) === i} onClick={() => setChosen(i)} className={`chip ${Math.min(chosen, receivers.length - 1) === i ? 'chip-on' : ''}`}>Receiver {i + 1}</button>)}
        </div>
      )}
      <div ref={shown} className="relative min-h-0 flex-1">
        {tab === 'listen' && listen}
        {tab === 'reflections' && reflections}
        {tab === 'thirds' && thirdOctaves}
        {tab === 'measured' && <MeasuredPanel measured={measured} onMeasured={setMeasured} simulated={view?.metrics ?? null} />}
        {tab !== 'listen' && tab !== 'reflections' && tab !== 'measured' && tab !== 'thirds' && !result && <div className="absolute inset-0 grid place-items-center text-sm text-slate-500">{busy ? 'Calculating…' : 'Press Run simulation to calculate the room.'}</div>}
        {busy && result && <div className="absolute right-4 top-3 z-10 text-xs text-accent">Calculating…</div>}
        {tab === 'iso' && view && <IsoTable view={view} />}
        {tab === 'receivers' && result && <ReceiversTable result={result} />}
        {tab === 'office' && result && <OpenPlanPanel op={result.openPlan} />}
        {tab === 'workroom' && result && <WorkroomPanel w={result.workroom} />}
        {tab !== 'listen' && tab !== 'reflections' && tab !== 'measured' && tab !== 'thirds' && tab !== 'iso' && tab !== 'receivers' && tab !== 'office' && tab !== 'workroom' && result && (
          <div className="flex h-full flex-col">
          <div className="min-h-0 flex-1">
          <ResponsiveContainer width="100%" height="100%">
            {tab === 'rt' ? (
              <LineChart data={rt!}>
                <CartesianGrid stroke={GRID} vertical={false} />
                <XAxis dataKey="band" {...AXIS} />
                <YAxis {...AXIS} unit=" s" />
                <Tooltip {...tooltip} formatter={(v: number) => (v === null ? '–' : `${v.toFixed(2)} s`)} />
                <Legend wrapperStyle={{ fontSize: 11 }} verticalAlign="top" height={24} />
                <Line type="monotone" dataKey="T30" stroke="#a78bfa" strokeWidth={2.5} dot={{ r: 3 }} />
                <Line type="monotone" dataKey="EDT" stroke="#e2e8f0" strokeWidth={2} dot={{ r: 2 }} />
                <Line type="monotone" dataKey="Sabine" stroke="#94a3b8" strokeDasharray="5 4" dot={false} />
                <Line type="monotone" dataKey="Eyring" stroke="#fbbf24" strokeDasharray="5 4" dot={false} />
                <Line type="monotone" dataKey="Millington" stroke="#34d399" strokeDasharray="2 3" dot={false} />
                <Line type="monotone" dataKey="Arau-Puchades" stroke="#f472b6" strokeDasharray="2 3" dot={false} />
                <Line type="monotone" dataKey="Fitzroy" stroke="#f87171" strokeDasharray="2 3" dot={false} />
              </LineChart>
            ) : tab === 'clarity' ? (
              <BarChart data={clarity!}>
                <CartesianGrid stroke={GRID} vertical={false} />
                <XAxis dataKey="band" {...AXIS} />
                <YAxis {...AXIS} unit=" dB" />
                <Tooltip {...tooltip} cursor={{ fill: 'rgba(255,255,255,0.03)' }} formatter={(v: number) => (v === null ? '–' : `${v.toFixed(1)} dB`)} />
                <Legend wrapperStyle={{ fontSize: 11 }} verticalAlign="top" height={24} />
                <ReferenceLine y={0} stroke="#475569" />
                <Bar dataKey="C80" fill="#a78bfa" radius={[3, 3, 0, 0]} />
                <Bar dataKey="C50" fill="#34d399" radius={[3, 3, 0, 0]} />
              </BarChart>
            ) : tab === 'response' ? (
              <AreaChart data={response!}>
                <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="#a78bfa" stopOpacity={0.5} /><stop offset="100%" stopColor="#a78bfa" stopOpacity={0.02} /></linearGradient></defs>
                <CartesianGrid stroke={GRID} vertical={false} />
                <XAxis dataKey="band" {...AXIS} />
                <YAxis {...AXIS} unit=" dB" />
                <Tooltip {...tooltip} formatter={(v: number) => (v === null ? '–' : `${v.toFixed(1)} dB`)} />
                <Area type="monotone" dataKey="G" name="Strength G" stroke="#a78bfa" strokeWidth={2.5} fill="url(#g)" />
              </AreaChart>
            ) : tab === 'decay' ? (
              <LineChart data={decay!}>
                <CartesianGrid stroke={GRID} vertical={false} />
                <XAxis dataKey="t" type="number" domain={[0, 'dataMax']} {...AXIS} unit=" s" />
                <YAxis {...AXIS} unit=" dB" domain={[-60, 0]} allowDataOverflow />
                <Tooltip {...tooltip} labelFormatter={(t) => `${t} s`} formatter={(v: number) => (v === null ? '–' : `${v.toFixed(1)} dB`)} />
                {BANDS_HZ.map((hz, b) => (
                  <Line key={hz} type="monotone" dataKey={`b${hz}`} name={`${bandLabel(hz)} Hz`} dot={false} isAnimationActive={false}
                        stroke={!overall && b === bandIndex ? '#a78bfa' : '#475569'} strokeWidth={!overall && b === bandIndex ? 2.5 : 1} strokeOpacity={!overall && b === bandIndex ? 1 : 0.5} />
                ))}
                {overall && <Line type="monotone" dataKey="overall" name="Overall" dot={false} isAnimationActive={false} stroke="#a78bfa" strokeWidth={2.5} />}
              </LineChart>
            ) : (
              <LineChart data={impulse!}>
                <CartesianGrid stroke={GRID} vertical={false} />
                <XAxis dataKey="t" type="number" domain={[0, 'dataMax']} {...AXIS} unit=" s" />
                <YAxis {...AXIS} unit=" dB" domain={[Math.ceil(impulseTop / 10) * 10 - 80, Math.ceil(impulseTop / 10) * 10]} tickCount={5} allowDataOverflow />
                <Tooltip {...tooltip} labelFormatter={(t) => `${t} s`} formatter={(v: number) => (v === null ? '–' : `${v.toFixed(1)} dB`)} />
                <Line type="linear" dataKey="level" name={overall ? 'Overall' : `${bandLabel(BANDS_HZ[bandIndex])} Hz`} stroke="#34d399" strokeWidth={1.25} dot={false} isAnimationActive={false} connectNulls={false} />
              </LineChart>
            )}
          </ResponsiveContainer>
          </div>
          {tab === 'rt' && (
            <p className="mt-1 text-[11px] leading-snug text-slate-500" data-testid="rt-note">
              The dotted lines are estimates from the room's absorption alone. Fitzroy is the arithmetic mean of the three face pairs' times, so one hard pair (bare side walls, say) pulls it far above the rest;
              Millington runs short when some surfaces absorb strongly. The simulated T30 and EDT are the reference.
            </p>
          )}
          </div>
        )}
      </div>
      </div>
    </div>
  );
}
