/** The user's own material shortcuts, kept in this browser: favourites, recently used, and materials they typed in themselves. */

/** A material the user entered: a name and the absorption coefficient in each octave band, 63 Hz to 8 kHz. */
export type CustomMaterial = { name: string; alpha: number[] };

const FAVOURITES = 'ra3d.materials.favourites';
const RECENTS = 'ra3d.materials.recents';
const CUSTOMS = 'ra3d.materials.custom';
const MAX_RECENTS = 12;

// storage can be missing or refuse (a private window, blocked site data), so every use is guarded and the picker works without it
function read<T>(key: string, fallback: T): T {
  try {
    const text = window.localStorage.getItem(key);
    return text ? (JSON.parse(text) as T) : fallback;
  } catch { return fallback; }
}
function write(key: string, value: unknown) {
  try { window.localStorage.setItem(key, JSON.stringify(value)); } catch { /* not kept; the picker still works for this visit */ }
}

const names = (key: string): string[] => { const v = read<unknown>(key, []); return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : []; };

export const loadFavourites = (): string[] => names(FAVOURITES);
/** Adds the name to the favourites, or removes it if it is there; returns the new list. */
export function toggleFavourite(name: string): string[] {
  const now = loadFavourites();
  const next = now.includes(name) ? now.filter((n) => n !== name) : [...now, name];
  write(FAVOURITES, next);
  return next;
}

export const loadRecents = (): string[] => names(RECENTS);
/** Puts the name first in the recently used list (the last few are kept); returns the new list. */
export function pushRecent(name: string): string[] {
  const next = [name, ...loadRecents().filter((n) => n !== name)].slice(0, MAX_RECENTS);
  write(RECENTS, next);
  return next;
}

const valid = (m: unknown): m is CustomMaterial =>
  !!m && typeof (m as CustomMaterial).name === 'string' && Array.isArray((m as CustomMaterial).alpha) && (m as CustomMaterial).alpha.length === 8
  && (m as CustomMaterial).alpha.every((a) => typeof a === 'number' && Number.isFinite(a) && a >= 0 && a <= 1);

export function loadCustoms(): CustomMaterial[] {
  const v = read<unknown>(CUSTOMS, []);
  return Array.isArray(v) ? v.filter(valid) : [];
}
/** Saves a material under its name, replacing one of the same name; returns the new list. */
export function saveCustom(material: CustomMaterial): CustomMaterial[] {
  const next = [...loadCustoms().filter((c) => c.name !== material.name), material];
  write(CUSTOMS, next);
  return next;
}
export function deleteCustom(name: string): CustomMaterial[] {
  const next = loadCustoms().filter((c) => c.name !== name);
  write(CUSTOMS, next);
  return next;
}

/** A category as the workbook writes it, in capitals, shown in sentence case ("MASONRY WALLS" as "Masonry walls"); other spellings are left alone. */
export const tidyCategory = (category: string): string =>
  category === category.toUpperCase() ? category.charAt(0) + category.slice(1).toLowerCase() : category;
