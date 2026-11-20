import { useEffect, useMemo, useRef, useState } from 'react';
import { Canvas, useFrame, useThree, type ThreeEvent } from '@react-three/fiber';
import { Grid, Html, Line, OrbitControls, TransformControls } from '@react-three/drei';
import * as THREE from 'three';
import type { Directivity, ExtraReceiver, ExtraSource, GeometryFault, MapResult, Model, Point, Ray, Room, Screen, SurfaceInfo } from '../types';
import { bandHzText } from '../types';
import { absorptionColour, fromThree, groupColour, mapRange, rainbow, size, toThree, type Frame } from '../frame';
import { contourLevels, contourSegments } from '../contours';
import { screenPieces } from '../screens';
import type { GeometryEdit } from '../api';
import { NO_SHAPE, ShapeMarks, ShapePanel, type ShapeState } from './ShapeEditor';

const PULSE_SPEED = 40; // metres per second on screen: sound slowed down so the bounces can be followed
const BALL_SPEED = 15; // the billiard balls are slower still, so each bounce can be seen
const TRAIL_SAMPLES = 8;

/** How the rays are shown: every path at rest, a wave leaving the source, or billiard balls changing colour as they decay. */
export type RayMode = 'still' | 'wave' | 'ball';

type Props = {
  frame: Frame; model: Model | null; box: Room | null; source: Point; receiver: Point; rays: Ray[]; mode: RayMode; map: MapResult | null; directivity: Directivity | null;
  surfaces: SurfaceInfo[]; selectedSurface: string | null; onSelectSurface: (key: string | null) => void; bandHz: number | null;
  onAim: (azimuthDeg: number, elevationDeg: number) => void;
  onSource: (p: Point) => void; onReceiver: (p: Point) => void;
  /** the other sources, which one the panel is editing (0 is the first source), and how to move, aim and pick them */
  extras: ExtraSource[]; activeSource: number; onActiveSource: (id: number) => void;
  extraReceivers: ExtraReceiver[]; onExtraReceiverMove: (id: number, p: Point) => void;
  /** A reflection's route, source to receiver, drawn over the room. */
  highlight?: Point[] | null;
  onExtraMove: (id: number, p: Point) => void; onExtraAim: (id: number, azimuthDeg: number, elevationDeg: number) => void;
  /** the way the listener faces, in degrees from +x towards +y */
  facing: number; onFacing: (azimuthDeg: number) => void;
  /** start with the room's surfaces hidden (the map's view does) */
  surfacesHidden?: boolean;
  /** the free-standing screens drawn in the app (a model's own are part of it) */
  screens?: Screen[];
  /** where an imported model is not a closed surface, drawn in red through the walls, and the one picked out in yellow */
  faults?: GeometryFault[]; selectedFault?: number | null;
  /** changes an imported model's shape (a corner moved, a face drawn, a surface deleted); the view offers it when given, and `shapeBusy` while one is under way */
  onShape?: (edit: GeometryEdit) => Promise<boolean>; shapeBusy?: boolean;
  /** changes when a new model is read in, so the view is framed afresh for it (but not when the model is edited) */
  viewKey?: number;
};

/** The standard camera positions: where the camera sits as a direction from the middle of the room (Three.js axes: x is the room's x, y is up, z is the room's -y). */
const CAMERA_VIEWS = [
  { name: 'Perspective', dir: [0.45, 0.45, 0.77] },
  { name: 'Top', dir: [0, 1, 0.0001] },
  { name: 'Front', dir: [0, 0, 1] },
  { name: 'Back', dir: [0, 0, -1] },
  { name: 'Left', dir: [-1, 0, 0] },
  { name: 'Right', dir: [1, 0, 0] },
] as const;
type CameraView = { name: string; n: number }; // n changes on every press, so the same view can be asked for again after orbiting away

/** Moves the camera to a standard view, looking at the middle of the room, when one is asked for. */
function CameraRig({ view, target, reach }: { view: CameraView | null; target: [number, number, number]; reach: number }) {
  const camera = useThree((s) => s.camera);
  const controls = useThree((s) => s.controls) as unknown as { target: THREE.Vector3; update: () => void } | null;
  useEffect(() => {
    const dir = CAMERA_VIEWS.find((v) => v.name === view?.name)?.dir;
    if (!dir || !controls) return;
    const d = new THREE.Vector3(...dir).normalize().multiplyScalar(reach * 1.9);
    camera.position.set(target[0] + d.x, target[1] + d.y, target[2] + d.z);
    controls.target.set(...target);
    controls.update();
    // only a press of a button moves the camera, not a change of room size
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view?.n]);
  return null;
}

/** The floor plane and grid, under whatever the room is. */
function Floor({ frame }: { frame: Frame }) {
  const s = size(frame);
  const reach = Math.max(s.x, s.y, s.z);
  return (
    <group position={[0, frame.min.z, 0]}>
      <mesh rotation={[-Math.PI / 2, 0, 0]} position={[0, 0.001, 0]}>
        <planeGeometry args={[s.x, s.y]} />
        <meshBasicMaterial color="#141417" transparent opacity={0.9} />
      </mesh>
      {/* a metre grid with a heavier line every five metres, running well past the room and fading out */}
      <Grid position={[0, -0.002, 0]} infiniteGrid cellSize={1} sectionSize={5} cellThickness={0.6} sectionThickness={1.1}
            cellColor="#303038" sectionColor="#4a4a55" fadeDistance={reach * 5} fadeStrength={1.5} side={THREE.DoubleSide} />
    </group>
  );
}

