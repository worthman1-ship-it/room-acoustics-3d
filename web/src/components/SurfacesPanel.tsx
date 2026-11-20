import type { Material, Model, Room, Surface, SurfaceInfo } from '../types';
import type { GroupAction } from '../api';
import Reduction from './Reduction';
import { bandHzText } from '../types';
import { absorptionColour, groupColour } from '../frame';
import MaterialPicker from './MaterialPicker';
import { Note, Section, Slider } from './ui';

type Props = {
  room: Room; onRoom: (r: Room) => void; model: Model | null; groupSurfaces: Record<string, Surface>; onGroupSurface: (group: string, patch: Partial<Surface>) => void;
  materials: Material[]; surfaces: SurfaceInfo[]; selectedSurface: string | null; onSelectSurface: (key: string | null) => void; bandHz: number | null;
  /** splits a model's surface group into its flat surfaces */
  onSplit: (group: string) => void; busy: boolean;
  /** renames a model's surface group, merges it into another or deletes it */
  onEditGroup: (group: string, action: GroupAction, to?: string) => void;
};

/** Renaming, merging and deleting a model's surface group. */
function GroupEdit({ name, others, onEdit, busy }: { name: string; others: string[]; onEdit: (action: GroupAction, to?: string) => void; busy: boolean }) {
  // the panel lives in a window of its own, so its questions are asked there, not in the main window
  const at = (e: { currentTarget: Element }) => e.currentTarget.ownerDocument.defaultView ?? window;
  return (
    <div className="flex flex-wrap items-center gap-1.5 text-[11px] text-slate-400">
      <button type="button" className="chip" disabled={busy} aria-label={`Rename ${name}`}
              onClick={(e) => { const to = at(e).prompt(`A new name for ${name}`, name)?.trim(); if (to && to !== name) onEdit('rename', to); }}>Rename</button>
      {others.length > 0 && (
        <select aria-label={`Merge ${name} into`} className="field !w-auto !px-1.5 !py-0.5 text-[11px]" disabled={busy} value=""
                onChange={(e) => { const to = e.target.value; if (to && at(e).confirm(`Put ${name}'s surfaces into ${to}? They take ${to}'s finish.`)) onEdit('merge', to); }}>
          <option value="">Merge into…</option>
          {others.map((o) => <option key={o} value={o}>{o}</option>)}
        </select>
      )}
      {others.length > 0 && (
        <button type="button" className="chip" disabled={busy} aria-label={`Delete ${name}`}
                onClick={(e) => { if (at(e).confirm(`Take ${name}'s surfaces out of the model? Reload the file to have them back.`)) onEdit('delete'); }}>Delete</button>
      )}
    </div>
  );
}

/** The head of a surface's block: click it to isolate that surface in the 3-D view; the swatch is its absorption in the band being shown. */
function SurfaceHead({ info, selected, onSelect, dot, hz }: { info: SurfaceInfo | undefined; selected: boolean; onSelect: () => void; dot?: string; hz: number | null }) {
  if (!info) return null;
  return (
    <button type="button" onClick={onSelect} aria-pressed={selected} title="Isolate this surface in the 3-D view, coloured by its absorption"
            className={`flex w-full items-center justify-between gap-2 rounded-md px-1.5 py-1 text-left transition ${selected ? 'bg-violet-400/15 ring-1 ring-violet-400/50' : 'hover:bg-white/5'}`}>
      <span className="flex min-w-0 items-center gap-1.5 label">
        {dot && <span className="h-2 w-2 shrink-0 rounded-full" style={{ background: dot }} />}
        <span className="truncate capitalize">{info.label}</span>
      </span>
      <span className="flex shrink-0 items-center gap-1.5 text-[10px] tabular-nums text-slate-400">
        {info.areaM2.toFixed(1)} m²
        <span className="inline-flex items-center gap-1 rounded bg-black/30 px-1 py-0.5" title={`Absorption at ${bandHzText(hz)}`}>
          <span className="h-2.5 w-2.5 rounded-sm" style={{ background: absorptionColour(info.alpha) }} />
          {info.alpha === null ? '–' : `α ${info.alpha.toFixed(2)}`}
        </span>
      </span>
    </button>
  );
}

