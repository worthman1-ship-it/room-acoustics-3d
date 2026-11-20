import type { LevelEntry } from '../types';
import { useRef, useState, type ReactNode } from 'react';
import type { Model, Room } from '../types';
import { BANDS_HZ, bandLabel } from '../types';
import { Running, Section, Slider } from './ui';

export type Settings = {
  rayCount: number; imageSourceOrder: number; temperatureC: number; humidityPercent: number; bandIndex: number; overall: boolean; drawnRays: number;
  /** For the Speech Transmission Index: speech at 1 m in dBA, the background noise as an NC rating (0 for none) and the voice. */
  speechLevelDbA: number; backgroundNc: number; voice: 'male' | 'female';
  /** The first source's sound power level in each octave band (63 Hz to 8 kHz), and the shape it was set from, for the sound pressure levels. */
  lwBandsDb: number[]; lwShape: string;
  /** how the first source's level was entered (absent: as a sound power), and whether it is muted or soloed */
  level1?: LevelEntry; source1Muted?: boolean; source1Solo?: boolean;
  /** the STI's speech: the sources' own levels at the receiver, or a talker at speechLevelDbA at 1 m (absent in projects saved before: a talker) */
  stiSignal?: 'sources' | 'talker';
  /** each reflection takes the absorption of its angle of incidence (a locally reacting surface) rather than the random-incidence coefficient */
  angleDependent?: boolean;
  /** ISO 14257's d1 and d2, m, dividing the near, middle and far regions (absent: the standard's typical 5 m and 16 m) */
  workroomD1M?: number; workroomD2M?: number;
};

type Props = {
  collapsed: boolean; onToggle: () => void;
  room: Room; onRoom: (r: Room) => void;
  settings: Settings; onSettings: (s: Settings) => void;
  busy: boolean; elapsed: number; progress: number; onCancel: () => void; online: boolean | null; canRun: boolean; onRun: () => void; onQuick: () => void;
  mapBusy: boolean; mapProgress: number; hasMap: boolean; mapDisabled: boolean; onMap: () => void; onCancelMap: () => void;
  model: Model | null; surfacesOpen: boolean; onToggleSurfaces: () => void; settingsOpen: boolean; onToggleSettings: () => void;
  examples: string[]; importing: boolean;
  onImportFile: (file: File, scale: number, yUp: boolean) => void; onLoadExample: (name: string) => void; onClearModel: () => void;
  /** reads another room (IfcSpace) of the imported IFC file */
  onPickSpace: (space: number) => void;
  /** the sources and receiver: built by the app, which owns what they edit */
  sourcesPanel: ReactNode;
};

const UNITS = [{ label: 'Metres', scale: 1 }, { label: 'Centimetres', scale: 0.01 }, { label: 'Millimetres', scale: 0.001 }, { label: 'Feet', scale: 0.3048 }, { label: 'Inches', scale: 0.0254 }];

