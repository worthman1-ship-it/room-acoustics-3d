// The shapes the C# engine (src/RoomAcoustics.Api) sends and receives. Positions are in the engine's frame: x along the length, y across the width, z up, metres.

export type Point = { x: number; y: number; z: number };
/** A surface's finish: a material from the engine's list, or (when `customAlpha` is set, eight coefficients 63 Hz to 8 kHz) one the user entered. */
/** `audienceEdgeM` set makes the surface an audience (seating) area with that much of its edge open to the room (0.5 m strips are added along it); `open` makes it an opening that sound leaves through. */
/** `screen`, for a group of an imported model, makes its surfaces free-standing screens inside the room; `transmissionLossDb`, the sound reduction R of a screen or partition in each octave band, lets that much through it. */
export type Surface = { materialId: number; scattering: number; customAlpha?: number[]; customName?: string; audienceEdgeM?: number; open?: boolean; screen?: boolean; transmissionLossDb?: number[] };
/** An opening in a screen standing on its base, `offsetM` along it from its first end. */
export type Door = { offsetM: number; widthM: number; heightM: number };
/** A free-standing screen as the engine takes it: a vertical rectangle centred on (x, y), its bottom edge at baseZ, turned angleDeg from x towards y. */
export type ScreenDto = { x: number; y: number; baseZ: number; lengthM: number; heightM: number; angleDeg: number; finish: Surface; door?: Door };
/** A screen as the page keeps it. */
export type Screen = ScreenDto & { id: number };
export type Room = { lengthM: number; widthM: number; heightM: number; floor: Surface; walls: Surface; ceiling: Surface; screens?: ScreenDto[] };

/** One surface of the room as the interface sees it: a box's floor, walls (all four) or ceiling, or an imported model's surface group. */
export type SurfaceInfo = { key: string; label: string; materialName: string; alpha: number | null; areaM2: number };

export type Material = { id: number; name: string; category: string; alpha: (number | null)[] };

export type Ray = { points: number[][]; reflections: number; scattered: boolean; reachesReceiver: boolean; levelsDb: number[] };

export type Band = (number | null)[];

export type Metrics = {
  t30S: Band; t20S: Band; edtS: Band; c50Db: Band; c80Db: Band; d50: Band; centreTimeS: Band;
  strengthGDb: Band; sabineT60S: Band; eyringT60S: Band;
  /** The other global estimates: Millington-Sette, Arau-Puchades and Fitzroy (the last two weigh the three pairs of room faces). */
  millingtonT60S: Band; arauPuchadesT60S: Band; fitzroyT60S: Band;
  /** The rest of ISO 3382-1 per band: early and late strength, lateral fraction and its coefficient, late lateral level LJ, and stage support (null where a statistical estimate cannot give them). */
  earlyStrengthGDb: Band; lateStrengthGDb: Band; lf80: Band; lfc80: Band; lateLateralLevelDb: Band; stageSupportEarlyDb: Band; stageSupportLateDb: Band;
  /** Sound pressure level, un-weighted and A-weighted (given the source sound power), and the echo criterion for speech and for music. */
  splDb: Band; splADb: Band; echoCriterionSpeech: Band; echoCriterionMusic: Band;
  /** G of the direct sound alone, and the direct sound against all that follows it (null where a wall blocks the direct sound; absent from a quick estimate). */
  directStrengthGDb?: Band | null; directToReverberantDb?: Band | null;
  /** Gade's total stage support (20 ms to 1 s over the first 10 ms) and early ensemble level (the first 80 ms over the direct sound at 1 m); absent from a quick estimate. */
  stageSupportTotalDb?: Band | null; earlyEnsembleLevelDb?: Band | null;
  /** The interaural cross-correlation coefficient of a head facing the source, 0 to 80 ms and 80 ms to 1 s after the first arrival; absent from a quick estimate. */
  iaccEarly?: Band | null; iaccLate?: Band | null;
  /** the same over the whole response, 0 to 1 s */
  iaccAll?: Band | null;
};

