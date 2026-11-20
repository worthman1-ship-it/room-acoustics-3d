import type { ScreenDto } from './types';

/**
 * The rectangles a screen is made of, as the engine builds it, each from u0 to u1 along the screen (from its first end) and z0 to z1 up from its base: the whole
 * screen, or with a door the five pieces round the opening (below and above the door's head each side, and over the door).
 */
export function screenPieces(s: ScreenDto): { u0: number; u1: number; z0: number; z1: number }[] {
  if (!s.door) return [{ u0: 0, u1: s.lengthM, z0: 0, z1: s.heightM }];
  const a = Math.max(0, s.door.offsetM), b = Math.min(s.lengthM, s.door.offsetM + s.door.widthM), h = Math.min(s.heightM, s.door.heightM);
  return [
    { u0: 0, u1: a, z0: 0, z1: h }, { u0: 0, u1: a, z0: h, z1: s.heightM }, { u0: a, u1: b, z0: h, z1: s.heightM },
    { u0: b, u1: s.lengthM, z0: h, z1: s.heightM }, { u0: b, u1: s.lengthM, z0: 0, z1: h },
  ].filter((p) => p.u1 - p.u0 > 1e-6 && p.z1 - p.z0 > 1e-6);
}
