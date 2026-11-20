import type { AmbisonicResult, AuraliseResult, Balloon, Hrtf, Measured, Directivity, MapMode, MapRaw, Material, Model, Point, Ray, ReflectionsResult, RoomSpec, SimulateResponse, SourceDto, ThirdOctaveResult } from './types';

async function send<T>(path: string, init: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, init);
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try { message = (await response.json()).error ?? message; } catch { /* not JSON */ }
    throw new Error(message);
  }
  return response.json() as Promise<T>;
}

const get = <T>(path: string) => send<T>(path, { method: 'GET' });
const post = <T>(path: string, body: unknown, signal?: AbortSignal) =>
  send<T>(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal });

/** What can be done to a model's surface group: give it a new name, put it into another group, or take it out of the model. */
export type GroupAction = 'rename' | 'merge' | 'delete';

/** A change to a model's shape: a corner moved (from the first point to the second), a flat face drawn round the points in a group, or the flat surface of the face with the three points as corners deleted. */
export type GeometryEdit = { action: 'move'; points: [Point, Point] } | { action: 'face'; points: Point[]; group: string } | { action: 'delete'; points: [Point, Point, Point] };

export type SimulateParams = RoomSpec & {
  source: Point; receiver: Point; rayCount: number; imageSourceOrder: number; temperatureC: number; humidityPercent: number; directivity?: Directivity | null;
  extraSources?: SourceDto[];
  /** For the Speech Transmission Index: speech at 1 m in dBA, the background as an NC rating (0 for none) and the voice. */
  speechLevelDbA?: number; backgroundNc?: number; voice?: 'male' | 'female';
  /** The first source's sound power level in each octave band (63 Hz to 8 kHz), for the sound pressure levels. */
  soundPowerDb?: number[];
  /** Further receivers: results are given for each, the main one first. */
  extraReceivers?: Point[];
  /** The STI's speech: the sources' own levels, or a talker at speechLevelDbA at 1 m. */
  stiSignal?: 'sources' | 'talker';
  /** Each reflection takes the absorption of its angle of incidence. */
  angleDependentAbsorption?: boolean;
  /** ISO 14257's d1 and d2, m (the engine takes 5 m and 16 m when absent) */
  workroomD1M?: number; workroomD2M?: number;
};

export type RaysParams = RoomSpec & {
  source: Point; receiver: Point; rayCount: number; maxReflections: number; band: number; temperatureC: number; humidityPercent: number; directivity?: Directivity | null;
  extraSources?: SourceDto[];
};

export type MapParams = RoomSpec & {
  source: Point; metric: string; heightM: number; spacingM: number; rayCount: number; imageSourceOrder: number;
  temperatureC: number; humidityPercent: number; directivity?: Directivity | null; extraSources?: SourceDto[];
  mode?: MapMode; axis?: 'x' | 'y'; positionM?: number; heightsM?: number[]; surfaceGroup?: string;
  /** For the STI map: speech at 1 m in dBA, the background as an NC rating (0 for none) and the voice. */
  speechLevelDbA?: number; backgroundNc?: number; voice?: 'male' | 'female';
  soundPowerDb?: number[]; stiSignal?: 'sources' | 'talker';
  /** The surface whose first reflections to map (the Reflector parameter), and the window after the direct sound they must arrive in. */
  reflectorGroup?: string; reflectorFromMs?: number; reflectorToMs?: number;
  angleDependentAbsorption?: boolean;
};

export type ReflectionsParams = RoomSpec & {
  source: Point; receiver: Point; maxOrder: number; temperatureC: number; humidityPercent: number; directivity?: Directivity | null;
};

export type AuraliseParams = RoomSpec & {
  source: Point; listener: Point; facingAzimuthDeg: number; rayCount: number; imageSourceOrder: number; temperatureC: number; humidityPercent: number;
  sampleRateHz: number; directivity?: Directivity | null; extraSources?: SourceDto[]; soundPowerDb?: number[];
  /** also each source's response on its own, so each can play its own sound */
  separate?: boolean;
  /** a measured head (uploaded SOFA file) to hear through instead of the spherical model */
  hrtfId?: string;
  angleDependentAbsorption?: boolean;
  /** for the Ambisonic response: its order, 1 to 3 (4, 9 or 16 channels) */
  ambisonicOrder?: number;
};

type JobView<T> = { id: string; status: 'running' | 'done' | 'failed' | 'cancelled'; progress: number; elapsedSeconds: number; error: string | null; result: T | null };

const aborted = () => new DOMException('The calculation was cancelled.', 'AbortError');

const sleep = (ms: number, signal: AbortSignal) => new Promise<void>((resolve, reject) => {
  if (signal.aborted) { reject(aborted()); return; }
  const timer = setTimeout(() => { signal.removeEventListener('abort', onAbort); resolve(); }, ms);
  const onAbort = () => { clearTimeout(timer); reject(aborted()); };
  signal.addEventListener('abort', onAbort, { once: true });
});