/** The ISO 3382-1 single-number values: each the mean of the octave bands the standard averages it over. */
export type Broadband = {
  t30S: number | null; t20S: number | null; edtS: number | null; c50Db: number | null; c80Db: number | null; d50: number | null; centreTimeS: number | null;
  strengthGDb: number | null; earlyStrengthGDb: number | null; lateStrengthGDb: number | null;
  lf80: number | null; lfc80: number | null; lateLateralLevelDb: number | null; stageSupportEarlyDb: number | null; stageSupportLateDb: number | null;
  /** Sound pressure level (the bands' energies added; A-weighted too), the worst echo criterion (500 Hz to 2 kHz) and Beranek's bass and brilliance ratios. */
  splDb: number | null; splADb: number | null; echoCriterionSpeech: number | null; echoCriterionMusic: number | null; bassRatio: number | null; brilliance: number | null;
  directStrengthGDb: number | null; directToReverberantDb: number | null;
  stageSupportTotalDb: number | null; earlyEnsembleLevelDb: number | null;
  /** IACC E3 and L3 (the early and late coefficients' mean over 500 Hz to 2 kHz) and the binaural quality index, 1 - IACC E3. */
  iaccEarly: number | null; iaccLate: number | null; binauralQualityIndex: number | null;
  /** IACC A3, the whole response's coefficient over 500 Hz to 2 kHz */
  iaccAll: number | null;
};

/** The Speech Transmission Index at the receiver, with the talker and the background it was worked out for. */
export type Sti = {
  sti: number | null; rating: string; voice: 'male' | 'female'; speechLevelDbA: number; backgroundNc: number; alconsPercent: number | null; stipa?: number | null; rasti?: number | null;
  /** what the speech was: a talker at speechLevelDbA at 1 m, or the sources' own levels; and the A-weighted speech level that reached the receiver */
  signalFrom?: 'talker' | 'sources'; signalDbA?: number | null;
  /** the Speech Intelligibility Index (ANSI S3.5, octave bands, reverberation counted through the modulation transfer), 0 to 1 */
  sii?: number | null;
};

/** Every result at one receiver; the first receiver is the one the main results are for. */
export type ReceiverResult = { position: Point; directDistanceM: number; metrics: Metrics; broadband: Broadband; sti: Sti; decay?: SimulateResponse['decay']; impulse?: SimulateResponse['impulse'] };

export type SimulateResponse = {
  imageSourceOrderUsed: number;
  volumeM3: number; surfaceAreaM2: number; speedOfSoundMs: number; directDistanceM: number; computeSeconds: number;
  metrics: Metrics;
  /** `overall…` is the eight bands' energies added together */
  decay: { stepS: number; schroederDb: Band[]; overallSchroederDb: Band };
  impulse: { stepS: number; levelDb: Band[]; overallDb: Band };
  broadband: Broadband;
  sti: Sti;
  /** The main receiver first, then each further one (absent from the quick estimate). */
  receivers?: ReceiverResult[];
  /** The ISO 3382-3 open-plan office results, the receivers as the positions on a line away from the source (absent from the quick estimate). */
  openPlan?: OpenPlan | null;
  /** The ISO 14257 workroom results, the receivers as the positions on a line away from the source (absent from the quick estimate). */
  workroom?: Workroom | null;
};

/**
 * ISO 14257 (workrooms): each receiver a position on a line away from one omnidirectional source, with its sound distribution value Lp - LW in each octave band
 * 125 Hz to 4 kHz, for A-weighted pink noise and for the source's own sound power (null without one), and the free-field reference there. `curves` has DL2
 * (decay per doubling), DLf (average excess over free field) and DL'fr (the regression line's excess at `atM`) of each curve in each region (near, middle, far,
 * divided at `d1M` and `d2M`; null where fewer than three positions lie in it); `notWorkedOut` says why there are none.
 */
export type Workroom = {
  notWorkedOut: string | null; omnidirectional: boolean; d1M: number; d2M: number;
  positions: { receiver: number; distanceM: number; referenceDb: number; bandsDb: (number | null)[]; normalisedDb: number | null; sourceDb: number | null }[];
  curves: { curve: string; regions: WorkroomRegion[] }[];
};