/** Which way a directional source points: an arrow along its aim and a pyramid for its -6 dB coverage (in the source's own frame). */
/** A surface source's rectangle, centred on the source and facing where it is aimed, its width level. */
function SurfacePatch({ directivity, colour }: { directivity: Directivity; colour: string }) {
  const quaternion = useMemo(() => {
    const az = THREE.MathUtils.degToRad(directivity.aimAzimuthDeg), el = THREE.MathUtils.degToRad(directivity.aimElevationDeg);
    const facing = new THREE.Vector3(Math.cos(el) * Math.cos(az), Math.cos(el) * Math.sin(az), Math.sin(el));
    // as the engine spreads it: across is level and at right angles to the facing; for a surface facing straight up or down, the aim's azimuth turned a right angle
    const across = Math.abs(facing.z) < 0.999 ? new THREE.Vector3(0, 0, 1).cross(facing).normalize() : new THREE.Vector3(-Math.sin(az), Math.cos(az), 0);
    const up = facing.clone().cross(across).normalize();
    const three = (v: THREE.Vector3) => new THREE.Vector3(v.x, v.z, -v.y); // the room's z is Three's y
    return new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(three(across), three(up), three(facing)));
  }, [directivity.aimAzimuthDeg, directivity.aimElevationDeg]);
  const s = directivity.surface;
  if (!s) return null;
  return (
    <mesh quaternion={quaternion}>
      <planeGeometry args={[s.widthM, s.heightM]} />
      <meshBasicMaterial color={colour} transparent opacity={0.35} side={THREE.DoubleSide} depthWrite={false} />
    </mesh>
  );
}

function SourceAim({ directivity, reach, onAim, colour = '#34d399' }: { directivity: Directivity; reach: number; onAim: (azimuthDeg: number, elevationDeg: number) => void; colour?: string }) {
  const here = useRef<THREE.Group>(null);
  const dragging = useRef(false);
  const controls = useThree((s) => s.controls) as unknown as { enabled: boolean } | null;
  const geo = useMemo(() => {
    const az = THREE.MathUtils.degToRad(directivity.aimAzimuthDeg);
    const el = THREE.MathUtils.degToRad(directivity.aimElevationDeg);
    // the engine's azimuth is 0 along +x and 90 along +y; three.js has y up and engine y running towards -z
    const f = new THREE.Vector3(Math.cos(el) * Math.cos(az), Math.sin(el), -Math.cos(el) * Math.sin(az)).normalize();
    const up = Math.abs(f.y) > 0.999 ? new THREE.Vector3(0, 0, -1) : new THREE.Vector3(0, 1, 0);
    const r = new THREE.Vector3().crossVectors(f, up).normalize();
    const u = new THREE.Vector3().crossVectors(r, f).normalize();
    const len = Math.max(1.5, reach * 0.3);
    const half = (deg: number) => Math.tan(THREE.MathUtils.degToRad(Math.min(deg, 160) / 2));
    const hw = half(directivity.horizontalDeg) * len, hh = half(directivity.verticalDeg) * len;
    const centre = f.clone().multiplyScalar(len);
    const corner = (sx: number, sy: number) => centre.clone().addScaledVector(r, sx * hw).addScaledVector(u, sy * hh);
    const c = [corner(1, 1), corner(-1, 1), corner(-1, -1), corner(1, -1)];
    const quaternion = new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, 1, 0), f);
    return { f, len, c, quaternion };
  }, [directivity, reach]);
  const o = new THREE.Vector3();
  const tip = geo.f.clone().multiplyScalar(geo.len * 1.1);

  // dragging the dot at the arrow's tip swings the aim about the source: the pointer's ray is met with a sphere round the source, and the direction to that point is the aim
  const down = (e: ThreeEvent<PointerEvent>) => {
    e.stopPropagation();
    (e.target as unknown as Element).setPointerCapture?.(e.pointerId);
    dragging.current = true;
    if (controls) controls.enabled = false;
  };
  const move = (e: ThreeEvent<PointerEvent>) => {
    if (!dragging.current || !here.current) return;
    e.stopPropagation();
    const centre = here.current.getWorldPosition(new THREE.Vector3());
    const radius = geo.len * 1.1;
    let p = e.ray.intersectSphere(new THREE.Sphere(centre, radius), new THREE.Vector3());
    if (!p) { const near = e.ray.closestPointToPoint(centre, new THREE.Vector3()); p = centre.clone().add(near.sub(centre).setLength(radius)); }
    const d = p.sub(centre).normalize();
    const round5 = (v: number) => Math.round(v / 5) * 5;
    onAim(round5(THREE.MathUtils.radToDeg(Math.atan2(-d.z, d.x))), round5(THREE.MathUtils.radToDeg(Math.asin(THREE.MathUtils.clamp(d.y, -1, 1)))));
  };
  const up = (e: ThreeEvent<PointerEvent>) => {
    if (!dragging.current) return;
    e.stopPropagation();
    dragging.current = false;
    (e.target as unknown as Element).releasePointerCapture?.(e.pointerId);
    if (controls) controls.enabled = true;
  };
  return (
    <group ref={here}>
      <mesh position={tip} onPointerDown={down} onPointerMove={move} onPointerUp={up}>
        <sphereGeometry args={[0.4, 16, 16]} />
        <meshBasicMaterial color="#6ee7b7" transparent opacity={0.45} depthWrite={false} />
      </mesh>
      <Line points={[o, tip]} color="#6ee7b7" lineWidth={2.5} />
      <mesh position={tip} quaternion={geo.quaternion}><coneGeometry args={[0.16, 0.45, 16]} /><meshBasicMaterial color="#6ee7b7" /></mesh>
      {geo.c.map((p, i) => <Line key={i} points={[o, p]} color={colour} transparent opacity={0.35} lineWidth={1} />)}
      <Line points={[...geo.c, geo.c[0]]} color={colour} transparent opacity={0.5} lineWidth={1} />
    </group>
  );
}

