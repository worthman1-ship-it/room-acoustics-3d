import type { Door, Material, Screen } from '../types';
import MaterialPicker from './MaterialPicker';
import Reduction from './Reduction';
import { Note, Section } from './ui';

type Props = { screens: Screen[]; onScreens: (s: Screen[]) => void; onAdd: () => void; onAddPartition: () => void; materials: Material[]; fromModel: string[] };

const DOOR_FIELDS: { key: keyof Door; label: string }[] = [{ key: 'offsetM', label: 'Door from end (m)' }, { key: 'widthM', label: 'Door width (m)' }, { key: 'heightM', label: 'Door height (m)' }];

const FIELDS: { key: 'x' | 'y' | 'baseZ' | 'lengthM' | 'heightM' | 'angleDeg'; label: string; step: number; min?: number }[] = [
  { key: 'x', label: 'x (m)', step: 0.1 }, { key: 'y', label: 'y (m)', step: 0.1 }, { key: 'baseZ', label: 'Base (m)', step: 0.05 },
  { key: 'lengthM', label: 'Length (m)', step: 0.1, min: 0.1 }, { key: 'heightM', label: 'Height (m)', step: 0.05, min: 0.1 }, { key: 'angleDeg', label: 'Angle (°)', step: 5 },
];

/** Free-standing screens: each a vertical rectangle with a finish on both sides, placed by its centre, base height, length, height and angle. */
export default function ScreensPanel({ screens, onScreens, onAdd, onAddPartition, materials, fromModel }: Props) {
  const change = (id: number, patch: Partial<Screen>) => onScreens(screens.map((s) => (s.id === id ? { ...s, ...patch } : s)));
  return (
    <Section title="Screens">
      {screens.map((s, i) => (
        <div key={s.id} className="space-y-1.5" data-testid="screen">
          <div className="flex items-center justify-between">
            <span className="label">Screen {i + 1}</span>
            <button type="button" onClick={() => onScreens(screens.filter((x) => x.id !== s.id))} className="chip">Remove screen {i + 1}</button>
          </div>
          <div className="grid grid-cols-3 gap-1.5">
            {FIELDS.map((f) => (
              <label key={f.key} className="text-[11px] text-slate-500">{f.label}
                <input type="number" step={f.step} min={f.min} aria-label={`Screen ${i + 1} ${f.label}`} value={Math.round(s[f.key] * 100) / 100} className="field mt-0.5 px-1.5 py-1 text-xs"
                       onChange={(e) => { const v = e.target.valueAsNumber; if (Number.isFinite(v)) change(s.id, { [f.key]: f.min === undefined ? v : Math.max(f.min, v) }); }} />
              </label>
            ))}
          </div>
          <MaterialPicker materials={materials} value={s.finish} label={`Screen ${i + 1}`} onChange={(patch) => change(s.id, { finish: { ...s.finish, ...patch } })} />
          <Reduction value={s.finish.transmissionLossDb} label={`Screen ${i + 1}`} onChange={(db) => change(s.id, { finish: { ...s.finish, transmissionLossDb: db } })} />
          <label className="flex items-center gap-1.5 text-[11px] text-slate-400">
            <input type="checkbox" aria-label={`Screen ${i + 1} has a door`} checked={!!s.door} className="accent-violet-400"
                   onChange={(e) => change(s.id, { door: e.target.checked ? { offsetM: Math.max(0, Math.round((s.lengthM - 0.9) / 2 * 10) / 10), widthM: Math.min(0.9, s.lengthM), heightM: Math.min(2.1, s.heightM) } : undefined })} />
            A door (an opening) in it
          </label>
          {s.door && (
            <div className="grid grid-cols-3 gap-1.5">
              {DOOR_FIELDS.map((f) => (
                <label key={f.key} className="text-[11px] text-slate-500">{f.label}
                  <input type="number" step={0.05} min={f.key === 'offsetM' ? 0 : 0.1} aria-label={`Screen ${i + 1} ${f.label}`} value={Math.round(s.door![f.key] * 100) / 100} className="field mt-0.5 px-1.5 py-1 text-xs"
                         onChange={(e) => { const v = e.target.valueAsNumber; if (Number.isFinite(v)) change(s.id, { door: { ...s.door!, [f.key]: Math.max(f.key === 'offsetM' ? 0 : 0.1, v) } }); }} />
                </label>
              ))}
            </div>
          )}
        </div>
      ))}
      <div className="grid grid-cols-2 gap-1.5">
        <button type="button" onClick={onAdd} className="chip text-center">+ Add screen</button>
        <button type="button" onClick={onAddPartition} className="chip text-center" title="A wall across the middle of the room, side to side and floor to ceiling, with a door: two rooms coupled through it">+ Add partition</button>
      </div>
      {fromModel.length > 0 && <p className="text-[11px] text-slate-400">The model's {fromModel.join(', ')} {fromModel.length === 1 ? 'layer is' : 'layers are'} screens too (set under Surfaces).</p>}
      <Note>
        A screen reflects from both sides, with its finish on each. Sound gets past it only round its free edges (the top, and the sides unless they touch a wall):
        each edge's path is cut by the Kurze-Anderson barrier formula for its octave band, at most 20 dB a screen. The rays bounce off it but do not bend round it.
        Receivers must be at least 0.5 m from a screen. The angle turns the screen from the x axis towards y.
      </Note>
      <Note>
        A partition is a screen from wall to wall and floor to ceiling: the two halves are coupled rooms. Sound passes through it by its sound reduction R in each
        octave band (a ray goes through with τ = 10^(−R/10) of its energy; the direct sound and the image sources' paths through it are cut by τ), and through
        its door both as rays and round the door's edges. The level difference between the rooms is then about R + 10 lg(A / S), as in a test suite.
      </Note>
    </Section>
  );
}