/** One ISO 14257 region of distance from the source (`toM` null for the far region, which has no end), and a curve's single numbers in it. */
export type WorkroomRegion = {
  region: 'near' | 'middle' | 'far'; fromM: number; toM: number | null; atM: number;
  decayPerDoublingDb: number | null; excessDb: number | null; excessAtDb: number | null; positions: number;
};

/**
 * ISO 3382-3 (open-plan offices): the source as the standard's omnidirectional source of normal speech, each receiver a position. `notWorkedOut` says why
 * the single numbers are missing (more than one source heard, fewer than four receivers more than 1 m away); `omnidirectional` is false when the source has a
 * pattern, which the standard does not use. Each position has the standard's A-weighted speech level there and its STI (male weighting, the background noise).
 */
export type OpenPlan = {
  notWorkedOut: string | null; omnidirectional: boolean; backgroundDbA: number | null;
  positions: { receiver: number; distanceM: number; speechDbA: number | null; sti: number | null }[];
  spatialDecayDb: number | null; speechAt4mDbA: number | null; comfortDistanceM: number | null; distractionDistanceM: number | null; privacyDistanceM: number | null;
  nearestSti: number | null; decayPositions: number; stiPositions: number;
};

/** The NC curves the Speech Transmission Index can take for the background noise (the engine's list). */
export const NC_RATINGS = [15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70] as const;

export const BANDS_HZ = [63, 125, 250, 500, 1000, 2000, 4000, 8000] as const;
export const bandLabel = (hz: number) => (hz >= 1000 ? `${hz / 1000}k` : `${hz}`);
/** The band as the page words it: a band's frequency, or (null) the overall response, for which absorption is the mean of the 500 Hz and 1 kHz bands. */
export const bandHzText = (hz: number | null) => (hz === null ? '500 Hz–1 kHz (mean)' : `${bandLabel(hz)} Hz`);
/** A surface's absorption in the band shown, or for the overall response the mean of its 500 Hz and 1 kHz figures. */
export const alphaShown = (alpha: (number | null)[], bandIndex: number, overall: boolean): number | null => {
  if (!overall) return alpha[bandIndex] ?? null;
  const mid = [alpha[3], alpha[4]].filter((a): a is number => a !== null && a !== undefined);
  return mid.length === 0 ? null : mid.reduce((x, y) => x + y, 0) / mid.length;
};

// An imported model (DXF, OBJ or STL): its surface groups with their triangles for drawing, and what the engine made of it.
/** `surfaces`: how many flat surfaces the group could be split into. */
/** `exposedEdgeM` is how much of the group's outline is open to the room (not against a wall), the exposed edge it would have as an audience area. */
export type ModelGroup = { name: string; areaM2: number; positions: number[]; surfaces?: number; exposedEdgeM?: number };
export type Model = {
  id: string; name: string; triangleCount: number; groups: ModelGroup[];
  bounds: { min: Point; max: Point }; volumeM3: number | null; closedError: string | null;
  suggestedSource: Point | null; suggestedReceiver: Point | null; skipped: Record<string, number>;
  /** the groups named as screens, taken to be free-standing screens to begin with */
  screenGroups?: string[];
  /** where a model that is not a closed surface fails to be one */
  check?: GeometryCheck | null;
  /** an IFC file's room volumes, sorted by name, and which of them was read */
  spaces?: ModelSpace[] | null; space?: number | null;
  /** after a repair, what it did */
  repairs?: string[] | null;
};
/** One room volume (IfcSpace) of an IFC file; an area object (a floor's gross area, say) is not a room. */
export type ModelSpace = { name: string; volumeM3: number; isArea: boolean };
/** One place a model is not a closed surface: its edges as [x1, y1, z1, x2, y2, z2] (a separate part's bounding box), the groups there, and how to put it right. */
export type GeometryFault = { kind: 'hole' | 'crowded' | 'separate'; edges: number[][]; groups: string[]; lengthM: number; hint: string; fillable?: boolean };
export type GeometryCheck = { openEdges: number; crowdedEdges: number; parts: number; faults: GeometryFault[] };
/** `outdoors` takes the model as standing in the open air: every surface two-sided, in an open box round it, so it need not be closed. */
export type ModelSpec = { modelId: string; surfaces: Record<string, Surface>; screens?: ScreenDto[]; outdoors?: boolean };

