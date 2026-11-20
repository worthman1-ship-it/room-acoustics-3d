import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import type { Material, Surface } from '../types';
import { BANDS_HZ, bandLabel } from '../types';
import { absorptionColour } from '../frame';
import { deleteCustom, loadCustoms, loadFavourites, loadRecents, pushRecent, saveCustom, tidyCategory, toggleFavourite, type CustomMaterial } from '../materialLibrary';

/** One line of the browser: a material from the engine's list, or one of the user's own. */
type Row = { key: string; name: string; category: string; alpha: (number | null)[]; id?: number; custom?: boolean };

type Tab = 'all' | 'favourites' | 'recent' | 'mine' | 'new';
const TABS: { key: Tab; label: string }[] = [
  { key: 'all', label: 'All' }, { key: 'favourites', label: 'Favourites' }, { key: 'recent', label: 'Recent' }, { key: 'mine', label: 'My materials' }, { key: 'new', label: 'New material' },
];
const MINE = 'My materials';
const HEADINGS = BANDS_HZ.map(bandLabel);
type Sort = { col: number; dir: 1 | -1 } | null; // col -1 is the name, 0 to 7 a band

const fromCustom = (c: CustomMaterial): Row => ({ key: `c:${c.name}`, name: c.name, category: MINE, alpha: c.alpha, custom: true });

/** The absorption of a material across the bands as a line, with the material in use shown dashed for comparison. */
function Curve({ row, current }: { row: Row | null; current: Row | null }) {
  const w = 320, h = 120, left = 26, bottom = 18, top = 6, right = 8;
  const top1 = Math.max(1, ...[row, current].flatMap((r) => (r ? r.alpha.map((a) => a ?? 0) : [])));
  const x = (i: number) => left + (i * (w - left - right)) / 7;
  const y = (a: number) => top + (1 - a / top1) * (h - top - bottom);
  const path = (r: Row) => r.alpha.map((a, i) => (a === null ? null : `${x(i)},${y(a)}`)).filter(Boolean).join(' ');
  return (
    <svg viewBox={`0 0 ${w} ${h}`} role="img" aria-label="Absorption curve" className="h-full w-full">
      {[0, 0.5, 1].map((t) => (
        <g key={t}>
          <line x1={left} x2={w - right} y1={y(t * top1)} y2={y(t * top1)} stroke="#232329" />
          <text x={left - 4} y={y(t * top1) + 3} textAnchor="end" fontSize={9} fill="#64748b">{(t * top1).toFixed(1)}</text>
        </g>
      ))}
      {HEADINGS.map((hz, i) => <text key={hz} x={x(i)} y={h - 5} textAnchor="middle" fontSize={9} fill="#64748b">{hz}</text>)}
      {current && current.key !== row?.key && <polyline points={path(current)} fill="none" stroke="#94a3b8" strokeWidth={1.5} strokeDasharray="4 3" />}
      {row && <polyline points={path(row)} fill="none" stroke="#a78bfa" strokeWidth={2} />}
      {row && row.alpha.map((a, i) => (a === null ? null : <circle key={i} cx={x(i)} cy={y(a)} r={2.5} fill="#a78bfa" />))}
    </svg>
  );
}

/**
 * The material of one surface: a button showing the current choice, which opens a browser over the whole window with every material in sections,
 * a search, a sortable table of absorption by band, a curve to compare with the current material, favourites, recently used ones, and a form for
 * materials of the user's own. `onChange` is given the parts of the surface that the choice sets.
 */
