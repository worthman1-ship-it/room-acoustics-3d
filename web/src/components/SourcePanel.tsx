import { useRef, useState } from 'react';
import type { Balloon, Directivity, ExtraReceiver, ExtraSource, LineArray, Point, PolarCut } from '../types';
import { BANDS_HZ, bandLabel } from '../types';
import type { Frame } from '../frame';
import { overallPower } from '../types';
import { spectrumName, type Spectrum } from '../spectra';
import PowerEditor, { type Power } from './PowerEditor';
import { Note, PointFields, Section, Slider } from './ui';

export const MAX_SOURCES = 8;
export const MAX_RECEIVERS = 8;

const HORN: Directivity = { kind: 'horn', horizontalDeg: 90, verticalDeg: 40, aimAzimuthDeg: 0, aimElevationDeg: 0 };
const ARRAY: LineArray = { elements: 8, spacingM: 0.2, orientation: 'vertical', delayStepMs: 0, splayDegPerElement: 0, shading: 'uniform', sidelobeDb: 30 };

type Kind = 'omni' | 'horn' | 'lineArray' | 'balloon' | 'talker' | 'surface';
const SURFACE = { widthM: 2, heightM: 1.5, oneSided: true };
const kindOf = (d: Directivity | null): Kind => (d === null ? 'omni' : d.kind ?? 'horn');

/** Two cuts through a measured pattern on a polar plot: horizontal (elevation 0) and vertical (azimuth 0), 0° at the top, in the band chosen. The rings are 6 dB apart. */
function Polar({ balloon, bandIndex }: { balloon: Balloon; bandIndex: number }) {
  const R = 52;
  const top = balloon.maxDb, floor = Math.max(balloon.minDb, top - 36);
  const radius = (db: number) => R * (0.12 + 0.88 * Math.min(1, Math.max(0, (db - floor) / (top - floor || 1))));
  const path = (cut: PolarCut | undefined) => {
    if (!cut) return '';
    return cut.anglesDeg.map((a, i) => {
      const r = radius(cut.levelsDb[i]), t = (a * Math.PI) / 180;
      return `${i === 0 ? 'M' : 'L'}${(r * Math.sin(t)).toFixed(1)},${(-r * Math.cos(t)).toFixed(1)}`;
    }).join(' ') + 'Z';
  };
  const h = balloon.horizontal[bandIndex], v = balloon.vertical[bandIndex];
  const rings = Array.from({ length: Math.floor((top - floor) / 6) + 1 }, (_, i) => top - i * 6);
  return (
    <figure className="flex items-center gap-3">
      <svg viewBox={`${-R - 4} ${-R - 4} ${2 * R + 8} ${2 * R + 8}`} className="h-28 w-28 shrink-0" role="img" aria-label={`Polar pattern of ${balloon.name} at ${bandLabel(BANDS_HZ[bandIndex])} Hz`}>
        {rings.map((db) => <circle key={db} r={radius(db)} fill="none" stroke="#303038" strokeWidth={0.5} />)}
        <line x1={-R} x2={R} stroke="#303038" strokeWidth={0.5} /><line y1={-R} y2={R} stroke="#303038" strokeWidth={0.5} />
        <path d={path(v)} fill="#a78bfa" fillOpacity={0.12} stroke="#a78bfa" strokeWidth={1.2} />
        <path d={path(h)} fill="#34d399" fillOpacity={0.12} stroke="#34d399" strokeWidth={1.2} />
      </svg>
      <figcaption className="text-[11px] leading-snug text-slate-400">
        <div><span className="text-emerald-400">━</span> horizontal cut</div>
        <div><span className="text-violet-400">━</span> vertical cut</div>
        <div className="mt-1 text-slate-500">{bandLabel(BANDS_HZ[bandIndex])} Hz · rings every 6 dB, outer {top.toFixed(0)} dB</div>
      </figcaption>
    </figure>
  );
}

