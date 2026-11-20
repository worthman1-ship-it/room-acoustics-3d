import { useState } from 'react';
import { api } from '../api';
import type { Band, Metrics, Measured } from '../types';
import { BANDS_HZ, bandLabel } from '../types';

/**
 * The parameters compared, each with its just-noticeable difference (ISO 3382-1 Annex A): 5 % of the measured value for the decay times, 1 dB for the
 * clarities, 0.05 for D50 and 10 ms for the centre time.
 */
const ROWS: { key: keyof Measured & keyof Metrics; label: string; unit: string; digits: number; jnd: (measured: number) => number }[] = [
  { key: 't30S', label: 'T30', unit: 's', digits: 2, jnd: (m) => 0.05 * m },
  { key: 't20S', label: 'T20', unit: 's', digits: 2, jnd: (m) => 0.05 * m },
  { key: 'edtS', label: 'EDT', unit: 's', digits: 2, jnd: (m) => 0.05 * m },
  { key: 'c80Db', label: 'C80', unit: 'dB', digits: 1, jnd: () => 1 },
  { key: 'c50Db', label: 'C50', unit: 'dB', digits: 1, jnd: () => 1 },
  { key: 'd50', label: 'D50', unit: '', digits: 2, jnd: () => 0.05 },
  { key: 'centreTimeS', label: 'Ts', unit: 's', digits: 3, jnd: () => 0.01 },
];

const text = (v: number | null | undefined, digits: number) => (v === null || v === undefined ? '–' : v.toFixed(digits));

/** Simulated less measured in JNDs, per band; null where either is missing. */
const differences = (row: (typeof ROWS)[number], measured: Band, simulated: Band | undefined) =>
  measured.map((m, b) => {
    const s = simulated?.[b];
    return m === null || s === null || s === undefined || !(row.jnd(m) > 0) ? null : (s - m) / row.jnd(m);
  });

/**
 * A measured impulse response (a WAV file) analysed as the simulation is, set beside the simulated receiver chosen: each parameter per octave band,
 * measured, simulated and the difference in just-noticeable differences, shaded where it is more than one.
 */