export default function MaterialPicker({ materials, value, label, onChange }: {
  materials: Material[]; value: Pick<Surface, 'materialId' | 'customAlpha' | 'customName'>; label: string; onChange: (patch: Partial<Surface>) => void;
}) {
  const [open, setOpen] = useState(false);
  const [tab, setTab] = useState<Tab>('all');
  const [query, setQuery] = useState('');
  const [category, setCategory] = useState('');
  const [sort, setSort] = useState<Sort>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set()); // sections folded shut
  const [favourites, setFavourites] = useState<string[]>([]);
  const [recents, setRecents] = useState<string[]>([]);
  const [customs, setCustoms] = useState<CustomMaterial[]>([]);
  const [draft, setDraft] = useState<{ name: string; alpha: string[] }>({ name: '', alpha: Array(8).fill('') });
  const panel = useRef<HTMLDivElement>(null);

  const catalogue = useMemo<Row[]>(() => materials.map((m) => ({ key: `m:${m.id}`, name: m.name, category: m.category, alpha: m.alpha, id: m.id })), [materials]);
  const own = useMemo(() => customs.map(fromCustom), [customs]);
  const current: Row | null = value.customAlpha
    ? { key: `c:${value.customName ?? 'Custom'}`, name: value.customName ?? 'Custom', category: MINE, alpha: value.customAlpha, custom: true }
    : catalogue.find((r) => r.id === value.materialId) ?? null;

  const show = () => {
    setFavourites(loadFavourites()); setRecents(loadRecents()); setCustoms(loadCustoms());
    setSelected(current?.key ?? null); setQuery(''); setCategory(''); setSort(null); setTab('all'); setCollapsed(new Set());
    setOpen(true);
  };

  // the browser covers the window it is in: Escape closes it, and its search box has the focus
  useEffect(() => {
    if (!open) return;
    const doc = panel.current?.ownerDocument ?? document; // it may be in the surfaces window
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    doc.addEventListener('keydown', key);
    return () => doc.removeEventListener('keydown', key);
  }, [open]);

  const categories = useMemo(() => {
    const counts = new Map<string, number>();
    for (const r of [...own, ...catalogue]) counts.set(r.category, (counts.get(r.category) ?? 0) + 1);
    return [...counts];
  }, [own, catalogue]);

  const rows = useMemo(() => {
    const all = [...own, ...catalogue];
    const byName = (names: string[]) => names.flatMap((n) => { const r = all.find((x) => x.name === n); return r ? [r] : []; });
    let list = tab === 'favourites' ? all.filter((r) => favourites.includes(r.name)) : tab === 'recent' ? byName(recents) : tab === 'mine' ? own : all;
    const q = query.trim().toLowerCase();
    if (q) list = list.filter((r) => `${r.name} ${r.category}`.toLowerCase().includes(q));
    if (category && tab === 'all') list = list.filter((r) => r.category === category);
    if (sort && tab !== 'recent') {
      list = [...list].sort((a, b) => sort.dir * (sort.col < 0 ? a.name.localeCompare(b.name) : (a.alpha[sort.col] ?? -1) - (b.alpha[sort.col] ?? -1)));
    }
    return list;
  }, [tab, own, catalogue, favourites, recents, query, category, sort]);

  const picked = rows.find((r) => r.key === selected) ?? [...own, ...catalogue].find((r) => r.key === selected) ?? null;
  const sections = !sort && tab !== 'recent'; // sorted by a column, the list is one run; otherwise it is in sections
  const searching = query.trim() !== ''; // a search shows every match, so no section hides its results
  const shownCategories = [...new Set(rows.map((r) => r.category))];
  const allCollapsed = shownCategories.length > 0 && shownCategories.every((c) => collapsed.has(c));
  const toggleSection = (c: string) => setCollapsed((s) => { const next = new Set(s); if (!next.delete(c)) next.add(c); return next; });

  const sortBy = (col: number) => setSort((s) => (s?.col !== col ? { col, dir: col < 0 ? 1 : -1 } : s.dir === (col < 0 ? 1 : -1) ? { col, dir: (col < 0 ? -1 : 1) as 1 | -1 } : null));
  const mark = (col: number) => (sort?.col === col ? (sort.dir === 1 ? ' ▲' : ' ▼') : '');

  const choose = (row: Row) => {
    onChange(row.custom ? { customAlpha: row.alpha as number[], customName: row.name } : { materialId: row.id!, customAlpha: undefined, customName: undefined });
    setRecents(pushRecent(row.name));
    setOpen(false);
  };

  const drafted = draft.alpha.map((a) => (a.trim() === '' ? NaN : Number(a)));
  const draftError = draft.name.trim() === '' ? 'Give the material a name.'
    : drafted.some((a) => !Number.isFinite(a) || a < 0 || a > 1) ? 'Each band needs an absorption coefficient between 0 and 1.' : null;
  const saveDraft = () => {
    const material = { name: draft.name.trim(), alpha: drafted };
    setCustoms(saveCustom(material));
    choose(fromCustom(material));
    setDraft({ name: '', alpha: Array(8).fill('') });
  };

  const star = (row: Row) => (
    <button type="button" aria-pressed={favourites.includes(row.name)} aria-label={`Favourite ${row.name}`} title="Keep in favourites"
            onClick={(e) => { e.stopPropagation(); setFavourites(toggleFavourite(row.name)); }}
            className={`w-5 shrink-0 text-center ${favourites.includes(row.name) ? 'text-amber-300' : 'text-slate-600 hover:text-slate-300'}`}>{favourites.includes(row.name) ? '★' : '☆'}</button>
  );

  return (
    <>
      <button type="button" onClick={show} aria-label={`Choose the material of ${label}`} className="field flex items-center justify-between gap-2 text-left">
        <span className="truncate">{current ? (current.custom ? `${current.name} (custom)` : current.name) : 'Loading…'}</span>
        <span className="shrink-0 text-xs text-slate-400">Browse ▾</span>
      </button>
      {open && (
        <div ref={panel} role="dialog" aria-label="Materials" className="fixed inset-0 z-50 flex flex-col bg-ink-900 text-slate-200">
          <div className="flex items-center justify-between gap-2 border-b border-white/10 px-3 py-2">
            <div className="min-w-0">
              <div className="truncate text-sm font-semibold text-slate-100">Material for {label}</div>
              <div className="truncate text-[11px] text-slate-500">Now: {current ? current.name : 'none'}</div>
            </div>
            <button type="button" onClick={() => setOpen(false)} className="chip shrink-0">Close</button>
          </div>

          <div role="tablist" aria-label="Which materials" className="flex flex-wrap gap-1.5 px-3 pt-2">
            {TABS.map((t) => (
              <button key={t.key} type="button" role="tab" aria-selected={tab === t.key} onClick={() => setTab(t.key)} className={`chip ${tab === t.key ? 'chip-on' : ''}`}>{t.label}</button>
            ))}
          </div>

          {tab === 'new' ? (
            <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-3">
              <p className="text-[11px] leading-snug text-slate-500">Type the absorption coefficient (0 to 1) in each octave band. The material is kept in this browser, under My materials, for any surface.</p>
              <label className="block"><span className="label">Name</span>
                <input aria-label="Material name" className="field mt-1" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} placeholder="e.g. Felt wall panel" />
              </label>
              <div className="grid grid-cols-4 gap-2">
                {HEADINGS.map((hz, i) => (
                  <label key={hz} className="block"><span className="label">{hz} Hz</span>
                    <input aria-label={`Absorption at ${hz} Hz`} inputMode="decimal" className="field mt-1 !px-2" value={draft.alpha[i]}
                           onChange={(e) => setDraft({ ...draft, alpha: draft.alpha.map((a, j) => (j === i ? e.target.value : a)) })} />
                  </label>
                ))}
              </div>
              <div className="flex flex-wrap items-center gap-2">
                <button type="button" className="chip" disabled={!picked}
                        onClick={() => picked && setDraft({ name: picked.custom ? picked.name : `${picked.name} (edited)`, alpha: picked.alpha.map((a) => String(a ?? 0)) })}>
                  Start from {picked ? picked.name.slice(0, 28) : 'the selected material'}
                </button>
                <button type="button" onClick={saveDraft} disabled={draftError !== null} className="btn !py-1.5">Save and use</button>
              </div>
              {draft.name !== '' || draft.alpha.some((a) => a !== '') ? <p className="text-[11px] text-amber-300/90">{draftError}</p> : null}
            </div>
          ) : (
            <>
              <div className="flex gap-2 px-3 py-2">
                <input autoFocus aria-label="Search materials" value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search materials…" className="field min-w-0 flex-1" />
                {tab === 'all' && (
                  <select aria-label="Category" value={category} onChange={(e) => setCategory(e.target.value)} className="field !w-44 shrink-0">
                    <option value="">All categories ({own.length + catalogue.length})</option>
                    {categories.map(([c, n]) => <option key={c} value={c}>{tidyCategory(c)} ({n})</option>)}
                  </select>
                )}
                {sections && !searching && (
                  <button type="button" className="chip shrink-0" onClick={() => setCollapsed(allCollapsed ? new Set() : new Set(shownCategories))}>{allCollapsed ? 'Expand all' : 'Collapse all'}</button>
                )}
              </div>

              <div className="min-h-0 flex-1 overflow-auto border-y border-white/10">
                <table className="w-full border-collapse text-[11px]">
                  <thead className="sticky top-0 z-10 bg-ink-800 text-slate-400">
                    <tr>
                      <th className="w-5" />
                      <th className="px-1 py-1 text-left font-medium"><button type="button" onClick={() => sortBy(-1)}>Material{mark(-1)}</button></th>
                      {HEADINGS.map((hz, i) => <th key={hz} className="w-9 px-0.5 py-1 text-right font-medium"><button type="button" title={`Sort by absorption at ${hz} Hz`} onClick={() => sortBy(i)}>{hz}{mark(i)}</button></th>)}
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((r, i) => (
                      <RowGroup key={r.key} head={sections && (i === 0 || rows[i - 1].category !== r.category) ? r.category : null}
                                count={sections && i === 0 || sections && rows[i - 1].category !== r.category ? rows.filter((x) => x.category === r.category).length : 0}
                                folded={sections && !searching && collapsed.has(r.category)} onToggle={() => toggleSection(r.category)}>
                        <tr aria-selected={selected === r.key} onClick={() => setSelected(r.key)} onDoubleClick={() => choose(r)}
                            className={`cursor-pointer border-t border-white/5 ${selected === r.key ? 'bg-violet-400/15' : 'hover:bg-white/5'} ${current?.key === r.key ? 'text-accent' : ''}`}>
                          <td className="pl-1">{star(r)}</td>
                          <td className="max-w-0 px-1 py-1"><span className="block truncate" title={r.name}>{r.name}</span></td>
                          {r.alpha.map((a, j) => (
                            <td key={j} className="px-0.5 py-1 text-right tabular-nums">
                              <span className="inline-flex items-center gap-0.5"><span className="h-2 w-1 rounded-sm" style={{ background: absorptionColour(a) }} />{a === null ? '–' : a.toFixed(2).replace(/^0\./, '.')}</span>
                            </td>
                          ))}
                        </tr>
                      </RowGroup>
                    ))}
                  </tbody>
                </table>
                {rows.length === 0 && (
                  <p className="px-3 py-4 text-xs text-slate-500">
                    {tab === 'favourites' ? 'No favourites yet: press ☆ on a material to keep it here.'
                      : tab === 'recent' ? 'Materials you choose will appear here.'
                      : tab === 'mine' ? 'You have not made a material yet: use New material.' : 'No material matches.'}
                  </p>
                )}
              </div>
            </>
          )}

          <div className="space-y-1.5 border-t border-white/10 bg-ink-800/60 px-3 py-2">
            {picked ? (
              <>
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <div className="truncate text-xs font-medium text-slate-100" title={picked.name}>{picked.name}</div>
                    <div className="truncate text-[10px] text-slate-500">{tidyCategory(picked.category)} · solid line this material, dashed the one in use</div>
                  </div>
                  <div className="flex shrink-0 gap-1.5">
                    {picked.custom && <button type="button" className="chip" onClick={() => { setCustoms(deleteCustom(picked.name)); setSelected(null); }}>Delete</button>}
                    <button type="button" className="btn !px-3 !py-1.5" onClick={() => choose(picked)}>Use this material</button>
                  </div>
                </div>
                <div className="h-28"><Curve row={picked} current={current} /></div>
              </>
            ) : <p className="py-2 text-xs text-slate-500">Select a material to see its absorption curve; double-click one to use it.</p>}
          </div>
        </div>
      )}
    </>
  );
}

/** A table row, with a heading row of the section it starts in front of it; the heading folds the section shut, which hides its rows. */
function RowGroup({ head, count, folded, onToggle, children }: { head: string | null; count: number; folded: boolean; onToggle: () => void; children: ReactNode }) {
  return (
    <>
      {head && (
        <tr className="bg-ink-800">
          <th colSpan={10} className="sticky top-6 text-left text-[10px] font-semibold uppercase tracking-wide text-slate-400">
            <button type="button" aria-expanded={!folded} onClick={onToggle} className="flex w-full items-center gap-1.5 px-2 py-1 text-left uppercase hover:text-slate-200">
              <span aria-hidden className="w-2 text-slate-500">{folded ? '▸' : '▾'}</span>
              <span>{tidyCategory(head)}</span> <span className="font-normal text-slate-600">· {count}</span>
            </button>
          </th>
        </tr>
      )}
      {!folded && children}
    </>
  );
}