function ModelSection({ model, examples, importing, onImportFile, onLoadExample, onClearModel, onPickSpace }: Pick<Props, 'model' | 'examples' | 'importing' | 'onImportFile' | 'onLoadExample' | 'onClearModel' | 'onPickSpace'>) {
  const input = useRef<HTMLInputElement>(null);
  const [unit, setUnit] = useState(0);
  const [yUp, setYUp] = useState(false);

  return (
    <Section title="3-D model">
      {model ? (
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0">
            <div className="truncate text-sm text-slate-100">{model.name}</div>
            <div className="text-[11px] text-slate-500">{model.groups.length} surface groups · {model.triangleCount} triangles</div>
          </div>
          <button onClick={onClearModel} className="chip shrink-0">Use box room</button>
        </div>
      ) : (
        <p className="text-[11px] leading-snug text-slate-500">Import a DXF, DWG, OBJ, STL, COLLADA (.dae, from SketchUp), IFC or 3D Studio (.3ds) model of a closed room, or try an example. An IFC file gives its rooms (IfcSpace) to choose from, or its surfaces if it has none; IFC and COLLADA carry their own units. Each layer (DXF) or material (OBJ, 3DS) becomes a surface you give a finish.</p>
      )}
      {model?.spaces && model.spaces.length > 1 && (
        <label className="block">
          <span className="label">Room in the IFC file</span>
          <select aria-label="Room in the IFC file" value={model.space ?? 0} disabled={importing} onChange={(e) => onPickSpace(Number(e.target.value))} className="field mt-1 w-full">
            {model.spaces.map((s, i) => <option key={i} value={i}>{s.name} · {Math.round(s.volumeM3)} m³{s.isArea ? ' (area)' : ''}</option>)}
          </select>
        </label>
      )}
      <div className="grid grid-cols-2 gap-2">
        <button onClick={() => input.current?.click()} disabled={importing} className="btn !bg-ink-700 !px-2 hover:!bg-ink-600">{importing ? 'Reading…' : 'Import model…'}</button>
        <select aria-label="Drawing units" value={unit} onChange={(e) => setUnit(Number(e.target.value))} className="field">
          {UNITS.map((u, i) => <option key={u.label} value={i}>{u.label}</option>)}
        </select>
      </div>
      <input ref={input} type="file" accept=".dxf,.dwg,.obj,.stl,.dae,.ifc,.3ds" hidden
             onChange={(e) => { const f = e.target.files?.[0]; if (f) onImportFile(f, UNITS[unit].scale, yUp); e.target.value = ''; }} />
      <label className="flex cursor-pointer items-center gap-1.5 text-[11px] text-slate-400">
        <input type="checkbox" checked={yUp} onChange={(e) => setYUp(e.target.checked)} className="accent-violet-400" />
        OBJ is y-up (most modelling tools)
      </label>
      {examples.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="label">Examples</span>
          {examples.map((name) => <button key={name} onClick={() => onLoadExample(name)} disabled={importing} className="chip">{name.replace(/\.[^.]+$/, '')}</button>)}
        </div>
      )}
    </Section>
  );
}

