import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../api';
import type { AuraliseResult, Directivity, Hrtf, Point, RoomSpec, SourceDto } from '../types';
import { BANDS_HZ, bandLabel } from '../types';
import { wavBytes } from '../export';
import { download } from '../project';

type Settings = { imageSourceOrder: number; temperatureC: number; humidityPercent: number; lwBandsDb: number[]; angleDependent?: boolean };

const SAMPLE_RATE = 44_100;
const MAX_SECONDS = 20;

function floats(base64: string): Float32Array {
  const text = atob(base64);
  const bytes = new Uint8Array(text.length);
  for (let i = 0; i < text.length; i++) bytes[i] = text.charCodeAt(i);
  return new Float32Array(bytes.buffer);
}

/** Deterministic noise so the demo sounds are the same every time. */
function noise(seed: number) {
  let s = seed >>> 0;
  return () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 2147483648 - 1; };
}

type Demo = { key: string; label: string; make: (sr: number) => Float32Array };
const DEMOS: Demo[] = [
  { key: 'claps', label: 'Hand claps', make: (sr) => {
    const out = new Float32Array(sr * 6); const r = noise(3);
    for (let k = 0; k < 6; k++) { const at = Math.floor(sr * (0.2 + k * 0.95)); for (let i = 0; i < sr * 0.03; i++) out[at + i] = r() * Math.exp(-i / (sr * 0.004)); }
    return out; } },
  { key: 'speech', label: 'Speech-like syllables', make: (sr) => {
    // voiced "syllables" (a 120 Hz pulse train through two formant-like resonances) with pauses: a crude stand-in for speech
    const out = new Float32Array(sr * 8); const r = noise(11);
    let y1 = 0, y2 = 0, z1 = 0, z2 = 0;
    const res = (f: number, q: number) => { const w = 2 * Math.PI * f / sr; const rr = Math.exp(-w / (2 * q)); return { a1: 2 * rr * Math.cos(w), a2: -rr * rr }; };
    const f1 = res(600, 6), f2 = res(1700, 8);
    for (let n = 0; n < out.length; n++) {
      const t = n / sr; const syllable = (t % 0.42) / 0.42; const on = (t % 1.9) < 1.3 ? Math.sin(Math.PI * syllable) : 0;
      const pulse = (n % Math.floor(sr / 120)) === 0 ? 1 : 0;
      const e = (pulse + 0.02 * r()) * on;
      const a = e + f1.a1 * y1 + f1.a2 * y2; y2 = y1; y1 = a;
      const b = e + f2.a1 * z1 + f2.a2 * z2; z2 = z1; z1 = b;
      out[n] = 0.01 * (a + b);
    }
    return out; } },
  { key: 'bursts', label: 'Noise bursts', make: (sr) => {
    const out = new Float32Array(sr * 6); const r = noise(5);
    for (let k = 0; k < 4; k++) { const at = Math.floor(sr * (0.2 + k * 1.5)); for (let i = 0; i < sr * 0.4; i++) out[at + i] = r() * Math.min(1, i / (sr * 0.01)) * Math.min(1, (sr * 0.4 - i) / (sr * 0.01)); }
    return out; } },
];

function normalise(...channels: Float32Array[]) {
  let peak = 0;
  for (const c of channels) for (let i = 0; i < c.length; i++) peak = Math.max(peak, Math.abs(c[i]));
  if (peak > 0) for (const c of channels) for (let i = 0; i < c.length; i++) c[i] *= 0.8 / peak;
}

const HEAD_RADIUS_M = 0.0875;
const SPEED_OF_SOUND = 343;

/**
 * Cancels the crosstalk of two loudspeakers, so a binaural signal made for the ears can be played from them: each speaker also sends a copy of what is meant for the
 * far ear, and a delayed, softened, inverted copy is added to the other speaker to cancel it (Atal and Schroeder's recursive canceller). The delay is how much later
 * the sound reaches the far ear round the head (Woodworth), the softening is the head's shadow. It is an approximation: it works best for a listener exactly midway
 * between the speakers, facing them, in a fairly dead room, and it is not part of the room that was simulated.
 */
