import { useEffect, useState } from 'react';
import { api } from '../api';
import type { Directivity, Point, ReflectionsResult, RoomSpec } from '../types';
import { bandHzText } from '../types';
import { download } from '../project';
import { reflectionsCsv } from '../export';

type Props = {
  spec: RoomSpec; source: Point; receiver: Point; directivity: Directivity | null; temperatureC: number; humidityPercent: number;
  bandIndex: number; overall: boolean; shownHz: number | null; online: boolean | null; disabled: boolean;
  /** The picked reflection's route, to draw in the 3-D view (null for none). */
  onHighlight: (path: Point[] | null) => void;
  /** Told each new list, so the workbook can carry it. */
  onData?: (list: ReflectionsResult) => void;
};

/** The reflections at the receiver from the first source, earliest first: the surfaces each bounces off, how long after the direct sound it arrives and how loud it is against it. */
export default function Reflections({ spec, source, receiver, directivity, temperatureC, humidityPercent, bandIndex, overall, shownHz, online, disabled, onHighlight, onData }: Props) {
  const [order, setOrder] = useState(2);
  const [data, setData] = useState<ReflectionsResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [picked, setPicked] = useState<number | null>(null);

  // the list is cheap (no rays), so it follows the room, the source and the receiver after a short pause
  useEffect(() => {
    if (!online || disabled) return;
    const controller = new AbortController();
    setBusy(true);
    const timer = setTimeout(() => {
      api.reflections({ ...spec, source, receiver, maxOrder: order, temperatureC, humidityPercent, directivity }, controller.signal)
        .then((r) => { setData(r); onData?.(r); setError(null); setPicked(null); })
        .catch((e) => { if ((e as Error).name !== 'AbortError') setError((e as Error).message); })
        .finally(() => { if (!controller.signal.aborted) setBusy(false); });
    }, 250);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [online, disabled, spec, source, receiver, order, temperatureC, humidityPercent, directivity]);

  // leaving the tab (or a new list) takes the drawn route away
  useEffect(() => { onHighlight(picked === null || !data ? null : data.reflections[picked].path.map(([x, y, z]) => ({ x, y, z }))); }, [picked, data]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => () => onHighlight(null), []); // eslint-disable-line react-hooks/exhaustive-deps

  const level = (r: ReflectionsResult['reflections'][number]) => (overall ? r.overallDb : r.levelsDb[bandIndex]);
  return (
    <div className="flex h-full flex-col gap-2" data-testid="reflections">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <div role="group" aria-label="Reflection order" className="flex items-center gap-1.5 text-[11px] text-slate-400">
          Up to order
          {[1, 2, 3, 4].map((o) => <button key={o} type="button" aria-pressed={order === o} onClick={() => setOrder(o)} className={`chip ${order === o ? 'chip-on' : ''}`}>{o}</button>)}
        </div>
        {data && (
          <span className="text-[11px] text-slate-500">
            Direct sound {data.directDistanceM.toFixed(2)} m, arriving at {data.directDelayMs.toFixed(1)} ms. {data.total} reflection{data.total === 1 ? '' : 's'}
            {data.reflections.length < data.total && ` (the first ${data.reflections.length} shown)`}; level against the direct sound at {overall ? '500 Hz–1 kHz (mean)' : bandHzText(shownHz)}.
            {data.imageSourceOrderUsed < order && ` Limited to order ${data.imageSourceOrderUsed} (many faces).`}
          </span>
        )}
        {data && <button type="button" className="chip ml-auto" onClick={() => download('reflections.csv', reflectionsCsv(data), 'text/csv')}>Reflections CSV</button>}
        {busy && <span className="text-xs text-accent">Calculating…</span>}
      </div>
      {error && <div className="rounded-lg border border-rose-500/30 bg-rose-500/10 px-3 py-2 text-xs text-rose-300">{error}</div>}
      {!data && !error && <div className="grid flex-1 place-items-center text-sm text-slate-500">{disabled ? 'This model cannot be simulated yet.' : 'Working out the reflections…'}</div>}
      {data && (
        <div className="min-h-0 flex-1 overflow-auto">
          <table className="w-full min-w-[480px] border-collapse text-xs tabular-nums">
            <thead className="sticky top-0 bg-ink-900/95">
              <tr className="text-left text-slate-400">
                <th className="py-1 pr-2 font-medium">#</th><th className="px-2 py-1 font-medium">Order</th><th className="px-2 py-1 font-medium">Surfaces, in order from the source</th>
                <th className="px-2 py-1 text-right font-medium">Delay (ms)</th><th className="px-2 py-1 text-right font-medium">Path (m)</th><th className="px-2 py-1 text-right font-medium">Level (dB)</th>
              </tr>
            </thead>
            <tbody>
              {data.reflections.map((r, i) => (
                <tr key={i} onClick={() => setPicked(picked === i ? null : i)} aria-selected={picked === i} data-testid="reflection-row"
                    className={`cursor-pointer border-t border-white/5 ${picked === i ? 'bg-violet-400/15 text-slate-50' : 'text-slate-200 hover:bg-white/5'}`}>
                  <td className="py-1 pr-2 text-slate-500">{i + 1}</td><td className="px-2 py-1">{r.order}</td><td className="px-2 py-1">{r.surfaces.join(' → ')}</td>
                  <td className="px-2 py-1 text-right">{r.delayMs.toFixed(1)}</td><td className="px-2 py-1 text-right">{r.distanceM.toFixed(2)}</td>
                  <td className="px-2 py-1 text-right">{level(r) === null ? '–' : level(r)!.toFixed(1)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="mt-2 text-[11px] leading-snug text-slate-500">
            Specular paths only, worked out as the simulation does (each bounce keeps what the surface neither absorbs nor scatters, then spreading, air and the source's pattern); the level is against the direct sound as if
            nothing blocked it. Click a row to draw its route in the 3-D view. Further sources are not listed: this is the first source.
          </p>
        </div>
      )}
    </div>
  );
}
