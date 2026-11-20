import { useState } from 'react';
import type { MapMode, MapSettings } from '../types';
import type { Frame } from '../frame';
import { Note, Section, Slider } from './ui';

const MODES: { key: MapMode; label: string; hint: string }[] = [
  { key: 'plan', label: 'Plan', hint: 'A flat map of the room from above, at one receiver height.' },
  { key: 'section', label: 'Section', hint: 'A vertical slice through the room, so you can see how the metric changes with height.' },
  { key: 'volume', label: 'Height matrix', hint: 'A plan at each of several heights, shown one height at a time (and all together in the 3-D view).' },
  { key: 'surface', label: 'Above a surface', hint: 'A map a fixed height above a surface, so it rises and falls with a raked or stepped floor. Cells with no such surface beneath them are left out.' },
];

const tenth = (v: number) => Math.round(v * 10) / 10;

/** Parses "1.2, 2.4" into heights inside the room (at most 12, sorted, no repeats); anything that is not a number is ignored. */
export function parseHeights(text: string, lo: number, hi: number): number[] {
  const values = text.split(/[\s,;]+/).map(Number).filter((v) => text.trim() !== '' && Number.isFinite(v));
  return [...new Set(values.map((v) => tenth(Math.min(hi, Math.max(lo, v)))))].sort((a, b) => a - b).slice(0, 12);
}

type Props = {
  settings: MapSettings; onSettings: (m: MapSettings) => void; surfaceChoices: string[]; reflectorChoices: string[]; frame: Frame;
};

