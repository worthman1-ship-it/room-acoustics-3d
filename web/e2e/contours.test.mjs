// The contour code on its own, with no page or engine: node runs the TypeScript directly.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contourLabels, contourLevels, contourSegments, MAX_CONTOURS, stepDecimals } from '../src/contours.ts';

// a 1 m grid from 0.5 to 4.5 m in x and y, the value given by f
const grid = (f, z = 1.2) => {
  const points = [];
  for (let i = 0; i < 5; i++) for (let j = 0; j < 5; j++) { const x = 0.5 + i, y = 0.5 + j; points.push({ x, y, z, value: f(x, y) }); }
  return points;
};

test('the contour levels are the whole multiples of the step between the values', () => {
  assert.deepEqual(contourLevels(0.3, 2.1, 0.5), [0.5, 1, 1.5, 2]);
  assert.deepEqual(contourLevels(-3.2, -0.9, 1), [-3, -2, -1]);
  assert.deepEqual(contourLevels(0.1, 0.35, 0.1), [0.1, 0.2, 0.3], 'no 0.30000000000000004');
  assert.deepEqual(contourLevels(0, 1, 0), [], 'no step, no lines');
  assert.deepEqual(contourLevels(0, 1000, 1), [], `more than ${MAX_CONTOURS} lines is none`);
});

test('a level across a slope is a straight line where the slope reaches it', () => {
  const segments = contourSegments(grid((x) => x), 1, ['x', 'y'], [2]);
  assert.equal(segments.length, 4, 'one piece in each of the four squares the line runs through');
  for (const [p, q] of segments) {
    assert.ok(Math.abs(p.x - 2) < 1e-9 && Math.abs(q.x - 2) < 1e-9, 'the line is at x = 2');
    assert.ok(Math.abs(Math.abs(p.y - q.y) - 1) < 1e-9, 'each piece crosses its square');
  }
  const ys = segments.flatMap(([p, q]) => [p.y, q.y]);
  assert.equal(Math.min(...ys), 0.5); assert.equal(Math.max(...ys), 4.5);
});

test('a line between grid points is placed by interpolation, and carries the height of the points', () => {
  const [[p]] = contourSegments(grid((x) => 10 * x, 2.5).filter((pt) => pt.y < 1.6), 1, ['x', 'y'], [12.5]);
  assert.ok(Math.abs(p.x - 1.25) < 1e-9); assert.equal(p.z, 2.5);
});

test('a peak gives a closed ring round it', () => {
  const segments = contourSegments(grid((x, y) => -Math.hypot(x - 2.5, y - 2.5)), 1, ['x', 'y'], [-0.9]);
  assert.equal(segments.length, 4, 'one piece in each square round the middle point');
  // every end is shared by two pieces, so the ring has no loose ends
  const key = (p) => `${p.x.toFixed(6)},${p.y.toFixed(6)}`;
  const ends = new Map();
  for (const s of segments) for (const p of s.slice(0, 2)) ends.set(key(p), (ends.get(key(p)) ?? 0) + 1);
  assert.ok([...ends.values()].every((n) => n === 2));
});

test('squares with a point left out of the map give no line', () => {
  const points = grid((x) => x).map((p) => (p.x === 1.5 ? { ...p, value: null } : p));
  const segments = contourSegments(points, 1, ['x', 'y'], [2]);
  assert.equal(segments.length, 0, 'the line at x = 2 runs only through squares that lack x = 1.5');
});

test('a section runs in its own two directions', () => {
  const points = [];
  for (let i = 0; i < 4; i++) for (let k = 0; k < 3; k++) points.push({ x: 3, y: 0.5 + i, z: 0.5 + k, value: k });
  const segments = contourSegments(points, 1, ['y', 'z'], [1.5]);
  assert.equal(segments.length, 3);
  for (const [p, q] of segments) { assert.equal(p.x, 3); assert.ok(Math.abs(p.z - 2) < 1e-9 && Math.abs(q.z - 2) < 1e-9, 'half way between the values 1 and 2, at z 1.5 and 2.5'); }
});

test('a saddle is settled by the middle of its square', () => {
  // corners (0,0) and (1,1) high, the other two low; their mean is 0.5
  const square = [{ x: 0, y: 0, z: 0, value: 1 }, { x: 1, y: 0, z: 0, value: 0 }, { x: 1, y: 1, z: 0, value: 1 }, { x: 0, y: 1, z: 0, value: 0 }];
  // the corner each piece cuts off is the one nearest its middle
  const cutOff = (level) => contourSegments(square, 1, ['x', 'y'], [level])
    .map(([p, q]) => square.reduce((best, c) => (Math.hypot(c.x - (p.x + q.x) / 2, c.y - (p.y + q.y) / 2) < Math.hypot(best.x - (p.x + q.x) / 2, best.y - (p.y + q.y) / 2) ? c : best)))
    .map((c) => `${c.x},${c.y}`).sort();
  assert.deepEqual(cutOff(0.4), ['0,1', '1,0'], 'the middle is above the level, so the low corners are cut off');
  assert.deepEqual(cutOff(0.6), ['0,0', '1,1'], 'the middle is below it, so the high ones are');
});

test('each level is labelled once, on its own line', () => {
  const segments = contourSegments(grid((x) => x), 1, ['x', 'y'], [1, 2, 3]);
  const labels = contourLabels(segments);
  assert.deepEqual(labels.map((l) => l.level).sort(), [1, 2, 3]);
  for (const { at, level } of labels) {
    assert.ok(Math.abs(at.x - level) < 1e-9, 'on the line');
    assert.ok(at.y > 1.5 && at.y < 3.5, 'part way along it, not at an end');
  }
});

test('a step is written with as many decimals as it has', () => {
  assert.deepEqual([2, 1, 0.5, 0.25, 0.1, 0.05].map(stepDecimals), [0, 0, 1, 2, 1, 2]);
});

test('where lines crowd together, a figure too close to another is left out', () => {
  // lines every 0.25 m, 4 m long: figures kept 2 m apart cannot all fit, however they are staggered along the lines
  const segments = contourSegments(grid((x) => x), 1, ['x', 'y'], [1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75, 3]);
  assert.equal(contourLabels(segments).length, 9, 'with no gap, every level');
  const spaced = contourLabels(segments, 2);
  assert.ok(spaced.length >= 2 && spaced.length < 9, `${spaced.length}`);
  for (const a of spaced) for (const b of spaced) if (a !== b) assert.ok(Math.hypot(a.at.x - b.at.x, a.at.y - b.at.y) >= 2);
});