/** Marks a surface as an opening (sound that reaches it leaves the room) or as an audience area, whose exposed edge adds absorption. */
/** `foundEdgeM`, for a model's group, is the exposed edge found from the model: what an audience area starts with, and can go back to. */
function SurfaceKind({ surface, label, onChange, screenable = false, foundEdgeM }: { surface: Surface; label: string; onChange: (patch: Partial<Surface>) => void; screenable?: boolean; foundEdgeM?: number }) {
  const audience = surface.audienceEdgeM !== undefined;
  return (
    <div className="space-y-1 text-[11px] text-slate-400">
      {screenable && (
        <label className="flex items-center gap-1.5" title="Thin free-standing surfaces inside the room (office screens, barriers): sound reflects off both sides and bends round their free edges, and they need not be closed">
          <input type="checkbox" aria-label={`${label} is screens`} checked={!!surface.screen} onChange={(e) => onChange({ screen: e.target.checked || undefined })} className="accent-violet-400" />
          Free-standing screens
        </label>
      )}
      {screenable && surface.screen && <Reduction value={surface.transmissionLossDb} label={label} onChange={(db) => onChange({ transmissionLossDb: db })} />}
      <label className="flex items-center gap-1.5" title="A window or an opening to outside: all the sound that reaches it is lost, whatever the material">
        <input type="checkbox" aria-label={`${label} is open`} checked={!!surface.open} onChange={(e) => onChange({ open: e.target.checked || undefined })} className="accent-violet-400" />
        Open (sound passes out)
      </label>
      {!surface.open && (
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
          <label className="flex items-center gap-1.5" title="Seating: the absorption is worked out as if the area were larger by a 0.5 m strip along each edge that is open to the room">
            <input type="checkbox" aria-label={`${label} is an audience area`} checked={audience} onChange={(e) => onChange({ audienceEdgeM: e.target.checked ? foundEdgeM ?? 0 : undefined })} className="accent-violet-400" />
            Audience area
          </label>
          {audience && (
            <label className="flex items-center gap-1.5">Exposed edge (m)
              <input type="number" min={0} step={1} aria-label={`Exposed edge of ${label}`} value={surface.audienceEdgeM} className="field !w-20 !px-1.5 !py-1 text-xs"
                     onChange={(e) => { const v = e.target.valueAsNumber; onChange({ audienceEdgeM: Number.isFinite(v) ? Math.max(0, v) : 0 }); }} />
            </label>
          )}
          {audience && foundEdgeM !== undefined && (
            <span className="text-slate-500" title="The group's outline edges that are not against a wall or a face rising more than 0.3 m above them">
              {foundEdgeM.toFixed(1)} m found in the model
              {surface.audienceEdgeM !== foundEdgeM && <button type="button" className="chip ml-1.5" onClick={() => onChange({ audienceEdgeM: foundEdgeM })}>Use it</button>}
            </span>
          )}
        </div>
      )}
    </div>
  );
}

/** Each surface of the room with its finish and scattering. Lives in a window of its own, so it can sit beside the 3-D view. */
export default function SurfacesPanel({ room, onRoom, model, groupSurfaces, onGroupSurface, materials, surfaces, selectedSurface, onSelectSurface, bandHz, onSplit, onEditGroup, busy }: Props) {
  const setSurface = (key: 'floor' | 'walls' | 'ceiling', patch: Partial<Surface>) => onRoom({ ...room, [key]: { ...room[key], ...patch } });
  return (
    <Section title="Surfaces">
      {model
        ? model.groups.map((g, i) => {
            const surface = groupSurfaces[g.name];
            if (!surface) return null;
            return (
              <div key={g.name} className="space-y-1.5">
                <SurfaceHead info={surfaces.find((x) => x.key === g.name)} selected={selectedSurface === g.name} dot={groupColour(i)} hz={bandHz}
                             onSelect={() => onSelectSurface(selectedSurface === g.name ? null : g.name)} />
                {(g.surfaces ?? 1) > 1 && (
                  <div className="flex items-center gap-2 text-xs text-slate-400">
                    <span>{g.surfaces} flat surfaces, such as a floor and walls drawn on one layer.</span>
                    <button type="button" className="chip" disabled={busy} onClick={() => onSplit(g.name)} aria-label={`Split ${g.name}`}>Split</button>
                  </div>
                )}
                <GroupEdit name={g.name} others={model.groups.map((o) => o.name).filter((o) => o !== g.name)} onEdit={(action, to) => onEditGroup(g.name, action, to)} busy={busy} />
                <MaterialPicker materials={materials} value={surface} label={g.name} onChange={(patch) => onGroupSurface(g.name, patch)} />
                <SurfaceKind surface={surface} label={g.name} onChange={(patch) => onGroupSurface(g.name, patch)} screenable foundEdgeM={g.exposedEdgeM} />
                <Slider label="Scattering" value={surface.scattering} min={0} max={1} step={0.05} onChange={(v) => onGroupSurface(g.name, { scattering: v })} />
              </div>
            );
          })
        : (['floor', 'walls', 'ceiling'] as const).map((key) => (
            <div key={key} className="space-y-1.5">
              <SurfaceHead info={surfaces.find((x) => x.key === key)} selected={selectedSurface === key} hz={bandHz}
                           onSelect={() => onSelectSurface(selectedSurface === key ? null : key)} />
              <MaterialPicker materials={materials} value={room[key]} label={key} onChange={(patch) => setSurface(key, patch)} />
              <SurfaceKind surface={room[key]} label={key} onChange={(patch) => setSurface(key, patch)} />
              <Slider label="Scattering" value={room[key].scattering} min={0} max={1} step={0.05} onChange={(v) => setSurface(key, { scattering: v })} />
            </div>
          ))}
      <Note>An audience area's exposed edge is the length of its boundary that is open to the room rather than against a wall; its absorption acts as if the area were larger by a 0.5 m strip along it (the usual edge-effect rule for seating, approximate). An open surface absorbs everything, like a window to outside.</Note>
    </Section>
  );
}
