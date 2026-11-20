import { useState } from 'react';
import type { GeometryCheck as Check } from '../types';

const KIND = { hole: 'Hole', crowded: 'More than two faces', separate: 'Separate part' } as const;
const SHOWN = 30;

/**
 * The geometry check of a model that is not a closed surface: a line for each fault, which picks it out in the 3-D view. A hole that can be closed
 * (a loop of edges in a plane, or in planes that meet, such as a missing ceiling and the doorway open to it) has a Fill button, and Fill all closes
 * every such hole at once. Repair puts right what it can of the rest, as well as filling the holes.
 */
export default function GeometryCheck({ check, selected, onSelect, onFill, onRepair, busy }: {
  check: Check; selected: number | null; onSelect: (i: number | null) => void; onFill: (holes: number[]) => void; onRepair: (boxes: boolean) => void; busy: boolean;
}) {
  const [boxes, setBoxes] = useState(false);
  const fillable = check.faults.flatMap((f, i) => (f.fillable ? [i] : []));
  const counts = [
    check.openEdges && `${check.openEdges} edge${check.openEdges === 1 ? '' : 's'} with one face`,
    check.crowdedEdges && `${check.crowdedEdges} with more than two`,
    check.parts > 1 && `${check.parts} separate surfaces`,
  ].filter(Boolean).join(', ');
  return (
    <div className="mt-1.5 space-y-1" data-testid="geometry-check">
      <div className="text-amber-100">Geometry check: {counts || 'no open or crowded edges'}. The faults are drawn in red in the 3-D view; click one to pick it out in yellow.</div>
      {fillable.length > 0 && (
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-amber-200/90">{fillable.length === 1 ? 'One hole' : `${fillable.length} holes`} can be closed with surfaces of their own, to be given a finish.</span>
          <button type="button" onClick={() => onFill(fillable)} disabled={busy} className="chip">{fillable.length === 1 ? 'Fill the hole' : `Fill all ${fillable.length} holes`}</button>
        </div>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-amber-200/90">
          Or repair what can be put right: fill the holes, make furniture and loose sheets standing in the room free-standing screens, join corners
          that miss by a millimetre or two, and cut edges at T-junctions.
        </span>
        <label className="inline-flex items-center gap-1 text-amber-100" title="Replace each solid standing in the room (a chair, a table) by the box round it: much faster to calculate, and it reflects about the same">
          <input type="checkbox" checked={boxes} onChange={(e) => setBoxes(e.target.checked)} /> Furniture as boxes
        </label>
        <button type="button" onClick={() => onRepair(boxes)} disabled={busy} className="chip">Repair the model</button>
      </div>
      <ul className="max-h-40 space-y-0.5 overflow-y-auto pr-1">
        {check.faults.slice(0, SHOWN).map((f, i) => (
          <li key={i} className="flex items-start gap-1">
            <button type="button" aria-pressed={selected === i} onClick={() => onSelect(selected === i ? null : i)}
                    className={`min-w-0 flex-1 rounded px-1.5 py-0.5 text-left hover:bg-amber-500/10 ${selected === i ? 'bg-amber-500/20' : ''}`}>
              <span className="font-medium text-amber-100">{KIND[f.kind]}</span>
              <span className="text-amber-200/80"> · {f.edges.length} edge{f.edges.length === 1 ? '' : 's'}, {f.lengthM.toFixed(2)} m · {f.groups.join(', ')}</span>
              {selected === i && <span className="block text-amber-200/90">{f.hint}</span>}
            </button>
            {f.fillable && <button type="button" onClick={() => onFill([i])} disabled={busy} aria-label={`Fill hole ${i + 1}`} className="chip shrink-0">Fill</button>}
          </li>
        ))}
      </ul>
      {check.faults.length > SHOWN && <div className="text-amber-200/70">and {check.faults.length - SHOWN} more; put the first right and import the model again.</div>}
    </div>
  );
}
