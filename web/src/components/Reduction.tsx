import { BANDS_HZ, bandLabel } from '../types';

/**
 * Typical sound reductions R of common partitions, 63 Hz to 8 kHz, in dB: textbook shapes with the usual single-number rating, for a first look. Use
 * the product's measured values (Custom) for a design.
 */
export const REDUCTIONS: { name: string; db: number[] }[] = [
  { name: 'Door, ordinary (Rw ≈ 20)', db: [12, 15, 18, 20, 22, 24, 25, 25] },
  { name: 'Glazing, 6 mm (Rw ≈ 30)', db: [18, 22, 25, 28, 32, 34, 30, 35] },
  { name: 'Plasterboard on studs (Rw ≈ 35)', db: [15, 20, 28, 36, 42, 45, 40, 45] },
  { name: 'Blockwork, 100 mm (Rw ≈ 45)', db: [30, 33, 37, 42, 48, 54, 58, 60] },
  { name: 'Double stud, insulated (Rw ≈ 50)', db: [20, 30, 42, 52, 58, 62, 58, 62] },
  { name: 'Brick, 215 mm (Rw ≈ 55)', db: [35, 40, 46, 52, 58, 64, 68, 70] },
];

const same = (a: number[], b: number[]) => a.length === b.length && a.every((v, i) => v === b[i]);

/** How much sound passes through a screen or partition: none, a typical partition, or the user's own sound reduction in each octave band. */
export default function Reduction({ value, label, onChange }: { value: number[] | undefined; label: string; onChange: (db: number[] | undefined) => void }) {
  const preset = value ? REDUCTIONS.findIndex((r) => same(r.db, value)) : -1;
  const choice = !value ? 'none' : preset >= 0 ? String(preset) : 'custom';
  return (
    <div className="space-y-1">
      <label className="block text-[11px] text-slate-500">Sound passing through
        <select aria-label={`${label} sound reduction`} className="field mt-0.5 px-1.5 py-1 text-xs" value={choice}
                onChange={(e) => onChange(e.target.value === 'none' ? undefined : e.target.value === 'custom' ? [...(value ?? REDUCTIONS[2].db)] : [...REDUCTIONS[Number(e.target.value)].db])}>
          <option value="none">None (solid)</option>
          {REDUCTIONS.map((r, i) => <option key={r.name} value={i}>{r.name}</option>)}
          <option value="custom">Custom</option>
        </select>
      </label>
      {value && (
        <div className="grid grid-cols-8 gap-0.5" role="group" aria-label={`${label} sound reduction by band`}>
          {BANDS_HZ.map((hz, b) => (
            <label key={hz} className="text-center text-[10px] text-slate-500">{bandLabel(hz)}
              <input type="number" min={0} max={200} step={1} aria-label={`${label} R at ${bandLabel(hz)} Hz`} value={value[b]} className="field mt-0.5 px-0.5 py-0.5 text-center text-[11px]"
                     onChange={(e) => { const v = e.target.valueAsNumber; if (Number.isFinite(v)) onChange(value.map((x, k) => (k === b ? Math.min(200, Math.max(0, v)) : x))); }} />
            </label>
          ))}
        </div>
      )}
    </div>
  );
}
