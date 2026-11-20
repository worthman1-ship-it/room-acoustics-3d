import { BANDS_HZ, MAP_METRICS, ONE_NUMBER_METRICS, bandLabel, type MapScale } from '../types';

type Props = {
  metric: string; overall: boolean; bandIndex: number;
  onMetric: (metric: string) => void; onOverall: (overall: boolean) => void; onBand: (band: number) => void;
  /** Saves the calculated map as a CSV, and the flat map as a picture; absent until a map has been calculated. */
  onExport?: () => void; onImage?: () => void;
  /** Whether the calculated map has the Reflector coverage parameter (it needs a reflector surface chosen in Settings before Calculate map). */
  reflectorAvailable?: boolean;
  /** The shown parameter's unit and the scale the map has of itself (null before there is a map), and the scale the user set for this parameter. */
  unit?: string | null; ownRange?: [number, number] | null; scale?: MapScale; onScale?: (scale: MapScale) => void;
};

const NO_SCALE: MapScale = { min: null, max: null, contourStep: null };

/** A number box that is empty for null, and gives null when it is emptied or holds something that is not a number. */
function NumberBox({ label, value, placeholder, onChange }: { label: string; value: number | null; placeholder: string; onChange: (v: number | null) => void }) {
  return (
    <input type="number" step="any" aria-label={label} placeholder={placeholder} className="field !w-20 px-1.5 py-1 text-xs tabular-nums" value={value ?? ''}
           onChange={(e) => onChange(Number.isFinite(e.target.valueAsNumber) ? e.target.valueAsNumber : null)} />
  );
}

/** What the map shows: the parameter, and an octave band or the broadband (single-number) value. The STI and SII are one number each, so they have no bands. */
export default function MapBar({ metric, overall, bandIndex, onMetric, onOverall, onBand, onExport, reflectorAvailable = false, unit = null, ownRange = null, scale = NO_SCALE, onScale, onImage }: Props) {
  const oneNumber = ONE_NUMBER_METRICS.includes(metric);
  const set = (patch: Partial<MapScale>) => onScale?.({ ...scale, ...patch });
  const upsideDown = scale.min !== null && scale.max !== null && scale.min >= scale.max;
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-1" role="group" aria-label="Map shows">
      <select aria-label="Map parameter" className="field !w-auto" value={metric} onChange={(e) => onMetric(e.target.value)}>
        {MAP_METRICS.map((m) => <option key={m.key} value={m.key} disabled={m.key === 'Reflector' && !reflectorAvailable}>{m.label}{m.key === 'Reflector' && !reflectorAvailable ? ' (choose a surface in Settings)' : ''}</option>)}
      </select>
      <div role="group" aria-label="Map band" className="flex flex-wrap items-center gap-1">
        {BANDS_HZ.map((hz, i) => (
          <button key={hz} type="button" disabled={oneNumber} aria-pressed={!oneNumber && !overall && bandIndex === i} onClick={() => onBand(i)}
                  className={`chip ${!oneNumber && !overall && bandIndex === i ? 'chip-on' : ''} disabled:opacity-40`}>{bandLabel(hz)}</button>
        ))}
        <button type="button" disabled={oneNumber} aria-pressed={oneNumber || overall} onClick={() => onOverall(true)}
                title="The single number ISO 3382-1 gives: the mean of the octave bands it averages the parameter over"
                className={`chip ${oneNumber || overall ? 'chip-on' : ''}`}>Broadband</button>
      </div>
      {onExport && <button type="button" onClick={onExport} className="chip" title="Every parameter at every point of the map, by octave band and broadband, as a CSV file">Map CSV</button>}
      {onImage && <button type="button" onClick={onImage} className="chip" title="The flat map with its scale and figures as a PNG picture, for a report">Map PNG</button>}
      {onScale && ownRange && (
        <div role="group" aria-label="Map scale" className="flex flex-wrap items-center gap-1.5 text-[11px] text-slate-400"
             title="Set the ends of the colour scale to compare maps on the same scale; values beyond an end take its colour. Each parameter keeps its own scale, in every band.">
          Scale
          <NumberBox label="Scale bottom" value={scale.min} placeholder={ownRange[0].toFixed(2)} onChange={(min) => set({ min })} />
          to
          <NumberBox label="Scale top" value={scale.max} placeholder={ownRange[1].toFixed(2)} onChange={(max) => set({ max })} />
          {unit}
          <span className="ml-1">Contours every</span>
          <NumberBox label="Contour step" value={scale.contourStep} placeholder="none" onChange={(v) => set({ contourStep: v !== null && v > 0 ? v : null })} />
          {(scale.min !== null || scale.max !== null || scale.contourStep !== null) && <button type="button" className="chip" onClick={() => onScale(NO_SCALE)}>Auto</button>}
          {upsideDown && <span className="text-amber-300">The bottom must be below the top; the map's own scale is used.</span>}
        </div>
      )}
      {oneNumber && <span className="text-[11px] text-slate-500">This one is a single number at each point (the STI, SII and %ALcons use the talker and noise set in Settings).</span>}
    </div>
  );
}
