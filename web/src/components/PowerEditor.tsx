import { useState } from 'react';
import { BANDS_HZ, bandLabel, overallPower, type LevelEntry } from '../types';
import { LP_1M_TO_LW_DB, SPECTRA, lp1mFromLw, lp1mFromSensitivity, lwFromLp1m, mySpectrum, soundPowerOf, spectrumBands, spectrumById, type Spectrum } from '../spectra';
import { Note, Slider } from './ui';

/** A source's level: the spectrum it was set from, its sound power by band (what the engine takes) and how it was entered. */
export type Power = { shape: string; bandsDb: number[]; level?: LevelEntry };

const MIN_DB = -20, MAX_DB = 200; // what the engine accepts
const held = (v: number) => Math.min(MAX_DB, Math.max(MIN_DB, v));
const ZERO = [0, 0, 0, 0, 0, 0, 0, 0];

/** One band's level: the text is the user's while they type, and only a number is passed on, held within what the engine accepts. */
function BandInput({ label, value, onCommit }: { label: string; value: number; onCommit: (v: number) => void }) {
  const [text, setText] = useState<string | null>(null); // what is being typed; null shows the value
  return (
    <input aria-label={label} type="number" step={0.5} min={MIN_DB} max={MAX_DB} className="field mt-0.5 !px-1.5 !py-1 text-[11px]" value={text ?? String(value)}
           onChange={(e) => { setText(e.target.value); const v = e.target.valueAsNumber; if (Number.isFinite(v)) onCommit(held(v)); }}
           onBlur={() => setText(null)} />
  );
}

/** A number typed in a field, passed on only when it is one and within its range. */
function NumberField({ label, value, min, max, step, onCommit }: { label: string; value: number; min: number; max: number; step: number; onCommit: (v: number) => void }) {
  const [text, setText] = useState<string | null>(null); // what is being typed; null shows the value
  return (
    <label className="block text-[11px] text-slate-500">{label}
      <input aria-label={label} type="number" step={step} min={min} max={max} className="field mt-1 px-1.5 py-1 text-xs" value={text ?? String(value)}
             onChange={(e) => { setText(e.target.value); const v = e.target.valueAsNumber; if (Number.isFinite(v) && v >= min && v <= max) onCommit(v); }}
             onBlur={() => setText(null)} />
    </label>
  );
}

const MODES: { key: LevelEntry['mode']; label: string }[] = [
  { key: 'lw', label: 'Sound power' }, { key: 'lp1m', label: 'Level at 1 m' }, { key: 'sens', label: 'Sensitivity + power' },
];

/**
 * One source's level: a spectrum to start from (noise, speech from the standards, or one of the user's own), entered as a sound power, as the level 1 m in
 * front of the source on its axis, or as a loudspeaker's sensitivity and the power fed to it. The last two go through the pattern's on-axis directivity
 * index (`di`, null while it is being worked out), so a horn or a talker gets the right sound power for its level in front of it.
 */
