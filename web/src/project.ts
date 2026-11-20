import type { GeometryEdit } from './api';
import type { Directivity, ExtraReceiver, ExtraSource, MapSettings, Point, Room, Screen, Surface } from './types';
import type { Spectrum } from './spectra';
import type { Settings } from './components/Sidebar';
import type { RayMode } from './components/Viewport';

export const PROJECT_FORMAT = 'room-acoustics-3d-project';

/** Where an imported model came from, so loading a project can read it again: the file itself, or one of the engine's examples. */
/**
 * `edits` are what was done to it after reading it, replayed in order: holes closed (their places in the geometry check's faults at that step),
 * surface groups split into their flat surfaces, repairs (with the groups that were screens then), group edits and shape edits (corners moved, faces drawn and
 * surfaces deleted).
 */
export type ModelOrigin = { kind: 'file'; file: File; scale: number; yUp: boolean; space?: number; edits?: ModelEdit[] } | { kind: 'example'; name: string; space?: number; edits?: ModelEdit[] };

export type ModelEdit = { fill: number[] } | { split: string } | { repair: { boxes: boolean; screens: string[] } }
  | { group: { name: string; action: 'rename' | 'merge' | 'delete'; to?: string } } | { geometry: GeometryEdit };

/** `fills` is how projects saved before groups could be split listed the holes closed. */
type StoredOrigin = { kind: 'file'; name: string; scale: number; yUp: boolean; space?: number; edits?: ModelEdit[]; fills?: number[][]; dataBase64: string }
  | { kind: 'example'; name: string; space?: number; edits?: ModelEdit[]; fills?: number[][] };

/** Everything the user has set up, as saved in a project file. Imported models and measured patterns are held inside it, so the file stands alone. */
export type ProjectFile = {
  format: typeof PROJECT_FORMAT; version: 1; savedAt: string;
  room: Room; model: { origin: StoredOrigin; surfaces: Record<string, Surface>; outdoors?: boolean } | null;
  source: Point; receiver: Point; directivity: Directivity | null; extras: ExtraSource[];
  /** absent from projects saved before there were several receivers */
  extraReceivers?: ExtraReceiver[];
  balloons: { id: string; name: string; dataBase64: string }[];
  settings: Settings; mapSettings: MapSettings; facing: number; rayMode: RayMode; showRays: boolean;
  /** the user's own spectra that its sources use (absent from projects saved before there were any) */
  mySpectra?: Spectrum[];
  /** the free-standing screens (absent from projects saved before there were any) */
  screens?: Screen[];
};

export type ProjectState = {
  room: Room; model: { origin: ModelOrigin; surfaces: Record<string, Surface>; outdoors?: boolean } | null;
  source: Point; receiver: Point; directivity: Directivity | null; extras: ExtraSource[]; extraReceivers: ExtraReceiver[]; balloonFiles: Record<string, File>;
  settings: Settings; mapSettings: MapSettings; facing: number; rayMode: RayMode; showRays: boolean; mySpectra: Spectrum[]; screens: Screen[];
};

async function toBase64(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let text = '';
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(text);
}

export function fromBase64(text: string): Uint8Array<ArrayBuffer> {
  const raw = atob(text);
  const bytes = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}

/** Gathers the state into a project file; only the measured patterns still in use are kept. */
export async function buildProject(state: ProjectState): Promise<ProjectFile> {
  const origin = state.model?.origin;
  const used = new Set([state.directivity, ...state.extras.map((e) => e.directivity)].flatMap((d) => (d?.balloonId ? [d.balloonId] : [])));
  return {
    format: PROJECT_FORMAT, version: 1, savedAt: new Date().toISOString(),
    room: state.room,
    model: state.model && origin
      ? { surfaces: state.model.surfaces, outdoors: state.model.outdoors || undefined, origin: origin.kind === 'file'
          ? { kind: 'file', name: origin.file.name, scale: origin.scale, yUp: origin.yUp, space: origin.space, edits: origin.edits, dataBase64: await toBase64(origin.file) }
          : origin }
      : null,
    source: state.source, receiver: state.receiver, directivity: state.directivity, extras: state.extras, extraReceivers: state.extraReceivers,
    balloons: await Promise.all([...used].filter((id) => state.balloonFiles[id]).map(async (id) => ({ id, name: state.balloonFiles[id].name, dataBase64: await toBase64(state.balloonFiles[id]) }))),
    settings: state.settings, mapSettings: state.mapSettings, facing: state.facing, rayMode: state.rayMode, showRays: state.showRays,
    mySpectra: state.mySpectra.filter((sp) => sp.id === state.settings.lwShape || state.extras.some((e) => e.lwShape === sp.id)),
    screens: state.screens,
  };
}

/** Reads a project file's text; throws an error that says what is wrong if it is not one of ours. */
export function parseProject(text: string): ProjectFile {
  let data: Partial<ProjectFile>;
  try { data = JSON.parse(text); } catch { throw new Error('That file is not a project: it is not valid JSON.'); }
  if (!data || data.format !== PROJECT_FORMAT) throw new Error('That file is not a Room Acoustics 3D project.');
  if (data.version !== 1) throw new Error(`That project was saved by a newer version (format ${String(data.version)}); this one reads format 1.`);
  for (const key of ['room', 'source', 'receiver', 'settings', 'mapSettings', 'extras', 'balloons'] as const)
    if (data[key] === undefined || data[key] === null) throw new Error(`That project file is incomplete: "${key}" is missing.`);
  return data as ProjectFile;
}

/** Offers a file to the user for saving. */
export function download(name: string, data: BlobPart, type = 'application/json') {
  const url = URL.createObjectURL(new Blob([data], { type }));
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
