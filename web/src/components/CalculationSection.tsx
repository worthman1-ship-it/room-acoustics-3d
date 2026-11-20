import type { Settings } from './Sidebar';
import { NC_RATINGS } from '../types';
import { Note, Section, Slider } from './ui';

/** ISO 14257's typical d1 and d2, m. */
const WORKROOM_D1 = 5, WORKROOM_D2 = 16;

/** Ready-made rays and image-source order: quick and rough, the usual, thorough. */
const PRESETS = [
  { name: 'Draft', rayCount: 20_000, imageSourceOrder: 1, note: 'quick and rough' },
  { name: 'Standard', rayCount: 60_000, imageSourceOrder: 2, note: 'the usual' },
  { name: 'Detailed', rayCount: 200_000, imageSourceOrder: 3, note: 'slow and smooth' },
] as const;

/** How hard the engine works, and the air it works in. Lives in the settings window. */
export default function CalculationSection({ settings, onSettings }: { settings: Settings; onSettings: (s: Settings) => void }) {
  const set = (patch: Partial<Settings>) => onSettings({ ...settings, ...patch });
  return (
    <Section title="Calculation">
      <div role="group" aria-label="Calculation preset" className="flex flex-wrap items-center gap-1.5 text-[11px] text-slate-400">
        Preset
        {PRESETS.map((p) => {
          const on = settings.rayCount === p.rayCount && settings.imageSourceOrder === p.imageSourceOrder;
          return <button key={p.name} type="button" aria-pressed={on} title={`${p.rayCount.toLocaleString()} rays, image sources to order ${p.imageSourceOrder}: ${p.note}`}
                         onClick={() => set({ rayCount: p.rayCount, imageSourceOrder: p.imageSourceOrder })} className={`chip ${on ? 'chip-on' : ''}`}>{p.name}</button>;
        })}
      </div>
      <Slider label="Rays traced" value={settings.rayCount} min={5000} max={300000} step={5000} onChange={(v) => set({ rayCount: v })} />
      <Slider label="Image-source order" value={settings.imageSourceOrder} min={0} max={4} step={1} onChange={(v) => set({ imageSourceOrder: v })} />
      <Slider label="Rays drawn" value={settings.drawnRays} min={20} max={600} step={10} onChange={(v) => set({ drawnRays: v })} />
      <Slider label="Temperature" value={settings.temperatureC} min={0} max={40} step={1} unit=" °C" onChange={(v) => set({ temperatureC: v })} />
      <Slider label="Humidity" value={settings.humidityPercent} min={10} max={95} step={5} unit=" %" onChange={(v) => set({ humidityPercent: v })} />
      <label className="flex items-center gap-1.5 text-[11px] text-slate-400"
             title="Each reflection takes the absorption of the angle it meets the surface at, from a locally reacting surface whose diffuse-field average is the surface's coefficient: sound meeting it head on is absorbed less than the coefficient, oblique sound more, up to a peak close to grazing">
        <input type="checkbox" aria-label="Absorption by angle of incidence" checked={!!settings.angleDependent} onChange={(e) => set({ angleDependent: e.target.checked || undefined })} className="accent-violet-400" />
        Absorption by angle of incidence
      </label>
      <div className="mt-2 border-t border-white/5 pt-2">
        <span className="label">Speech Transmission Index</span>
        <div className="mt-2 space-y-2">
          <div role="group" aria-label="Speech for the STI" className="flex flex-wrap items-center gap-1.5 text-[11px] text-slate-400">
            Speech
            {([['sources', "The sources' own levels"], ['talker', 'A talker']] as const).map(([v, label]) => (
              <button key={v} type="button" aria-pressed={(settings.stiSignal ?? 'talker') === v} onClick={() => set({ stiSignal: v })}
                      className={`chip ${(settings.stiSignal ?? 'talker') === v ? 'chip-on' : ''}`}>{label}</button>
            ))}
          </div>
          {(settings.stiSignal ?? 'talker') === 'talker'
            ? <Slider label="Speech level at 1 m" value={settings.speechLevelDbA} min={40} max={90} step={1} unit=" dBA" onChange={(v) => set({ speechLevelDbA: v })} />
            : <Note>The speech is what every source heard gives at the receiver, from each one's own sound power and spectrum (set them in the sources window): a sound system's STI. Choose a talker to check one person speaking instead.</Note>}
          <label className="block"><span className="label">Background noise</span>
            <select aria-label="Background noise" className="field mt-1" value={settings.backgroundNc} onChange={(e) => set({ backgroundNc: Number(e.target.value) })}>
              <option value={0}>None</option>
              {NC_RATINGS.map((nc) => <option key={nc} value={nc}>NC {nc}</option>)}
            </select>
          </label>
          <div role="group" aria-label="Voice" className="flex items-center gap-1.5 text-[11px] text-slate-400">
            Voice
            {(['male', 'female'] as const).map((v) => (
              <button key={v} type="button" aria-pressed={settings.voice === v} onClick={() => set({ voice: v })} className={`chip ${settings.voice === v ? 'chip-on' : ''}`}>{v === 'male' ? 'Male' : 'Female'}</button>
            ))}
          </div>
          <Note>The speech and noise are those the Speech Transmission Index (IEC 60268-16) is worked out for at the receiver; the voice sets its band weighting.</Note>
        </div>
      </div>
      <div className="mt-2 border-t border-white/5 pt-2">
        <span className="label">Workroom regions (ISO 14257)</span>
        <div className="mt-2 flex flex-wrap items-center gap-3 text-[11px] text-slate-400">
          {([['workroomD1M', 'd1', 'Near to middle', WORKROOM_D1], ['workroomD2M', 'd2', 'Middle to far', WORKROOM_D2]] as const).map(([key, name, title, typical]) => (
            <label key={key} className="flex items-center gap-1.5" title={`${title}: the standard's typical value is ${typical} m`}>{name}
              <input type="number" min={1.5} step={0.5} aria-label={`Workroom ${name}`} className="field !w-20 !px-1.5 !py-1 text-xs" value={settings[key] ?? typical}
                     onChange={(e) => { const v = e.target.valueAsNumber; set({ [key]: Number.isFinite(v) && v !== typical ? v : undefined }); }} /> m
            </label>
          ))}
          {(settings.workroomD1M !== undefined || settings.workroomD2M !== undefined) && (
            <button type="button" className="chip" onClick={() => set({ workroomD1M: undefined, workroomD2M: undefined })}>Typical (5 and 16 m)</button>
          )}
        </div>
        <Note>Where the near region (from 1 m) gives way to the middle, and the middle to the far. The standard's typical values are 5 m and 16 m; others suit some rooms, and are then reported with the results.</Note>
      </div>
    </Section>
  );
}
