import { BANDS_HZ, totalDb, type LevelEntry } from './types';

/**
 * Source spectra a level can be shaped by. `shape` is in dB by octave band (63 Hz to 8 kHz). A spectrum from a standard that gives absolute levels has
 * `standard`: what its levels are (the level 1 m in front of the source on its axis, or the sound power) and their overall level, all bands' energies added;
 * picking it sets that level. Without one only the shape matters, and the level already set is kept.
 */
export type Spectrum = {
  id: string; group: string; name: string; shape: number[]; note: string;
  standard?: { quantity: 'lp1m' | 'lw'; overallDb: number };
};

/** Lw = Lp(1 m) + 10 lg(4 pi) for a source that radiates equally in every direction: a free field's level 1 m from it. */
export const LP_1M_TO_LW_DB = 10 * Math.log10(4 * Math.PI);

const round1 = (v: number) => Math.round(v * 10) / 10;

// ANSI S3.5-1997 Table 4 (octave bands) and Table 1 (the 150 Hz critical band): the standard speech spectrum level, in dB per Hz, for each vocal effort
const ANSI_SPECTRUM_LEVEL_PER_HZ = {
  normal: { 150: 31.44, 250: 34.75, 500: 34.27, 1000: 25.01, 2000: 17.32, 4000: 9.33, 8000: 1.13 },
  raised: { 150: 34.06, 250: 38.98, 500: 40.15, 1000: 33.86, 2000: 25.32, 4000: 16.78, 8000: 5.07 },
  loud: { 150: 34.21, 250: 41.55, 500: 44.85, 1000: 42.16, 2000: 34.39, 4000: 25.41, 8000: 11.39 },
  shout: { 150: 28.69, 250: 42.5, 500: 49.24, 1000: 51.31, 2000: 44.32, 4000: 34.41, 8000: 20.72 },
} as const;
const ANSI_OVERALL_DB = { normal: 62.35, raised: 68.34, loud: 74.85, shout: 82.3 } as const;

/** An octave band's level from a spectrum level per Hz: plus 10 lg of the band's width, 0.7071 of its centre frequency. */
const octaveBand = (perHz: number, centreHz: number) => perHz + 10 * Math.log10(0.70710678 * centreHz);

/** The 63 Hz to 8 kHz band levels of an ANSI vocal effort, 1 m in front of the mouth: 125 Hz from the 150 Hz critical band, the lowest the standard goes, and 63 Hz 12 dB below that. */
function ansiShape(effort: keyof typeof ANSI_SPECTRUM_LEVEL_PER_HZ): number[] {
  const t = ANSI_SPECTRUM_LEVEL_PER_HZ[effort];
  const at125 = octaveBand(t[150], 125);
  return [at125 - 12, at125, octaveBand(t[250], 250), octaveBand(t[500], 500), octaveBand(t[1000], 1000), octaveBand(t[2000], 2000), octaveBand(t[4000], 4000), octaveBand(t[8000], 8000)]
    .map(round1);
}

const ANSI_NOTE = 'ANSI/ASA S3.5-1997 (the SII standard) standard speech, 1 m in front of the mouth, as octave bands from its spectrum levels per Hz. The standard stops at 150 Hz, so 125 Hz comes from its 150 Hz band and 63 Hz is a placeholder 12 dB lower; edit them if you know better. Picking it sets the level 1 m in front of the source on its axis; with the Talker pattern that gives a talker\'s sound power.';

// IEC 60268-16 (2011) Table A.4 as ODEON's STI application note gives it: octave levels relative to the A-weighted speech level; the female 125 Hz is ISO 9921's
const IEC_MALE = [2.9, 2.9, -0.8, -6.8, -12.8, -18.8, -24.8];
const IEC_FEMALE = [-4.4, 5.3, -1.9, -9.1, -15.8, -16.7, -18.0];
// ISO 9921's vocal efforts: the A-weighted level 1 m in front of the mouth (ODEON's STI application note, Table 2)
const ISO_9921_EFFORTS = [['relaxed', 54], ['normal', 60], ['raised', 66], ['loud', 72], ['very loud', 78]] as const;
const IEC_NOTE = 'The IEC 60268-16 (2011, Table A.4) speech spectrum, octave levels relative to the A-weighted level (the female 125 Hz value is ISO 9921\'s), set to ISO 9921\'s A-weighted level 1 m in front of the mouth for the vocal effort. IEC\'s spectra are close-talking ones, meant for sound systems; 63 Hz is a placeholder 12 dB below 125 Hz. ISO 9921 also asks for the STI to take a level 0.4 (L - 75) dB lower above 75 dBA, which is not applied here.';