/** Which way the listener's head points: a short arrow flat in the horizontal plane from the receiver, with a dot at its tip to drag round. */
function ListenerFacing({ facing, onFacing }: { facing: number; onFacing: (azimuthDeg: number) => void }) {
  const here = useRef<THREE.Group>(null);
  const dragging = useRef(false);
  const controls = useThree((s) => s.controls) as unknown as { enabled: boolean } | null;
  const LENGTH = 1.1;
  const a = THREE.MathUtils.degToRad(facing);
  const tip = new THREE.Vector3(Math.cos(a) * LENGTH, 0, -Math.sin(a) * LENGTH);
  const quaternion = new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, 1, 0), tip.clone().normalize());
  const down = (e: ThreeEvent<PointerEvent>) => {
    e.stopPropagation();
    (e.target as unknown as Element).setPointerCapture?.(e.pointerId);
    dragging.current = true;
    if (controls) controls.enabled = false;
  };
  const move = (e: ThreeEvent<PointerEvent>) => {
    if (!dragging.current || !here.current) return;
    e.stopPropagation();
    const centre = here.current.getWorldPosition(new THREE.Vector3());
    const p = e.ray.intersectPlane(new THREE.Plane(new THREE.Vector3(0, 1, 0), -centre.y), new THREE.Vector3());
    if (!p) return;
    const deg = THREE.MathUtils.radToDeg(Math.atan2(-(p.z - centre.z), p.x - centre.x));
    onFacing(((Math.round(deg / 5) * 5) % 360 + 360) % 360);
  };
  const up = (e: ThreeEvent<PointerEvent>) => {
    if (!dragging.current) return;
    e.stopPropagation();
    dragging.current = false;
    (e.target as unknown as Element).releasePointerCapture?.(e.pointerId);
    if (controls) controls.enabled = true;
  };
  return (
    <group ref={here}>
      <Line points={[new THREE.Vector3(), tip]} color="#67e8f9" lineWidth={2.5} />
      <mesh position={tip} quaternion={quaternion}><coneGeometry args={[0.1, 0.3, 12]} /><meshBasicMaterial color="#67e8f9" /></mesh>
      <mesh position={tip.clone().multiplyScalar(1.15)} onPointerDown={down} onPointerMove={move} onPointerUp={up}>
        <sphereGeometry args={[0.3, 16, 16]} />
        <meshBasicMaterial color="#67e8f9" transparent opacity={0.4} depthWrite={false} />
      </mesh>
    </group>
  );
}

/** What the 3-D view needs to show one surface picked out from the rest. */
type Pick = { infos: SurfaceInfo[]; selected: string | null; onSelect: (key: string | null) => void; bandHz: number | null };

/** The callout that hangs on the selected surface: its name, finish, area and absorption in the band being shown, with a button to put the other surfaces back. */
function SurfaceCallout({ info, bandHz, position, onClose }: { info: SurfaceInfo; bandHz: number | null; position: [number, number, number]; onClose: () => void }) {
  return (
    <Html center position={position} zIndexRange={[20, 0]} style={{ pointerEvents: 'none' }}>
      <div className="pointer-events-auto w-52 rounded-lg border border-white/10 bg-ink-900/90 p-2.5 text-[11px] text-slate-300 shadow-xl backdrop-blur">
        <div className="flex items-start justify-between gap-2">
          <span className="text-xs font-semibold capitalize text-slate-100">{info.label}</span>
          <button type="button" onClick={onClose} title="Show all surfaces again" className="-mr-1 -mt-1 rounded px-1.5 text-slate-400 hover:bg-white/10 hover:text-slate-100">✕</button>
        </div>
        <div className="mt-0.5 leading-snug text-slate-400">{info.materialName}</div>
        <div className="mt-2 flex items-center gap-2">
          <span className="h-4 w-4 shrink-0 rounded" style={{ background: absorptionColour(info.alpha) }} />
          <span className="tabular-nums">{info.alpha === null ? `No figure at ${bandHzText(bandHz)}` : <>α <b className="text-slate-100">{info.alpha.toFixed(2)}</b> at {bandHzText(bandHz)}</>}</span>
        </div>
        <div className="mt-1 tabular-nums text-slate-400">{info.areaM2.toFixed(1)} m²{info.alpha !== null && <> · {(info.alpha * info.areaM2).toFixed(1)} m² sabins</>}</div>
      </div>
    </Html>
  );
}

/** A box room as separate faces (floor, ceiling and four walls, the walls counting as one surface), so one can be picked, isolated and coloured by its absorption. */
function BoxShell({ frame, pick }: { frame: Frame; pick: Pick }) {
  const s = size(frame);
  const L = s.x, W = s.y, H = s.z;
  const box = useMemo(() => new THREE.BoxGeometry(L, H, W), [L, W, H]);
  // each face is a plane whose normal points out of the room, so seen from inside it is the back that faces the camera: only the far faces are hit by a click, never a near wall in front of the source
  const faces = useMemo(() => [
    { key: 'floor', at: [0, -H / 2, 0], turn: [Math.PI / 2, 0, 0], dims: [L, W] },
    { key: 'ceiling', at: [0, H / 2, 0], turn: [-Math.PI / 2, 0, 0], dims: [L, W] },
    { key: 'walls', at: [L / 2, 0, 0], turn: [0, Math.PI / 2, 0], dims: [W, H] },
    { key: 'walls', at: [-L / 2, 0, 0], turn: [0, -Math.PI / 2, 0], dims: [W, H] },
    { key: 'walls', at: [0, 0, W / 2], turn: [0, 0, 0], dims: [L, H] },
    { key: 'walls', at: [0, 0, -W / 2], turn: [0, Math.PI, 0], dims: [L, H] },
  ] as { key: string; at: [number, number, number]; turn: [number, number, number]; dims: [number, number] }[], [L, W, H]);
  const info = pick.infos.find((i) => i.key === pick.selected);
  const anchor: Record<string, [number, number, number]> = { floor: [0, -H / 2 + 0.02, 0], ceiling: [0, H / 2 - 0.02, 0], walls: [0, 0, -W / 2 + 0.02] };
  return (
    <group position={[0, frame.min.z + H / 2, 0]}>
      {faces.map((f, i) => {
        const on = f.key === pick.selected;
        return (
          <mesh key={i} position={on && f.key === 'floor' ? [0, f.at[1] + 0.03, 0] : f.at} rotation={f.turn} onClick={(e) => { e.stopPropagation(); pick.onSelect(on ? null : f.key); }}>
            <planeGeometry args={f.dims} />
            {on
              ? <meshBasicMaterial color={absorptionColour(info?.alpha ?? null)} transparent opacity={0.8} side={THREE.DoubleSide} depthWrite={false} />
              : <meshBasicMaterial color="#a78bfa" transparent opacity={pick.selected ? 0.006 : 0.035} side={THREE.BackSide} depthWrite={false} />}
          </mesh>
        );
      })}
      <lineSegments>
        <edgesGeometry args={[box]} />
        <lineBasicMaterial color="#c4b5fd" transparent opacity={pick.selected ? 0.2 : 0.55} />
      </lineSegments>
      {info && <SurfaceCallout info={info} bandHz={pick.bandHz} position={anchor[info.key] ?? [0, 0, 0]} onClose={() => pick.onSelect(null)} />}
    </group>
  );
}