export default function Sidebar(props: Props) {
  const { collapsed, onToggle, room, onRoom, settings, onSettings, busy, elapsed, progress, onCancel, online, canRun, onRun,
    model, surfacesOpen, onToggleSurfaces, settingsOpen, onToggleSettings, sourcesPanel } = props;
  const set = (patch: Partial<Settings>) => onSettings({ ...settings, ...patch });
  const disabled = busy || !online || !canRun;

  if (collapsed) {
    return (
      <aside className="flex w-12 shrink-0 flex-col items-center gap-3 border-r border-white/5 bg-ink-900/90 py-3">
        <button onClick={onToggle} title="Open the panel" className="rounded-md p-2 text-slate-400 hover:bg-white/5 hover:text-slate-100">▶</button>
        <button onClick={onRun} disabled={disabled} title="Run simulation" className="rounded-md bg-accent-soft p-2 text-white disabled:opacity-40">{busy ? '…' : '▶'}</button>
        <button onClick={props.onQuick} disabled={!online || !canRun} title="Quick estimate from the standard equations" className="rounded-md bg-ink-700 p-2 text-white disabled:opacity-40">≈</button>
        <button onClick={props.onMap} disabled={!online || !canRun || props.mapBusy || props.mapDisabled} title="Calculate map" className="rounded-md bg-ink-700 p-2 text-white disabled:opacity-40">{props.mapBusy ? '…' : '▦'}</button>
      </aside>
    );
  }

  return (
    <aside className="flex w-80 shrink-0 flex-col border-r border-white/5 bg-ink-900/90">
      <div className="flex items-center justify-between border-b border-white/5 px-4 py-3">
        <span className="text-sm font-semibold text-slate-100">Parameters</span>
        <button onClick={onToggle} title="Collapse the panel" className="rounded-md px-2 py-1 text-slate-400 hover:bg-white/5 hover:text-slate-100">◀</button>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto">
        <ModelSection model={model} examples={props.examples} importing={props.importing}
                      onImportFile={props.onImportFile} onLoadExample={props.onLoadExample} onClearModel={props.onClearModel} onPickSpace={props.onPickSpace} />

        {!model && (
          <Section title="Room">
            <Slider label="Length" value={room.lengthM} min={3} max={60} step={0.5} unit=" m" onChange={(v) => onRoom({ ...room, lengthM: v })} />
            <Slider label="Width" value={room.widthM} min={3} max={40} step={0.5} unit=" m" onChange={(v) => onRoom({ ...room, widthM: v })} />
            <Slider label="Height" value={room.heightM} min={2} max={20} step={0.1} unit=" m" onChange={(v) => onRoom({ ...room, heightM: Math.round(v * 10) / 10 })} />
          </Section>
        )}

        {sourcesPanel}

        <Section title="Surfaces">
          <p className="text-[11px] leading-snug text-slate-500">The finish and scattering of each surface, in a window of its own that you can move beside the 3-D view. Pick a surface there to isolate it and see its absorption.</p>
          <button onClick={onToggleSurfaces} className="btn w-full !bg-ink-700 hover:!bg-ink-600">{surfacesOpen ? 'Close surfaces window' : 'Open surfaces window'}</button>
        </Section>

        <Section title="Settings">
          <p className="text-[11px] leading-snug text-slate-500">The calculation (rays, image-source order, air) and the map, in a window of their own that you can move beside the 3-D view.</p>
          <button onClick={onToggleSettings} className="btn w-full !bg-ink-700 hover:!bg-ink-600">{settingsOpen ? 'Close settings window' : 'Open settings window'}</button>
        </Section>

        <Section title="Octave band shown">
          <div className="flex flex-wrap gap-1.5">
            {BANDS_HZ.map((hz, i) => (
              <button key={hz} onClick={() => set({ bandIndex: i, overall: false })} className={`chip ${!settings.overall && settings.bandIndex === i ? 'chip-on' : ''}`}>{bandLabel(hz)} Hz</button>
            ))}
            <button onClick={() => set({ overall: true })} aria-pressed={settings.overall} className={`chip ${settings.overall ? 'chip-on' : ''}`}>Overall</button>
          </div>
          <p className="text-[11px] leading-snug text-slate-500">Sets the band for the decay curve and impulse response, the map, and the surface absorption the drawn rays use. Overall adds the eight bands' energies for the decay curve and impulse response, shows each map parameter as its single ISO 3382-1 number, and uses the mean of the 500 Hz and 1 kHz absorption for the rays and the surfaces. The reverberation and clarity charts always show all eight bands.</p>
        </Section>
      </div>

      <div className="border-t border-white/5 p-4">
        <div className="space-y-2">
          {busy
            ? <Running label="Calculating" progress={progress} elapsed={elapsed} onCancel={onCancel} />
            : <button onClick={onRun} disabled={disabled} className="btn w-full">Run simulation</button>}
          <button onClick={props.onQuick} disabled={!online || !canRun} title="At once, from the standard equations: Sabine and Eyring times, critical distance, clarity and strength, from the volume, the surfaces and the source-to-receiver distance alone"
                  className="btn w-full !bg-ink-700 hover:!bg-ink-600">Quick estimate</button>
          {props.mapBusy
            ? <Running label="Mapping" progress={props.mapProgress} elapsed={elapsed} onCancel={props.onCancelMap} />
            : <button onClick={props.onMap} disabled={!online || !canRun || props.mapDisabled} className="btn w-full !bg-ink-700 hover:!bg-ink-600">{props.hasMap ? 'Recalculate map' : 'Calculate map'}</button>}
        </div>
      </div>
    </aside>
  );
}