// Hopkins, Graetzer and Seiffert, "Sound power of normal speech for building acoustics", Proc. IOA 46 Pt 2 (2024), Table 1: measured sound power of
// six male and six female talkers, normalised to 60 dB LAeq 1 m in front; their third octaves added into octaves (63 Hz from 63 and 80 Hz only)
const HOPKINS = {
  unisex: [53.4, 65.4, 67.2, 68.3, 63.4, 57.3, 54.3, 55.7],
  male: [55.1, 67.2, 66.9, 68.5, 62.5, 57.3, 55.0, 54.9],
  female: [50.2, 62.2, 67.5, 68.2, 64.1, 57.3, 53.5, 56.4],
} as const;
const HOPKINS_NOTE = 'Sound power of normal speech measured over a hemisphere in an anechoic room (Hopkins, Graetzer and Seiffert, Proc. IOA 46 Pt 2, 2024, Table 1), for talkers whose level 1 m in front is 60 dB LAeq, as ISO 3382-3 assumes; their third octaves added into octaves. Female talkers had no speech energy below 100 Hz, so their 63 Hz (and the unisex one) is a placeholder 12 dB below 125 Hz. They found ISO 3382-3\'s values too low at 125 Hz, 4 kHz and 8 kHz. Picking it sets the sound power.';

// Pearsons, Bennett and Fidell (1977), via Olsen, Am. J. Audiol. 7 (1998), Table 6: third-octave levels 1 m in front, rounded to 1 dB and read from the
// report's figures; added into octaves (125 Hz from 125 and 160 Hz, 8 kHz from 6.3 and 8 kHz: the table has no 100 Hz or 10 kHz)
const PEARSONS = {
  female: { casual: [24.4, 36.4, 49.2, 49.5, 42.0, 39.2, 34.8, 35.5], normal: [25.3, 37.3, 51.1, 53.8, 48.8, 44.2, 43.1, 38.1], raised: [23.4, 35.4, 55.3, 60.1, 57.8, 53.8, 49.0, 42.5],
    loud: [22.2, 34.2, 58.2, 64.3, 66.5, 63.7, 56.8, 49.1], shouted: [18.4, 30.4, 56.3, 70.0, 77.1, 76.0, 69.3, 59.1] },
  male: { casual: [34.1, 46.1, 50.2, 51.6, 42.8, 41.2, 38.8, 35.5], normal: [37.2, 49.2, 55.4, 57.8, 50.8, 46.8, 43.3, 36.8], raised: [41.1, 53.1, 59.2, 63.5, 58.5, 53.5, 48.7, 42.8],
    loud: [44.2, 56.2, 64.9, 72.4, 70.8, 66.1, 59.5, 50.1], shouted: [33.0, 45.0, 70.3, 79.8, 83.5, 80.0, 72.5, 63.1] },
  child: { casual: [15.1, 27.1, 48.7, 52.1, 45.2, 41.6, 37.8, 36.0], normal: [18.2, 30.2, 52.8, 56.1, 51.0, 45.9, 43.8, 39.5], raised: [19.2, 31.2, 55.9, 60.6, 60.0, 55.9, 51.6, 46.0],
    loud: [18.2, 30.2, 55.9, 64.2, 66.8, 65.1, 58.2, 51.5], shouted: [33.0, 45.0, 54.8, 69.6, 75.6, 75.0, 70.0, 58.8] },
} as const;
const PEARSONS_NOTE = 'Speech of females, males and children (13 to 60 years; under 13) at five vocal efforts 1 m in front, in an anechoic room (Pearsons, Bennett and Fidell, 1977, as Olsen 1998 tabulates it in third octaves read from the report\'s figures). The octaves miss the 100 Hz and 10 kHz thirds and 63 Hz is a placeholder 12 dB below 125 Hz, so loud and shouted speech come out 2 to 3 dBA under the report\'s own A-weighted levels. Picking it sets the level 1 m in front of the source on its axis.';

// ISO 3382-3 (2012 and 2022, Table 1) normal unisex speech, 125 Hz to 8 kHz: its sound power, and its level 1 m in front of a talker (ANSI S3.5's normal effort
// from 250 Hz, 125 Hz set 6 dB below 250 Hz); the standard has no 63 Hz, so that is a placeholder 12 dB below 125 Hz
const ISO_3382_3_LW = [48.9, 60.9, 65.3, 69.0, 63.0, 55.8, 49.8, 44.5];
const ISO_3382_3_AXIS = [39.2, 51.2, 57.2, 59.8, 53.5, 48.8, 43.8, 38.6];