/** A click on a model while its shape is being edited: the group and corners (in the engine's frame, exactly as the model has them) of the face under the pointer, and where it was. */
type ShapePick = { group: string; corners: [Point, Point, Point]; at: Point };

/**
 * An imported model: each surface group as translucent triangles in its own colour (the same colour as in the sidebar), with its crease lines.
 * With `onShapePick` a click picks the face under the pointer for editing the shape, instead of isolating its surface.
 */
function ModelShell({ model, frame, pick, onShapePick }: { model: Model; frame: Frame; pick: Pick; onShapePick?: (p: ShapePick) => void }) {
  const parts = useMemo(() => model.groups.map((g, i) => {
    const positions = new Float32Array(g.positions.length);
    for (let k = 0; k < g.positions.length; k += 3) {
      const [x, y, z] = toThree({ x: g.positions[k], y: g.positions[k + 1], z: g.positions[k + 2] }, frame);
      positions[k] = x; positions[k + 1] = y; positions[k + 2] = z;
    }
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    // the middle of the group's vertices, where its callout hangs
    const centre = new THREE.Vector3();
    for (let k = 0; k < positions.length; k += 3) centre.add(new THREE.Vector3(positions[k], positions[k + 1], positions[k + 2]));
    centre.divideScalar(Math.max(1, positions.length / 3));
    return { name: g.name, geometry, edges: new THREE.EdgesGeometry(geometry, 25), colour: groupColour(i), centre: centre.toArray() as [number, number, number] };
  }), [model, frame]);
  const info = pick.infos.find((i) => i.key === pick.selected);
  const chosen = parts.find((p) => p.name === pick.selected);

  return (
    <group>
      {parts.map((p) => (
        <group key={p.name}>
          <mesh geometry={p.geometry} onClick={(e) => {
            e.stopPropagation();
            const g = model.groups.find((x) => x.name === p.name);
            if (onShapePick && g && e.faceIndex != null) {
              const k = e.faceIndex * 9, q = g.positions;
              const corner = (i: number): Point => ({ x: q[k + 3 * i], y: q[k + 3 * i + 1], z: q[k + 3 * i + 2] });
              onShapePick({ group: p.name, corners: [corner(0), corner(1), corner(2)], at: fromThree(e.point, frame) });
            } else pick.onSelect(pick.selected === p.name ? null : p.name);
          }}>
            {p.name === pick.selected
              ? <meshBasicMaterial color={absorptionColour(info?.alpha ?? null)} transparent opacity={0.85} side={THREE.DoubleSide} depthWrite={false} />
              : <meshBasicMaterial color={p.colour} transparent opacity={pick.selected ? 0.012 : 0.16} side={THREE.DoubleSide} depthWrite={false} />}
          </mesh>
          <lineSegments geometry={p.edges}>
            <lineBasicMaterial color={p.colour} transparent opacity={pick.selected ? (p.name === pick.selected ? 0.9 : 0.1) : 0.7} />
          </lineSegments>
        </group>
      ))}
      {info && chosen && <SurfaceCallout info={info} bandHz={pick.bandHz} position={chosen.centre} onClose={() => pick.onSelect(null)} />}
    </group>
  );
}

/** A map: a coloured square at each grid point, flat for a plan or a height matrix and upright for a section. */
function MapLayer({ map, frame }: { map: MapResult; frame: Frame }) {
  const geometry = useMemo(() => {
    const finite = map.points.filter((p) => p.value !== null).map((p) => p.value as number);
    const range = mapRange(finite, map.unit, map.scale);
    const half = map.spacingM * 0.46;
    const positions: number[] = [];
    const colours: number[] = [];
    const colour = new THREE.Color();
    for (const p of map.points) {
      if (p.value === null || !range) continue;
      colour.set(rainbow((p.value - range[0]) / (range[1] - range[0])));
      const [cx, cy, cz] = toThree(p, frame);
      const corners = [[-half, -half], [half, -half], [half, half], [-half, half]];
      for (const k of [0, 1, 2, 0, 2, 3]) {
        const [da, db] = corners[k];
        // plan: across x and the engine's y; section at constant x: across the engine's y and up; section at constant y: across x and up
        if (map.mode !== 'section') positions.push(cx + da, cy, cz + db);
        else if (map.axis === 'x') positions.push(cx, cy + db, cz + da);
        else positions.push(cx + da, cy + db, cz);
        colours.push(colour.r, colour.g, colour.b);
      }
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3));
    g.setAttribute('color', new THREE.Float32BufferAttribute(colours, 3));
    return g;
  }, [map, frame]);
  // the contour lines of each layer: every height of a height matrix, or the one plan, section or map above a surface
  const contours = useMemo(() => {
    const step = map.scale?.contourStep ?? null;
    if (step === null) return null;
    const layers = map.mode === 'volume' ? [...new Set(map.points.map((p) => Math.round(p.z * 100)))].map((cm) => map.points.filter((p) => Math.round(p.z * 100) === cm)) : [map.points];
    const axes: ['x' | 'y' | 'z', 'x' | 'y' | 'z'] = map.mode !== 'section' ? ['x', 'y'] : map.axis === 'x' ? ['y', 'z'] : ['x', 'z'];
    const data: number[] = [];
    for (const layer of layers) {
      const values = layer.filter((p) => p.value !== null).map((p) => p.value as number);
      if (values.length === 0) continue;
      for (const [p, q] of contourSegments(layer, map.spacingM, axes, contourLevels(Math.min(...values), Math.max(...values), step))) data.push(...toThree(p, frame), ...toThree(q, frame));
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(data, 3));
    return g;
  }, [map, frame]);
  return (
    <group>
      <mesh geometry={geometry}>
        <meshBasicMaterial vertexColors transparent opacity={map.mode === 'volume' ? 0.5 : 0.8} side={THREE.DoubleSide} depthWrite={false} />
      </mesh>
      {contours && <lineSegments geometry={contours}><lineBasicMaterial color="#0b0b0e" /></lineSegments>}
    </group>
  );
}

