import type { ReactNode } from 'react';
import type { Point } from '../types';
import { clampTo, type Frame } from '../frame';

/** A labelled range slider with its value beside the label. */
export function Slider({ label, value, min, max, step, unit, onChange }: {
  label: string; value: number; min: number; max: number; step: number; unit?: string; onChange: (v: number) => void;
}) {
  return (
    <div>
      <div className="mb-1 flex items-baseline justify-between">
        <span className="label">{label}</span>
        <span className="text-xs tabular-nums text-slate-300">{value}{unit}</span>
      </div>
      <input type="range" min={min} max={max} step={step} value={value} aria-label={label} onChange={(e) => onChange(Number(e.target.value))} className="w-full accent-violet-400" />
    </div>
  );
}

/** A calculation under way: a progress bar, the percentage and seconds, and a way to stop it (which stops it on the engine too). */
export function Running({ label, progress, elapsed, onCancel }: { label: string; progress: number; elapsed: number; onCancel: () => void }) {
  const percent = Math.round(Math.min(1, Math.max(0, progress)) * 100);
  return (
    <div className="space-y-1.5" role="status" aria-live="polite">
      <div className="flex items-center justify-between text-xs text-slate-300">
        <span>{label}… {percent}%</span>
        <span className="tabular-nums text-slate-500">{elapsed} s</span>
      </div>
      <div className="h-1.5 overflow-hidden rounded-full bg-ink-700"><div className="h-full rounded-full bg-accent transition-[width] duration-300" style={{ width: `${percent}%` }} /></div>
      <button onClick={onCancel} className="chip w-full text-center">Cancel</button>
    </div>
  );
}

export function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="space-y-3 border-b border-white/5 px-4 py-4">
      <h2 className="text-xs font-semibold uppercase tracking-widest text-slate-500">{title}</h2>
      {children}
    </section>
  );
}

export const Note = ({ children }: { children: ReactNode }) => <p className="text-[11px] leading-snug text-slate-500">{children}</p>;

/** X, Y and Z of a point, in metres, kept inside the room (a box needs the receiver sphere clear of its surfaces). */
export function PointFields({ label, value, frame, onChange }: { label: string; value: Point; frame: Frame; onChange: (p: Point) => void }) {
  const set = (axis: 'x' | 'y' | 'z', v: number) => {
    if (!Number.isFinite(v)) return;
    onChange(clampTo({ ...value, [axis]: v }, frame));
  };
  return (
    <div>
      <span className="label">{label}</span>
      <div className="mt-1 grid grid-cols-3 gap-2">
        {(['x', 'y', 'z'] as const).map((axis) => (
          <label key={axis} className="flex items-center gap-1 text-[11px] uppercase text-slate-500">
            {axis}
            <input type="number" step={0.1} aria-label={`${label} ${axis}`} value={Math.round(value[axis] * 100) / 100}
                   onChange={(e) => set(axis, e.target.valueAsNumber)} className="field px-1.5 py-1 text-xs" />
          </label>
        ))}
      </div>
    </div>
  );
}