export const SPECTRA: Spectrum[] = [
  { id: 'flat', group: 'Noise', name: 'Flat (pink noise: equal level in every octave band)', shape: [0, 0, 0, 0, 0, 0, 0, 0], note: 'The same level in every octave band, which is what pink noise has. The overall level is yours to set.' },
  { id: 'white', group: 'Noise', name: 'White noise (rises 3 dB per octave)', shape: BANDS_HZ.map((_, b) => 3 * b), note: 'Equal energy at every frequency, so each octave band holds twice the energy of the one below. The overall level is yours to set.' },
  ...(['normal', 'raised', 'loud', 'shout'] as const).map((effort): Spectrum => {
    const shape = ansiShape(effort);
    return {
      id: `ansi-${effort}`, group: 'Speech: ANSI S3.5 (1 m in front of the mouth)', name: `${effort[0].toUpperCase()}${effort.slice(1)} voice (${ANSI_OVERALL_DB[effort]} dB at 1 m)`,
      shape, standard: { quantity: 'lp1m', overallDb: round1(totalDb(shape)) }, note: ANSI_NOTE,
    };
  }),
  { id: 'iso3382-3', group: 'Speech: ISO 3382-3 (open-plan offices)', name: 'Normal speech, sound power', shape: ISO_3382_3_LW, standard: { quantity: 'lw', overallDb: round1(totalDb(ISO_3382_3_LW)) },
    note: 'ISO 3382-3 (2012 and 2022, Table 1): the sound power of normal unisex speech, 68.4 dB(A), which the standard radiates from an omnidirectional source (57.4 dB(A) at 1 m). It is ANSI S3.5\'s normal effort with a talker\'s directivity taken out. The standard has no 63 Hz, so that is a placeholder 12 dB below 125 Hz. Hopkins et al. (2024) measured more at 125 Hz, 4 kHz and 8 kHz. Picking it sets the sound power.' },
  { id: 'iso3382-3-axis', group: 'Speech: ISO 3382-3 (open-plan offices)', name: 'Normal speech, 1 m in front of a talker', shape: ISO_3382_3_AXIS, standard: { quantity: 'lp1m', overallDb: round1(totalDb(ISO_3382_3_AXIS)) },
    note: 'ISO 3382-3 (2012 and 2022, Table 1): normal unisex speech 1 m in front of a talker, 59.5 dB(A): ANSI S3.5\'s normal effort from 250 Hz, with 125 Hz set 6 dB below 250 Hz; 63 Hz is a placeholder 12 dB lower. With the Talker pattern it gives close to the standard\'s sound power. Picking it sets the level 1 m in front of the source on its axis.' },
  ...(['unisex', 'male', 'female'] as const).map((who): Spectrum => ({
    id: `hopkins-${who}`, group: 'Speech: Hopkins et al. 2024 (sound power, measured)', name: `Hopkins ${who}, normal speech (sound power)`,
    shape: [...HOPKINS[who]], standard: { quantity: 'lw', overallDb: round1(totalDb(HOPKINS[who])) }, note: HOPKINS_NOTE,
  })),
  ...(['female', 'male'] as const).flatMap((who) => ISO_9921_EFFORTS.map(([effort, dBA]): Spectrum => {
    const relative = who === 'male' ? IEC_MALE : IEC_FEMALE;
    const shape = [relative[0] - 12, ...relative].map((l) => round1(l + dBA));
    // the normal efforts keep the ids these spectra had when they were shapes alone
    return { id: effort === 'normal' ? `iec-${who}` : `iec-${who}-${effort.replace(' ', '-')}`, group: 'Speech: IEC 60268-16 at ISO 9921 efforts (1 m in front)',
      name: `IEC ${who}, ${effort} (${dBA} dBA at 1 m)`, shape, standard: { quantity: 'lp1m', overallDb: round1(totalDb(shape)) }, note: IEC_NOTE };
  })),
  ...(['female', 'male', 'child'] as const).flatMap((who) => (['casual', 'normal', 'raised', 'loud', 'shouted'] as const).map((effort): Spectrum => ({
    id: `pearsons-${who}-${effort}`, group: `Speech: Pearsons et al. 1977, ${who === 'child' ? 'children' : `${who}s`} (1 m in front)`,
    name: `Pearsons ${who}, ${effort}`, shape: [...PEARSONS[who][effort]], standard: { quantity: 'lp1m', overallDb: round1(totalDb(PEARSONS[who][effort])) }, note: PEARSONS_NOTE,
  }))),
  { id: 'speech', group: 'Other', name: 'STI predictor reference (this program\'s quick STI predictor)', shape: [40, 49.5, 55.5, 59.6, 56.3, 50.8, 45.3, 36.6],
    note: 'The reference speech spectrum of the quick STI predictor this program was ported from (125 Hz to 8 kHz; 63 Hz is a placeholder). It is not the IEC 60268-16 spectrum. The overall level is yours to set.' },
];