type Path = { pts: THREE.Vector3[]; cumulative: number[]; total: number; hit: boolean; levels: number[] };

const buildPaths = (rays: Ray[], frame: Frame): Path[] => rays.filter((ray) => ray.points.length >= 2).map((ray) => {
  const pts = ray.points.map(([x, y, z]) => new THREE.Vector3(...toThree({ x, y, z }, frame)));
  const cumulative = [0];
  for (let i = 1; i < pts.length; i++) cumulative.push(cumulative[i - 1] + pts[i].distanceTo(pts[i - 1]));
  return { pts, cumulative, total: cumulative[cumulative.length - 1], hit: ray.reachesReceiver, levels: ray.levelsDb };
});

/** The point `d` metres along a path (the end of it once `d` is past it), written into `out`; returns which leg of the path it is on. */
function along(path: Path, d: number, out: THREE.Vector3) {
  const c = path.cumulative;
  const at = Math.min(Math.max(d, 0), path.total);
  let s = 1;
  while (s < c.length - 1 && c[s] < at) s++;
  out.copy(path.pts[s - 1]).lerp(path.pts[s], Math.min(1, (at - c[s - 1]) / (c[s] - c[s - 1] || 1)));
  return s - 1;
}

/** The rays at rest: faint lines for every path, brighter for the ones that reach the receiver. */
function RayLines({ paths, dim }: { paths: Path[]; dim: boolean }) {
  const [miss, hit] = useMemo(() => [false, true].map((wanted) => {
    const data: number[] = [];
    for (const path of paths) if (path.hit === wanted) for (let i = 1; i < path.pts.length; i++) data.push(...path.pts[i - 1].toArray(), ...path.pts[i].toArray());
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(data, 3));
    return g;
  }), [paths]);
  return (
    <group>
      <lineSegments geometry={miss}><lineBasicMaterial color="#a78bfa" transparent opacity={dim ? 0.03 : 0.16} depthWrite={false} /></lineSegments>
      <lineSegments geometry={hit}><lineBasicMaterial color="#34d399" transparent opacity={dim ? 0.1 : 0.55} depthWrite={false} /></lineSegments>
    </group>
  );
}

/**
 * The sound leaving the source as a wave: the room starts clear, every ray sets off at once as a bright head with a fading trail,
 * the heads bounce round the room and drop out as their rays end, and when the last has gone the room is clear again and it repeats.
 */
function RayWave({ paths, trail }: { paths: Path[]; trail: number }) {
  const built = useMemo(() => {
    const segments = TRAIL_SAMPLES - 1;
    const attributes = (g: THREE.BufferGeometry, vertices: number) => {
      g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(vertices * 3), 3));
      g.setAttribute('color', new THREE.BufferAttribute(new Float32Array(vertices * 3), 3));
      return g;
    };
    return {
      lines: attributes(new THREE.BufferGeometry(), paths.length * segments * 2),
      heads: attributes(new THREE.BufferGeometry(), paths.length),
      longest: Math.max(1, ...paths.map((p) => p.total)),
    };
  }, [paths]);

  const clock = useRef(0);
  const here = useMemo(() => [new THREE.Vector3(), new THREE.Vector3(), new THREE.Color()] as const, []);
  useFrame((_, delta) => {
    clock.current += delta;
    // the wave runs until the last trail has gone, then rests for a second with nothing in the room
    const period = (built.longest + trail) / PULSE_SPEED + 1;
    const head = (clock.current % period) * PULSE_SPEED;
    const [a, b, colour] = here;
    const pos = built.lines.getAttribute('position') as THREE.BufferAttribute;
    const col = built.lines.getAttribute('color') as THREE.BufferAttribute;
    const hp = built.heads.getAttribute('position') as THREE.BufferAttribute;
    const hc = built.heads.getAttribute('color') as THREE.BufferAttribute;
    const step = trail / (TRAIL_SAMPLES - 1);
    paths.forEach((path, i) => {
      colour.set(path.hit ? '#34d399' : '#a78bfa');
      for (let k = 0; k < TRAIL_SAMPLES - 1; k++) {
        // segment k joins trail samples k and k + 1, brightest at the head
        along(path, head - k * step, a);
        along(path, head - (k + 1) * step, b);
        const fade = (1 - k / (TRAIL_SAMPLES - 1)) ** 2;
        const v = (i * (TRAIL_SAMPLES - 1) + k) * 2;
        pos.setXYZ(v, a.x, a.y, a.z); pos.setXYZ(v + 1, b.x, b.y, b.z);
        col.setXYZ(v, colour.r * fade, colour.g * fade, colour.b * fade);
        col.setXYZ(v + 1, colour.r * fade * 0.75, colour.g * fade * 0.75, colour.b * fade * 0.75);
      }
      along(path, head, a);
      const live = head <= path.total ? 1 : 0; // a head that has reached the end of its ray goes out; black adds nothing
      hp.setXYZ(i, a.x, a.y, a.z);
      hc.setXYZ(i, live, live, live);
    });
    pos.needsUpdate = col.needsUpdate = hp.needsUpdate = hc.needsUpdate = true;
  });

  return (
    <group>
      <lineSegments geometry={built.lines} frustumCulled={false}><lineBasicMaterial vertexColors transparent depthWrite={false} blending={THREE.AdditiveBlending} /></lineSegments>
      <points geometry={built.heads} frustumCulled={false}><pointsMaterial vertexColors size={0.16} sizeAttenuation transparent depthWrite={false} blending={THREE.AdditiveBlending} /></points>
    </group>
  );
}