/** Starts a calculation on the engine, follows its progress, and gives the result; aborting the signal cancels it on the engine too, so it stops using the processor. */
async function job<T>(kind: 'simulate' | 'map' | 'auralise' | 'ambisonic' | 'thirdoctave', body: unknown, onProgress: ((fraction: number) => void) | undefined, signal: AbortSignal | undefined): Promise<T> {
  const control = signal ?? new AbortController().signal;
  const started = await post<JobView<T>>(`/jobs/${kind}`, body);
  const cancel = () => { fetch(`/api/jobs/${started.id}`, { method: 'DELETE' }).catch(() => { /* it was going to stop anyway */ }); };
  control.addEventListener('abort', cancel, { once: true });
  try {
    for (;;) {
      await sleep(300, control);
      const status = await get<JobView<T>>(`/jobs/${started.id}`);
      onProgress?.(status.progress);
      if (status.status === 'done') return status.result as T;
      if (status.status === 'failed') throw new Error(status.error ?? 'The calculation failed.');
      if (status.status === 'cancelled') throw aborted();
    }
  } finally {
    control.removeEventListener('abort', cancel);
  }
}

export const api = {
  health: () => get<{ status: string; version?: string }>('/health'),
  materials: () => get<Material[]>('/materials'),
  examples: () => get<string[]>('/examples'),
  simulate: (p: SimulateParams, signal?: AbortSignal, onProgress?: (fraction: number) => void) => job<SimulateResponse>('simulate', p, onProgress, signal),
  /** The standard statistical equations on the room, at once: no rays, so no job to follow. */
  statistical: (p: SimulateParams, signal?: AbortSignal) => post<SimulateResponse>('/statistical', p, signal),
  /** The same request worked out at the main receiver in the 23 third-octave bands 50 Hz to 8 kHz (three passes of the tracer). */
  thirdOctaves: (p: SimulateParams, signal?: AbortSignal, onProgress?: (fraction: number) => void) => job<ThirdOctaveResult>('thirdoctave', p, onProgress, signal),
  map: (p: MapParams, signal?: AbortSignal, onProgress?: (fraction: number) => void) => job<MapRaw>('map', p, onProgress, signal),
  auralise: (p: AuraliseParams, signal?: AbortSignal, onProgress?: (fraction: number) => void) => job<AuraliseResult>('auralise', p, onProgress, signal),
  /** The first-order Ambisonic (AmbiX) impulse response at the listener's position, from the same request. */
  ambisonic: (p: AuraliseParams, signal?: AbortSignal, onProgress?: (fraction: number) => void) => job<AmbisonicResult>('ambisonic', p, onProgress, signal),
  rays: (p: RaysParams, signal?: AbortSignal) => post<Ray[]>('/rays', p, signal),
  /** The specular reflections at the receiver from the first source: surfaces, delay and level against the direct sound. */
  reflections: (p: ReflectionsParams, signal?: AbortSignal) => post<ReflectionsResult>('/reflections', p, signal),
  /** Uploads a measured head-related transfer function as a SOFA file; the file's bytes are the body. */
  uploadHrtf: async (file: File) => send<Hrtf>(`/hrtf/import?name=${encodeURIComponent(file.name)}`, { method: 'POST', body: await file.arrayBuffer() }),
  analyseMeasured: async (file: File) => send<Measured>(`/measured?name=${encodeURIComponent(file.name)}`, { method: 'POST', body: await file.arrayBuffer() }),
  /** Uploads a measured source pattern (CSV: azimuth, elevation, then the eight octave bands or one column); the file's bytes are the body. */
  uploadBalloon: async (file: File) =>
    send<Balloon>(`/balloon/import?name=${encodeURIComponent(file.name)}`, { method: 'POST', body: await file.arrayBuffer() }),
  /** A pattern's directivity index on its axis in each octave band, dB, to turn a level 1 m in front of it into a sound power. */
  directivityIndex: (d: Directivity, signal?: AbortSignal) => post<{ onAxisDb: number[] }>('/directivity/index', d, signal),
  /** Closes holes in a model (their places in its geometry check's faults): a new model, with a group for each hole's new faces. */
  fillHoles: (modelId: string, holes: number[]) => post<Model>('/model/fill', { modelId, holes }),
  splitGroup: (modelId: string, group: string) => post<Model>('/model/split', { modelId, group }),
  /** Renames a surface group, merges it into another or deletes it. */
  editGroup: (modelId: string, group: string, action: GroupAction, to?: string) => post<Model>('/model/group', { modelId, group, action, to }),
  editGeometry: (modelId: string, edit: GeometryEdit) => post<Model>('/model/geometry', { modelId, ...edit }),
  repairModel: (modelId: string, boxes: boolean, screens: string[]) => post<Model>('/model/repair', { modelId, boxes, screens }),
  loadExample: (name: string, space?: number) => post<Model>(`/model/example?name=${encodeURIComponent(name)}${space !== undefined ? `&space=${space}` : ''}`, {}),
  /** The file's bytes are the body; scale is metres per drawing unit (0.001 for millimetres). */
  importModel: async (file: File, scale: number, yUp: boolean, space?: number) =>
    send<Model>(`/model/import?name=${encodeURIComponent(file.name)}&scale=${scale}&yUp=${yUp}${space !== undefined ? `&space=${space}` : ''}`, { method: 'POST', body: await file.arrayBuffer() }),
};
