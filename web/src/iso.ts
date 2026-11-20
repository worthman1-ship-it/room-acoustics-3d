import type { Broadband } from './types';

/** The ISO 3382 parameters, each per octave band and as its single number, and the bands that number is the mean of. */
export const ISO_ROWS: { key: keyof Broadband; label: string; unit: string; digits: number; over: string }[] = [
  { key: 't30S', label: 'T30', unit: 's', digits: 2, over: '500, 1k' }, { key: 't20S', label: 'T20', unit: 's', digits: 2, over: '500, 1k' },
  { key: 'edtS', label: 'EDT', unit: 's', digits: 2, over: '500, 1k' }, { key: 'centreTimeS', label: 'Ts (centre time)', unit: 's', digits: 3, over: '500, 1k' },
  { key: 'd50', label: 'D50 (definition)', unit: '', digits: 2, over: '500, 1k' },
  { key: 'c50Db', label: 'C50', unit: 'dB', digits: 1, over: '500, 1k' }, { key: 'c80Db', label: 'C80', unit: 'dB', digits: 1, over: '500, 1k' },
  { key: 'strengthGDb', label: 'G (strength)', unit: 'dB', digits: 1, over: '500, 1k' }, { key: 'earlyStrengthGDb', label: 'G early (0–80 ms)', unit: 'dB', digits: 1, over: '500, 1k' },
  { key: 'lateStrengthGDb', label: 'G late (after 80 ms)', unit: 'dB', digits: 1, over: '500, 1k' },
  { key: 'directStrengthGDb', label: 'G direct (direct sound only)', unit: 'dB', digits: 1, over: '500, 1k' },
  { key: 'directToReverberantDb', label: 'D/R (direct to reverberant)', unit: 'dB', digits: 1, over: '500, 1k' },
  { key: 'lf80', label: 'LF (lateral fraction, 5–80 ms)', unit: '', digits: 2, over: '125–1k' },
  { key: 'lfc80', label: 'LFC (lateral coefficient)', unit: '', digits: 2, over: '125–1k' },
  { key: 'lateLateralLevelDb', label: 'LJ (late lateral level)', unit: 'dB', digits: 1, over: '125–1k, energy mean' },
  { key: 'stageSupportEarlyDb', label: 'ST early (stage support)', unit: 'dB', digits: 1, over: '250–2k' },
  { key: 'stageSupportLateDb', label: 'ST late (stage support)', unit: 'dB', digits: 1, over: '250–2k' },
  { key: 'stageSupportTotalDb', label: 'ST total (stage support)', unit: 'dB', digits: 1, over: '250–2k' },
  { key: 'earlyEnsembleLevelDb', label: 'EEL (early ensemble level)', unit: 'dB', digits: 1, over: '250–2k' },
  { key: 'iaccEarly', label: 'IACC early (0–80 ms)', unit: '', digits: 2, over: '500–2k (IACC E3)' },
  { key: 'iaccLate', label: 'IACC late (80 ms–1 s)', unit: '', digits: 2, over: '500–2k (IACC L3)' },
  { key: 'iaccAll', label: 'IACC whole (0–1 s)', unit: '', digits: 2, over: '500–2k (IACC A3)' },
  { key: 'binauralQualityIndex', label: 'BQI (binaural quality index)', unit: '', digits: 2, over: '1 − IACC E3' },
  { key: 'splDb', label: 'SPL (Lp)', unit: 'dB', digits: 1, over: 'all bands, added' }, { key: 'splADb', label: 'SPL(A)', unit: 'dB(A)', digits: 1, over: 'all bands, A-weighted' },
  { key: 'echoCriterionSpeech', label: 'EK, speech (echo criterion)', unit: '', digits: 2, over: 'worst of 500–2k' },
  { key: 'echoCriterionMusic', label: 'EK, music (echo criterion)', unit: '', digits: 2, over: 'worst of 500–2k' },
  { key: 'bassRatio', label: 'BR (bass ratio)', unit: '', digits: 2, over: 'T30 (125+250)/(500+1k)' }, { key: 'brilliance', label: 'Br (brilliance ratio)', unit: '', digits: 2, over: 'T30 (2k+4k)/(500+1k)' },
];