/**
 * Every ray as a ball, all sent from the source together. A ball is red at full level and shifts through to blue as its ray loses energy at each
 * bounce (to the lowest level any ray reaches, `floorDb`), and goes out when its ray ends; when the last has gone the room is clear and they are sent again.
 */
function BilliardBalls({ paths, floorDb }: { paths: Path[]; floorDb: number }) {
  const balls = useRef<THREE.InstancedMesh>(null);
  const clock = useRef(0);
  const longest = useMemo(() => Math.max(1, ...paths.map((p) => p.total)), [paths]);
  const shades = useMemo(() => Array.from({ length: 33 }, (_, i) => new THREE.Color(rainbow(i / 32))), []);
  const scratch = useMemo(() => ({ at: new THREE.Vector3(), matrix: new THREE.Matrix4(), turn: new THREE.Quaternion(), one: new THREE.Vector3(1, 1, 1), none: new THREE.Vector3(0, 0, 0) }), []);
  useFrame((_, delta) => {
    const mesh = balls.current;
    if (!mesh) return;
    clock.current += delta;
    const d = (clock.current % (longest / BALL_SPEED + 1)) * BALL_SPEED; // the last second of each round has no ball in the room
    const { at, matrix, turn, one, none } = scratch;
    paths.forEach((path, i) => {
      const leg = along(path, d, at);
      matrix.compose(at, turn, d <= path.total ? one : none);
      mesh.setMatrixAt(i, matrix);
      const fallen = Math.min(1, Math.max(0, path.levels[leg] / floorDb)); // 0 at the source's level, 1 at the lowest
      mesh.setColorAt(i, shades[Math.round((1 - fallen) * 32)]);
    });
    mesh.instanceMatrix.needsUpdate = true;
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
  });
  return (
    <instancedMesh key={paths.length} ref={balls} args={[undefined, undefined, paths.length]} frustumCulled={false}>
      <sphereGeometry args={[0.15, 12, 8]} />
      <meshBasicMaterial />
    </instancedMesh>
  );
}

/** Something the user can pick up: click it to show the move gizmo, drag, and the new position is kept inside the room. */
function Handle({ position, frame, active, onSelect, onMove, children }: {
  position: [number, number, number]; frame: Frame; active: boolean; onSelect: () => void; onMove: (p: Point) => void; children: React.ReactNode;
}) {
  const [object, setObject] = useState<THREE.Group | null>(null);
  const keep = () => {
    if (!object) return;
    const s = size(frame);
    const m = frame.margin;
    const p = object.position;
    p.x = THREE.MathUtils.clamp(p.x, -s.x / 2 + m, s.x / 2 - m);
    p.y = THREE.MathUtils.clamp(p.y, frame.min.z + m, frame.max.z - m);
    p.z = THREE.MathUtils.clamp(p.z, -s.y / 2 + m, s.y / 2 - m);
  };
  return (
    <>
      <group ref={setObject} position={position} onClick={(e) => { e.stopPropagation(); onSelect(); }}>
        {children}
        {/* a generous invisible target, so the small marker is easy to pick */}
        <mesh><sphereGeometry args={[0.8, 12, 12]} /><meshBasicMaterial transparent opacity={0} depthWrite={false} /></mesh>
      </group>
      {object && active && (
        <TransformControls object={object} mode="translate" size={0.7} onObjectChange={keep} onMouseUp={() => { keep(); onMove(fromThree(object.position, frame)); }} />
      )}
    </>
  );
}

