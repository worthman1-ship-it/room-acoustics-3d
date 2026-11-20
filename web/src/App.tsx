import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, type GeometryEdit, type GroupAction, type SimulateParams } from './api';
import type { Balloon, Directivity, ExtraReceiver, ExtraSource, LevelEntry, MapRaw, MapResult, MapSettings, Material, Model, Point, Ray, ReflectionsResult, Room, RoomSpec, Screen, SimulateResponse, SourceDto, Surface, SurfaceInfo } from './types';
import { BANDS_HZ, MAP_METRICS, alphaShown, atBand, overallPower } from './types';
import { snapshotPng } from './chartImage';
import { boxFrame, clampTo, colourRange, size, type Frame } from './frame';
import Sidebar, { type Settings } from './components/Sidebar';
import Viewport, { type RayMode } from './components/Viewport';
import PopOut from './components/PopOut';
import SurfacesPanel from './components/SurfacesPanel';
import GeometryCheck from './components/GeometryCheck';
import JobList, { useJobs } from './components/JobList';
import ScreensPanel from './components/ScreensPanel';
import { REDUCTIONS } from './components/Reduction';
import Charts, { MapView } from './components/Charts';
import Listen from './components/Listen';
import ThirdOctaves from './components/ThirdOctaves';
import Reflections from './components/Reflections';
import SourcePanel, { MAX_RECEIVERS, MAX_SOURCES } from './components/SourcePanel';
import { loadMySpectra, soundPowerOf, spectrumBands, spectrumName, storeMySpectra, type Spectrum } from './spectra';
import MapSection from './components/MapSection';
import MapBar from './components/MapBar';
import CalculationSection from './components/CalculationSection';
import { buildProject, download, fromBase64, parseProject, type ModelEdit, type ModelOrigin } from './project';
import { mapCsv, type ReportInfo, type SourceInfo } from './export';
import { receiverLine } from './openPlan';

// Material 0 is the engine's "hard/reflective" preset and 1 its "soft/absorptive" one; the picker lets the user choose any in the library.
const initialRoom: Room = {
  lengthM: 12, widthM: 8, heightM: 4,
  floor: { materialId: 1, scattering: 0.1 },
  walls: { materialId: 0, scattering: 0.2 },
  ceiling: { materialId: 0, scattering: 0.1 },
};

const initialSettings: Settings = { rayCount: 60_000, imageSourceOrder: 2, temperatureC: 20, humidityPercent: 60, bandIndex: 4, overall: false, drawnRays: 300, speechLevelDbA: 60, backgroundNc: 30, voice: 'male', lwBandsDb: spectrumBands('flat', 90), lwShape: 'flat', stiSignal: 'sources' };
const initialMapSettings: MapSettings = {
  metric: 'C80', heightM: 1.2, spacingM: 1, rayCount: 20_000, mode: 'plan', axis: 'x', positionM: 6, heightsM: [1.2, 2.4], surfaceGroup: null, aboveM: 1.2,
  reflectorGroup: null, reflectorFromMs: 5, reflectorToMs: 80, scales: {},
};

const patternName = (d: Directivity | null) => d === null ? 'Omnidirectional'
  : d.kind === 'lineArray' ? `Line array, ${d.lineArray?.elements ?? 8} elements` : d.kind === 'balloon' ? 'Loudspeaker' : d.kind === 'talker' ? 'Talker'
  : d.kind === 'surface' ? `Surface source, ${d.surface?.widthM ?? 0} × ${d.surface?.heightM ?? 0} m${d.surface?.oneSided === false ? '' : ', one-sided'}`
  : `Horn-like, ${d.horizontalDeg}° × ${d.verticalDeg}°`;

/** How a source's level was entered, for the exports. */
const levelNote = (bandsDb: number[], level: LevelEntry | undefined) => !level || level.mode === 'lw' ? 'Entered as sound power'
  : level.mode === 'lp1m' ? `Entered as ${overallPower(level.lp1mBandsDb ?? bandsDb).toFixed(1)} dB at 1 m on axis`
  : `Entered as sensitivity ${level.sensitivityDb ?? 90} dB (1 W, 1 m) at ${level.watts ?? 1} W`;

/** The setup a result was asked with, so an export describes that run and not what has been changed since. */
type Setup = { title: string; roomLine: string; sources: SourceInfo[]; settings: Settings; inputsKey: string };

const ZERO_DI = [0, 0, 0, 0, 0, 0, 0, 0];
/** A pattern as its directivity index depends on it: where it is aimed makes no difference to its index on its own axis. */
const diKey = (d: Directivity) => JSON.stringify({ ...d, aimAzimuthDeg: 0, aimElevationDeg: 0 });

const usable = (d: Directivity | null): Directivity | null => (d && d.kind === 'balloon' && !d.balloonId ? null : d);

/** A first guess at a surface group's finish from its name (seats and carpet absorb, everything else reflects); the user can change any of them. */
const defaultSurface = (group: string): Surface => ({ materialId: /seat|audience|carpet/i.test(group) ? 1 : 0, scattering: 0.2 });