export default function PowerEditor({ value, onChange, di, mine, onSave }: {
  value: Power; onChange: (p: Power) => void; di: number[] | null; mine: Spectrum[]; onSave: (s: Spectrum) => void;
}) {
  const mode = value.level?.mode ?? 'lw';
  const axis = di ?? ZERO; // until the pattern's index is known, a level at 1 m is taken as an omnidirectional source's
  const lp1m = value.level?.lp1mBandsDb ?? lp1mFromLw(value.bandsDb, axis);
  const sensitivity = value.level?.sensitivityDb ?? 90, watts = value.level?.watts ?? 1;
  const shown = mode === 'lw' ? value.bandsDb : mode === 'lp1m' ? lp1m : lp1mFromSensitivity(value.shape, sensitivity, watts, mine);
  const overall = overallPower(shown);
  const chosen = spectrumById(value.shape, mine);
  const groups = [...new Set([...SPECTRA, ...mine].map((s) => s.group))];
  const [naming, setNaming] = useState<string | null>(null);

  // the sound power follows from the level as entered; while the index is not known, the last sound power stands
  const emit = (shape: string, level: LevelEntry | undefined, lw?: number[]) =>
    onChange({ shape, level, bandsDb: (lw ?? soundPowerOf({ shape, bandsDb: value.bandsDb, level }, di, mine) ?? value.bandsDb).map(held) });

  const setMode = (m: LevelEntry['mode']) => {
    if (m === mode) return;
    if (m === 'lw') emit(value.shape, undefined, value.bandsDb);
    else if (m === 'lp1m') emit(value.shape, { mode: 'lp1m', lp1mBandsDb: lp1mFromLw(value.bandsDb, axis) });
    // a sensitivity that gives the level now on axis at 1 W
    else emit(value.shape, { mode: 'sens', sensitivityDb: Math.round(overallPower(lp1mFromLw(value.bandsDb, axis))), watts: 1 });
  };

  const pick = (id: string) => {
    const s = spectrumById(id, mine);
    if (mode === 'sens') { emit(id, value.level); return; } // the spectrum only spreads the sensitivity over the bands
    // a spectrum with a standard level takes it, in whatever its standard gives (a level at 1 m or a sound power); one without keeps the level already set
    const standard = s?.standard ? [...s.shape] : null; // a standard's own levels, exactly as it gives them
    if (mode === 'lw') {
      const lw = standard ? (s!.standard!.quantity === 'lw' ? standard : lwFromLp1m(standard, axis)) : spectrumBands(id, overall, mine);
      emit(id, undefined, lw);
    } else {
      const lp = standard ? (s!.standard!.quantity === 'lp1m' ? standard : lp1mFromLw(standard, axis)) : spectrumBands(id, overall, mine);
      emit(id, { mode: 'lp1m', lp1mBandsDb: lp });
    }
  };

  const setBands = (bands: number[]) => (mode === 'lw' ? emit('custom', undefined, bands) : emit('custom', { mode: 'lp1m', lp1mBandsDb: bands }));
  const quantity = mode === 'lw' ? 'Sound power level Lw' : 'Level 1 m in front, on axis';

  return (
    <div className="space-y-2">
      <div role="group" aria-label="Enter the level as" className="flex flex-wrap items-center gap-1.5 text-[11px] text-slate-400">
        As
        {/* a level at 1 m needs the pattern's directivity index to turn into a sound power, so it waits until that is known */}
        {MODES.map((m) => (
          <button key={m.key} type="button" aria-pressed={mode === m.key} disabled={m.key !== 'lw' && m.key !== mode && di === null} onClick={() => setMode(m.key)}
                  title={m.key !== 'lw' && di === null ? "Working out the pattern's directivity…" : undefined} className={`chip ${mode === m.key ? 'chip-on' : ''}`}>{m.label}</button>
        ))}
      </div>
      <label className="block"><span className="label">Spectrum</span>
        <select aria-label="Source spectrum" className="field mt-1" value={value.shape} onChange={(e) => e.target.value !== 'custom' && pick(e.target.value)}>
          {groups.map((g) => (
            <optgroup key={g} label={g}>{[...SPECTRA, ...mine].filter((s) => s.group === g).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}</optgroup>
          ))}
          {value.shape === 'custom' && <option value="custom">Custom (bands edited)</option>}
        </select>
      </label>
      {mode === 'sens'
        ? (
          <div className="grid grid-cols-2 gap-2">
            <NumberField label="Sensitivity (dB SPL, 1 W, 1 m)" value={sensitivity} min={40} max={160} step={0.5} onCommit={(v) => emit(value.shape, { mode: 'sens', sensitivityDb: v, watts })} />
            <NumberField label="Power fed to it (W)" value={watts} min={0.001} max={100000} step={1} onCommit={(v) => emit(value.shape, { mode: 'sens', sensitivityDb: sensitivity, watts: v })} />
          </div>
        )
        : (
          <>
            <Slider label={`Overall ${mode === 'lw' ? 'sound power level Lw' : 'level at 1 m on axis'}`} value={overall}
                    min={Math.min(40, Math.floor(overall))} max={Math.max(140, Math.ceil(overall))} step={0.1} unit=" dB"
                    onChange={(v) => { const delta = v - overall; setBands(shown.map((l) => held(Math.round((l + delta) * 10) / 10))); }} />
            <div className="grid grid-cols-4 gap-1.5">
              {BANDS_HZ.map((hz, b) => (
                <label key={hz} className="block text-[10px] text-slate-500">{bandLabel(hz)} Hz
                  <BandInput label={`${mode === 'lw' ? 'Lw' : 'Lp at 1 m'} at ${bandLabel(hz)} Hz`} value={shown[b]} onCommit={(v) => setBands(shown.map((l, i) => (i === b ? v : l)))} />
                </label>
              ))}
            </div>
          </>
        )}
      <p className="text-[11px] tabular-nums text-slate-300" data-testid="power-summary">
        Sound power Lw {overallPower(value.bandsDb).toFixed(1)} dB{mode !== 'lw' && <>; {quantity.toLowerCase()} {overallPower(shown).toFixed(1)} dB</>}
        {mode !== 'lw' && di === null && <span className="text-slate-500"> (working out the pattern's directivity…)</span>}
      </p>
      {naming === null
        ? <button type="button" className="chip" onClick={() => setNaming(chosen && value.shape !== 'custom' ? `${chosen.name.replace(/ \(.*\)$/, '')} (mine)` : 'My spectrum')}>Save as my spectrum…</button>
        : (
          <form className="flex gap-1.5" onSubmit={(e) => {
            e.preventDefault();
            if (!naming.trim()) return;
            const saved = mySpectrum(naming.trim(), mode === 'lw' ? value.bandsDb : shown, mode === 'lw' ? 'lw' : 'lp1m');
            onSave(saved);
            emit(saved.id, value.level);
            setNaming(null);
          }}>
            <input aria-label="Name of my spectrum" className="field px-1.5 py-1 text-xs" value={naming} onChange={(e) => setNaming(e.target.value)} autoFocus />
            <button type="submit" className="chip">Save</button>
            <button type="button" className="chip" onClick={() => setNaming(null)}>Cancel</button>
          </form>
        )}
      <Note>
        {mode === 'lw'
          ? <>Sound power level (dB re 1 pW) in each octave band. If the source radiated equally in every direction its level 1 m away in a free field would be {(overall - LP_1M_TO_LW_DB).toFixed(1)} dB (Lp = Lw − 11 dB).</>
          : <>Lw = Lp(1 m, on axis) + 11 − DI, where DI is this pattern's directivity index on its axis{di ? <> ({di.map((d) => d.toFixed(1)).join(', ')} dB, 63 Hz to 8 kHz)</> : ''}, so a directional source needs less power for the same level in front of it.{mode === 'sens' ? ' The sensitivity is spread over the bands by the spectrum; an electrical power above what the loudspeaker can take is not checked.' : ''}</>}
        {' '}At the receiver, Lp = Lw + G − 31 dB. {chosen ? chosen.note : 'You have changed the bands from a standard spectrum.'}
      </Note>
    </div>
  );
}