/** Everything about a map: plan, vertical section, height matrix or a map above a surface, and the grid. Which parameter, and which octave band or the broadband value, is chosen in the Map view. */
export default function MapSection({ settings, onSettings, surfaceChoices, reflectorChoices, frame }: Props) {
  const set = (patch: Partial<MapSettings>) => onSettings({ ...settings, ...patch });
  const lo = tenth(frame.min.z + 0.6), hi = Math.max(lo + 0.1, tenth(frame.max.z - 0.6));
  const [heightsText, setHeightsText] = useState(settings.heightsM.join(', '));
  const axisLo = (settings.axis === 'x' ? frame.min.x : frame.min.y) + 0.6, axisHi = (settings.axis === 'x' ? frame.max.x : frame.max.y) - 0.6;
  const mode = MODES.find((m) => m.key === settings.mode) ?? MODES[0];
  const position = Math.min(Math.max(settings.positionM, tenth(axisLo)), Math.max(tenth(axisLo), tenth(axisHi)));

  return (
    <Section title="Map">
      <div role="group" aria-label="Map type" className="flex flex-wrap gap-1.5">
        {MODES.map((m) => (
          <button key={m.key} type="button" aria-pressed={settings.mode === m.key} onClick={() => set({ mode: m.key, spacingM: m.key === 'plan' ? settings.spacingM : Math.min(settings.spacingM, 1) })}
                  className={`chip ${settings.mode === m.key ? 'chip-on' : ''}`}>{m.label}</button>
        ))}
      </div>
      <Note>{mode.hint}</Note>

      {settings.mode === 'plan' && (
        <Slider label="Receiver height (z)" value={Math.min(hi, Math.max(lo, settings.heightM))} min={lo} max={hi} step={0.1} unit=" m" onChange={(v) => set({ heightM: v })} />
      )}
      {settings.mode === 'surface' && (
        <>
          <label className="block"><span className="label">Surface</span>
            <select aria-label="Surface to map above" className="field mt-1" value={surfaceChoices.includes(settings.surfaceGroup ?? '') ? settings.surfaceGroup! : surfaceChoices[0]}
                    onChange={(e) => set({ surfaceGroup: e.target.value })}>
              {surfaceChoices.map((name) => <option key={name} value={name}>{name}</option>)}
            </select>
          </label>
          <Slider label="Height above the surface" value={settings.aboveM} min={0.6} max={6} step={0.1} unit=" m" onChange={(v) => set({ aboveM: v })} />
        </>
      )}
      {settings.mode === 'section' && (
        <>
          <div role="group" aria-label="Section plane" className="flex flex-wrap gap-1.5">
            <button type="button" aria-pressed={settings.axis === 'x'} onClick={() => set({ axis: 'x', positionM: tenth((frame.min.x + frame.max.x) / 2) })} className={`chip ${settings.axis === 'x' ? 'chip-on' : ''}`}>Across the room (at x)</button>
            <button type="button" aria-pressed={settings.axis === 'y'} onClick={() => set({ axis: 'y', positionM: tenth((frame.min.y + frame.max.y) / 2) })} className={`chip ${settings.axis === 'y' ? 'chip-on' : ''}`}>Along the room (at y)</button>
          </div>
          <Slider label={`Position (${settings.axis})`} value={position} min={tenth(axisLo)} max={Math.max(tenth(axisLo), tenth(axisHi))} step={0.1} unit=" m" onChange={(v) => set({ positionM: v })} />
        </>
      )}
      {settings.mode === 'volume' && (
        <label className="block"><span className="label">Heights (m, up to 12)</span>
          <input aria-label="Heights in metres" className="field mt-1" value={heightsText} placeholder="1.2, 2.4"
                 onChange={(e) => { setHeightsText(e.target.value); set({ heightsM: parseHeights(e.target.value, lo, hi) }); }} />
          <span className="mt-1 block text-[11px] text-slate-500">{settings.heightsM.length === 0 ? 'Give at least one height.' : `Using ${settings.heightsM.map((h) => h.toFixed(1)).join(', ')} m (between ${lo} and ${hi} m).`}</span>
        </label>
      )}

      <div className="space-y-1.5 border-t border-white/5 pt-2">
        <label className="block"><span className="label">Reflector coverage from</span>
          <select aria-label="Reflector surface" className="field mt-1" value={settings.reflectorGroup && reflectorChoices.includes(settings.reflectorGroup) ? settings.reflectorGroup : ''}
                  onChange={(e) => set({ reflectorGroup: e.target.value || null })}>
            <option value="">None</option>
            {reflectorChoices.map((name) => <option key={name} value={name}>{name}</option>)}
          </select>
        </label>
        {settings.reflectorGroup && (
          <div className="grid grid-cols-2 gap-2">
            <label className="text-[11px] text-slate-500">Arrives from (ms)
              <input type="number" min={0} step={1} aria-label="Reflector window from" className="field mt-1 px-1.5 py-1 text-xs" value={settings.reflectorFromMs}
                     onChange={(e) => { const v = e.target.valueAsNumber; if (Number.isFinite(v)) set({ reflectorFromMs: Math.max(0, v) }); }} />
            </label>
            <label className="text-[11px] text-slate-500">to (ms)
              <input type="number" min={1} step={1} aria-label="Reflector window to" className="field mt-1 px-1.5 py-1 text-xs" value={settings.reflectorToMs}
                     onChange={(e) => { const v = e.target.valueAsNumber; if (Number.isFinite(v)) set({ reflectorToMs: Math.min(500, v) }); }} />
            </label>
          </div>
        )}
        <Note>With a surface chosen, the map also gives the level of the strongest first reflection from that surface arriving in the window after the direct sound, against the direct sound (the Reflector coverage parameter, for the first source); points that get no such reflection are left blank.</Note>
      </div>
      <Slider label="Grid spacing" value={settings.spacingM} min={0.25} max={4} step={0.25} unit=" m" onChange={(v) => set({ spacingM: v })} />
      <Slider label="Rays for the map" value={settings.rayCount} min={5000} max={100000} step={5000} onChange={(v) => set({ rayCount: v })} />
      <Note>
        Calculate map (beside Run simulation in the side panel) works out every ISO 3382 parameter and the Speech Transmission Index at every grid point, in every octave band and as its broadband number, all at once: choose what to see in the Map view, with no need to recalculate. The time grows with both the rays and the number of points.
        Grid points close to a source, or too near a surface, are left out.
      </Note>
    </Section>
  );
}