export function cancelCrosstalk(left: Float32Array, right: Float32Array, sr: number, halfAngleDeg: number): [Float32Array, Float32Array] {
  const theta = (halfAngleDeg * Math.PI) / 180;
  const delay = Math.max(1, Math.round(sr * (HEAD_RADIUS_M / SPEED_OF_SOUND) * (theta + Math.sin(theta))));
  const gain = 0.85;
  const smooth = 1 - Math.exp((-2 * Math.PI * 2000) / sr); // the head's shadow takes the highs off the crossing path
  const yl = new Float32Array(left.length), yr = new Float32Array(right.length);
  let sl = 0, sr_ = 0;
  for (let n = 0; n < left.length; n++) {
    const fromRight = n >= delay ? yr[n - delay] : 0, fromLeft = n >= delay ? yl[n - delay] : 0;
    sl += smooth * (gain * fromRight - sl);
    sr_ += smooth * (gain * fromLeft - sr_);
    yl[n] = left[n] - sl;
    yr[n] = right[n] - sr_;
  }
  return [yl, yr];
}

/** Convolves a mono sound with the two ear responses and returns the stereo result, peak-normalised so it plays at a safe level. For loudspeakers the result is crosstalk-cancelled first. */
const render = (dry: Float32Array, left: Float32Array, right: Float32Array, sr: number, speakers: number | null) => renderMix([dry], [{ left, right }], sr, speakers);

/** Each source's own sound through its own two ear responses, added together, then as {@link render}. The responses must be on one scale. */
async function renderMix(dries: Float32Array[], ears: { left: Float32Array; right: Float32Array }[], sr: number, speakers: number | null): Promise<AudioBuffer> {
  const length = Math.max(...dries.map((d, k) => d.length + ears[k].left.length));
  const offline = new OfflineAudioContext(2, length, sr);
  dries.forEach((dry, k) => {
    const input = offline.createBuffer(1, dry.length, sr); input.copyToChannel(new Float32Array(dry), 0);
    const response = offline.createBuffer(2, ears[k].left.length, sr); response.copyToChannel(new Float32Array(ears[k].left), 0); response.copyToChannel(new Float32Array(ears[k].right), 1);
    const source = offline.createBufferSource(); source.buffer = input;
    const convolver = offline.createConvolver(); convolver.normalize = false; convolver.buffer = response;
    source.connect(convolver); convolver.connect(offline.destination); source.start();
  });
  const out = await offline.startRendering();
  if (speakers !== null) {
    const [l, r] = cancelCrosstalk(out.getChannelData(0), out.getChannelData(1), sr, speakers);
    out.copyToChannel(l as Float32Array<ArrayBuffer>, 0); out.copyToChannel(r as Float32Array<ArrayBuffer>, 1);
  }
  normalise(out.getChannelData(0), out.getChannelData(1));
  return out;
}