/** What the engine is asked to simulate: a box room, or an imported model with a surface for each of its groups. */
export type RoomSpec = { room: Room; model?: undefined } | { model: ModelSpec; room?: undefined };

/** A coherent line array: N elements at a spacing, up and down or across, steered by a delay step and curved by a splay, shaded to trade beam width for side lobes. */
export type LineArray = {
  elements: number; spacingM: number; orientation: 'vertical' | 'horizontal'; delayStepMs: number; splayDegPerElement: number;
  shading: 'uniform' | 'hamming' | 'hann' | 'sine' | 'chebyshev'; sidelobeDb: number;
};

/**
 * A directional source, aimed along an azimuth (from +x towards +y) and an elevation. Null in the app means omnidirectional. The default kind is a horn-like
 * pattern from -6 dB coverage angles; a line array's elements have that pattern too; a measured pattern is one uploaded earlier (its id).
 */
export type Directivity = {
  kind?: 'horn' | 'lineArray' | 'balloon' | 'talker' | 'surface'; horizontalDeg: number; verticalDeg: number; aimAzimuthDeg: number; aimElevationDeg: number; floorDb?: number;
  lineArray?: LineArray; balloonId?: string;
  /** a surface source: a rectangle centred on the source, facing where it is aimed, radiating evenly over its area (to one side only, as a cosine, if one-sided) */
  surface?: SurfaceSource;
};
export type SurfaceSource = { widthM: number; heightM: number; oneSided: boolean };

/** Another receiver in the same room. */
export type ExtraReceiver = { id: number; position: Point };
/** Another source in the same room: where it is, how it radiates, its delay, and its own sound power level by octave band and the spectrum it was set from. */
export type ExtraSource = {
  id: number; position: Point; directivity: Directivity | null; gainDb: number; delayMs: number; lwBandsDb: number[]; lwShape: string;
  /** how its level was entered, when not as a sound power (absent in projects saved before) */
  level?: LevelEntry;
  /** left out of every calculation; a solo leaves out every source that is not soloed */
  muted?: boolean; solo?: boolean;
};

/**
 * How a source's level was entered: as its sound power (`lw`, the bands are the source's Lw), as its level 1 m in front of it on its axis (`lp1m`, kept in
 * `lp1mBandsDb`), or as a loudspeaker's sensitivity (dB SPL at 1 W and 1 m, on axis) and the electrical power fed to it (`sens`), spread over the bands by
 * the spectrum. The last two give the sound power through the pattern's on-axis directivity index: Lw = Lp(1 m) + 11 - DI.
 */
export type LevelEntry = { mode: 'lw' | 'lp1m' | 'sens'; lp1mBandsDb?: number[]; sensitivityDb?: number; watts?: number };
export type SourceDto = { position: Point; directivity: Directivity | null; gainDb: number; delayMs: number; soundPowerDb?: number[] };

/** A measured balloon after upload: its size, level range and a cut through it for each band (horizontal: elevation 0; vertical: azimuth 0). */
export type PolarCut = { bandHz: number; anglesDeg: number[]; levelsDb: number[] };
export type Balloon = { id: string; name: string; azimuths: number; elevations: number; minDb: number; maxDb: number; horizontal: PolarCut[]; vertical: PolarCut[] };