export default function MeasuredPanel({ measured, onMeasured, simulated }: {
  measured: Measured | null; onMeasured: (m: Measured | null) => void; simulated: Metrics | null;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const load = async (file: File) => {
    setBusy(true); setError(null);
    try { onMeasured(await api.analyseMeasured(file)); } catch (x) { setError((x as Error).message); } finally { setBusy(false); }
  };

  return (
    <div className="h-full overflow-auto" data-testid="measured">
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <label className="chip cursor-pointer">
          {busy ? 'Analysing…' : measured ? 'Load another WAV' : 'Load measured response (WAV)'}
          <input type="file" accept=".wav,audio/wav" className="hidden" aria-label="Measured impulse response (WAV)" disabled={busy}
                 onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ''; if (f) void load(f); }} />
        </label>
        {measured && <button type="button" className="chip" onClick={() => onMeasured(null)}>Clear</button>}
        {measured && (
          <span className="text-[11px] text-slate-400" data-testid="measured-info">
            {measured.name} · {(measured.sampleRateHz / 1000).toFixed(1)} kHz{measured.channels > 1 ? `, ${measured.channels} channels (the first analysed)` : ''}
            {' '}· {measured.durationS.toFixed(2)} s from the direct sound, which comes {(measured.directArrivalS * 1000).toFixed(0)} ms in
          </span>
        )}
        {error && <span className="text-[11px] text-rose-300" role="alert">{error}</span>}
      </div>

      {!measured ? (
        <p className="max-w-2xl text-[11px] leading-snug text-slate-500">
          Load an impulse response measured in the room (a WAV file, as a measurement system saves it, at 16 kHz or more) to see its ISO 3382-1 parameters in each octave band,
          worked out as the simulation's are, beside the simulated receiver's. Put the receiver and source where the microphone and loudspeaker stood.
        </p>
      ) : (
        <>
          <table className="w-full min-w-[680px] border-collapse text-xs tabular-nums">
            <thead>
              <tr className="text-left text-slate-400">
                <th className="py-1 pr-3 font-medium">Parameter</th>
                <th className="py-1 pr-3 font-medium" />
                {BANDS_HZ.map((hz) => <th key={hz} className="px-2 py-1 text-right font-medium">{bandLabel(hz)}</th>)}
                <th className="border-l border-white/10 px-2 py-1 text-right font-medium text-accent" title="The mean of the bands' differences, ignoring their sign">Mean |Δ|</th>
              </tr>
            </thead>
            <tbody>
              <tr className="border-t border-white/5 text-slate-400">
                <th scope="row" className="py-1 pr-3 text-left font-normal">Dynamic range</th>
                <td className="py-1 pr-3 text-slate-500">dB</td>
                {measured.dynamicRangeDb.map((v, b) => <td key={b} className="px-2 py-1 text-right">{v === null ? '∞' : v === 0 ? 'unusable' : v.toFixed(0)}</td>)}
                <td className="border-l border-white/10" />
              </tr>
              {ROWS.map((row) => {
                const sim = simulated?.[row.key] as Band | undefined;
                const d = differences(row, measured[row.key] as Band, sim);
                const shown = d.filter((x): x is number => x !== null);
                const mean = shown.length ? shown.reduce((a, x) => a + Math.abs(x), 0) / shown.length : null;
                return [
                  <tr key={`${row.key}-m`} className="border-t border-white/10 text-slate-200" data-testid={`measured-${row.key}`}>
                    <th scope="row" rowSpan={simulated ? 3 : 1} className="py-1 pr-3 text-left align-top font-normal">{row.label}{row.unit && <span className="text-slate-500"> ({row.unit})</span>}</th>
                    <td className="py-1 pr-3 text-slate-500">Measured</td>
                    {(measured[row.key] as Band).map((v, b) => <td key={b} className="px-2 py-1 text-right">{text(v, row.digits)}</td>)}
                    <td className="border-l border-white/10" />
                  </tr>,
                  simulated && (
                    <tr key={`${row.key}-s`} className="text-slate-300">
                      <td className="py-1 pr-3 text-slate-500">Simulated</td>
                      {BANDS_HZ.map((_, b) => <td key={b} className="px-2 py-1 text-right">{text(sim?.[b], row.digits)}</td>)}
                      <td className="border-l border-white/10" />
                    </tr>
                  ),
                  simulated && (
                    <tr key={`${row.key}-d`} className="text-slate-400" data-testid={`difference-${row.key}`}>
                      <td className="py-1 pr-3 text-slate-500">Δ (JND)</td>
                      {d.map((v, b) => (
                        <td key={b} className={`px-2 py-1 text-right ${v !== null && Math.abs(v) > 1 ? 'bg-rose-500/15 text-rose-200' : ''}`}>
                          {v === null ? '–' : `${v > 0 ? '+' : ''}${v.toFixed(1)}`}
                        </td>
                      ))}
                      <td className={`border-l border-white/10 px-2 py-1 text-right font-semibold ${mean !== null && mean > 1 ? 'text-rose-200' : 'text-slate-100'}`}>{text(mean, 1)}</td>
                    </tr>
                  ),
                ];
              })}
            </tbody>
          </table>
          <p className="mt-2 text-[11px] leading-snug text-slate-500">
            {simulated ? 'Δ is simulated less measured, in just-noticeable differences (ISO 3382-1 Annex A): 5 % for the decay times, 1 dB for C80 and C50, 0.05 for D50, 10 ms for Ts; shaded where it is more than one. '
              : 'Press Run simulation to set the simulated receiver beside it. '}
            Time zero is the direct sound (the recording's loudest sample). A decay time needs 10 dB of dynamic range more than it is fitted over (EDT 20 dB, T20 35, T30 45), so a band short of that has –.
          </p>
        </>
      )}
    </div>
  );
}
