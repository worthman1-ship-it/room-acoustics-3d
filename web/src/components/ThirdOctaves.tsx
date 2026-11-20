import { useEffect, useRef, useState } from 'react';
import { CartesianGrid, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { api, type SimulateParams } from '../api';
import type { ThirdOctaveBand } from '../types';
import { download } from '../project';

const AXIS = { stroke: '#64748b', fontSize: 11 } as const;
const GRID = '#232329';

/** The parameters shown for each band: the key in the result, the row's label and the digits it is shown to. */
const ROWS: { key: keyof ThirdOctaveBand; label: string; digits: number }[] = [
  { key: 't30S', label: 'T30 (s)', digits: 2 }, { key: 't20S', label: 'T20 (s)', digits: 2 }, { key: 'edtS', label: 'EDT (s)', digits: 2 },
  { key: 'c80Db', label: 'C80 (dB)', digits: 1 }, { key: 'c50Db', label: 'C50 (dB)', digits: 1 }, { key: 'd50', label: 'D50', digits: 2 },
  { key: 'centreTimeS', label: 'Ts (s)', digits: 3 }, { key: 'strengthGDb', label: 'G (dB)', digits: 1 },
];

const label = (hz: number) => (hz >= 1000 ? `${hz / 1000}k` : String(hz));
const text = (v: number | null, digits: number) => (v === null ? '–' : v.toFixed(digits));

/** The results in the 23 third-octave bands as CSV: a row per parameter, a column per band. */
function csv(bands: ThirdOctaveBand[]): string {
  const lines = [['Parameter', ...bands.map((b) => `${b.hz} Hz`)].join(',')];
  for (const row of ROWS) lines.push([row.label, ...bands.map((b) => (b[row.key] === null ? '' : String(b[row.key])))].join(','));
  return lines.join('\r\n') + '\r\n';
}

/**
 * The room at the receiver in the 23 third-octave bands 50 Hz to 8 kHz, worked out on request: the tracer runs three times, each with the surfaces' absorption
 * and scattering interpolated to eight of the bands, so it takes about three times as long as Run simulation.
 */
export default function ThirdOctaves({ params, disabled }: { params: SimulateParams; disabled: boolean }) {
  const [bands, setBands] = useState<ThirdOctaveBand[] | null>(null);
  const [progress, setProgress] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const abort = useRef<AbortController | null>(null);

  // a change to the room, the sources, the receiver or the settings makes the last results stale
  useEffect(() => { abort.current?.abort(); setBands(null); setProgress(null); }, [params]);
  useEffect(() => () => abort.current?.abort(), []);

  const calculate = async () => {
    abort.current?.abort();
    const controller = new AbortController();
    abort.current = controller;
    setError(null);
    setProgress(0);
    try {
      const r = await api.thirdOctaves(params, controller.signal, setProgress);
      setBands(r.bands);
    } catch (e) {
      if ((e as Error).name !== 'AbortError') setError((e as Error).message);
    } finally {
      if (abort.current === controller) setProgress(null);
    }
  };

  const chart = bands?.map((b) => ({ band: label(b.hz), T30: b.t30S, T20: b.t20S, EDT: b.edtS }));
  return (
    <div className="flex h-full flex-col gap-2 overflow-y-auto pr-1" data-testid="third-octaves">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <button type="button" className="btn" disabled={disabled || progress !== null} onClick={calculate}>Calculate third octaves</button>
        {progress !== null && (
          <>
            <span className="text-xs text-accent">Calculating… {Math.round(progress * 100)}%</span>
            <button type="button" className="chip" onClick={() => abort.current?.abort()}>Cancel</button>
          </>
        )}
        {bands && <button type="button" className="chip ml-auto" onClick={() => download('third-octaves.csv', csv(bands), 'text/csv')}>Third octaves CSV</button>}
        {error && <span role="alert" className="text-xs text-rose-300">{error}</span>}
      </div>
      <p className="text-[11px] leading-snug text-slate-500">
        The 23 bands from 50 Hz to 8 kHz that ISO 3382-2 asks for, at the main receiver. The tracer runs three times, each with every surface's absorption and scattering
        interpolated between its octave values to eight of the bands, so it takes about three times a run. A material with a sharp resonance is smoothed out.
      </p>
      {!bands && progress === null && <p className="text-xs text-slate-500">Press Calculate third octaves{disabled ? ' (this model cannot be simulated yet)' : ''}; the results are kept until you change the room, sources, receiver or settings.</p>}
      {bands && (
        <>
          <div className="h-52 shrink-0">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={chart}>
                <CartesianGrid stroke={GRID} vertical={false} />
                <XAxis dataKey="band" {...AXIS} interval={0} tick={{ fontSize: 10 }} />
                <YAxis {...AXIS} unit=" s" />
                <Tooltip contentStyle={{ background: '#0f1524', border: '1px solid #1e293b', fontSize: 12 }} formatter={(v: number) => (v === null ? '–' : `${v.toFixed(2)} s`)} />
                <Legend wrapperStyle={{ fontSize: 11 }} verticalAlign="top" height={24} />
                <Line type="monotone" dataKey="T30" stroke="#a78bfa" strokeWidth={2.5} dot={{ r: 2.5 }} />
                <Line type="monotone" dataKey="T20" stroke="#34d399" strokeWidth={1.5} dot={false} />
                <Line type="monotone" dataKey="EDT" stroke="#e2e8f0" strokeWidth={1.5} dot={false} />
              </LineChart>
            </ResponsiveContainer>
          </div>
          <div className="shrink-0 overflow-x-auto">
            <table className="w-full text-[11px] tabular-nums" data-testid="third-octave-table">
              <thead><tr className="text-slate-500"><th className="py-1 pr-2 text-left font-normal">Hz</th>{bands.map((b) => <th key={b.hz} className="px-1 py-1 text-right font-normal">{label(b.hz)}</th>)}</tr></thead>
              <tbody>
                {ROWS.map((row) => (
                  <tr key={row.key} className="border-t border-white/5 text-slate-200">
                    <th scope="row" className="py-1 pr-2 text-left font-normal">{row.label}</th>
                    {bands.map((b) => <td key={b.hz} className="px-1 py-1 text-right">{text(b[row.key], row.digits)}</td>)}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