export default function Listen({ spec, source, receiver, directivity, extraSources, facing, onFacing, settings, online, disabled }:
  { spec: RoomSpec; source: Point; receiver: Point; directivity: Directivity | null; extraSources: SourceDto[]; facing: number; onFacing: (deg: number) => void;
    settings: Settings; online: boolean | null; disabled: boolean }) {
  const [output, setOutput] = useState<'headphones' | 'speakers'>('headphones');
  const [speakerAngle, setSpeakerAngle] = useState(30);
  const [rays, setRays] = useState(50_000);
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [response, setResponse] = useState<AuraliseResult | null>(null);
  const [demo, setDemo] = useState('speech');
  const [fileName, setFileName] = useState<string | null>(null);
  const [fileAudio, setFileAudio] = useState<Float32Array | null>(null);
  const [mode, setMode] = useState<'room' | 'dry'>('room');
  // with several sources, each can play a sound of its own: a demo's key, or 'file' for a file of its own
  const [separate, setSeparate] = useState(false);
  const [soundOf, setSoundOf] = useState<string[]>([]);
  const [filesOf, setFilesOf] = useState<Record<number, { name: string; audio: Float32Array }>>({});
  const several = extraSources.length > 0;
  // the head heard through: the spherical model, or a measured one uploaded as a SOFA file
  const [hrtf, setHrtf] = useState<Hrtf | null>(null);
  const [measuredHead, setMeasuredHead] = useState(false);
  const [uploadingHead, setUploadingHead] = useState(false);
  const head = measuredHead && hrtf ? hrtf.id : null;
  const apart = separate && several;
  const [playing, setPlaying] = useState(false);
  const [preparing, setPreparing] = useState(false);
  const abort = useRef<AbortController | null>(null);
  const ctx = useRef<AudioContext | null>(null);
  const node = useRef<AudioBufferSourceNode | null>(null);

  function stop() {
    try { node.current?.stop(); } catch { /* not started */ }
    node.current = null;
    setPlaying(false);
  }

  // a response belongs to one room, source, listener and facing: change any and it is out of date
  const key = JSON.stringify([spec, source, receiver, directivity, extraSources, facing, settings.imageSourceOrder, settings.temperatureC, settings.humidityPercent, extraSources.length ? settings.lwBandsDb : null, apart, head, !!settings.angleDependent]);
  const lastKey = useRef(key);
  useEffect(() => { if (lastKey.current !== key) { lastKey.current = key; setResponse(null); stop(); } }, [key]);
  useEffect(() => () => { abort.current?.abort(); stop(); ctx.current?.close().catch(() => undefined); }, []);

  // what the engine is asked for: the binaural response, or the Ambisonic one at the same place
  const asked = () => ({ ...spec, source, listener: receiver, facingAzimuthDeg: facing, rayCount: rays, imageSourceOrder: settings.imageSourceOrder,
    temperatureC: settings.temperatureC, humidityPercent: settings.humidityPercent, sampleRateHz: SAMPLE_RATE, directivity, extraSources: extraSources.length ? extraSources : undefined,
    soundPowerDb: extraSources.length ? settings.lwBandsDb : undefined, separate: apart || undefined, hrtfId: head ?? undefined,
    angleDependentAbsorption: settings.angleDependent || undefined });

  const calculate = async () => {
    abort.current?.abort();
    const controller = new AbortController();
    abort.current = controller;
    stop();
    setBusy(true); setProgress(0); setError(null);
    try {
      const r = await api.auralise(asked(), controller.signal, setProgress);
      setResponse(r);
    } catch (e) {
      if ((e as Error).name !== 'AbortError') setError((e as Error).message);
    } finally {
      if (abort.current === controller) setBusy(false);
    }
  };

  const ears = useMemo(() => (response ? { left: floats(response.leftF32), right: floats(response.rightF32) } : null), [response]);
  const eachEars = useMemo(() => response?.sources?.map((s) => ({ left: floats(s.leftF32), right: floats(s.rightF32) })) ?? null, [response]);

  /** A sound file as mono at the response's rate, at most MAX_SECONDS long; null (with the error shown) if it cannot be read. */
  const decode = async (file: File) => {
    setError(null);
    try {
      ctx.current ??= new AudioContext({ sampleRate: SAMPLE_RATE }); // created at the response's rate, so the decoder resamples the file to it
      const decoded = await ctx.current.decodeAudioData(await file.arrayBuffer());
      const mono = new Float32Array(Math.min(decoded.length, MAX_SECONDS * decoded.sampleRate));
      for (let c = 0; c < decoded.numberOfChannels; c++) { const d = decoded.getChannelData(c); for (let i = 0; i < mono.length; i++) mono[i] += d[i] / decoded.numberOfChannels; }
      return mono;
    } catch {
      setError('That file could not be read as audio. Try a WAV, MP3 or other common audio file.');
      return null;
    }
  };
  const loadFile = async (file: File) => {
    const mono = await decode(file);
    if (mono) { setFileAudio(mono); setFileName(file.name); setDemo('file'); }
  };

  // what each source plays when they play apart: its own file, or a demo, a different one for each to begin with
  const soundKey = (k: number) => soundOf[k] ?? DEMOS[(k + 1) % DEMOS.length].key;
  const dryOf = (k: number, sr: number) => (soundKey(k) === 'file' && filesOf[k] ? filesOf[k].audio : (DEMOS.find((d) => d.key === soundKey(k)) ?? DEMOS[0]).make(sr));
  /** What is heard in the room: every source's own sound through its own response, or the one sound through all of theirs. */
  const inRoom = (sr: number) => {
    if (apart && eachEars) return renderMix(eachEars.map((_, k) => dryOf(k, sr)), eachEars, sr, output === 'speakers' ? speakerAngle : null);
    const dry = demo === 'file' && fileAudio ? fileAudio : (DEMOS.find((d) => d.key === demo) ?? DEMOS[0]).make(sr);
    return render(dry, ears!.left, ears!.right, sr, output === 'speakers' ? speakerAngle : null);
  };

  const play = async () => {
    if (!ears || !response) return;
    stop();
    setPreparing(true); setError(null);
    try {
      ctx.current ??= new AudioContext({ sampleRate: SAMPLE_RATE });
      await ctx.current.resume();
      const sr = response.sampleRateHz;
      const dry = demo === 'file' && fileAudio ? fileAudio : (DEMOS.find((d) => d.key === demo) ?? DEMOS[0]).make(sr);
      let buffer: AudioBuffer;
      if (mode === 'room') {
        buffer = await inRoom(sr);
      } else {
        const copy = new Float32Array(dry); normalise(copy);
        buffer = ctx.current.createBuffer(2, copy.length, sr); buffer.copyToChannel(copy, 0); buffer.copyToChannel(copy, 1);
      }
      const src = ctx.current.createBufferSource(); src.buffer = buffer; src.connect(ctx.current.destination);
      src.onended = () => { if (node.current === src) { node.current = null; setPlaying(false); } };
      src.start(); node.current = src; setPlaying(true);
    } catch (e) {
      setError(`Could not play: ${(e as Error).message}`);
    } finally {
      setPreparing(false);
    }
  };

  const iacc = response?.iaccEarly ?? [];

  // the Ambisonic response at the listener, of the order chosen, worked out on its own and saved as an AmbiX WAV of (order + 1)² channels
  const [ambisonic, setAmbisonic] = useState<number | null>(null);
  const [ambisonicOrder, setAmbisonicOrder] = useState(1);
  const saveAmbisonic = async () => {
    setAmbisonic(0); setError(null);
    try {
      const r = await api.ambisonic({ ...asked(), ambisonicOrder }, undefined, setAmbisonic);
      download(`ambisonic-ambix-order${ambisonicOrder}.wav`, wavBytes(r.channelsF32.map(floats), r.sampleRateHz), 'audio/wav');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setAmbisonic(null);
    }
  };

  // the response itself, or the chosen sound heard through it (as the Play button would), saved as a stereo WAV
  const saveImpulse = () => { if (ears && response) download('impulse-response.wav', wavBytes([ears.left, ears.right], response.sampleRateHz), 'audio/wav'); };
  const saveSound = async () => {
    if (!ears || !response) return;
    setPreparing(true); setError(null);
    try {
      const sr = response.sampleRateHz;
      const dry = demo === 'file' && fileAudio ? fileAudio : (DEMOS.find((d) => d.key === demo) ?? DEMOS[0]).make(sr);
      let left: Float32Array, right: Float32Array;
      if (mode === 'room') {
        const buffer = await inRoom(sr);
        left = buffer.getChannelData(0); right = buffer.getChannelData(1);
      } else {
        left = right = new Float32Array(dry); normalise(left);
      }
      download(`${mode === 'room' ? 'auralised' : 'dry'}-sound.wav`, wavBytes([left, right], sr), 'audio/wav');
    } catch (e) {
      setError(`Could not save: ${(e as Error).message}`);
    } finally {
      setPreparing(false);
    }
  };

  return (
    <div className="grid h-full grid-cols-1 gap-4 overflow-auto lg:grid-cols-2">
      <div className="flex flex-col gap-3">
        <p className="text-xs text-slate-400">
          Hear the room at the receiver: the engine traces the source to a head at the receiver, with the head's shadow and the delay between the ears,
          and a sound of your choice is played through that response. Headphones give the truest result; loudspeakers are an approximation (below).
        </p>
        <div className="grid grid-cols-2 gap-3">
          <label className="label">Listener faces ({facing}°)
            <input type="range" min={0} max={359} value={facing} onChange={(e) => onFacing(+e.target.value)} className="w-full accent-violet-400" />
          </label>
          <label className="label">Rays ({rays.toLocaleString()})
            <input type="range" min={20_000} max={200_000} step={10_000} value={rays} onChange={(e) => setRays(+e.target.value)} className="w-full accent-violet-400" />
          </label>
        </div>
        <p className="text-[11px] text-slate-500">0° is along the room's length (+x), 90° along its width (+y). You can also turn the head by dragging the dot on the arrow at the receiver in the 3-D view. More rays give a smoother tail but take longer.</p>
        <div className="space-y-1.5">
          <div role="group" aria-label="Head" className="flex flex-wrap items-center gap-1.5">
            <span className="label">Head</span>
            <button type="button" aria-pressed={!measuredHead} onClick={() => setMeasuredHead(false)} className={`chip ${!measuredHead ? 'chip-on' : ''}`}>Spherical model</button>
            <button type="button" aria-pressed={measuredHead} disabled={!hrtf} onClick={() => setMeasuredHead(true)} className={`chip ${measuredHead ? 'chip-on' : ''}`} title={hrtf?.name}>
              {hrtf ? (hrtf.name.length > 28 ? hrtf.name.slice(0, 26) + '…' : hrtf.name) : 'Measured'}
            </button>
            <label className="chip cursor-pointer">{uploadingHead ? 'Reading…' : 'Measured HRTF (SOFA)…'}
              <input type="file" accept=".sofa" className="hidden" aria-label="SOFA file"
                     onChange={async (e) => {
                       const f = e.target.files?.[0]; e.target.value = '';
                       if (!f) return;
                       setUploadingHead(true); setError(null);
                       try { setHrtf(await api.uploadHrtf(f)); setMeasuredHead(true); } catch (x) { setError((x as Error).message); } finally { setUploadingHead(false); }
                     }} />
            </label>
          </div>
          {measuredHead && hrtf && <p className="text-[11px] text-slate-500">{hrtf.measurements} directions measured. Each ear's energy is weighted by the measured head's gain in each octave band for the direction the sound comes from (the nearest measured one, equalised to the diffuse field); the delay between the ears is still the sphere's. The head faces where the slider says, level.</p>}
        </div>
        <div className="space-y-1.5">
          <div role="group" aria-label="Listening on" className="flex flex-wrap items-center gap-1.5">
            <span className="label">Listening on</span>
            <button type="button" aria-pressed={output === 'headphones'} onClick={() => setOutput('headphones')} className={`chip ${output === 'headphones' ? 'chip-on' : ''}`}>Headphones</button>
            <button type="button" aria-pressed={output === 'speakers'} onClick={() => setOutput('speakers')} className={`chip ${output === 'speakers' ? 'chip-on' : ''}`}>Loudspeakers</button>
          </div>
          {output === 'speakers' && (
            <>
              <label className="label block">Speakers at ±{speakerAngle}° from straight ahead
                <input type="range" aria-label="Speaker angle" min={10} max={60} step={5} value={speakerAngle} onChange={(e) => setSpeakerAngle(+e.target.value)} className="w-full accent-violet-400" />
              </label>
              <p className="text-[11px] leading-snug text-slate-500">
                The sound is processed so that each ear mostly hears its own channel from a pair of speakers (crosstalk cancellation: a delayed, softened, inverted copy is sent to the other speaker).
                Sit midway between the speakers, facing them, and keep your head still; it is an approximation, and it does not simulate the speakers' own room.
              </p>
            </>
          )}
        </div>
        <div className="flex items-center gap-2">
          {!busy
            ? <button className="btn" disabled={!online || disabled} onClick={calculate}>{response ? 'Recalculate response' : 'Calculate response'}</button>
            : <button className="btn" onClick={() => abort.current?.abort()}>Cancel ({Math.round(progress * 100)}%)</button>}
          {response && <span className="text-xs text-slate-500">{response.durationS.toFixed(1)} s of response, {response.computeSeconds.toFixed(1)} s to compute</span>}
        </div>
        {error && <div className="rounded-lg border border-rose-500/30 bg-rose-500/10 px-3 py-2 text-xs text-rose-300">{error}</div>}
      </div>

      <div className="flex flex-col gap-3">
        <div className="flex flex-wrap items-center gap-2">
          {DEMOS.map((d) => (
            <button key={d.key} onClick={() => setDemo(d.key)} className={`chip ${demo === d.key ? 'chip-on' : ''}`}>{d.label}</button>
          ))}
          {fileName && <button onClick={() => setDemo('file')} className={`chip ${demo === 'file' ? 'chip-on' : ''}`} title={fileName}>{fileName.length > 22 ? fileName.slice(0, 20) + '…' : fileName}</button>}
          <label className="chip cursor-pointer">Your own sound…
            <input type="file" accept="audio/*" className="hidden" onChange={(e) => { const f = e.target.files?.[0]; if (f) void loadFile(f); e.target.value = ''; }} />
          </label>
        </div>
        {several && (
          <div className="space-y-1.5">
            <label className="flex items-center gap-1.5 text-xs text-slate-300">
              <input type="checkbox" aria-label="Each source plays its own sound" checked={separate} onChange={(e) => setSeparate(e.target.checked)} className="accent-violet-400" />
              Each source plays its own sound
            </label>
            {separate && (
              <div className="space-y-1" role="group" aria-label="Sound of each source">
                {[0, ...extraSources.map((_, i) => i + 1)].map((k) => (
                  <div key={k} className="flex flex-wrap items-center gap-1.5 text-xs text-slate-400">
                    <span className="w-16">Source {k + 1}</span>
                    <select aria-label={`Sound of source ${k + 1}`} className="field !w-auto px-1.5 py-1 text-xs" value={soundKey(k)}
                            onChange={(e) => setSoundOf((list) => { const next = [...list]; next[k] = e.target.value; return next; })}>
                      {DEMOS.map((d) => <option key={d.key} value={d.key}>{d.label}</option>)}
                      {filesOf[k] && <option value="file">{filesOf[k].name}</option>}
                    </select>
                    <label className="chip cursor-pointer">A file…
                      <input type="file" accept="audio/*" className="hidden" aria-label={`File for source ${k + 1}`}
                             onChange={async (e) => {
                               const f = e.target.files?.[0]; e.target.value = '';
                               const mono = f && await decode(f);
                               if (f && mono) { setFilesOf((all) => ({ ...all, [k]: { name: f.name, audio: mono } })); setSoundOf((list) => { const next = [...list]; next[k] = 'file'; return next; }); }
                             }} />
                    </label>
                  </div>
                ))}
                <p className="text-[11px] text-slate-500">Each source's response is worked out on its own, at its level against the others, and its sound played through it; Calculate response again after turning this on.</p>
              </div>
            )}
          </div>
        )}
        <div className="flex items-center gap-2">
          <div className="flex gap-1">
            <button onClick={() => setMode('room')} className={`chip ${mode === 'room' ? 'chip-on' : ''}`}>In the room</button>
            <button onClick={() => setMode('dry')} className={`chip ${mode === 'dry' ? 'chip-on' : ''}`}>Dry (no room)</button>
          </div>
          {!playing
            ? <button className="btn" disabled={!response || preparing} onClick={play}>{preparing ? 'Preparing…' : 'Play'}</button>
            : <button className="btn" onClick={stop}>Stop</button>}
        </div>
        {response && (
          <div role="group" aria-label="Save as WAV" className="flex flex-wrap items-center gap-1.5">
            <span className="label">Save as WAV</span>
            <button type="button" className="chip" onClick={saveImpulse} title="The two ears' impulse response, scaled so the larger peak is 0.5">Impulse response</button>
            <button type="button" className="chip" disabled={preparing} onClick={saveSound} title="The chosen sound as it is played now (in the room or dry, headphones or loudspeakers)">Sound as played</button>
          </div>
        )}
        <div className="flex flex-wrap items-center gap-1.5">
          <button type="button" className="chip" disabled={disabled || ambisonic !== null} onClick={saveAmbisonic}
                  title="An Ambisonic impulse response at the listener's position (the facing does not matter) in the AmbiX convention (ACN order, SN3D): 4 channels at first order, 9 at second, 16 at third, for an Ambisonic decoder or a binaural renderer with your own HRTFs">
            {ambisonic === null ? 'Ambisonic response (AmbiX WAV)' : `Working out… ${Math.round(ambisonic * 100)}%`}
          </button>
          <div role="group" aria-label="Ambisonic order" className="flex items-center gap-1 text-[11px] text-slate-400">
            Order
            {[1, 2, 3].map((o) => <button key={o} type="button" aria-pressed={ambisonicOrder === o} onClick={() => setAmbisonicOrder(o)} className={`chip ${ambisonicOrder === o ? 'chip-on' : ''}`}>{o}</button>)}
          </div>
          <span className="text-[11px] text-slate-500">Each moment's sound is a plane wave from where it mostly comes, plus a diffuse part (as in DirAC).</span>
        </div>
        {!response && !busy && <p className="text-xs text-slate-500">Calculate the response first{disabled ? ' (this model cannot be simulated yet)' : ''}; it is kept until you change the room, source or receiver.</p>}
        {response && (
          <div className="grid grid-cols-3 gap-2 text-xs text-slate-300">
            <div className="rounded-md bg-white/5 px-2 py-1.5"><div className="text-[10px] uppercase tracking-wide text-slate-500">Level, left − right</div>{response.interauralLevelDifferenceDb.toFixed(1)} dB</div>
            <div className="rounded-md bg-white/5 px-2 py-1.5"><div className="text-[10px] uppercase tracking-wide text-slate-500">Time, left − right</div>{response.interauralTimeDifferenceMs.toFixed(2)} ms</div>
            <div className="rounded-md bg-white/5 px-2 py-1.5"><div className="text-[10px] uppercase tracking-wide text-slate-500">IACC early (E3)</div>{response.iaccE3 === null ? '–' : response.iaccE3.toFixed(2)}</div>
            <div className="rounded-md bg-white/5 px-2 py-1.5" data-testid="iacc-late"><div className="text-[10px] uppercase tracking-wide text-slate-500">IACC late (L3)</div>{response.iaccL3 === null ? '–' : response.iaccL3.toFixed(2)}</div>
            <div className="rounded-md bg-white/5 px-2 py-1.5" data-testid="bqi"><div className="text-[10px] uppercase tracking-wide text-slate-500">BQI (1 − IACC E3)</div>{response.binauralQualityIndex === null ? '–' : response.binauralQualityIndex.toFixed(2)}</div>
          </div>
        )}
        {response && (
          <div className="text-[11px] text-slate-500">
            Early interaural correlation by band: {BANDS_HZ.map((hz, i) => `${bandLabel(hz)} ${iacc[i] == null ? '–' : iacc[i]!.toFixed(2)}`).join(' · ')}.
            For the head as it faces, from where the sound comes from (a sphere's shadow and delay at each ear), as in the ISO table, which faces the head to the source.
          </div>
        )}
      </div>
    </div>
  );
}