export default function Viewport({ frame, model, source, receiver, rays, mode, map, directivity, surfaces, selectedSurface, onSelectSurface, bandHz, onAim, onSource, onReceiver,
  extras, activeSource, onActiveSource, onExtraMove, onExtraAim, extraReceivers, onExtraReceiverMove, highlight, facing, onFacing, surfacesHidden = false, screens = [], faults = [], selectedFault = null,
  onShape, shapeBusy = false, viewKey = 0 }: Props) {
  // the surfaces can be hidden: they are then neither drawn nor hit by a click, so the map shows clear and a source or the receiver behind a wall can be picked up
  const [showSurfaces, setShowSurfaces] = useState(!surfacesHidden);
  const [selected, setSelected] = useState<string | null>(null); // 'source', 'receiver' or 'x' and an extra source's id
  const [cameraView, setCameraView] = useState<CameraView | null>(null);
  // editing the model's shape: what is picked, and the face being drawn
  const [shaping, setShaping] = useState(false);
  const [shape, setShape] = useState<ShapeState>(NO_SHAPE);
  useEffect(() => setShape(NO_SHAPE), [viewKey]); // a new model: nothing of the old one stays picked
  const editing = shaping && !!model && !!onShape && showSurfaces;
  // the corner of the face clicked that is nearest the click
  const pickShape = (p: ShapePick) => {
    const d = (q: Point) => Math.hypot(q.x - p.at.x, q.y - p.at.y, q.z - p.at.z);
    const corner = p.corners.reduce((a, b) => (d(b) < d(a) ? b : a));
    setShape((now) => ({ ...now, corner, at: corner, face: { group: p.group, corners: p.corners } }));
  };
  const s = size(frame);
  const reach = Math.max(s.x, s.y, s.z);
  const target: [number, number, number] = [0, frame.min.z + s.z / 2, 0];
  const pick: Pick = { infos: surfaces, selected: selectedSurface, onSelect: onSelectSurface, bandHz };
  const chosen = surfaces.find((i) => i.key === selectedSurface);
  const paths = useMemo(() => buildPaths(rays, frame), [rays, frame]);
  // the lowest level any ray carries, to the next 5 dB, is the blue end of the balls' colours (never less than -10 dB or more than -60 dB)
  const floorDb = useMemo(() => Math.max(-60, Math.min(-10, Math.ceil(Math.min(0, ...paths.flatMap((p) => p.levels)) / 5) * 5)), [paths]);
  const dim = selectedSurface !== null;
  return (
    <div className="absolute inset-0">
      {/* a new model gets a camera of its own, framed to fit; an edited one keeps the camera where it was */}
      <Canvas key={model ? `model${viewKey}` : 'box'} dpr={[1, 2]} camera={{ position: [reach * 0.9, frame.min.z + reach * 0.75, reach * 1.1], fov: 45, near: 0.1, far: 1000 }}
              onPointerMissed={() => { setSelected(null); onSelectSurface(null); }} gl={{ antialias: true }}>
        <color attach="background" args={['#070a12']} />
        <ambientLight intensity={0.8} />
        <Floor frame={frame} />
        {showSurfaces && (model ? <ModelShell model={model} frame={frame} pick={pick} onShapePick={editing ? pickShape : undefined} /> : <BoxShell frame={frame} pick={pick} />)}
        {editing && <ShapeMarks shape={shape} frame={frame} />}
        {map && <MapLayer map={map} frame={frame} />}
        {screens.map((sc) => screenPieces(sc).map((p, k) => {
          // a thin slab for each piece, standing on the screen's base, turned about the vertical (Three's y; the room's y is Three's -z, so the same angle)
          const angle = sc.angleDeg * Math.PI / 180, u = (p.u0 + p.u1) / 2 - sc.lengthM / 2;
          const centre = { x: sc.x + Math.cos(angle) * u, y: sc.y + Math.sin(angle) * u, z: sc.baseZ + (p.z0 + p.z1) / 2 };
          return (
            <mesh key={`${sc.id}-${k}`} position={toThree(centre, frame)} rotation={[0, angle, 0]}>
              <boxGeometry args={[p.u1 - p.u0, p.z1 - p.z0, 0.05]} />
              <meshBasicMaterial color={sc.finish.transmissionLossDb ? '#a3e635' : '#fbbf24'} transparent opacity={0.55} />
            </mesh>
          );
        }))}
        {mode === 'still' && <RayLines paths={paths} dim={dim} />}
        {mode === 'wave' && !dim && <RayWave paths={paths} trail={Math.max(2, reach * 0.25)} />}
        {mode === 'ball' && !dim && paths.length > 0 && <BilliardBalls paths={paths} floorDb={floorDb} />}

        {faults.map((f, i) => (
          <Line key={`fault${i}`} segments points={f.edges.flatMap(([x1, y1, z1, x2, y2, z2]) => [toThree({ x: x1, y: y1, z: z1 }, frame), toThree({ x: x2, y: y2, z: z2 }, frame)])}
                color={i === selectedFault ? '#facc15' : '#f43f5e'} lineWidth={i === selectedFault ? 5 : 2.5} depthTest={false} renderOrder={i === selectedFault ? 11 : 10} />
        ))}
        {highlight && highlight.length > 1 && <Line points={highlight.map((p) => toThree(p, frame))} color="#facc15" lineWidth={3.5} />}

        <Handle position={toThree(source, frame)} frame={frame} active={selected === 'source'} onSelect={() => { setSelected('source'); onActiveSource(0); }} onMove={onSource}>
          <mesh><sphereGeometry args={[0.28, 24, 24]} /><meshBasicMaterial color="#34d399" /></mesh>
          {activeSource === 0 && <mesh><sphereGeometry args={[0.42, 20, 20]} /><meshBasicMaterial color="#34d399" wireframe transparent opacity={0.5} /></mesh>}
          {directivity && <SourceAim directivity={directivity} reach={reach} onAim={onAim} />}
          {directivity?.kind === 'surface' && <SurfacePatch directivity={directivity} colour="#34d399" />}
          <Html center position={[0, 0.55, 0]} style={{ pointerEvents: 'none' }}><span className="whitespace-nowrap rounded bg-black/60 px-1.5 py-0.5 text-[10px] text-emerald-300">{extras.length ? 'Source 1' : 'Source'}</span></Html>
        </Handle>

        {extras.map((x, i) => (
          <Handle key={x.id} position={toThree(x.position, frame)} frame={frame} active={selected === `x${x.id}`} onSelect={() => { setSelected(`x${x.id}`); onActiveSource(x.id); }} onMove={(p) => onExtraMove(x.id, p)}>
            <mesh><sphereGeometry args={[0.26, 24, 24]} /><meshBasicMaterial color="#f472b6" /></mesh>
            {activeSource === x.id && <mesh><sphereGeometry args={[0.4, 20, 20]} /><meshBasicMaterial color="#f472b6" wireframe transparent opacity={0.5} /></mesh>}
            {x.directivity && <SourceAim directivity={x.directivity} reach={reach} colour="#f472b6" onAim={(az, el) => onExtraAim(x.id, az, el)} />}
            {x.directivity?.kind === 'surface' && <SurfacePatch directivity={x.directivity} colour="#f472b6" />}
            <Html center position={[0, 0.55, 0]} style={{ pointerEvents: 'none' }}><span className="whitespace-nowrap rounded bg-black/60 px-1.5 py-0.5 text-[10px] text-purple-300">Source {i + 2}</span></Html>
          </Handle>
        ))}

        <Handle position={toThree(receiver, frame)} frame={frame} active={selected === 'receiver'} onSelect={() => setSelected('receiver')} onMove={onReceiver}>
          <mesh><sphereGeometry args={[0.22, 24, 24]} /><meshBasicMaterial color="#22d3ee" /></mesh>
          <mesh><sphereGeometry args={[0.5, 24, 24]} /><meshBasicMaterial color="#22d3ee" transparent opacity={0.1} depthWrite={false} /></mesh>
          <ListenerFacing facing={facing} onFacing={onFacing} />
          <Html center position={[0, 0.8, 0]} style={{ pointerEvents: 'none' }}><span className="whitespace-nowrap rounded bg-black/60 px-1.5 py-0.5 text-[10px] text-cyan-300">{extraReceivers.length ? 'Receiver 1' : 'Receiver'}</span></Html>
        </Handle>

        {extraReceivers.map((r, i) => (
          <Handle key={r.id} position={toThree(r.position, frame)} frame={frame} active={selected === `r${r.id}`} onSelect={() => setSelected(`r${r.id}`)} onMove={(p) => onExtraReceiverMove(r.id, p)}>
            <mesh><sphereGeometry args={[0.2, 24, 24]} /><meshBasicMaterial color="#2dd4bf" /></mesh>
            <Html center position={[0, 0.7, 0]} style={{ pointerEvents: 'none' }}><span className="whitespace-nowrap rounded bg-black/60 px-1.5 py-0.5 text-[10px] text-teal-300">Receiver {i + 2}</span></Html>
          </Handle>
        ))}

        <OrbitControls makeDefault enableDamping dampingFactor={0.08} target={target} maxPolarAngle={Math.PI * 0.95} />
        <CameraRig view={cameraView} target={target} reach={reach} />
      </Canvas>
      <div role="group" aria-label="Camera view" className="absolute left-3 right-3 top-3 flex flex-wrap gap-1 sm:right-52">
        {CAMERA_VIEWS.map((v) => (
          <button key={v.name} type="button" onClick={() => setCameraView({ name: v.name, n: (cameraView?.n ?? 0) + 1 })}
                  className={`chip backdrop-blur ${cameraView?.name === v.name ? 'chip-on' : ''}`}>{v.name}</button>
        ))}
        <button type="button" aria-pressed={showSurfaces} onClick={() => { setShowSurfaces(!showSurfaces); onSelectSurface(null); }}
                title="Show or hide the room's surfaces" className={`chip backdrop-blur ${showSurfaces ? 'chip-on' : ''}`}>Show surfaces</button>
        {model && onShape && (
          <button type="button" aria-pressed={editing} onClick={() => { setShaping(!editing); setShowSurfaces(true); onSelectSurface(null); }}
                  title="Move the model's corners, draw faces and delete surfaces" className={`chip backdrop-blur ${editing ? 'chip-on' : ''}`}>Edit shape</button>
        )}
      </div>
      {editing && model && onShape && (
        <ShapePanel shape={shape} onShape={setShape} groups={model.groups.map((g) => g.name)} onEdit={onShape} busy={shapeBusy} onClose={() => setShaping(false)} />
      )}
      <div className="pointer-events-none absolute bottom-3 left-3 right-3 flex flex-wrap items-center gap-x-4 gap-y-0.5 rounded-lg bg-black/40 px-3 py-1.5 text-[11px] text-slate-400 backdrop-blur sm:right-auto">
        <span>Drag to orbit · scroll to zoom · right-drag to pan</span>
        <span className="hidden text-slate-500 sm:inline">|</span>
        <span>{editing ? 'Click the model to pick a corner and a face · ' : ''}Click a source or the receiver to move it · {editing ? '' : 'click a surface to isolate it · '}drag the dot on a source's arrow to aim it, or the one on the receiver's to turn the listener's head</span>
      </div>
      {chosen && showSurfaces && (
        <div className="pointer-events-none absolute right-3 top-24 w-44 space-y-1 rounded-lg bg-black/40 px-3 py-2 text-[11px] text-slate-300 backdrop-blur">
          <div>Absorption α at {bandHzText(bandHz)}</div>
          <div className="h-2.5 rounded" style={{ background: `linear-gradient(to right, ${[0, 0.25, 0.5, 0.75, 1].map((a) => absorptionColour(a)).join(', ')})` }} />
          <div className="flex justify-between tabular-nums text-slate-400"><span>0 reflects</span><span>1 absorbs</span></div>
        </div>
      )}
      {mode === 'ball' && (
        <div className="pointer-events-none absolute right-3 top-3 w-44 space-y-1 rounded-lg bg-black/40 px-3 py-2 text-[11px] text-slate-300 backdrop-blur">
          <div>Level of each ball</div>
          <div className="h-2.5 rounded" style={{ background: `linear-gradient(to right, ${[1, 0.75, 0.5, 0.25, 0].map(rainbow).join(', ')})` }} />
          <div className="flex justify-between tabular-nums text-slate-400"><span>0 dB</span><span>{floorDb} dB</span></div>
        </div>
      )}
      {mode !== 'ball' && (
        <div className="pointer-events-none absolute right-3 top-3 hidden space-y-1 rounded-lg sm:block bg-black/40 px-3 py-2 text-[11px] text-slate-300 backdrop-blur">
          <div className="flex items-center gap-2"><span className="h-0.5 w-4 bg-violet-400/60" />Ray path</div>
          <div className="flex items-center gap-2"><span className="h-0.5 w-4 bg-emerald-400" />Reaches the receiver</div>
        </div>
      )}
    </div>
  );
}