/** A spectrum by id, from the built-in ones or the user's own. */
export const spectrumById = (id: string, mine: Spectrum[] = []): Spectrum | undefined => SPECTRA.find((s) => s.id === id) ?? mine.find((s) => s.id === id);

/** A spectrum's shape set to an overall level (all bands' energies added), to a tenth of a decibel. */
export function spectrumBands(id: string, overallDb: number, mine: Spectrum[] = []): number[] {
  const shape = spectrumById(id, mine)?.shape ?? SPECTRA[0].shape;
  const offset = overallDb - totalDb(shape);
  return shape.map((l) => round1(l + offset));
}

/** What a source's spectrum is called in a summary or an export. */
export const spectrumName = (id: string, mine: Spectrum[] = []) => (id === 'custom' ? 'Custom' : spectrumById(id, mine)?.name.replace(/ \(.*\)$/, '') ?? 'Custom');

/** Sound power from the level 1 m in front on axis, and back, through the pattern's on-axis directivity index in each band. */
export const lwFromLp1m = (lp1m: readonly number[], di: readonly number[]) => lp1m.map((l, b) => round1(l + LP_1M_TO_LW_DB - di[b]));
export const lp1mFromLw = (lw: readonly number[], di: readonly number[]) => lw.map((l, b) => round1(l - LP_1M_TO_LW_DB + di[b]));

/** The level 1 m in front on axis that a loudspeaker's sensitivity (dB SPL at 1 W, 1 m) gives with this electrical power, spread over the bands by the spectrum. */
export const lp1mFromSensitivity = (shape: string, sensitivityDb: number, watts: number, mine: Spectrum[] = []) =>
  spectrumBands(shape, sensitivityDb + 10 * Math.log10(watts), mine);

/** A source's sound power in each band from however its level was entered; null when that needs the pattern's directivity index and it is not known yet. */
export function soundPowerOf(p: { shape: string; bandsDb: number[]; level?: LevelEntry }, di: readonly number[] | null, mine: Spectrum[] = []): number[] | null {
  const level = p.level;
  if (!level || level.mode === 'lw') return p.bandsDb;
  if (!di) return null;
  if (level.mode === 'lp1m') return lwFromLp1m(level.lp1mBandsDb ?? lp1mFromLw(p.bandsDb, di), di);
  return lwFromLp1m(lp1mFromSensitivity(p.shape, level.sensitivityDb ?? 90, level.watts ?? 1, mine), di);
}

const STORE = 'roomacoustics3d.spectra';

/** The spectra the user saved, kept in this browser (a project file carries the ones it uses too). */
export function loadMySpectra(): Spectrum[] {
  try {
    const list = JSON.parse(localStorage.getItem(STORE) ?? '[]');
    return Array.isArray(list) ? list.filter((s) => typeof s?.id === 'string' && Array.isArray(s.shape) && s.shape.length === 8) : [];
  } catch { return []; }
}

export function storeMySpectra(list: Spectrum[]) {
  try { localStorage.setItem(STORE, JSON.stringify(list)); } catch { /* storage off: they last until the page is closed */ }
}

/** A spectrum the user saves from a source's bands: its levels as they are, so picking it again gives them back. */
export function mySpectrum(name: string, bandsDb: number[], quantity: 'lw' | 'lp1m'): Spectrum {
  return {
    id: `mine-${Date.now().toString(36)}`, group: 'My spectra', name, shape: [...bandsDb], standard: { quantity, overallDb: round1(totalDb(bandsDb)) },
    note: `Saved by you, as ${quantity === 'lw' ? 'a sound power' : 'a level 1 m in front on axis'}. Picking it sets those levels.`,
  };
}