function Segment<T extends string>({ value, options, onChange, label }: { value: T; options: { key: T; label: string }[]; onChange: (v: T) => void; label: string }) {
  return (
    <div role="group" aria-label={label} className="flex flex-wrap gap-1.5">
      {options.map((o) => <button key={o.key} type="button" aria-pressed={value === o.key} onClick={() => onChange(o.key)} className={`chip ${value === o.key ? 'chip-on' : ''}`}>{o.label}</button>)}
    </div>
  );
}

/** Everything about how one source radiates: omnidirectional, horn-like, a line array, or a pattern measured elsewhere and uploaded. */
function PatternEditor({ value, onChange, balloons, bandIndex, onUpload, uploading, uploadError }: {
  value: Directivity | null; onChange: (d: Directivity | null) => void; balloons: Record<string, Balloon>; bandIndex: number;
  onUpload: (file: File) => Promise<Balloon | null>; uploading: boolean; uploadError: string | null;
}) {
  const file = useRef<HTMLInputElement>(null);
  const anyFile = useRef<HTMLInputElement>(null);
  const kind = kindOf(value);
  const choose = (k: Kind) => {
    if (k === kind) return;
    const aim = value ? { aimAzimuthDeg: value.aimAzimuthDeg, aimElevationDeg: value.aimElevationDeg } : { aimAzimuthDeg: 0, aimElevationDeg: 0 };
    if (k === 'omni') onChange(null);
    else if (k === 'horn') onChange({ ...HORN, ...aim, horizontalDeg: value?.horizontalDeg ?? HORN.horizontalDeg, verticalDeg: value?.verticalDeg ?? HORN.verticalDeg });
    else if (k === 'lineArray') onChange({ ...HORN, ...aim, horizontalDeg: 120, verticalDeg: 120, kind: 'lineArray', lineArray: value?.lineArray ?? ARRAY });
    else if (k === 'talker') onChange({ ...HORN, ...aim, kind: 'talker' });
    else if (k === 'surface') onChange({ ...HORN, ...aim, kind: 'surface', surface: value?.surface ?? SURFACE });
    else onChange({ ...HORN, ...aim, kind: 'balloon', balloonId: value?.balloonId });
  };
  const array = value?.lineArray ?? ARRAY;
  const setArray = (patch: Partial<LineArray>) => value && onChange({ ...value, lineArray: { ...array, ...patch } });
  const balloon = value?.balloonId ? balloons[value.balloonId] : undefined;

  return (
    <div className="space-y-3">
      <Segment label="Source pattern" value={kind} onChange={choose}
               options={[{ key: 'omni', label: 'Omnidirectional' }, { key: 'horn', label: 'Horn-like' }, { key: 'lineArray', label: 'Line array' }, { key: 'balloon', label: 'Loudspeaker' }, { key: 'talker', label: 'Talker' }, { key: 'surface', label: 'Surface' }]} />
      {value && kind === 'surface' && (
        <>
          <Slider label="Surface width" value={value.surface?.widthM ?? SURFACE.widthM} min={0.1} max={30} step={0.1} unit=" m" onChange={(v) => onChange({ ...value, surface: { ...(value.surface ?? SURFACE), widthM: v } })} />
          <Slider label="Surface height" value={value.surface?.heightM ?? SURFACE.heightM} min={0.1} max={30} step={0.1} unit=" m" onChange={(v) => onChange({ ...value, surface: { ...(value.surface ?? SURFACE), heightM: v } })} />
          <label className="flex items-center gap-1.5 text-[11px] text-slate-400">
            <input type="checkbox" aria-label="Radiates from one side" checked={value.surface?.oneSided ?? true} className="accent-violet-400"
                   onChange={(e) => onChange({ ...value, surface: { ...(value.surface ?? SURFACE), oneSided: e.target.checked } })} />
            Radiates from the side it faces only
          </label>
          <Note>A rectangle centred on the source and facing where it is aimed (up–down 90° for a floor, −90° for a ceiling), its power spread evenly over its area: a noisy wall or machine face, a window letting traffic noise in, a ceiling of diffusers. It is traced as a grid of points about 0.5 m apart (at most 8 each way), each radiating as a cosine to the side it faces, or every way if not one-sided. Keep it inside the room.</Note>
        </>
      )}
      {value && kind === 'talker' && (
        <Note>A person talking, facing where it is aimed: Chu and Warnock's measured directivity of 40 talkers (NRC report RR-104, 2002), louder in front, quieter behind and below. Its directivity index on axis is about 1.5 dB up to 1 kHz and 4 dB from 2 to 8 kHz (63 and 125 Hz take the 160 Hz third octave, the lowest measured; nothing was measured more than 53° below the horizontal). Enter the voice as a level at 1 m to get its sound power.</Note>
      )}
      {value && kind === 'horn' && (
        <>
          <Slider label="Horizontal coverage (−6 dB)" value={value.horizontalDeg} min={10} max={360} step={5} unit="°" onChange={(v) => onChange({ ...value, horizontalDeg: v })} />
          <Slider label="Vertical coverage (−6 dB)" value={value.verticalDeg} min={10} max={180} step={5} unit="°" onChange={(v) => onChange({ ...value, verticalDeg: v })} />
        </>
      )}
      {value && kind === 'lineArray' && (
        <>
          <Slider label="Elements" value={array.elements} min={2} max={32} step={1} onChange={(v) => setArray({ elements: v })} />
          <Slider label="Spacing between elements" value={array.spacingM} min={0.05} max={0.6} step={0.01} unit=" m" onChange={(v) => setArray({ spacingM: v })} />
          <Segment label="Array direction" value={array.orientation} onChange={(v) => setArray({ orientation: v })} options={[{ key: 'vertical', label: 'Hung vertically' }, { key: 'horizontal', label: 'Laid sideways' }]} />
          <Slider label="Delay step (steers the beam)" value={array.delayStepMs} min={-1} max={1} step={0.05} unit=" ms" onChange={(v) => setArray({ delayStepMs: v })} />
          <Slider label="Splay (curves the array)" value={array.splayDegPerElement} min={0} max={10} step={0.5} unit="° per element" onChange={(v) => setArray({ splayDegPerElement: v })} />
          <label className="block"><span className="label">Shading</span>
            <select aria-label="Array shading" className="field mt-1" value={array.shading} onChange={(e) => setArray({ shading: e.target.value as LineArray['shading'] })}>
              <option value="uniform">Uniform (narrowest beam, strongest side lobes)</option>
              <option value="hamming">Hamming</option><option value="hann">Hann</option><option value="sine">Sine</option>
              <option value="chebyshev">Chebyshev (side lobes held to a level)</option>
            </select>
          </label>
          {array.shading === 'chebyshev' && <Slider label="Side lobes held below" value={array.sidelobeDb} min={15} max={60} step={1} unit=" dB" onChange={(v) => setArray({ sidelobeDb: v })} />}
          <Slider label="Each element's horizontal coverage" value={value.horizontalDeg} min={10} max={360} step={5} unit="°" onChange={(v) => onChange({ ...value, horizontalDeg: v })} />
          <Slider label="Each element's vertical coverage" value={value.verticalDeg} min={10} max={180} step={5} unit="°" onChange={(v) => onChange({ ...value, verticalDeg: v })} />
          <Note>The elements add coherently, so the beam narrows at high frequencies and the array is nearly omnidirectional at low ones. A positive delay step tilts the beam towards the later elements.</Note>
        </>
      )}
      {value && kind === 'balloon' && (
        <>
          {/* the engine tells the kinds apart by what a file holds, so a file of any name can be offered; the second input has no filter */}
          {([[file, '.csv,.txt,.tab,.clf,.cf1,.cf2,.lob,text/csv', 'Loudspeaker or instrument file'], [anyFile, undefined, 'Pattern file of any type']] as const).map(([ref, accept, label]) => (
            <input key={label} ref={ref} type="file" accept={accept} hidden aria-label={label}
                   onChange={async (e) => { const f = e.target.files?.[0]; e.target.value = ''; if (!f) return; const b = await onUpload(f); if (b) onChange({ ...value, balloonId: b.id }); }} />
          ))}
          <button type="button" onClick={() => file.current?.click()} disabled={uploading} className="btn w-full !bg-ink-700 hover:!bg-ink-600">{uploading ? 'Reading…' : balloon ? `Replace “${balloon.name}”…` : 'Upload a loudspeaker or instrument file (CF2, CLF, EASE .lob, CIF or CSV)…'}</button>
          <button type="button" onClick={() => anyFile.current?.click()} disabled={uploading} className="chip w-full justify-center"
                  title="Choose from all files, whatever their extension: the file is read by what it holds">All files…</button>
          {uploadError && <div role="alert" className="rounded-md border border-rose-500/30 bg-rose-500/10 px-2 py-1.5 text-[11px] text-rose-300">{uploadError}</div>}
          {balloon
            ? <Polar balloon={balloon} bandIndex={bandIndex} />
            : <Note>A CSV with a header, then one line per direction: azimuth (0–360°), elevation (−90 to 90°) and the level in dB, either as one column or as the eight octave bands 63 Hz to 8 kHz. Every combination of the azimuths and elevations must be present. The pattern points along +x until you aim it. A Common Loudspeaker Format text file (CLF1 or CLF2, TAB separated) is also read: its model name is used, third-octave bands are joined into octave bands, and its sensitivity and cabinet are ignored. So is an instrument's or a talker's pattern in the Common Instrument Format as BYU's directivity archive gives it beside each .CI2 file (the "ASCII format" .csv, named after the file). Each is read by what it holds, whatever its extension (All files… shows every file). CLF's binary distribution file for third-octave speakers (.CF2, such as manufacturers offer for download) is read too; a .CF1 is not: save it as a CLF text file from the free CLF Viewer first. So is an EASE 3 or 4 speaker's balloon: its .lob file, beside the .spk (which describes the speaker but has no balloon). EASE's newer GLL files are not read.</Note>}
        </>
      )}
      {value && (
        <>
          <Slider label="Aim, left–right" value={value.aimAzimuthDeg} min={-180} max={180} step={5} unit="°" onChange={(v) => onChange({ ...value, aimAzimuthDeg: v })} />
          <Slider label="Aim, up–down" value={value.aimElevationDeg} min={-90} max={90} step={5} unit="°" onChange={(v) => onChange({ ...value, aimElevationDeg: v })} />
          <Note>Left–right is measured from the room's +x direction towards +y; 0° points along the length. You can also drag the dot on the arrow in the 3-D view.</Note>
        </>
      )}
    </div>
  );
}