export default function App() {
  const [room, setRoom] = useState<Room>(initialRoom);
  const [model, setModel] = useState<Model | null>(null);
  const [groupSurfaces, setGroupSurfaces] = useState<Record<string, Surface>>({});
  const [examples, setExamples] = useState<string[]>([]);
  const [importing, setImporting] = useState(false);
  const [settings, setSettings] = useState<Settings>(initialSettings);
  const [source, setSource] = useState<Point>({ x: 2, y: 4, z: 1.6 });
  const [receiver, setReceiver] = useState<Point>({ x: 9, y: 4, z: 1.2 });
  const [materials, setMaterials] = useState<Material[]>([]);
  const [rays, setRays] = useState<Ray[]>([]);
  const [result, setResult] = useState<SimulateResponse | null>(null);
  const [quick, setQuick] = useState(false); // the result is the statistical quick estimate, not a simulation
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [online, setOnline] = useState<boolean | null>(null);
  const NARROW = '(max-width: 900px)';
  const [narrow, setNarrow] = useState(() => window.matchMedia(NARROW).matches);
  const [collapsed, setCollapsed] = useState(() => window.matchMedia(NARROW).matches); // a narrow window starts with the panel folded away
  const [rayMode, setRayMode] = useState<RayMode>('wave');
  const [surfacesOpen, setSurfacesOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [sourcesOpen, setSourcesOpen] = useState(false);
  const [jobsOpen, setJobsOpen] = useState(false);
  const jobs = useJobs();
  const [showRays, setShowRays] = useState(true);
  const [view, setView] = useState<'work' | 'map'>('work'); // the main area shows the room with its results, or the map in a view of its own
  const [mapShown, setMapShown] = useState(false); // the map's 3-D view is built the first time it is wanted, then kept so its camera is as it was left
  const showView = (v: 'work' | 'map') => { setView(v); if (v === 'map') setMapShown(true); };
  const [selectedSurface, setSelectedSurface] = useState<string | null>(null);
  const [elapsed, setElapsed] = useState(0);
  const [progress, setProgress] = useState(0);
  const [mapProgress, setMapProgress] = useState(0);
  const [directivity, setDirectivity] = useState<Directivity | null>(null);
  const [extras, setExtras] = useState<ExtraSource[]>([]);
  const [activeSource, setActiveSource] = useState(0); // 0 is the first source, otherwise an extra source's id
  const [balloons, setBalloons] = useState<Record<string, Balloon>>({});
  const [facing, setFacing] = useState(180); // the way the listener's head points
  const nextExtra = useRef(1);
  const [extraReceivers, setExtraReceivers] = useState<ExtraReceiver[]>([]);
  const nextReceiver = useRef(1);
  const [screens, setScreens] = useState<Screen[]>([]);
  const nextScreen = useRef(1);
  // the screens as the engine takes them; undefined when there are none, so a room without sends what it always did
  const screenDtos = useMemo(() => (screens.length ? screens.map(({ id: _id, ...s }) => s) : undefined), [screens]);
  const extraReceiverPoints = useMemo(() => (extraReceivers.length ? extraReceivers.map((r) => r.position) : undefined), [extraReceivers]);
  const [mapSettings, setMapSettings] = useState<MapSettings>(initialMapSettings);
  // what the model and the measured patterns were read from, so a saved project can carry them
  const modelOrigin = useRef<ModelOrigin | null>(null);
  const balloonFiles = useRef<Record<string, File>>({});
  const projectInput = useRef<HTMLInputElement>(null);
  const [mapRaw, setMapRaw] = useState<MapRaw | null>(null);
  const mapPicture = useRef<HTMLElement>(null); // the flat map, for a picture of it
  const [mapBusy, setMapBusy] = useState(false);
  const mapAbort = useRef<AbortController | null>(null);
  const runAbort = useRef<AbortController | null>(null);
  const latest = useRef<Setup | null>(null); // the setup as it is now, filled in on every render
  const [ran, setRan] = useState<Setup | null>(null); // the setup the shown result was asked with
  const [mine, setMine] = useState<Spectrum[]>(loadMySpectra); // the user's own spectra
  const saveSpectrum = (sp: Spectrum) => setMine((list) => { const next = [...list.filter((x) => x.id !== sp.id), sp]; storeMySpectra(next); return next; });
  const [diCache, setDiCache] = useState<Record<string, number[]>>({});
  const [reflectionList, setReflectionList] = useState<ReflectionsResult | null>(null); // the reflection list as last worked out, for the workbook

  const frame = useMemo<Frame>(() => (model ? { min: model.bounds.min, max: model.bounds.max, margin: 0.1 } : boxFrame(room)), [model, room]);
  // a model taken as standing in the open air (it need not be closed)
  const [outdoors, setOutdoors] = useState(false);
  const spec = useMemo<RoomSpec>(() => (model ? { model: { modelId: model.id, surfaces: groupSurfaces, screens: screenDtos, outdoors: outdoors || undefined } } : { room: { ...room, screens: screenDtos } }),
    [model, groupSurfaces, room, screenDtos, outdoors]);
  // an imported model that is not a closed surface can be looked at but not simulated, unless it is taken as outdoors
  const unusable = outdoors ? null : model?.closedError ?? null;
  const [fault, setFault] = useState<number | null>(null); // the geometry check's fault picked out in the 3-D view
  const [repairs, setRepairs] = useState<string[] | null>(null); // what the last repair of the model did
  // the sources heard, as the engine takes them: a muted one is left out, and a solo leaves out every source that is not soloed. The earliest goes first,
  // since the engine's first source has no delay of its own and the others' delays count from it
  const live = useMemo(() => {
    const all = [
      { position: source, directivity, delayMs: 0, lwBandsDb: settings.lwBandsDb, muted: !!settings.source1Muted, solo: !!settings.source1Solo },
      ...extras.map((e) => ({ position: e.position, directivity: e.directivity, delayMs: e.delayMs, lwBandsDb: e.lwBandsDb, muted: !!e.muted, solo: !!e.solo })),
    ];
    const soloed = all.some((x) => x.solo);
    return all.filter((x) => (soloed ? x.solo : !x.muted)).sort((a, b) => a.delayMs - b.delayMs);
  }, [source, directivity, extras, settings.lwBandsDb, settings.source1Muted, settings.source1Solo]);
  const lead = live[0];
  const sentSource = lead?.position ?? source;
  const sentPower = lead?.lwBandsDb ?? settings.lwBandsDb;
  // a measured pattern chosen but not yet uploaded is sent as omnidirectional, so choosing it does not raise an error
  const leadDirectivity = lead ? lead.directivity : directivity;
  const sent = useMemo(() => usable(leadDirectivity), [leadDirectivity]);
  // the sources after the first; undefined when there are none, so a single source sends what it always did
  const extraSources = useMemo<SourceDto[]>(() => live.slice(1).map((x) => ({ position: x.position, directivity: usable(x.directivity), gainDb: 0, delayMs: x.delayMs - live[0].delayMs, soundPowerDb: x.lwBandsDb })), [live]);
  const extrasParam = extraSources.length ? extraSources : undefined;
  const listenSettings = useMemo(() => ({ ...settings, lwBandsDb: sentPower }), [settings, sentPower]);

  // each pattern's directivity index on its own axis, by band, for the levels entered as a level at 1 m in front: asked of the engine once per pattern
  const diOf = useCallback((d: Directivity | null) => { const u = usable(d); return u === null ? ZERO_DI : diCache[diKey(u)] ?? null; }, [diCache]);
  useEffect(() => {
    if (!online) return;
    const wanted = new Map<string, Directivity>();
    for (const d of [directivity, ...extras.map((e) => e.directivity)]) { const u = usable(d); if (u && diCache[diKey(u)] === undefined) wanted.set(diKey(u), { ...u, aimAzimuthDeg: 0, aimElevationDeg: 0 }); }
    if (wanted.size === 0) return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      for (const [key, d] of wanted) api.directivityIndex(d, controller.signal).then((r) => setDiCache((all) => ({ ...all, [key]: r.onAxisDb }))).catch(() => { /* asked again on the next change */ });
    }, 300);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [online, directivity, extras, diCache]);
  // a level entered at 1 m in front gives a new sound power when the pattern (and so its directivity index) changes
  useEffect(() => {
    const same = (a: number[], b: number[]) => a.every((v, i) => Math.abs(v - b[i]) < 0.05);
    const first = soundPowerOf({ shape: settings.lwShape, bandsDb: settings.lwBandsDb, level: settings.level1 }, diOf(directivity), mine);
    if (first && !same(first, settings.lwBandsDb)) setSettings((s0) => ({ ...s0, lwBandsDb: first }));
    let changed = false;
    const next = extras.map((e) => {
      const lw = soundPowerOf({ shape: e.lwShape, bandsDb: e.lwBandsDb, level: e.level }, diOf(e.directivity), mine);
      if (!lw || same(lw, e.lwBandsDb)) return e;
      changed = true;
      return { ...e, lwBandsDb: lw };
    });
    if (changed) setExtras(next);
  }, [settings.lwShape, settings.lwBandsDb, settings.level1, directivity, extras, diOf, mine]);

  // The engine is a separate process: say plainly when it is not running, then load the material list and the examples from it.
  const [version, setVersion] = useState<string | null>(null);
  useEffect(() => {
    api.health()
      .then((h) => { setVersion(h.version ?? null); return Promise.all([api.materials(), api.examples()]); })
      .then(([m, e]) => { setMaterials(m); setExamples(e); setOnline(true); })
      .catch(() => setOnline(false));
  }, []);

  // Keep the source and receiver inside the room when its size changes.
  useEffect(() => {
    setSource((p) => clampTo(p, frame));
    setReceiver((p) => clampTo(p, frame));
    setExtras((list) => list.map((e) => ({ ...e, position: clampTo(e.position, frame) })));
    setExtraReceivers((list) => list.map((r) => ({ ...r, position: clampTo(r.position, frame) })));
  }, [frame]);

  // The ray picture is cheap, so it follows every change after a short pause.
  useEffect(() => {
    if (!online || unusable || !showRays) { setRays([]); return; }
    const controller = new AbortController();
    const timer = setTimeout(() => {
      api.rays({ ...spec, source: sentSource, receiver, rayCount: settings.drawnRays, maxReflections: 12, band: settings.overall ? -1 : settings.bandIndex,
                 temperatureC: settings.temperatureC, humidityPercent: settings.humidityPercent, directivity: sent, extraSources: extrasParam }, controller.signal)
        .then((r) => { setRays(r); setError(null); })
        .catch((e) => { if (e.name !== 'AbortError') { setRays([]); setError(e.message); } });
    }, 250);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [online, unusable, showRays, spec, sentSource, receiver, sent, extrasParam, settings.drawnRays, settings.bandIndex, settings.overall, settings.temperatureC, settings.humidityPercent]);

  // A big model takes many seconds and the engine reports no progress, so at least show that time is passing.
  useEffect(() => {
    if (!busy && !mapBusy) return;
    setElapsed(0);
    const started = Date.now();
    const timer = setInterval(() => setElapsed(Math.floor((Date.now() - started) / 1000)), 500);
    return () => clearInterval(timer);
  }, [busy, mapBusy]);

  // what Run simulation sends: the job list keeps a copy of it for each job
  const simulateParams = useMemo<SimulateParams>(() => ({
    ...spec, source: sentSource, receiver, rayCount: settings.rayCount, imageSourceOrder: settings.imageSourceOrder,
    temperatureC: settings.temperatureC, humidityPercent: settings.humidityPercent, directivity: sent, extraSources: extrasParam,
    speechLevelDbA: settings.speechLevelDbA, backgroundNc: settings.backgroundNc, voice: settings.voice, soundPowerDb: sentPower, stiSignal: settings.stiSignal ?? 'talker', extraReceivers: extraReceiverPoints,
    angleDependentAbsorption: settings.angleDependent || undefined, workroomD1M: settings.workroomD1M, workroomD2M: settings.workroomD2M,
  }), [spec, sentSource, sentPower, receiver, settings, sent, extrasParam, extraReceiverPoints]);

  const run = useCallback(async () => {
    runAbort.current?.abort();
    const controller = new AbortController();
    runAbort.current = controller;
    setBusy(true);
    setProgress(0);
    try {
      const asked = latest.current;
      const r = await api.simulate(simulateParams, controller.signal, setProgress);
      setResult(r);
      setRan(asked);
      setQuick(false);
      setError(null);
    } catch (e) {
      if ((e as Error).name !== 'AbortError') setError((e as Error).message);
    } finally {
      if (runAbort.current === controller) setBusy(false);
    }
  }, [simulateParams]);

  // the standard equations take no time, so the results are shown as soon as they are in
  const quickEstimate = useCallback(async () => {
    try {
      const asked = latest.current;
      const r = await api.statistical({ ...spec, source: sentSource, receiver, rayCount: settings.rayCount, imageSourceOrder: settings.imageSourceOrder,
                                        temperatureC: settings.temperatureC, humidityPercent: settings.humidityPercent, directivity: sent, extraSources: extrasParam,
                                        speechLevelDbA: settings.speechLevelDbA, backgroundNc: settings.backgroundNc, voice: settings.voice, soundPowerDb: sentPower,
                                        stiSignal: settings.stiSignal ?? 'talker' });
      setResult(r);
      setRan(asked);
      setQuick(true);
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }, [spec, sentSource, sentPower, sent, extrasParam, receiver, settings]);

  // a map belongs to one room, one source and one source pattern: change any and it is out of date
  // (the STI too is for one talker and one background noise)
  useEffect(() => { setMapRaw(null); }, [spec, sentSource, sent, extrasParam, sentPower, settings.speechLevelDbA, settings.backgroundNc, settings.voice, settings.stiSignal]);
  // every parameter, in every octave band and as its broadband number, is worked out in one run, so the map follows the choice in the Map view and the band in the sidebar without another
  const mapScale = mapSettings.scales[mapSettings.metric];
  const mapResult = useMemo(() => (mapRaw ? { ...atBand(mapRaw, mapSettings.metric, settings.overall ? null : settings.bandIndex), scale: mapScale } : null),
    [mapRaw, mapSettings.metric, settings.overall, settings.bandIndex, mapScale]);
  // the scale the map would have of itself, shown in the scale's boxes until the user sets an end
  const mapOwnRange = useMemo(() => (mapResult ? colourRange(mapResult.points.filter((p) => p.value !== null).map((p) => p.value as number), mapResult.unit) : null), [mapResult]);
  // the surfaces a map can sit above: a model's groups, or a box room's floor
  const surfaceChoices = useMemo(() => (model ? model.groups.map((g) => g.name) : ['Floor']), [model]);
  // the surfaces whose reflections a map can show: a model's groups, or a box room's floor, walls (all four) or ceiling
  const reflectorChoices = useMemo(() => (model ? model.groups.map((g) => g.name) : ['Floor', 'Walls', 'Ceiling']), [model]);
  const [highlight, setHighlight] = useState<Point[] | null>(null); // a reflection's route, drawn in the 3-D view

  const calculateMap = useCallback(async () => {
    mapAbort.current?.abort();
    const controller = new AbortController();
    mapAbort.current = controller;
    setMapBusy(true);
    setMapProgress(0);
    try {
      const m = mapSettings;
      const r = await api.map({ ...spec, source: sentSource, metric: m.metric, heightM: m.mode === 'surface' ? m.aboveM : m.heightM, spacingM: m.spacingM,
                                rayCount: m.rayCount, imageSourceOrder: settings.imageSourceOrder, temperatureC: settings.temperatureC,
                                humidityPercent: settings.humidityPercent, directivity: sent, extraSources: extrasParam,
                                speechLevelDbA: settings.speechLevelDbA, backgroundNc: settings.backgroundNc, voice: settings.voice, soundPowerDb: sentPower,
                                stiSignal: settings.stiSignal ?? 'talker', angleDependentAbsorption: settings.angleDependent || undefined, mode: m.mode, axis: m.axis, positionM: m.positionM, heightsM: m.mode === 'volume' ? m.heightsM : undefined,
                                surfaceGroup: m.mode === 'surface' ? (surfaceChoices.includes(m.surfaceGroup ?? '') ? m.surfaceGroup! : surfaceChoices[0]) : undefined,
                                reflectorGroup: m.reflectorGroup && reflectorChoices.includes(m.reflectorGroup) ? m.reflectorGroup : undefined,
                                reflectorFromMs: m.reflectorFromMs, reflectorToMs: m.reflectorToMs },
                              controller.signal, setMapProgress);
      setMapRaw(r);
      setError(null);
      showView('map');
    } catch (e) {
      if ((e as Error).name !== 'AbortError') setError((e as Error).message);
    } finally {
      if (mapAbort.current === controller) setMapBusy(false);
    }
  }, [spec, sentSource, sentPower, mapSettings, settings, sent, extrasParam, surfaceChoices, reflectorChoices]);

  // counts the models read in, so the 3-D view is framed afresh for each but keeps its camera while one is edited
  const [viewKey, setViewKey] = useState(0);

  const adopt = (m: Model) => {
    setModel(m);
    setViewKey((k) => k + 1);
    setFault(null);
    setRepairs(null);
    setOutdoors(false);
    setExtras([]); setExtraReceivers([]); setActiveSource(0); setScreens([]); // the other sources, receivers and screens were placed in the old room
    // a group named as screens starts as free-standing screens
    setGroupSurfaces(Object.fromEntries(m.groups.map((g) => [g.name, { ...defaultSurface(g.name), screen: m.screenGroups?.includes(g.name) || undefined }])));
    if (m.suggestedSource) setSource(m.suggestedSource);
    if (m.suggestedReceiver) setReceiver(m.suggestedReceiver);
    setResult(null);
    setRays([]);
    setError(null);
  };

  const importWith = async (load: () => Promise<Model>, origin: ModelOrigin) => {
    setImporting(true);
    try { adopt(await load()); modelOrigin.current = origin; } catch (e) { setError((e as Error).message); } finally { setImporting(false); }
  };

  // changes the model in the engine (holes filled, a group split, a repair), keeping each group's finish; a new group gets the finish
  // `inherit` gives it, or the usual one (a screen if it is named as one). A model that was open and now closes is a room, so its suggested source and
  // receiver (inside it) are taken. The change is kept with the model's origin, so a saved project does it again. Says whether the change was made.
  const editModel = async (run: (m: Model) => Promise<Model>, edit: ModelEdit, inherit: (group: string, now: Record<string, Surface>) => Surface | undefined = () => undefined) => {
    if (!model) return false;
    setImporting(true);
    try {
      const next = await run(model);
      setModel({ ...next, spaces: model.spaces, space: model.space });
      setFault(null);
      setSelectedSurface(null);
      setGroupSurfaces((now) => Object.fromEntries(next.groups.map((g) => [g.name,
        now[g.name] ?? inherit(g.name, now) ?? { ...defaultSurface(g.name), screen: next.screenGroups?.includes(g.name) || undefined }])));
      if (model.closedError && !next.closedError) {
        setOutdoors(false);
        if (next.suggestedSource) setSource(next.suggestedSource);
        if (next.suggestedReceiver) setReceiver(next.suggestedReceiver);
      }
      setRepairs(next.repairs ?? null);
      setResult(null);
      setRays([]);
      setError(null);
      const o = modelOrigin.current;
      if (o) modelOrigin.current = { ...o, edits: [...(o.edits ?? []), edit] };
      return true;
    } catch (e) { setError((e as Error).message); return false; } finally { setImporting(false); }
  };

  // closes holes in the model
  const fillHoles = (holes: number[]) => { if (holes.length > 0) editModel((m) => api.fillHoles(m.id, holes), { fill: holes }); };

  // splits a surface group into its flat surfaces, each starting with the group's finish
  const splitGroup = (group: string) => editModel((m) => api.splitGroup(m.id, group), { split: group }, (_, now) => now[group]);

  // renames a group (keeping its finish), merges it into another (which keeps its own) or deletes it
  const editGroup = (name: string, action: GroupAction, to?: string) =>
    editModel((m) => api.editGroup(m.id, name, action, to), { group: { name, action, to } }, (g, now) => (action === 'rename' && g === to ? now[name] : undefined));

  // moves a corner, draws a face or deletes a surface; a face drawn in a new group takes the usual finish, and a group whose last surface goes, goes
  const editGeometry = (edit: GeometryEdit) => editModel((m) => api.editGeometry(m.id, edit), { geometry: edit });

  // repairs the model as far as it can be: a group's part made a screen ("Group screen") keeps the group's finish, as a screen
  const repairModel = (boxes: boolean) => {
    const screens = Object.entries(groupSurfaces).filter(([, s]) => s.screen).map(([g]) => g);
    editModel((m) => api.repairModel(m.id, boxes, screens), { repair: { boxes, screens } },
      (g, now) => (g.endsWith(' screen') && now[g.slice(0, -7)] ? { ...now[g.slice(0, -7)], screen: true } : undefined));
  };

  const clearModel = () => { setModel(null); setRepairs(null); modelOrigin.current = null; setExtras([]); setExtraReceivers([]); setScreens([]); setActiveSource(0); setResult(null); setError(null); };

  // a new screen stands on the floor half way between the first source and the receiver, across the line between them, and absorbs (a screen product where the list has one)
  const addScreen = () => {
    const angle = Math.atan2(receiver.y - source.y, receiver.x - source.x) * 180 / Math.PI + 90;
    const absorbent = materials.find((m) => /screensorba|screen/i.test(m.name))?.id ?? 1;
    setScreens((list) => [...list, { id: nextScreen.current++, x: (source.x + receiver.x) / 2, y: (source.y + receiver.y) / 2, baseZ: frame.min.z,
      lengthM: 1.6, heightM: 1.5, angleDeg: Math.round(((angle % 360) + 360) % 360), finish: { materialId: absorbent, scattering: 0.1 } }]);
  };

  // a partition: a wall across the middle of the room's longer side, from side to side and floor to ceiling, with a door in the middle of it, of blockwork
  const addPartition = () => {
    const alongX = frame.max.x - frame.min.x >= frame.max.y - frame.min.y; // across the longer side, so the two rooms are each about square
    const span = alongX ? frame.max.y - frame.min.y : frame.max.x - frame.min.x;
    const height = frame.max.z - frame.min.z;
    const wall = materials.find((m) => /block|brick|plaster/i.test(m.name))?.id ?? 1;
    setScreens((list) => [...list, { id: nextScreen.current++, x: (frame.min.x + frame.max.x) / 2, y: (frame.min.y + frame.max.y) / 2, baseZ: frame.min.z,
      lengthM: Math.round(span * 100) / 100, heightM: Math.round(height * 100) / 100, angleDeg: alongX ? 90 : 0,
      finish: { materialId: wall, scattering: 0.1, transmissionLossDb: [...REDUCTIONS[3].db] },
      door: { offsetM: Math.round((span - 0.9) / 2 * 100) / 100, widthM: 0.9, heightM: Math.min(2.1, height - 0.1) } }]);
  };

  const addExtra = () => {
    if (extras.length + 1 >= MAX_SOURCES) return;
    const id = nextExtra.current++;
    const position = clampTo({ x: source.x, y: source.y + 1.5 * (extras.length + 1), z: source.z }, frame);
    setExtras((list) => [...list, { id, position, directivity: null, gainDb: 0, delayMs: 0, lwBandsDb: settings.lwBandsDb, lwShape: settings.lwShape, level: settings.level1 }]);
    setActiveSource(id);
  };
  const addReceiver = () => {
    if (extraReceivers.length + 1 >= MAX_RECEIVERS) return;
    const id = nextReceiver.current++;
    const position = clampTo({ x: receiver.x, y: receiver.y + 1.5 * (extraReceivers.length + 1), z: receiver.z }, frame);
    setExtraReceivers((list) => [...list, { id, position }]);
  };
  // every receiver in a line away from the selected source, for the ISO 3382-3 open-plan office results
  const receiversInLine = () => {
    const from = extras.find((e) => e.id === activeSource)?.position ?? source;
    const [first, ...rest] = receiverLine(from, receiver, frame, MAX_RECEIVERS);
    setReceiver(first);
    setExtraReceivers(rest.map((position) => ({ id: nextReceiver.current++, position })));
  };
  const moveReceiver = (id: number, position: Point) => setExtraReceivers((list) => list.map((r) => (r.id === id ? { ...r, position } : r)));
  const uploadBalloon = async (file: File) => {
    const b = await api.uploadBalloon(file);
    balloonFiles.current[b.id] = file;
    setBalloons((all) => ({ ...all, [b.id]: b }));
    return b;
  };
  const editExtras = (list: ExtraSource[]) => setExtras(list);
  const extraAim = (id: number, az: number, el: number) => setExtras((list) => list.map((e) => (e.id === id && e.directivity ? { ...e, directivity: { ...e.directivity, aimAzimuthDeg: az, aimElevationDeg: el } } : e)));
  const moveExtra = (id: number, position: Point) => setExtras((list) => list.map((e) => (e.id === id ? { ...e, position } : e)));

  // everything a calculation is asked with: when it differs from the run's, the results shown are out of date
  const inputsKey = useMemo(() => JSON.stringify([spec, sentSource, receiver, sent, extrasParam, extraReceiverPoints, sentPower, settings.rayCount, settings.imageSourceOrder,
    settings.temperatureC, settings.humidityPercent, settings.speechLevelDbA, settings.backgroundNc, settings.voice, settings.stiSignal]),
    [spec, sentSource, receiver, sent, extrasParam, extraReceiverPoints, sentPower, settings]);
  const heard = (muted: boolean | undefined, solo: boolean | undefined) => (live.length > 0 && (settings.source1Solo || extras.some((e) => e.solo) ? !!solo : !muted));
  const now: Setup = {
    title: model ? model.name : 'Room acoustics',
    roomLine: model
      ? `${model.triangleCount} triangles, ${size(frame).x.toFixed(1)} × ${size(frame).y.toFixed(1)} × ${size(frame).z.toFixed(1)} m${model.volumeM3 !== null ? `, ${model.volumeM3.toFixed(0)} m³` : ''}`
      : `${room.lengthM} × ${room.widthM} × ${room.heightM} m, ${(room.lengthM * room.widthM * room.heightM).toFixed(0)} m³`,
    sources: [
      { name: 'Source 1', position: source, pattern: patternName(directivity), delayMs: 0, spectrum: spectrumName(settings.lwShape, mine), lwBandsDb: settings.lwBandsDb,
        levelNote: levelNote(settings.lwBandsDb, settings.level1), heard: heard(settings.source1Muted, settings.source1Solo) },
      ...extras.map((e, i) => ({ name: `Source ${i + 2}`, position: e.position, pattern: patternName(e.directivity), delayMs: e.delayMs, spectrum: spectrumName(e.lwShape, mine),
        lwBandsDb: e.lwBandsDb, levelNote: levelNote(e.lwBandsDb, e.level), heard: heard(e.muted, e.solo) })),
    ],
    settings, inputsKey,
  };
  latest.current = now;
  const shown = ran ?? now;
  // what a printed report or an export says about the setup: the one the result was asked with
  const report: ReportInfo = {
    title: shown.title, roomLine: shown.roomLine, sources: shown.sources,
    calculation: quick ? 'Statistical estimate from the standard equations: every source\'s direct sound, with its pattern towards the receiver, and one reverberant field.'
      : `${shown.settings.rayCount.toLocaleString()} rays, image sources to order ${result?.imageSourceOrderUsed ?? shown.settings.imageSourceOrder}, ${shown.settings.temperatureC} °C, ${shown.settings.humidityPercent}% relative humidity.`,
  };

  // the room's surfaces, each with its material and the absorption of that material in the band being shown
  const surfaceInfos = useMemo<SurfaceInfo[]>(() => {
    const byId = new Map(materials.map((m) => [m.id, m]));
    const info = (key: string, label: string, surface: Surface | undefined, areaM2: number): SurfaceInfo => {
      if (surface?.open) return { key, label, materialName: 'Open (sound passes out)', alpha: 1, areaM2 };
      // an audience area absorbs as if it were larger by a 0.5 m strip along its exposed edge
      const edge = (a: number | null) => (a === null || surface?.audienceEdgeM === undefined || a >= 1 || areaM2 <= 0 ? a : Math.min(1, a * (areaM2 + 0.5 * surface.audienceEdgeM) / areaM2));
      const audience = surface?.audienceEdgeM !== undefined ? ' (audience area)' : '';
      if (surface?.customAlpha) return { key, label, materialName: `${surface.customName ?? 'Custom'} (custom)${audience}`, alpha: edge(alphaShown(surface.customAlpha, settings.bandIndex, settings.overall)), areaM2 };
      const m = surface === undefined ? undefined : byId.get(surface.materialId);
      return { key, label, materialName: `${m?.name ?? 'No material'}${audience}`, alpha: m ? edge(alphaShown(m.alpha, settings.bandIndex, settings.overall)) : null, areaM2 };
    };
    if (model) return model.groups.map((g) => info(g.name, g.name, groupSurfaces[g.name], g.areaM2));
    const { lengthM: l, widthM: w, heightM: h } = room;
    return [
      info('floor', 'Floor', room.floor, l * w),
      info('walls', 'Walls', room.walls, 2 * (l + w) * h),
      info('ceiling', 'Ceiling', room.ceiling, l * w),
    ];
  }, [materials, model, groupSurfaces, room, settings.bandIndex, settings.overall]);

  const saveProject = async () => {
    try {
      const origin = modelOrigin.current;
      const project = await buildProject({ room, model: model && origin ? { origin, surfaces: groupSurfaces, outdoors } : null, source, receiver, directivity, extras, extraReceivers,
                                           balloonFiles: balloonFiles.current, settings, mapSettings, facing, rayMode, showRays, mySpectra: mine, screens });
      download(`${(model?.name ?? 'room').replace(/\.[^.]+$/, '')}.room3d.json`, JSON.stringify(project));
      setError(null);
    } catch (e) { setError((e as Error).message); }
  };

  /** Puts the app back as a project file left it: the model and measured patterns are read into the engine again, then every setting is set. */
  const loadProject = async (file: File) => {
    setImporting(true);
    try {
      const project = parseProject(await file.text());
      const ids: Record<string, string> = {};
      const patterns: Record<string, Balloon> = {};
      balloonFiles.current = {};
      for (const b of project.balloons) {
        const pattern = new File([fromBase64(b.dataBase64)], b.name);
        const uploaded = await api.uploadBalloon(pattern);
        ids[b.id] = uploaded.id; patterns[uploaded.id] = uploaded; balloonFiles.current[uploaded.id] = pattern;
      }
      const remap = (d: Directivity | null): Directivity | null => (d?.balloonId && ids[d.balloonId] ? { ...d, balloonId: ids[d.balloonId] } : d);

      let loaded: Model | null = null;
      if (project.model) {
        const o = project.model.origin;
        const edits: ModelEdit[] = o.edits ?? (o.fills ?? []).map((fill) => ({ fill }));
        if (o.kind === 'file') {
          const modelFile = new File([fromBase64(o.dataBase64)], o.name);
          loaded = await api.importModel(modelFile, o.scale, o.yUp, o.space);
          modelOrigin.current = { kind: 'file', file: modelFile, scale: o.scale, yUp: o.yUp, space: o.space, edits };
        } else {
          loaded = await api.loadExample(o.name, o.space);
          modelOrigin.current = { kind: 'example', name: o.name, space: o.space, edits };
        }
        // the holes closed, the groups split and the repairs, done again in the same order
        for (const step of edits) {
          const read: Model = loaded;
          const next = 'fill' in step ? api.fillHoles(read.id, step.fill) : 'split' in step ? api.splitGroup(read.id, step.split)
            : 'group' in step ? api.editGroup(read.id, step.group.name, step.group.action, step.group.to)
            : 'geometry' in step ? api.editGeometry(read.id, step.geometry)
            : api.repairModel(read.id, step.repair.boxes, step.repair.screens);
          loaded = { ...(await next), spaces: read.spaces, space: read.space };
        }
        adopt(loaded);
        setGroupSurfaces((now) => ({ ...now, ...Object.fromEntries(Object.entries(project.model!.surfaces).filter(([g]) => g in now)) }));
        setOutdoors(!!project.model.outdoors);
      } else {
        clearModel();
      }
      setBalloons(patterns);
      setRoom(project.room);
      setSource(project.source); setReceiver(project.receiver);
      setDirectivity(remap(project.directivity));
      setExtras(project.extras.map((e) => ({ ...e, directivity: remap(e.directivity),
        // a project saved when sources were set relative to the first has no spectrum of their own: the first's moved by the gain
        lwBandsDb: e.lwBandsDb ?? (project.settings.lwBandsDb ?? initialSettings.lwBandsDb).map((l) => Math.round((l + (e.gainDb ?? 0)) * 10) / 10), lwShape: e.lwShape ?? 'custom' })));
      nextExtra.current = Math.max(0, ...project.extras.map((e) => e.id)) + 1;
      setExtraReceivers(project.extraReceivers ?? []);
      nextReceiver.current = Math.max(0, ...(project.extraReceivers ?? []).map((r) => r.id)) + 1;
      setScreens(project.screens ?? []);
      nextScreen.current = Math.max(0, ...(project.screens ?? []).map((s) => s.id)) + 1;
      setActiveSource(0);
      // a project saved before the STI could come from the sources had a talker's
      setSettings({ ...initialSettings, ...project.settings, stiSignal: project.settings.stiSignal ?? 'talker' });
      if (project.mySpectra?.length) setMine((list) => { const next = [...list.filter((x) => !project.mySpectra!.some((y) => y.id === x.id)), ...project.mySpectra!]; storeMySpectra(next); return next; });
      setMapSettings({ ...initialMapSettings, ...project.mapSettings });
      setFacing(project.facing ?? 180); setRayMode(project.rayMode ?? 'wave'); setShowRays(project.showRays ?? true);
      setResult(null); setMapRaw(null); setRays([]);
      setError(null);
    } catch (e) { setError((e as Error).message); } finally { setImporting(false); }
  };

  // on a narrow window the panel floats over the picture instead of squeezing it, and folds itself away when the window becomes narrow
  useEffect(() => {
    const query = window.matchMedia(NARROW);
    const change = () => { setNarrow(query.matches); if (query.matches) setCollapsed(true); };
    query.addEventListener('change', change);
    return () => query.removeEventListener('change', change);
  }, []);

  // a selection means nothing once the room it belonged to is gone
  useEffect(() => { setSelectedSurface(null); }, [model?.id]);

  const dims = size(frame);
  const shownHz = settings.overall ? null : BANDS_HZ[settings.bandIndex]; // the frequency shown, or null for the overall response

  // the room to work in, and the same room again in the map's view, which is the only one with the map painted on it
  const viewport = (map: MapResult | null, surfacesHidden = false) => (
    <Viewport surfacesHidden={surfacesHidden}
      frame={frame} model={model} box={model ? null : room} source={source} receiver={receiver} rays={rays} mode={rayMode} map={map} directivity={directivity}
      surfaces={surfaceInfos} selectedSurface={selectedSurface} onSelectSurface={setSelectedSurface} bandHz={shownHz}
      onAim={(az, el) => setDirectivity((d) => (d ? { ...d, aimAzimuthDeg: az, aimElevationDeg: el } : d))}
      onSource={setSource} onReceiver={setReceiver}
      highlight={highlight} screens={screens} faults={unusable ? model?.check?.faults : undefined} selectedFault={fault}
      extras={extras} activeSource={activeSource} onActiveSource={setActiveSource} onExtraMove={moveExtra} onExtraAim={extraAim} extraReceivers={extraReceivers} onExtraReceiverMove={moveReceiver}
      facing={facing} onFacing={setFacing} onShape={editGeometry} shapeBusy={importing} viewKey={viewKey}
    />
  );

  // the sources and receivers: the short form in the side panel, all of it in the sources window
  const sourcePanel = (full: boolean) => (
    <SourcePanel source={source} onSource={setSource} receiver={receiver} onReceiver={setReceiver} frame={frame}
                 directivity={directivity} onDirectivity={setDirectivity} extras={extras} onExtras={editExtras} onAddExtra={addExtra}
                 extraReceivers={extraReceivers} onExtraReceivers={setExtraReceivers} onAddReceiver={addReceiver} onReceiverLine={receiversInLine}
                 active={activeSource} onActive={setActiveSource} balloons={balloons} onUpload={uploadBalloon} bandIndex={settings.bandIndex}
                 power={{ shape: settings.lwShape, bandsDb: settings.lwBandsDb, level: settings.level1 }}
                 onPower={(v) => setSettings((s0) => ({ ...s0, lwShape: v.shape, lwBandsDb: v.bandsDb, level1: v.level }))}
                 flags1={{ muted: !!settings.source1Muted, solo: !!settings.source1Solo }}
                 onFlags1={(f) => setSettings((s0) => ({ ...s0, source1Muted: f.muted ?? s0.source1Muted, source1Solo: f.solo ?? s0.source1Solo }))}
                 heard={live.length} di={diOf} mine={mine} onSaveSpectrum={saveSpectrum}
                 full={full} onOpenWindow={full ? () => setSourcesOpen(false) : () => setSourcesOpen((o) => !o)} windowOpen={full || sourcesOpen} />
  );

  return (
    <div className="relative flex h-full bg-gradient-to-br from-ink-950 via-ink-900 to-ink-950">
      <div className={narrow && !collapsed ? 'absolute inset-y-0 left-0 z-40 flex shadow-2xl shadow-black/70' : 'contents'}>
      <Sidebar
        collapsed={collapsed} onToggle={() => setCollapsed((c) => !c)}
        room={room} onRoom={setRoom} settings={settings} onSettings={setSettings}
        busy={busy} elapsed={elapsed} progress={progress} onCancel={() => runAbort.current?.abort()} online={online} canRun={!unusable && live.length > 0} onRun={run} onQuick={quickEstimate}
        mapBusy={mapBusy} mapProgress={mapProgress} hasMap={mapRaw !== null} mapDisabled={mapSettings.mode === 'volume' && mapSettings.heightsM.length === 0}
        onMap={calculateMap} onCancelMap={() => mapAbort.current?.abort()}
        model={model} surfacesOpen={surfacesOpen} onToggleSurfaces={() => setSurfacesOpen((o) => !o)} settingsOpen={settingsOpen} onToggleSettings={() => setSettingsOpen((o) => !o)}
        examples={examples} importing={importing}
        onImportFile={(file, scale, yUp) => importWith(() => api.importModel(file, scale, yUp), { kind: 'file', file, scale, yUp })}
        onLoadExample={(name) => importWith(() => api.loadExample(name), { kind: 'example', name })}
        onClearModel={clearModel}
        onPickSpace={(space) => { const o = modelOrigin.current; if (o) importWith(() => o.kind === 'file' ? api.importModel(o.file, o.scale, o.yUp, space) : api.loadExample(o.name, space), { ...o, space, edits: undefined }); }}
        sourcesPanel={sourcePanel(false)}
      />
      </div>
      {settingsOpen && (
        <PopOut title="Settings" width={400} height={760} onClose={() => setSettingsOpen(false)}>
          <CalculationSection settings={settings} onSettings={setSettings} />
          <MapSection settings={mapSettings} onSettings={setMapSettings} frame={frame} surfaceChoices={surfaceChoices} reflectorChoices={reflectorChoices} />
        </PopOut>
      )}
      {sourcesOpen && (
        <PopOut title="Sources and receivers" width={440} height={820} onClose={() => setSourcesOpen(false)}>
          {sourcePanel(true)}
        </PopOut>
      )}
      {jobsOpen && (
        <PopOut title="Job list" width={560} height={760} onClose={() => setJobsOpen(false)}>
          <JobList {...jobs} current={unusable || live.length === 0 ? null : simulateParams}
                   what={`${model ? model.name : `${room.lengthM} × ${room.widthM} × ${room.heightM} m box`}, ${live.length} source${live.length === 1 ? '' : 's'}, ${1 + extraReceivers.length} receiver${extraReceivers.length ? 's' : ''}, ${settings.rayCount.toLocaleString('en-GB')} rays`} />
        </PopOut>
      )}
      {surfacesOpen && (
        <PopOut title="Surfaces" width={520} height={760} onClose={() => setSurfacesOpen(false)}>
          <SurfacesPanel room={room} onRoom={setRoom} model={model} groupSurfaces={groupSurfaces} onSplit={splitGroup} onEditGroup={editGroup} busy={importing}
                         onGroupSurface={(group, patch) => setGroupSurfaces((s) => ({ ...s, [group]: { ...s[group], ...patch } }))}
                         materials={materials} surfaces={surfaceInfos} selectedSurface={selectedSurface} onSelectSurface={setSelectedSurface} bandHz={shownHz} />
          <ScreensPanel screens={screens} onScreens={setScreens} onAdd={addScreen} onAddPartition={addPartition} materials={materials}
                        fromModel={model ? model.groups.filter((g) => groupSurfaces[g.name]?.screen).map((g) => g.name) : []} />
        </PopOut>
      )}
      <main className="flex min-w-0 flex-1 flex-col gap-3 overflow-y-auto p-3">
        <header className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 px-1">
          <div>
            <h1 className="text-sm font-semibold tracking-wide text-slate-100">{model ? model.name : 'Room Acoustics'}</h1>
            <p className="text-xs text-slate-500">
              {model
                ? <>{model.triangleCount} triangles · {dims.x.toFixed(1)} × {dims.y.toFixed(1)} × {dims.z.toFixed(1)} m{model.volumeM3 !== null && <> · {model.volumeM3.toFixed(0)} m³</>}</>
                : <>{room.lengthM} × {room.widthM} × {room.heightM} m</>}
              {result && quick && <> · statistical estimate from the standard equations (Eyring decay, each source's direct sound with its pattern towards the receiver; no rays)</>}
              {result && !quick && <> · {result.computeSeconds.toFixed(1)} s to compute{result.imageSourceOrderUsed < settings.imageSourceOrder && <span title="Image sources multiply with the number of faces, so a model with many faces is limited to a lower order; the ray tracing is not affected."> · image sources limited to order {result.imageSourceOrderUsed} (many faces)</span>}</>}
              {result && ran && ran.inputsKey !== inputsKey && <span data-testid="stale" className="text-amber-300"> · inputs changed since this was calculated</span>}
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-slate-400 [&>*]:whitespace-nowrap">
            <div role="group" aria-label="Project" className="flex items-center gap-1">
              <button type="button" onClick={saveProject} className="chip" title="Save the room, model, surfaces, sources and settings to a file">Save project</button>
              <button type="button" onClick={() => projectInput.current?.click()} disabled={importing} className="chip" title="Open a saved project">Load project…</button>
              <button type="button" onClick={() => setJobsOpen((o) => !o)} aria-pressed={jobsOpen} className="chip" title="A list of setups to calculate one after another and compare">Job list</button>
              <input ref={projectInput} type="file" accept=".json,.room3d.json" hidden aria-label="Project file"
                     onChange={(e) => { const f = e.target.files?.[0]; if (f) void loadProject(f); e.target.value = ''; }} />
            </div>
            <label className="flex cursor-pointer items-center gap-1.5">
              <input type="checkbox" checked={showRays} onChange={(e) => setShowRays(e.target.checked)} className="accent-violet-400" />
              Show rays
            </label>
            <div role="group" aria-label="Ray display" className="flex items-center gap-1">
              {([['still', 'Still'], ['wave', 'Wave'], ['ball', 'Billiard balls']] as const).map(([m, label]) => (
                <button key={m} type="button" onClick={() => setRayMode(m)} aria-pressed={rayMode === m} className={`chip ${rayMode === m ? 'chip-on' : ''}`}>{label}</button>
              ))}
            </div>
            <span className={`inline-flex items-center gap-1.5 ${online ? 'text-emerald-400' : online === false ? 'text-rose-400' : ''}`} title={version ? `Room Acoustics 3D ${version}` : undefined}>
              <span className="h-1.5 w-1.5 rounded-full bg-current" />
              {online ? 'Engine connected' : online === false ? 'Engine offline' : 'Connecting…'}
            </span>
          </div>
        </header>
        {error && <div className="rounded-lg border border-rose-500/30 bg-rose-500/10 px-3 py-2 text-xs text-rose-300">{error}</div>}
        {repairs && (
          <div className="rounded-lg border border-emerald-500/30 bg-emerald-500/10 px-3 py-2 text-xs text-emerald-200" data-testid="repairs">
            {repairs.length === 0 ? 'The repair found nothing it could put right.' : (
              <>
                The model was repaired{model?.closedError ? ', but is not closed yet' : ' and is now a closed room'}:
                <ul className="mt-1 list-disc pl-5">{repairs.map((r, i) => <li key={i}>{r}</li>)}</ul>
                The new groups are in the Surfaces window, to be given finishes; a screen made from part of a group starts with that group's finish.
              </>
            )}
            <button type="button" className="chip mt-1.5" onClick={() => setRepairs(null)}>OK</button>
          </div>
        )}
        {unusable && (
          <div className="rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-200">
            This model cannot be simulated yet: {unusable} It is shown so you can see what was read; every edge must be shared by exactly two faces (a closed surface).
            {model?.check && <GeometryCheck check={model.check} selected={fault} onSelect={setFault} onFill={fillHoles} onRepair={repairModel} busy={importing} />}
            <button type="button" className="chip mt-1.5" onClick={() => setOutdoors(true)}
                    title="For a site, a courtyard or a street: every surface stands in the open air, sound reflecting off either side, inside an open box that keeps nothing that reaches it">Take it as outdoors (open air)</button>
          </div>
        )}
        {model && outdoors && (
          <div className="flex flex-wrap items-center gap-2 rounded-lg border border-violet-500/30 bg-violet-500/10 px-3 py-2 text-xs text-violet-100" data-testid="outdoors">
            <span>Outdoors: every surface of the model stands in the open air and reflects from either side; sound that leaves the model is lost, as to the sky. Sound bends round a surface's free edges, but not round a closed building. The statistical estimates and reverberation times mean little here.</span>
            <button type="button" className="chip" onClick={() => setOutdoors(false)}>Not outdoors</button>
          </div>
        )}
        {model && Object.keys(model.skipped).length > 0 && (
          <div className="rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-200">
            Not read: {Object.entries(model.skipped).map(([k, v]) => `${v} ${k}`).join(', ')}. Only 3-D faces are used; explode solids and meshes to faces in your CAD program first.
          </div>
        )}
        {online === false && (
          <div className="rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-200">
            The engine is not running. Start Room Acoustics (RoomAcoustics.exe) again; from the source code, <code className="text-amber-100">./run-app.ps1</code> or <code className="text-amber-100">./run-web.ps1</code>.
          </div>
        )}
        <div role="tablist" aria-label="View" className="flex gap-1.5 px-1">
          {([['work', '3-D and results'], ['map', 'Map']] as const).map(([v, label]) => (
            <button key={v} type="button" role="tab" aria-selected={view === v} onClick={() => showView(v)} className={`chip ${view === v ? 'chip-on' : ''}`}>{label}</button>
          ))}
          <a href="/guide.html" target="_blank" rel="noopener" className="chip ml-auto" title="How to use the program">User guide</a>
          <a href="/theory.html" target="_blank" rel="noopener" className="chip" title="The methods and formulas the engine uses, with references">Theory and references</a>
        </div>
        {/* the views stay mounted, so the cameras and the chart tab are as they were left */}
        <div className={view === 'work' ? 'contents' : 'hidden'}>
          <section className="card relative min-h-[260px] flex-1 overflow-hidden">{viewport(null)}</section>
          <section className="card min-h-[300px] flex-1 overflow-hidden">
            <Charts result={result} bandIndex={settings.bandIndex} overall={settings.overall} busy={busy} report={report} mapRaw={mapRaw} reflectionList={reflectionList}
                    reflections={<Reflections spec={spec} source={sentSource} receiver={receiver} directivity={sent} onData={setReflectionList} temperatureC={settings.temperatureC} humidityPercent={settings.humidityPercent}
                                              bandIndex={settings.bandIndex} overall={settings.overall} shownHz={shownHz} online={online} disabled={!!unusable} onHighlight={setHighlight} />}
                    thirdOctaves={<ThirdOctaves params={simulateParams} disabled={!!unusable || online === false} />}
                    listen={<Listen spec={spec} source={sentSource} receiver={receiver} directivity={sent} extraSources={extraSources} facing={facing} onFacing={setFacing} settings={listenSettings} online={online} disabled={!!unusable} />} />
          </section>
        </div>
        {mapShown && (
          <div className={`min-h-[320px] flex-1 flex-col gap-2 ${view === 'map' ? 'flex' : 'hidden'}`}>
            <MapBar metric={mapSettings.metric} overall={settings.overall} bandIndex={settings.bandIndex}
                    onMetric={(metric) => setMapSettings((m) => ({ ...m, metric }))} onOverall={(overall) => setSettings((s) => ({ ...s, overall }))}
                    onBand={(bandIndex) => setSettings((s) => ({ ...s, bandIndex, overall: false }))}
                    onExport={mapRaw ? () => download('map.csv', mapCsv(mapRaw), 'text/csv') : undefined}
                    onImage={mapResult ? async () => {
                      const png = mapPicture.current && await snapshotPng(mapPicture.current, `${MAP_METRICS.find((m) => m.key === mapResult.metric)?.label ?? mapResult.metric} map`);
                      if (png) download(`map-${mapResult.metric}.png`, png, 'image/png');
                    } : undefined}
                    reflectorAvailable={mapRaw?.parameters.Reflector !== undefined}
                    unit={mapResult?.unit ?? null} ownRange={mapOwnRange} scale={mapScale}
                    onScale={(scale) => setMapSettings((m) => ({ ...m, scales: { ...m.scales, [m.metric]: scale } }))} />
            <div className="flex min-h-0 flex-1 flex-col gap-3 lg:flex-row">
            <section className="card relative min-h-[240px] flex-1 overflow-hidden">{viewport(mapResult, true)}</section>
            <section ref={mapPicture} className="card relative min-h-[240px] flex-1 overflow-hidden p-3">
              {mapResult
                ? <MapView key={mapResult.computeSeconds} map={mapResult} frame={frame} source={source} receiver={receiver} extras={extras.map((e) => e.position)} onReceiver={setReceiver} />
                : <div className="absolute inset-0 grid place-items-center text-sm text-slate-500">Press Calculate map in the sidebar.</div>}
            </section>
            </div>
          </div>
        )}
      </main>
    </div>
  );
}