/** What a map can show: the ISO 3382 parameters and the speech measures (STI, SII: one number per point, so they have no octave bands). The lateral and stage-support ones need the arrival directions. */
export const MAP_METRICS = [
  { key: 'T30', label: 'T30 (reverberation time)' }, { key: 'T20', label: 'T20 (reverberation time)' }, { key: 'EDT', label: 'EDT (early decay time)' }, { key: 'Ts', label: 'Ts (centre time)' },
  { key: 'C80', label: 'C80 (clarity, music)' }, { key: 'C50', label: 'C50 (clarity, speech)' }, { key: 'D50', label: 'D50 (definition)' },
  { key: 'G', label: 'G (strength)' }, { key: 'Gearly', label: 'G early (0–80 ms)' }, { key: 'Glate', label: 'G late (after 80 ms)' },
  { key: 'Gdirect', label: 'G direct (direct sound only)' }, { key: 'DR', label: 'D/R (direct to reverberant)' },
  { key: 'LF', label: 'LF (lateral fraction)' }, { key: 'LFC', label: 'LFC (lateral coefficient)' }, { key: 'LJ', label: 'LJ (late lateral level)' },
  { key: 'STearly', label: 'ST early (stage support)' }, { key: 'STlate', label: 'ST late (stage support)' },
  { key: 'STtotal', label: 'ST total (stage support)' }, { key: 'EEL', label: 'EEL (early ensemble level)' },
  { key: 'IACCearly', label: 'IACC early (0–80 ms)' }, { key: 'IACClate', label: 'IACC late (80 ms–1 s)' }, { key: 'IACCall', label: 'IACC whole (0–1 s)' },
  { key: 'BQI', label: 'BQI (binaural quality index)' }, { key: 'STI', label: 'STI (speech transmission)' }, { key: 'SII', label: 'SII (speech intelligibility index)' },
  { key: 'Alcons', label: '%ALcons (speech consonant loss)' }, { key: 'BassRatio', label: 'BR (bass ratio)' }, { key: 'Brilliance', label: 'Br (brilliance ratio)' },
  { key: 'EKspeech', label: 'EK (echo criterion, speech)' }, { key: 'EKmusic', label: 'EK (echo criterion, music)' },
  { key: 'SPL', label: 'SPL (sound pressure level)' }, { key: 'SPLA', label: 'SPL(A) (A-weighted level)' },
  { key: 'Reflector', label: 'Reflector coverage (reflection level vs direct)' },
] as const;

/** The map parameters that are one number at a point, with no octave bands. */
export const ONE_NUMBER_METRICS: readonly string[] = ['STI', 'SII', 'Alcons', 'BassRatio', 'Brilliance', 'Reflector'];

/** Levels added as energies: the overall level of the octave bands, or of several sources in one band. */
export const totalDb = (levels: readonly number[]) => 10 * Math.log10(levels.reduce((s, l) => s + 10 ** (l / 10), 0));
export const overallPower = (bands: readonly number[]) => Math.round(totalDb(bands) * 10) / 10;

export type MapMode = 'plan' | 'section' | 'volume' | 'surface';
export type MapPoint = { x: number; y: number; z: number; value: number | null };
/**
 * A map of one metric as the engine sends it, with the value in every octave band at each point: a plan (one height), a vertical section (the plane x or y = positionM,
 * with the room's outline on it as U1 V1 U2 V2 segments), a volume (a plan at each height) or a surface map (a fixed height above `surfaceGroup`, so it follows its slope).
 */
export type MapRaw = {
  metric: string; unit: string; heightM: number; spacingM: number; computeSeconds: number; points: { x: number; y: number; z: number }[];
  /** Every parameter at every point, in the order of `points`: its value in each octave band (null for the STI) and its broadband single number. */
  parameters: Record<string, { unit: string; values: (number | null)[][] | null; broadband: (number | null)[] }>;
  mode: MapMode; axis: 'x' | 'y' | null; positionM: number | null; heights: number[] | null; outline: number[][] | null; surfaceGroup: string | null;
};
/** A map's colour scale as the user set it for one parameter: either end (null for the map's own), and the step between contour lines (null for none). */
export type MapScale = { min: number | null; max: number | null; contourStep: number | null };
/** The map as shown: one parameter, in one octave band or (`band` null) as its broadband number, at each point, with the scale set for that parameter. */
export type MapResult = Omit<MapRaw, 'points' | 'parameters'> & { band: number | null; points: MapPoint[]; scale?: MapScale };
export const atBand = (raw: MapRaw, metric: string, band: number | null): MapResult => {
  const shown = raw.parameters[metric] ?? raw.parameters[raw.metric];
  // a parameter with no octave bands (the STI) is always its single number
  const bands = band === null ? null : shown.values;
  return {
    ...raw, metric: raw.parameters[metric] ? metric : raw.metric, unit: shown.unit, band: bands ? band : null,
    points: raw.points.map((p, i) => ({ x: p.x, y: p.y, z: p.z, value: (bands && band !== null ? bands[i][band] : shown.broadband[i]) ?? null })),
  };
};