type Props = {
  source: Point; onSource: (p: Point) => void; receiver: Point; onReceiver: (p: Point) => void; frame: Frame;
  directivity: Directivity | null; onDirectivity: (d: Directivity | null) => void;
  extras: ExtraSource[]; onExtras: (e: ExtraSource[]) => void; onAddExtra: () => void;
  extraReceivers: ExtraReceiver[]; onExtraReceivers: (r: ExtraReceiver[]) => void; onAddReceiver: () => void; onReceiverLine: () => void;
  active: number; onActive: (id: number) => void;
  balloons: Record<string, Balloon>; onUpload: (file: File) => Promise<Balloon>; bandIndex: number;
  /** Source 1's level (it lives with the settings, the others' with each source), whether it is muted or soloed, and how many sources are heard */
  power: Power; onPower: (p: Power) => void;
  flags1: { muted: boolean; solo: boolean }; onFlags1: (f: { muted?: boolean; solo?: boolean }) => void; heard: number;
  /** a pattern's on-axis directivity index by band (null while it is being worked out), and the user's own spectra */
  di: (d: Directivity | null) => number[] | null; mine: Spectrum[]; onSaveSpectrum: (s: Spectrum) => void;
  /** the whole editor, as in the sources window, or the short form for the side panel, with a button to open the window */
  full: boolean; onOpenWindow: () => void; windowOpen: boolean;
};

