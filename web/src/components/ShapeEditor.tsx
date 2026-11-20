import { useEffect, useState } from 'react';
import { Line } from '@react-three/drei';
import type { GeometryEdit } from '../api';
import type { Point } from '../types';
import { toThree, type Frame } from '../frame';

/**
 * What has been picked for editing a model's shape: the corner nearest the last click, the coordinates typed for it (where it is to go, or a corner to
 * add to the face being drawn), the face it was on (and its group), and the corners of the face being drawn.
 */
export type ShapeState = { corner: Point | null; at: Point | null; face: { group: string; corners: [Point, Point, Point] } | null; drawing: Point[] };

export const NO_SHAPE: ShapeState = { corner: null, at: null, face: null, drawing: [] };

const same = (a: Point, b: Point) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z) < 1e-9;

/** Markers in the 3-D view: the picked corner in yellow, the face it was on outlined in yellow, and the face being drawn in green. */
export function ShapeMarks({ shape, frame }: { shape: ShapeState; frame: Frame }) {
  const dot = (p: Point, colour: string, key: string) => (
    <mesh key={key} position={toThree(p, frame)} renderOrder={12}><sphereGeometry args={[0.09, 16, 16]} /><meshBasicMaterial color={colour} depthTest={false} /></mesh>
  );
  return (
    <group>
      {shape.face && <Line points={[...shape.face.corners, shape.face.corners[0]].map((p) => toThree(p, frame))} color="#facc15" lineWidth={2} depthTest={false} renderOrder={11} />}
      {shape.drawing.length > 1 && (
        <Line points={[...shape.drawing, ...(shape.drawing.length > 2 ? [shape.drawing[0]] : [])].map((p) => toThree(p, frame))} color="#4ade80" lineWidth={3} depthTest={false} renderOrder={11} />
      )}
      {shape.drawing.map((p, i) => dot(p, '#4ade80', `d${i}`))}
      {shape.corner && dot(shape.corner, '#facc15', 'corner')}
    </group>
  );
}

/**
 * The panel for editing a model's shape: the picked corner's coordinates (type new ones and move it there, or add them to the face being drawn), the
 * picked face's surface (delete it), and the face being drawn (its corners so far, the group it goes in, and drawing it).
 */
export function ShapePanel({ shape, onShape, groups, onEdit, busy, onClose }: {
  shape: ShapeState; onShape: (s: ShapeState) => void; groups: string[]; onEdit: (edit: GeometryEdit) => Promise<boolean>; busy: boolean; onClose: () => void;
}) {
  const at = shape.at;
  const setAt = (p: Point) => onShape({ ...shape, at: p });
  const [group, setGroup] = useState(groups[0] ?? '');
  const [newGroup, setNewGroup] = useState('');
  useEffect(() => { if (group !== '' && !groups.includes(group)) setGroup(groups[0] ?? ''); }, [groups, group]);
  const into = group === '' ? newGroup.trim() : group;
  const field = (axis: 'x' | 'y' | 'z') => (
    <label className="flex items-center gap-1">{axis}
      <input type="number" step={0.01} aria-label={`Corner ${axis}`} className="field !w-[4.5rem] !px-1.5 !py-1 text-xs" disabled={!at}
             value={at ? Number(at[axis].toFixed(3)) : ''}
             onChange={(e) => { const v = e.target.valueAsNumber; if (at && Number.isFinite(v)) setAt({ ...at, [axis]: v }); }} />
    </label>
  );
  return (
    <div role="region" aria-label="Edit shape" className="absolute left-3 top-14 w-72 space-y-2 rounded-lg border border-white/10 bg-ink-900/90 p-3 text-[11px] text-slate-300 shadow-xl backdrop-blur">
      <div className="flex items-center justify-between">
        <span className="label">Edit shape</span>
        <button type="button" onClick={onClose} title="Stop editing the shape" className="-mr-1 rounded px-1.5 text-slate-400 hover:bg-white/10 hover:text-slate-100">✕</button>
      </div>
      <p className="text-slate-400">Click the model: the corner nearest the click is picked, and the face under it.</p>

      <div className="space-y-1.5">
        <div className="flex gap-2">{field('x')}{field('y')}{field('z')}<span className="self-center text-slate-500">m</span></div>
        <div className="flex flex-wrap gap-1.5">
          <button type="button" className="chip" disabled={busy || !shape.corner || !at || same(at, shape.corner)}
                  onClick={async () => { if (shape.corner && at && await onEdit({ action: 'move', points: [shape.corner, at] })) onShape({ ...shape, corner: at, face: null }); }}>Move corner here</button>
          <button type="button" className="chip" disabled={!at} onClick={() => at && onShape({ ...shape, drawing: [...shape.drawing, at] })}>Add to new face</button>
        </div>
      </div>

      {shape.face && (
        <div className="flex items-center justify-between gap-2 border-t border-white/10 pt-2">
          <span className="truncate">Face of {shape.face.group}</span>
          <button type="button" className="chip" disabled={busy}
                  onClick={async (e) => {
                    const face = shape.face!;
                    if ((e.currentTarget.ownerDocument.defaultView ?? window).confirm(`Delete the flat surface this face of ${face.group} is part of? Reload the file to have it back.`)
                        && await onEdit({ action: 'delete', points: face.corners })) onShape({ ...shape, face: null });
                  }}>Delete its surface</button>
        </div>
      )}

      <div className="space-y-1.5 border-t border-white/10 pt-2">
        <div className="flex items-center justify-between">
          <span>New face: {shape.drawing.length} {shape.drawing.length === 1 ? 'corner' : 'corners'}</span>
          <span className="flex gap-1.5">
            <button type="button" className="chip" disabled={shape.drawing.length === 0} onClick={() => onShape({ ...shape, drawing: shape.drawing.slice(0, -1) })}>Remove last</button>
            <button type="button" className="chip" disabled={shape.drawing.length === 0} onClick={() => onShape({ ...shape, drawing: [] })}>Clear</button>
          </span>
        </div>
        <div className="flex items-center gap-1.5">
          <select aria-label="Group of the new face" className="field !w-auto min-w-0 flex-1 !px-1.5 !py-1 text-xs" value={group} onChange={(e) => setGroup(e.target.value)}>
            {groups.map((g) => <option key={g} value={g}>{g}</option>)}
            <option value="">New group…</option>
          </select>
          {group === '' && <input aria-label="Name of the new group" placeholder="Name" className="field !w-28 !px-1.5 !py-1 text-xs" value={newGroup} onChange={(e) => setNewGroup(e.target.value)} />}
        </div>
        <button type="button" className="chip" disabled={busy || shape.drawing.length < 3 || into === ''}
                onClick={async () => { if (await onEdit({ action: 'face', points: shape.drawing, group: into })) onShape({ ...shape, drawing: [] }); }}>Add face</button>
        <p className="text-slate-500">Pick its corners in order round it (or type them in), all in one plane.</p>
      </div>
    </div>
  );
}