/** The room's response at a listener's two ears (little-endian 32-bit floats in base64); convolve any sound with it to hear the room. */
/** An Ambisonic impulse response of order 1 to 3: (order + 1)² channels in AmbiX order (ACN, W, Y, Z, X first; SN3D), little-endian 32-bit floats in base64. */
export type AmbisonicResult = { sampleRateHz: number; samples: number; channelsF32: string[]; durationS: number; computeSeconds: number };

/** A measured head-related transfer function uploaded as a SOFA file, kept by the engine under its id. */
export type Hrtf = { id: string; name: string; measurements: number; sampleRateHz: number };

/**
 * A measured impulse response's ISO 3382-1 parameters per octave band, timed from its direct sound (directArrivalS into the recording). dynamicRangeDb is
 * each band's peak over its noise floor (null where the end is silent, 0 where the band could not be used); a value that needs more range is null.
 */
export type Measured = {
  name: string; sampleRateHz: number; channels: number; durationS: number; directArrivalS: number; dynamicRangeDb: Band;
  t30S: Band; t20S: Band; edtS: Band; c50Db: Band; c80Db: Band; d50: Band; centreTimeS: Band;
};

/** One source's two ears' responses, on the same scale as the other sources'. */
export type Ears = { leftF32: string; rightF32: string };

export type AuraliseResult = {
  sampleRateHz: number; samples: number; leftF32: string; rightF32: string; durationS: number;
  interauralLevelDifferenceDb: number; interauralTimeDifferenceMs: number; iaccE3: number | null; iaccEarly: (number | null)[];
  /** The late coefficient (80 ms to 1 s) by band and as its 500 Hz to 2 kHz mean, and the binaural quality index, 1 minus IACC E3. */
  iaccLate: (number | null)[]; iaccL3: number | null; binauralQualityIndex: number | null; computeSeconds: number;
  /** each source on its own, when asked for separately */
  sources?: Ears[] | null;
};

/** What the user chose for a map. */
export type MapSettings = {
  /** The parameter shown, in the octave band chosen in the side panel (or its overall value). */
  metric: string; heightM: number; spacingM: number; rayCount: number;
  mode: MapMode; axis: 'x' | 'y'; positionM: number; heightsM: number[]; surfaceGroup: string | null; aboveM: number;
  /** The surface whose first reflections the Reflector coverage parameter shows (null for none), and the window after the direct sound they must arrive in. */
  reflectorGroup: string | null; reflectorFromMs: number; reflectorToMs: number;
  /** The colour scale and contours set for each parameter, by its key; a parameter not in it is scaled to its own values. */
  scales: Record<string, MapScale>;
};

/** One specular reflection at the receiver: the surfaces it bounces off, when it arrives after the direct sound, and its level against the direct sound. */
export type Reflection = { order: number; surfaces: string[]; distanceM: number; delayMs: number; levelsDb: (number | null)[]; overallDb: number | null; path: number[][] };
export type ReflectionsResult = { directDistanceM: number; directDelayMs: number; imageSourceOrderUsed: number; total: number; reflections: Reflection[] };

/** One third-octave band at the receiver (null where a value cannot be worked out). */
export type ThirdOctaveBand = {
  hz: number; t30S: number | null; t20S: number | null; edtS: number | null; c80Db: number | null; c50Db: number | null; d50: number | null;
  centreTimeS: number | null; strengthGDb: number | null;
};

/** The simulation at the main receiver in the 23 third-octave bands 50 Hz to 8 kHz. */
export type ThirdOctaveResult = { bands: ThirdOctaveBand[]; computeSeconds: number };