const KIND_NAMES: Record<Kind, string> = { omni: 'Omnidirectional', horn: 'Horn-like', lineArray: 'Line array', balloon: 'Loudspeaker', talker: 'Talker', surface: 'Surface source' };
const LEVEL_NAMES = { lw: 'Lw', lp1m: 'set by its level at 1 m', sens: 'set by sensitivity and power' } as const;

/** The sources: the first one, any others in the same room (each with its own sound power spectrum), and the receivers. In the side panel it is the short form; the sources window has all of it. */
export default function SourcePanel(p: Props) {
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const upload = async (file: File) => {
    setUploading(true); setUploadError(null);
    try { return await p.onUpload(file); } catch (e) { setUploadError((e as Error).message); return null; } finally { setUploading(false); }
  };
  const extra = p.extras.find((e) => e.id === p.active);
  const change = (patch: Partial<ExtraSource>) => extra && p.onExtras(p.extras.map((e) => (e.id === extra.id ? { ...e, ...patch } : e)));
  const number = (v: number, fallback: number) => (Number.isFinite(v) ? v : fallback);
  const power: Power = extra ? { shape: extra.lwShape, bandsDb: extra.lwBandsDb, level: extra.level } : p.power;
  const flags = extra ? { muted: !!extra.muted, solo: !!extra.solo } : p.flags1;
  const setFlags = (f: { muted?: boolean; solo?: boolean }) => (extra ? change(f) : p.onFlags1(f));
  // a muted source is struck through and a soloed one ringed, so the chips show at a glance which are heard
  const chipLook = (muted: boolean | undefined, solo: boolean | undefined) => `${muted ? 'line-through opacity-60' : ''} ${solo ? 'ring-1 ring-amber-400' : ''}`;
  const number1 = p.extras.indexOf(extra as ExtraSource) + 2;
  const name = extra ? `source ${number1}` : 'source 1';

  return (
    <>
      <Section title={p.full ? 'Sources and receivers' : 'Sources and receiver'}>
        <div role="group" aria-label="Sources" className="flex flex-wrap items-center gap-1.5">
          <button type="button" aria-pressed={p.active === 0} onClick={() => p.onActive(0)} className={`chip ${p.active === 0 ? 'chip-on' : ''} ${chipLook(p.flags1.muted, p.flags1.solo)}`}>Source 1</button>
          {p.extras.map((e, i) => (
            <button key={e.id} type="button" aria-pressed={p.active === e.id} onClick={() => p.onActive(e.id)} className={`chip ${p.active === e.id ? 'chip-on' : ''} ${chipLook(e.muted, e.solo)}`}>Source {i + 2}</button>
          ))}
          <button type="button" onClick={p.onAddExtra} disabled={p.extras.length + 1 >= MAX_SOURCES} title="Add another source in the same room" className="chip">+ Add</button>
        </div>
        <div role="group" aria-label={`Hear ${name}`} className="flex items-center gap-1.5 text-[11px] text-slate-400">
          <button type="button" aria-pressed={flags.muted} aria-label="Mute this source" title="Leave this source out of every calculation" onClick={() => setFlags({ muted: !flags.muted })} className={`chip ${flags.muted ? 'chip-on' : ''}`}>Mute</button>
          <button type="button" aria-pressed={flags.solo} aria-label="Solo this source" title="Hear only the soloed sources" onClick={() => setFlags({ solo: !flags.solo })} className={`chip ${flags.solo ? 'chip-on' : ''}`}>Solo</button>
          {p.heard === 0 && <span role="alert" className="text-amber-300">Every source is muted: nothing can be calculated.</span>}
        </div>
        {extra
          ? <PointFields label={`Source ${p.extras.indexOf(extra) + 2}`} value={extra.position} frame={p.frame} onChange={(position) => change({ position })} />
          : <PointFields label="Source 1" value={p.source} frame={p.frame} onChange={p.onSource} />}
        {extra && p.full && (
          <label className="block text-[11px] text-slate-500">Delay (ms)
            <input type="number" step={1} min={0} max={500} aria-label="Source delay in ms" value={extra.delayMs} onChange={(e) => change({ delayMs: Math.min(500, Math.max(0, number(e.target.valueAsNumber, 0))) })} className="field mt-1 px-1.5 py-1 text-xs" />
          </label>
        )}
        {extra && <button type="button" onClick={() => { p.onExtras(p.extras.filter((e) => e.id !== extra.id)); p.onActive(0); }} className="chip w-full text-center">Remove this source</button>}
        <PointFields label={p.extraReceivers.length ? 'Receiver 1' : 'Receiver'} value={p.receiver} frame={p.frame} onChange={p.onReceiver} />
        {p.extraReceivers.map((r, i) => (
          <div key={r.id} className="space-y-1" data-testid="extra-receiver">
            <PointFields label={`Receiver ${i + 2}`} value={r.position} frame={p.frame}
                         onChange={(position) => p.onExtraReceivers(p.extraReceivers.map((x) => (x.id === r.id ? { ...x, position } : x)))} />
            <button type="button" onClick={() => p.onExtraReceivers(p.extraReceivers.filter((x) => x.id !== r.id))} className="chip w-full text-center">Remove receiver {i + 2}</button>
          </div>
        ))}
        <button type="button" onClick={p.onAddReceiver} disabled={p.extraReceivers.length + 1 >= MAX_RECEIVERS} title="Add another receiver: Run simulation then gives every result for each" className="chip w-full text-center">+ Add receiver</button>
        <button type="button" onClick={p.onReceiverLine} title={`Put ${MAX_RECEIVERS} receivers in a line from the selected source towards receiver 1, 2 m to 16 m out (or to the wall), for the ISO 3382-3 open-plan office results`} className="chip w-full text-center">Receivers in a line</button>
        {p.full
          ? <Note>Or click a source or the receiver in the 3-D view and drag its arrows. Each source has its own sound power spectrum below; a delay is a delay line in front of that loudspeaker.</Note>
          : (
            <>
              <p className="text-[11px] leading-snug text-slate-400" data-testid="source-summary">
                {name[0].toUpperCase() + name.slice(1)}: {KIND_NAMES[kindOf(extra ? extra.directivity : p.directivity)]}, Lw {overallPower(power.bandsDb).toFixed(1)} dB ({spectrumName(power.shape, p.mine)}
                {power.level && power.level.mode !== 'lw' ? `; ${LEVEL_NAMES[power.level.mode]}` : ''})
                {extra && extra.delayMs > 0 ? `, ${extra.delayMs} ms delay` : ''}{flags.muted ? ', muted' : ''}{flags.solo ? ', soloed' : ''}
              </p>
              <button type="button" onClick={p.onOpenWindow} className="btn w-full !bg-ink-700 hover:!bg-ink-600">{p.windowOpen ? 'Close sources window' : 'Open sources window'}</button>
            </>
          )}
      </Section>

      {p.full && (
        <>
          <Section title={`Level and spectrum of ${name}`}>
            <PowerEditor key={p.active} value={power} onChange={(v) => (extra ? change({ lwShape: v.shape, lwBandsDb: v.bandsDb, level: v.level }) : p.onPower(v))}
                         di={p.di(extra ? extra.directivity : p.directivity)} mine={p.mine} onSave={p.onSaveSpectrum} />
          </Section>

          <Section title={`Pattern of ${name}`}>
            <PatternEditor
              key={p.active}
              value={extra ? extra.directivity : p.directivity}
              onChange={(d) => (extra ? change({ directivity: d }) : p.onDirectivity(d))}
              balloons={p.balloons} bandIndex={p.bandIndex} onUpload={upload} uploading={uploading} uploadError={uploadError} />
          </Section>
        </>
      )}
    </>
  );
}
