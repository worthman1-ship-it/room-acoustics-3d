// A browser-driven test of the page itself. It drives the system's Chrome or Edge (nothing is downloaded) against a running app:
//   ./run-app.ps1            (the API serves the built page on http://localhost:5080), or the dev servers on http://127.0.0.1:5173
// then:  npm run e2e        (E2E_URL overrides the address; E2E_BROWSER overrides the browser's path)
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { chromium } from 'playwright-core';

const URL = process.env.E2E_URL ?? 'http://localhost:5080';
const CANDIDATES = [
  process.env.E2E_BROWSER,
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/google-chrome', '/usr/bin/chromium', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
].filter(Boolean);
const executablePath = CANDIDATES.find((p) => existsSync(p));

let browser;
before(async () => {
  assert.ok(executablePath, 'no Chrome or Edge found: set E2E_BROWSER to the browser executable');
  // software GL so the 3-D view also draws on a machine with no graphics acceleration
  browser = await chromium.launch({ executablePath, args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
});
after(async () => { await browser?.close(); });

// the main area shows the 3-D room with its results, or the map in a view of its own
const showMap = (page) => page.getByRole('tab', { name: 'Map', exact: true }).click();

async function open(viewport = { width: 1280, height: 800 }) {
  const page = await browser.newPage({ viewport });
  const problems = [];
  page.on('pageerror', (e) => problems.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error') problems.push(m.text()); });
  await page.goto(URL);
  await page.getByText('Engine connected').waitFor({ timeout: 15000 });
  return { page, problems };
}

test('the page loads, finds the engine and draws the 3-D view without errors', async () => {
  const { page, problems } = await open();
  assert.equal(await page.locator('canvas').count(), 1);
  await page.getByText('Source', { exact: true }).first().waitFor();
  assert.deepEqual(problems.filter((p) => !/WebGL|GPU|swiftshader/i.test(p)), []);
  await page.close();
});

test('Run simulation fills the headline figures and the charts', async () => {
  const { page } = await open();
  await page.getByRole('button', { name: /Run simulation/ }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  const text = await page.locator('main').innerText();
  assert.match(text, /T30 \(500–1K\)\s+\d+\.\d+\s*s/i);
  await page.locator('.recharts-surface').first().waitFor(); // a chart is drawn once the results have their size
  await page.close();
});

// the calculation and the map settings live in a browser window of their own
async function settingsWindow(page) {
  const [popup] = await Promise.all([page.waitForEvent('popup'), page.getByRole('button', { name: 'Open settings window' }).click()]);
  await popup.getByText('Calculation', { exact: true }).waitFor();
  return popup;
}

// the sources' patterns, levels and spectra live in a browser window of their own
async function sourcesWindow(page) {
  const [popup] = await Promise.all([page.waitForEvent('popup'), page.getByRole('button', { name: 'Open sources window' }).click()]);
  await popup.getByText(/Level and spectrum of source \d/).waitFor(); // of whichever source is the active one
  return popup;
}

// the surfaces live in a browser window of their own
async function surfacesWindow(page) {
  const [popup] = await Promise.all([page.waitForEvent('popup'), page.getByRole('button', { name: 'Open surfaces window' }).click()]);
  await popup.getByText('Surfaces', { exact: true }).waitFor();
  return popup;
}

test('a screen added between the source and the receiver stands across the line between them and cuts the early sound behind it', async () => {
  const { page, problems } = await open();
  const earlyG = async () => {
    await page.getByRole('button', { name: /Run simulation/ }).click();
    await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
    await page.getByTestId('stale').waitFor({ state: 'detached', timeout: 90000 }); // the results are this run's
    await page.getByRole('button', { name: 'Receivers', exact: true }).click();
    return Number(await page.getByTestId('receiver-0-earlyStrengthGDb').innerText());
  };
  const open1 = await earlyG();
  const popup = await surfacesWindow(page);
  await popup.getByRole('button', { name: '+ Add screen' }).click();
  assert.equal(await popup.getByTestId('screen').count(), 1);
  // half way between the source at (2, 4) and the receiver at (9, 4), standing on the floor, turned across the line
  assert.equal(await popup.getByLabel('Screen 1 x (m)').inputValue(), '5.5');
  assert.equal(await popup.getByLabel('Screen 1 y (m)').inputValue(), '4');
  assert.equal(await popup.getByLabel('Screen 1 Base (m)').inputValue(), '0');
  assert.equal(await popup.getByLabel('Screen 1 Angle (°)').inputValue(), '90');
  // tall enough to hide the 1.6 m source from the 1.2 m receiver
  await popup.getByLabel('Screen 1 Height (m)').fill('2');
  await page.getByTestId('stale').waitFor();
  const screened = await earlyG();
  assert.ok(screened < open1 - 0.5, `G early falls behind the screen in this hard room (${open1} dB without, ${screened} dB with)`);
  await popup.getByRole('button', { name: 'Remove screen 1' }).click();
  assert.equal(await popup.getByTestId('screen').count(), 0);
  assert.deepEqual(problems, []);
  await page.close();
});

test('a partition with a door splits the room into two coupled rooms, and sound passes through it by its reduction', async () => {
  const { page, problems } = await open();
  const g = async () => {
    await page.getByRole('button', { name: /Run simulation/ }).click();
    await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
    await page.getByTestId('stale').waitFor({ state: 'detached', timeout: 90000 });
    await page.getByRole('button', { name: 'Receivers', exact: true }).click();
    return Number(await page.getByTestId('receiver-0-strengthGDb').innerText());
  };
  const settings = await settingsWindow(page);
  await settings.getByRole('group', { name: 'Calculation preset' }).getByRole('button', { name: 'Draft' }).click();
  await settings.close();
  const open1 = await g();
  const popup = await surfacesWindow(page);
  await popup.getByRole('button', { name: '+ Add partition' }).click();
  // across the 12 m room at x = 6, side to side and floor to ceiling: the source at x = 2 and the receiver at x = 9 are in different rooms
  assert.deepEqual([await popup.getByLabel('Screen 1 x (m)').inputValue(), await popup.getByLabel('Screen 1 Length (m)').inputValue(), await popup.getByLabel('Screen 1 Height (m)').inputValue(),
    await popup.getByLabel('Screen 1 Angle (°)').inputValue()], ['6', '8', '4', '90']);
  assert.equal(await popup.getByLabel('Screen 1 has a door').isChecked(), true);
  assert.match(await popup.getByLabel('Screen 1 sound reduction', { exact: true }).locator('option:checked').innerText(), /Blockwork/);
  await page.getByTestId('stale').waitFor();
  const withDoor = await g();
  assert.ok(withDoor < open1 - 3, `the partition cuts the level in the other room (${open1} dB without, ${withDoor} dB with)`);
  await popup.getByLabel('Screen 1 has a door').uncheck();
  await page.getByTestId('stale').waitFor();
  const shut = await g();
  assert.ok(shut < withDoor - 10, `with the door shut, far less gets through (${withDoor} dB, ${shut} dB)`);
  // a lighter wall lets more through
  await popup.getByLabel('Screen 1 sound reduction', { exact: true }).selectOption({ label: 'Door, ordinary (Rw ≈ 20)' });
  await page.getByTestId('stale').waitFor();
  assert.ok(await g() > shut + 10);
  assert.deepEqual(problems, []);
  await page.close();
});

test('the surfaces open in a window of their own; clicking one there isolates it, with a callout and the absorption scale', async () => {
  const { page } = await open();
  assert.equal(await page.locator('button[aria-pressed]').filter({ hasText: /walls/i }).count(), 0, 'not in the side panel');
  const popup = await surfacesWindow(page);
  const walls = popup.locator('button[aria-pressed]').filter({ hasText: /walls/i });
  await walls.click();
  assert.equal(await walls.getAttribute('aria-pressed'), 'true');
  await page.getByText(/α .* at 1k Hz/).first().waitFor();
  await page.getByText('Absorption α at 1k Hz').waitFor();
  await page.getByTitle('Show all surfaces again').click();
  await popup.locator('button[aria-pressed=false]').filter({ hasText: /walls/i }).waitFor();
  await page.getByRole('button', { name: 'Close surfaces window' }).click();
  await popup.waitForEvent('close').catch(() => {});
  assert.ok(popup.isClosed());
  await page.close();
});

test('materials are chosen from the whole list in sections, with favourites, recents and materials of your own', async () => {
  const { page } = await open();
  await page.evaluate(() => localStorage.clear());
  const popup = await surfacesWindow(page);
  const trigger = popup.getByRole('button', { name: 'Choose the material of walls' });
  const dialog = popup.getByRole('dialog', { name: 'Materials' });
  const rows = dialog.locator('tbody tr[aria-selected]');

  await trigger.click();
  await dialog.waitFor();
  assert.ok(await rows.count() > 90, 'the whole list, not a first few');
  assert.ok(await dialog.locator('tbody th').count() >= 5, 'in sections');

  // a section folds shut from its heading, and Collapse all / Expand all do every one
  const all = await rows.count();
  const firstSection = dialog.locator('tbody th button').first();
  assert.equal(await firstSection.getAttribute('aria-expanded'), 'true');
  await firstSection.click();
  assert.equal(await firstSection.getAttribute('aria-expanded'), 'false');
  const afterOne = await rows.count();
  assert.ok(afterOne < all, 'the section\'s materials are hidden');
  await firstSection.click();
  assert.equal(await rows.count(), all, 'and come back');
  await dialog.getByRole('button', { name: 'Collapse all' }).click();
  assert.equal(await rows.count(), 0);
  assert.ok(await dialog.locator('tbody th').count() >= 5, 'the headings stay');
  await dialog.getByLabel('Search materials').fill('carpet');
  assert.ok(await rows.count() > 0, 'a search shows its matches even in a folded section');
  await dialog.getByLabel('Search materials').fill('');
  await dialog.getByRole('button', { name: 'Expand all' }).click();
  assert.equal(await rows.count(), all);
  await dialog.getByLabel('Category').selectOption({ index: 2 });
  const inCategory = await rows.count();
  assert.ok(inCategory > 0 && inCategory < 200, 'one category');
  await dialog.getByLabel('Category').selectOption({ index: 0 });
  await dialog.getByLabel('Search materials').fill('carpet');
  const found = await rows.count();
  assert.ok(found > 0 && found < 100, 'the search narrows the list');

  const first = rows.first();
  const name = (await first.locator('td').nth(1).innerText()).trim();
  await first.getByRole('button', { name: /^Favourite/ }).click();
  await first.click();
  await dialog.getByRole('img', { name: 'Absorption curve' }).waitFor();
  await dialog.getByRole('button', { name: 'Use this material' }).click();
  await dialog.waitFor({ state: 'detached' });
  await trigger.filter({ hasText: name }).waitFor();

  await trigger.click();
  await dialog.getByRole('tab', { name: 'Favourites' }).click();
  assert.equal(await rows.count(), 1);
  await dialog.getByRole('tab', { name: 'Recent' }).click();
  assert.equal(await rows.count(), 1);

  // a material of your own: eight coefficients and a name, kept for any surface and sent to the engine in place of one from the list
  await dialog.getByRole('tab', { name: 'New material' }).click();
  await dialog.getByLabel('Material name').fill('Felt panel');
  for (const hz of ['63', '125', '250', '500', '1k', '2k', '4k', '8k']) await dialog.getByLabel(`Absorption at ${hz} Hz`).fill('0.3');
  await dialog.getByRole('button', { name: 'Save and use' }).click();
  await trigger.filter({ hasText: 'Felt panel (custom)' }).waitFor();
  await trigger.click();
  await dialog.getByRole('tab', { name: 'My materials' }).click();
  assert.equal(await rows.count(), 1);
  await dialog.getByRole('button', { name: 'Close' }).click();
  await page.getByRole('button', { name: /Run simulation/ }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('the 3-D room and the results share the screen, the map has a view of its own, and the room has standard camera views', async () => {
  const { page } = await open();
  const view = (name) => page.getByRole('tab', { name, exact: true });
  const canvas = page.locator('canvas');
  const camera = page.getByRole('group', { name: 'Camera view' });
  assert.equal(await view('3-D and results').getAttribute('aria-selected'), 'true');
  assert.ok(await canvas.isVisible(), 'the room is shown');
  assert.ok(await page.getByRole('button', { name: 'Frequency response' }).isVisible(), 'with the charts');
  assert.equal(await page.getByRole('button', { name: 'Map', exact: true }).count(), 0, 'the map is no longer a chart tab');
  for (const name of ['Perspective', 'Top', 'Front', 'Back', 'Left', 'Right']) await camera.getByRole('button', { name, exact: true }).waitFor();
  await camera.getByRole('button', { name: 'Top', exact: true }).click();
  await camera.getByRole('button', { name: 'Right', exact: true }).click();

  // the map's view has a 3-D room of its own and the flat map, and says so until a map is calculated
  await showMap(page);
  await page.getByText('Press Calculate map in the sidebar.').waitFor();
  assert.equal(await page.locator('canvas').count(), 2, 'a second 3-D room for the map');
  assert.equal(await page.getByRole('button', { name: 'Frequency response' }).isVisible(), false, 'the charts are left behind');
  await view('3-D and results').click();
  await page.getByRole('button', { name: 'Frequency response' }).waitFor();
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('a surface can be picked in the 3-D view itself', async () => {
  const { page } = await open();
  await page.getByLabel('Show rays').uncheck().catch(() => page.getByText('Show rays').click());
  const box = await page.locator('canvas').boundingBox();
  // seen from the starting camera, the far wall fills the upper left of the middle of the view
  await page.mouse.click(box.x + box.width * 0.4, box.y + box.height * 0.32);
  await page.getByTitle('Show all surfaces again').waitFor();
  await page.close();
});

test('the rays are shown still, as a wave, or as billiard balls coloured by their level', async () => {
  const { page } = await open();
  const mode = (name) => page.getByRole('group', { name: 'Ray display' }).getByRole('button', { name });
  assert.equal(await mode('Wave').getAttribute('aria-pressed'), 'true');
  await mode('Billiard balls').click();
  await page.getByText('Level of each ball').waitFor({ timeout: 15000 });
  assert.match(await page.getByText('Level of each ball').locator('xpath=..').innerText(), /0 dB\s+-\d+ dB/);
  await mode('Still').click();
  assert.equal(await page.getByText('Level of each ball').count(), 0);
  await page.close();
});

const dae = `<?xml version="1.0"?><COLLADA xmlns="http://www.collada.org/2005/11/COLLADASchema" version="1.4.1">
    <asset><unit name="inch" meter="0.5"/><up_axis>Z_UP</up_axis></asset>
    <library_geometries><geometry id="g" name="Box"><mesh>
      <source id="p"><float_array id="pa" count="24">0 0 0 20 0 0 20 12 0 0 12 0 0 0 6 20 0 6 20 12 6 0 12 6</float_array></source>
      <vertices id="v"><input semantic="POSITION" source="#p"/></vertices>
      <polylist count="6"><input semantic="VERTEX" source="#v" offset="0"/><vcount>4 4 4 4 4 4</vcount><p>0 3 2 1 4 5 6 7 0 1 5 4 1 2 6 5 2 3 7 6 3 0 4 7</p></polylist>
    </mesh></geometry></library_geometries>
    <library_visual_scenes><visual_scene id="s"><node id="n"><instance_geometry url="#g"/></node></visual_scene></library_visual_scenes></COLLADA>`;

test('a SketchUp COLLADA export can be imported, and its room is shown with its own units', async () => {
  const { page } = await open();
  await page.locator('input[type=file][accept*=".dae"]').setInputFiles({ name: 'Sketch.dae', mimeType: 'model/vnd.collada+xml', buffer: Buffer.from(dae) });
  await page.getByRole('heading', { name: 'Sketch.dae' }).waitFor();
  await page.getByText(/10\.0 × 6\.0 × 3\.0 m · 180 m³/).waitFor();
  await page.close();
});

/** The width and height a PNG says it is, after checking it is one. */
const pngSize = (bytes) => {
  assert.deepEqual([...bytes.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10], 'a PNG');
  return [bytes.readUInt32BE(16), bytes.readUInt32BE(20)];
};
const savedBytes = async (page, click) => {
  const [download] = await Promise.all([page.waitForEvent('download'), click()]);
  const { readFile } = await import('node:fs/promises');
  return { name: download.suggestedFilename(), bytes: await readFile(await download.path()) };
};

test('a chart and a map can be saved as pictures', async () => {
  const { page, problems } = await open();
  await page.getByRole('button', { name: 'Quick estimate' }).click();
  await page.getByText(/statistical estimate from the standard equations/).waitFor({ timeout: 5000 });
  await page.getByRole('button', { name: 'Decay curve' }).click();
  await page.locator('.recharts-line').first().waitFor();
  const chart = await savedBytes(page, () => page.getByRole('button', { name: 'Chart PNG' }).click());
  assert.equal(chart.name, 'decay.png');
  const [w, h] = pngSize(chart.bytes);
  assert.ok(w > 600 && h > 200, `twice the chart's size on screen (${w} x ${h})`);
  // a table has no chart
  await page.getByRole('button', { name: 'ISO 3382 / STI' }).click();
  assert.equal(await page.getByRole('button', { name: 'Chart PNG' }).count(), 0);

  await page.getByRole('button', { name: /Calculate map/ }).click();
  await page.locator('svg[aria-label="Plan map"]').waitFor({ timeout: 90000 });
  const map = await savedBytes(page, () => page.getByRole('button', { name: 'Map PNG' }).click());
  assert.equal(map.name, 'map-C80.png');
  pngSize(map.bytes);
  assert.deepEqual(problems, []);
  await page.close();
});

test('the job list calculates setups one after another and compares them', async () => {
  const { page, problems } = await open();
  const settings = await settingsWindow(page);
  await settings.getByRole('group', { name: 'Calculation preset' }).getByRole('button', { name: 'Draft' }).click(); // few rays, so the jobs are quick
  await settings.close();
  const [jobs] = await Promise.all([page.waitForEvent('popup'), page.getByRole('button', { name: 'Job list' }).click()]);
  await jobs.getByText('Now:').waitFor();
  assert.match(await jobs.getByText('Now:').innerText(), /12 × 8 × 4 m box, 1 source, 1 receiver, 20,000 rays/);

  await jobs.getByLabel('Job name').fill('Small room');
  await jobs.getByRole('button', { name: 'Add current setup' }).click();
  // a longer room for the second
  await page.getByLabel('Length').fill('30');
  await jobs.getByText(/30 × 8 × 4 m box/).waitFor();
  await jobs.getByRole('button', { name: 'Add current setup' }).click();
  assert.deepEqual(await jobs.getByTestId('job-status').allInnerTexts(), ['waiting', 'waiting']);
  assert.equal(await jobs.getByLabel('Name of Job 2').inputValue(), 'Job 2', 'an unnamed job is numbered');

  await jobs.getByRole('button', { name: 'Run all (2)' }).click();
  await jobs.waitForFunction(() => [...document.querySelectorAll('[data-testid=job-status]')].every((s) => s.textContent === 'done'), null, { timeout: 120000 });
  const table = jobs.getByTestId('jobs-compared');
  assert.deepEqual(await table.locator('thead th').allInnerTexts(), ['Single number', 'Small room', 'Job 2']);
  const t30 = (await table.locator('tbody tr', { hasText: 'T30' }).first().locator('td').allInnerTexts()).map(Number);
  assert.ok(t30[1] > t30[0], `the longer room rings longer (${t30})`);

  const [download] = await Promise.all([page.waitForEvent('download'), jobs.getByRole('button', { name: 'Jobs CSV' }).click()]); // the window's buttons run in the page
  const { readFile } = await import('node:fs/promises');
  const csv = await readFile(await download.path(), 'utf8');
  assert.match(csv, /^Job,Setup,Receiver,/);
  assert.match(csv, /\r\nSmall room,"12 × 8 × 4 m box, 1 source, 1 receiver, 20,000 rays",1,.*,T30,s,\d/);
  assert.match(csv, /\r\nJob 2,"30 × 8 × 4 m box/);
  assert.equal(await jobs.getByRole('button', { name: /Run all/ }).isDisabled(), true, 'nothing left to run');
  assert.deepEqual(problems, []);
  await page.close();
});

test('a model that is not closed can be taken as outdoors and simulated', async () => {
  const { page, problems } = await open();
  // ground 40 x 40 m and one 10 m wall standing on it
  const obj = ['v 0 0 0', 'v 40 0 0', 'v 40 40 0', 'v 0 40 0', 'v 30 10 0', 'v 30 30 0', 'v 30 30 4', 'v 30 10 4',
    'usemtl Ground', 'f 1 2 3 4', 'usemtl Wall', 'f 5 6 7 8'].join('\n');
  await page.locator('input[type=file][accept*=".obj"]').setInputFiles({ name: 'Site.obj', mimeType: 'text/plain', buffer: Buffer.from(obj) });
  await page.getByTestId('geometry-check').waitFor();
  assert.equal(await page.getByRole('button', { name: /Run simulation/ }).isDisabled(), true, 'not closed, so not yet');
  await page.getByRole('button', { name: 'Take it as outdoors (open air)' }).click();
  await page.getByTestId('outdoors').waitFor();
  assert.equal(await page.getByTestId('geometry-check').count(), 0);
  const settings = await settingsWindow(page);
  await settings.getByRole('group', { name: 'Calculation preset' }).getByRole('button', { name: 'Draft' }).click();
  await settings.close();
  await page.getByRole('button', { name: /Run simulation/ }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  await page.getByRole('button', { name: 'ISO 3382 / STI' }).click();
  assert.match(await page.getByTestId('broadband-directStrengthGDb').innerText(), /^-?\d+\.\d$/);
  await page.getByRole('button', { name: 'Not outdoors' }).click();
  await page.getByTestId('geometry-check').waitFor();
  assert.deepEqual(problems, []);
  await page.close();
});

test('a source can be a surface, and a 3D Studio model can be imported', async () => {
  const { page, problems } = await open();
  const popup = await sourcesWindow(page);
  await popup.getByRole('group', { name: 'Source pattern' }).getByRole('button', { name: 'Surface' }).click();
  await popup.getByLabel('Surface width').fill('3');
  assert.equal(await popup.getByLabel('Radiates from one side').isChecked(), true);
  await page.getByText(/Source 1: Surface source, Lw/).waitFor();
  await page.getByRole('button', { name: 'Quick estimate' }).click(); // the engine takes the surface source
  await page.getByText(/statistical estimate from the standard equations/).waitFor({ timeout: 5000 });
  await popup.close();

  // a 6 x 5 x 3 m box in 3DS chunks: the floor's faces "Carpet", the rest the object's name
  const chunk = (id, ...parts) => { const body = Buffer.concat(parts); const head = Buffer.alloc(6); head.writeUInt16LE(id, 0); head.writeUInt32LE(body.length + 6, 2); return Buffer.concat([head, body]); };
  const u16 = (...v) => { const b = Buffer.alloc(2 * v.length); v.forEach((x, i) => b.writeUInt16LE(x, 2 * i)); return b; };
  const v = [[0, 0, 0], [6, 0, 0], [6, 5, 0], [0, 5, 0], [0, 0, 3], [6, 0, 3], [6, 5, 3], [0, 5, 3]];
  const quads = [[0, 1, 2, 3], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
  const faces = quads.flatMap(([a, b, c, d]) => [[a, b, c], [a, c, d]]);
  const points = Buffer.alloc(12 * v.length); v.flat().forEach((x, i) => points.writeFloatLE(x, 4 * i));
  const file = chunk(0x4d4d, chunk(0x3d3d, chunk(0x4000, Buffer.from('Hall\0', 'latin1'), chunk(0x4100,
    chunk(0x4110, u16(v.length), points),
    chunk(0x4120, u16(faces.length), u16(...faces.flatMap((f) => [...f, 0])), chunk(0x4130, Buffer.from('Carpet\0', 'latin1'), u16(2, 0, 1)))))));
  await page.locator('input[type=file][accept*=".3ds"]').setInputFiles({ name: 'Hall.3ds', mimeType: 'application/octet-stream', buffer: file });
  await page.getByRole('heading', { name: 'Hall.3ds' }).waitFor();
  await page.getByText(/6\.0 × 5\.0 × 3\.0 m · 90 m³/).waitFor();
  assert.deepEqual(problems, []);
  await page.close();
});

test('with several sources, each can play its own sound through its own response', async () => {
  const { page, problems } = await open();
  await page.getByRole('button', { name: '+ Add', exact: true }).click(); // a second source
  await page.getByRole('button', { name: 'Listen', exact: true }).click();
  await page.getByLabel('Each source plays its own sound').check();
  assert.deepEqual([await page.getByLabel('Sound of source 1').inputValue(), await page.getByLabel('Sound of source 2').inputValue()], ['speech', 'bursts'], 'a different sound each to begin with');
  await page.getByLabel('Sound of source 2').selectOption('claps');
  let separate = null;
  page.on('request', (r) => { if (r.url().endsWith('/jobs/auralise')) separate = JSON.parse(r.postData() ?? '{}').separate; });
  await page.getByRole('button', { name: 'Calculate response' }).click();
  await page.getByText(/s of response, .* s to compute/).waitFor({ timeout: 120000 });
  assert.equal(separate, true);
  const saved = await savedBytes(page, () => page.getByRole('button', { name: 'Sound as played' }).click());
  assert.equal(saved.bytes.readUInt16LE(22), 2);
  assert.ok(saved.bytes.readUInt32LE(40) / 4 > 44_100 * 5, 'the longest of the two sounds, through the room');
  assert.deepEqual(problems, []);
  await page.close();
});

test('a measured HRTF uploaded as a SOFA file is the head the room is heard through', async () => {
  const { page, problems } = await open();
  await page.getByRole('button', { name: 'Listen', exact: true }).click();
  const head = page.getByRole('group', { name: 'Head' });
  assert.equal(await head.getByRole('button', { name: 'Spherical model' }).getAttribute('aria-pressed'), 'true');
  const { readFile } = await import('node:fs/promises');
  await page.getByLabel('SOFA file').setInputFiles({ name: 'test-head.sofa', mimeType: 'application/octet-stream', buffer: await readFile(new globalThis.URL('./fixtures/test-head.sofa', import.meta.url)) });
  await page.getByText('12 directions measured.').waitFor();
  assert.equal(await head.getByRole('button', { name: /Test head/ }).getAttribute('aria-pressed'), 'true');
  let asked = null;
  page.on('request', (r) => { if (r.url().endsWith('/jobs/auralise')) asked = JSON.parse(r.postData() ?? '{}').hrtfId; });
  await page.getByRole('button', { name: 'Calculate response' }).click();
  await page.getByText(/s of response, .* s to compute/).waitFor({ timeout: 120000 });
  assert.match(asked ?? '', /^[0-9a-f]{32}$/);
  await head.getByRole('button', { name: 'Spherical model' }).click();
  await page.getByRole('button', { name: 'Calculate response' }).waitFor(); // the response heard through the other head is out of date
  // a file that is not SOFA is refused with a message
  await page.getByLabel('SOFA file').setInputFiles({ name: 'bad.sofa', mimeType: 'application/octet-stream', buffer: Buffer.from('not HDF5') });
  await page.getByText(/Could not read 'bad.sofa' as an HRTF/).waitFor();
  assert.ok(problems.every((p) => /400/.test(p)), problems.join(' | '));
  await page.close();
});

test('the Listen tab saves an Ambisonic (AmbiX) impulse response, four channels at first order and sixteen at third', async () => {
  const { page, problems } = await open();
  await page.getByRole('button', { name: 'Listen', exact: true }).click();
  const button = page.getByRole('button', { name: 'Ambisonic response (AmbiX WAV)' });
  const saved = await savedBytes(page, () => button.click());
  assert.equal(saved.name, 'ambisonic-ambix-order1.wav');
  const b = saved.bytes;
  assert.equal(b.toString('latin1', 0, 4), 'RIFF');
  assert.equal(b.readUInt16LE(22), 4, 'W, Y, Z and X');
  assert.ok(b.readUInt32LE(40) / 8 > 22_050 / 4, 'at least a quarter of a second');
  await page.getByRole('group', { name: 'Ambisonic order' }).getByRole('button', { name: '3', exact: true }).click();
  await button.waitFor({ state: 'visible' });
  const third = await savedBytes(page, () => button.click());
  assert.equal(third.name, 'ambisonic-ambix-order3.wav');
  assert.equal(third.bytes.readUInt16LE(22), 16, 'the sixteen channels of third order');
  assert.deepEqual(problems, []);
  await page.close();
});

test('a model that is not closed lists where, and picks each fault out', async () => {
  const { page, problems } = await open();
  // a 6 x 5 x 3 m box with no ceiling
  const v = [[0, 0, 0], [6, 0, 0], [6, 5, 0], [0, 5, 0], [0, 0, 3], [6, 0, 3], [6, 5, 3], [0, 5, 3]];
  const obj = [...v.map((p) => `v ${p.join(' ')}`), 'usemtl Floor', 'f 1 2 3 4', 'usemtl Walls', 'f 1 2 6 5', 'f 2 3 7 6', 'f 3 4 8 7', 'f 4 1 5 8'].join('\n');
  await page.locator('input[type=file][accept*=".obj"]').setInputFiles({ name: 'Open.obj', mimeType: 'text/plain', buffer: Buffer.from(obj) });
  const check = page.getByTestId('geometry-check');
  await check.waitFor();
  assert.match(await check.innerText(), /4 edges with one face/);
  const hole = check.getByRole('button', { name: /^Hole · 4 edges, 22\.00 m · Walls/ });
  await hole.click();
  assert.equal(await hole.getAttribute('aria-pressed'), 'true');
  assert.match(await hole.innerText(), /a face is missing/);
  await hole.click();
  assert.equal(await hole.getAttribute('aria-pressed'), 'false');

  // the hole lies in a plane, so it can be filled: the room then closes and can be simulated
  assert.match(await check.innerText(), /can be closed/);
  await check.getByRole('button', { name: 'Fill hole 1' }).click();
  await check.waitFor({ state: 'detached' });
  assert.equal(await page.getByRole('button', { name: /Run simulation/ }).isDisabled(), false, 'closed now');
  assert.deepEqual(problems, []);
  await page.close();
});

test('a model drawn on one layer is split into its floor, ceiling and walls, each to be given a finish', async () => {
  const { page, problems } = await open();
  // a closed 6 x 5 x 3 m box, every face in one group
  const v = [[0, 0, 0], [6, 0, 0], [6, 5, 0], [0, 5, 0], [0, 0, 3], [6, 0, 3], [6, 5, 3], [0, 5, 3]];
  const obj = [...v.map((p) => `v ${p.join(' ')}`), 'usemtl Room', 'f 1 2 3 4', 'f 5 6 7 8', 'f 1 2 6 5', 'f 2 3 7 6', 'f 3 4 8 7', 'f 4 1 5 8'].join('\n');
  await page.locator('input[type=file][accept*=".obj"]').setInputFiles({ name: 'Box.obj', mimeType: 'text/plain', buffer: Buffer.from(obj) });
  const popup = await surfacesWindow(page);
  await popup.getByText('6 flat surfaces', { exact: false }).waitFor();
  await popup.getByRole('button', { name: 'Split Room' }).click();
  for (const name of ['Room: Floor', 'Room: Ceiling', 'Room: Wall 1', 'Room: Wall 4']) await popup.getByText(name, { exact: true }).filter({ visible: true }).first().waitFor();
  assert.equal(await popup.getByRole('button', { name: /^Split / }).count(), 0, 'each is one flat surface now');
  assert.equal(await page.getByRole('button', { name: /Run simulation/ }).isDisabled(), false, 'still a closed room');

  // a group renamed keeps its finish; one merged into another goes
  popup.once('dialog', (d) => d.accept('Stage floor'));
  await popup.getByRole('button', { name: 'Rename Room: Floor' }).click();
  await popup.getByText('Stage floor', { exact: true }).filter({ visible: true }).first().waitFor();
  popup.once('dialog', (d) => d.accept());
  await popup.getByLabel('Merge Room: Wall 4 into').selectOption('Room: Wall 3');
  await popup.getByText('Room: Wall 4', { exact: true }).filter({ visible: true }).waitFor({ state: 'detached' });
  assert.equal(await page.getByRole('button', { name: /Run simulation/ }).isDisabled(), false, 'still a closed room');
  assert.deepEqual(problems, []);
  await page.close();
});

test("a model's shape is edited in the 3-D view: its ceiling deleted, drawn again corner by corner, and a corner moved", async () => {
  const { page, problems } = await open();
  // a closed 6 x 5 x 3 m box, its ceiling a group of its own
  const v = [[0, 0, 0], [6, 0, 0], [6, 5, 0], [0, 5, 0], [0, 0, 3], [6, 0, 3], [6, 5, 3], [0, 5, 3]];
  const obj = [...v.map((p) => `v ${p.join(' ')}`), 'usemtl Room', 'f 1 2 3 4', 'f 1 2 6 5', 'f 2 3 7 6', 'f 3 4 8 7', 'f 4 1 5 8', 'usemtl Ceiling', 'f 5 6 7 8'].join('\n');
  await page.locator('input[type=file][accept*=".obj"]').setInputFiles({ name: 'Box.obj', mimeType: 'text/plain', buffer: Buffer.from(obj) });
  await page.getByRole('button', { name: 'Edit shape' }).click();
  const panel = page.getByRole('region', { name: 'Edit shape' });
  await panel.waitFor();

  // from above, a click near the middle of the view lands on the ceiling, and picks its corner nearest the click
  await page.getByRole('button', { name: 'Top', exact: true }).click();
  await page.waitForTimeout(400);
  const canvas = page.locator('canvas');
  const box = await canvas.boundingBox();
  await canvas.click({ position: { x: box.width / 2 + 30, y: box.height / 2 + 20 } });
  await panel.getByText('Face of Ceiling').waitFor();
  assert.equal(await panel.getByLabel('Corner z').inputValue(), '3');

  page.once('dialog', (d) => d.accept());
  await panel.getByRole('button', { name: 'Delete its surface' }).click();
  await page.getByTestId('geometry-check').waitFor();
  assert.equal(await page.getByRole('button', { name: /Run simulation/ }).isDisabled(), true, 'open without its ceiling');

  // a face round corners in a line is refused, and the corners picked so far are kept to put right
  for (const x of [0, 3, 6]) {
    await panel.getByLabel('Corner x').fill(String(x));
    await panel.getByLabel('Corner y').fill('0');
    await panel.getByLabel('Corner z').fill('3');
    await panel.getByRole('button', { name: 'Add to new face' }).click();
  }
  await panel.getByRole('button', { name: 'Add face' }).click();
  await page.getByText(/The corners enclose no area/).waitFor();
  assert.equal(await panel.getByText('New face: 3 corners').count(), 1);
  await panel.getByRole('button', { name: 'Clear' }).click();

  // the ceiling drawn again, its corners typed in, in a group of its own
  for (const [x, y] of [[0, 0], [6, 0], [6, 5], [0, 5]]) {
    await panel.getByLabel('Corner x').fill(String(x));
    await panel.getByLabel('Corner y').fill(String(y));
    await panel.getByLabel('Corner z').fill('3');
    await panel.getByRole('button', { name: 'Add to new face' }).click();
  }
  await panel.getByText('New face: 4 corners').waitFor();
  await panel.getByLabel('Group of the new face').selectOption('');
  await panel.getByLabel('Name of the new group').fill('New ceiling');
  await panel.getByRole('button', { name: 'Add face' }).click();
  await page.getByTestId('geometry-check').waitFor({ state: 'detached' });
  assert.equal(await page.getByRole('button', { name: /Run simulation/ }).isDisabled(), false, 'closed again');
  await panel.getByText('New face: 0 corners').waitFor();

  // a corner of the box raised 1 m: it is still closed, and the corner picked is where it went
  await canvas.click({ position: { x: box.width / 2 + 30, y: box.height / 2 + 20 } });
  await panel.getByText('Face of New ceiling').waitFor();
  await panel.getByLabel('Corner z').fill('4');
  await panel.getByRole('button', { name: 'Move corner here' }).click();
  await panel.getByRole('button', { name: 'Move corner here' }).and(page.locator(':disabled')).waitFor();
  assert.equal(await panel.getByLabel('Corner z').inputValue(), '4');
  assert.equal(await page.getByRole('button', { name: /Run simulation/ }).isDisabled(), false, 'still closed');

  // a saved project does the edits again on the model read afresh
  const [saved] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Save project' }).click()]);
  const { readFile } = await import('node:fs/promises');
  const edits = JSON.parse(await readFile(await saved.path(), 'utf8')).model.origin.edits;
  assert.deepEqual(edits.map((e) => e.geometry.action), ['delete', 'face', 'move']);
  await page.getByLabel('Project file').setInputFiles(await saved.path());
  const popup = await surfacesWindow(page);
  await popup.getByText('New ceiling', { exact: true }).filter({ visible: true }).first().waitFor();
  assert.equal(await popup.getByText('Ceiling', { exact: true }).filter({ visible: true }).count(), 0);
  assert.equal(await page.getByRole('button', { name: /Run simulation/ }).isDisabled(), false, 'closed, as it was saved');
  assert.ok(problems.every((p) => /400/.test(p)), problems.join(' | ')); // only the face refused
  await page.close();
});

test('a model that is not closed is repaired: its hole filled and the furniture standing in it made screens, as boxes', async () => {
  const { page, problems } = await open();
  // a 6 x 5 x 3 m box with no ceiling, and a chair (a closed block with a sloping top) standing in it
  const v = [[0, 0, 0], [6, 0, 0], [6, 5, 0], [0, 5, 0], [0, 0, 3], [6, 0, 3], [6, 5, 3], [0, 5, 3],
    [2, 2, 0], [2.5, 2, 0], [2.5, 2.5, 0], [2, 2.5, 0], [2, 2, 0.45], [2.5, 2, 0.45], [2.5, 2.5, 0.9], [2, 2.5, 0.9]];
  const obj = [...v.map((p) => `v ${p.join(' ')}`), 'usemtl Floor', 'f 1 2 3 4', 'usemtl Walls', 'f 1 2 6 5', 'f 2 3 7 6', 'f 3 4 8 7', 'f 4 1 5 8',
    'usemtl Chair', 'f 9 10 11 12', 'f 13 14 15', 'f 13 15 16', 'f 9 10 14 13', 'f 10 11 15 14', 'f 11 12 16 15', 'f 12 9 13 16'].join('\n');
  await page.locator('input[type=file][accept*=".obj"]').setInputFiles({ name: 'Furnished.obj', mimeType: 'text/plain', buffer: Buffer.from(obj) });
  const check = page.getByTestId('geometry-check');
  await check.waitFor();
  await check.getByLabel('Furniture as boxes').check();
  await check.getByRole('button', { name: 'Repair the model' }).click();
  const repairs = page.getByTestId('repairs');
  await repairs.waitFor();
  const said = await repairs.innerText();
  assert.match(said, /now a closed room/);
  assert.match(said, /Filled a hole with "Filled ceiling", 30\.0 m²/);
  assert.match(said, /1 separate surface free-standing screens \(1 solid, as boxes, 0 sheets\)/);
  assert.equal(await check.count(), 0, 'no faults left');
  assert.equal(await page.getByRole('button', { name: /Run simulation/ }).isDisabled(), false);
  const popup = await surfacesWindow(page);
  await popup.getByText('Chair screen', { exact: true }).filter({ visible: true }).first().waitFor();
  await popup.close();
  await repairs.getByRole('button', { name: 'OK' }).click();
  assert.equal(await repairs.count(), 0);
  assert.deepEqual(problems, []);
  await page.close();
});

test('a horn source starts with its coverage and aim sliders at their defaults', async () => {
  const { page } = await open();
  const popup = await sourcesWindow(page);
  await popup.getByRole('button', { name: 'Horn-like' }).click();
  const read = async (label) => popup.getByLabel(label).inputValue();
  assert.deepEqual(
    [await read('Horizontal coverage (−6 dB)'), await read('Vertical coverage (−6 dB)'), await read('Aim, left–right'), await read('Aim, up–down')],
    ['90', '40', '0', '0']);
  await page.close();
});

test('the calculation and the map are in a settings window, not the side panel', async () => {
  const { page } = await open();
  assert.equal(await page.getByLabel('Rays traced').count(), 0, 'not in the side panel');
  await page.getByRole('button', { name: 'Run simulation' }).waitFor();
  await page.getByRole('button', { name: 'Calculate map' }).waitFor(); // beside Run simulation, whether or not the settings window is open
  const popup = await settingsWindow(page);
  await popup.getByLabel('Rays traced').waitFor();
  assert.equal(await popup.getByRole('button', { name: /Calculate map/ }).count(), 0, 'the button stays in the side panel');
  await page.getByRole('button', { name: 'Close settings window' }).click();
  await popup.waitForEvent('close').catch(() => {});
  assert.ok(popup.isClosed());
  await page.close();
});

test('when the browser blocks pop-ups, a window\'s contents are shown in a panel on the page', async () => {
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  await page.addInitScript(() => { window.open = () => null; }); // as a pop-up blocker does
  await page.goto(URL);
  await page.getByText('Engine connected').waitFor({ timeout: 15000 });
  await page.getByRole('button', { name: 'Open sources window' }).click();
  const panel = page.getByRole('dialog', { name: 'Sources and receivers' });
  await panel.getByText('Level and spectrum of source 1').waitFor();
  await panel.getByLabel('Source spectrum').selectOption('ansi-normal');
  await page.getByTestId('source-summary').getByText(/Normal voice/).waitFor();
  await panel.getByRole('button', { name: 'Close', exact: true }).click();
  assert.equal(await page.getByRole('dialog').count(), 0);
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('Quick estimate gives the results at once from the standard equations, and says so', async () => {
  const { page } = await open();
  await page.getByRole('button', { name: 'Quick estimate' }).click();
  await page.getByText(/statistical estimate from the standard equations/).waitFor({ timeout: 5000 });
  assert.match(await page.locator('main').innerText(), /T30 \(500–1K\)\s+\d+\.\d+\s*s/i);
  await page.locator('.recharts-surface').first().waitFor();
  await page.getByRole('button', { name: 'Decay curve' }).click();
  await page.locator('.recharts-line').first().waitFor();
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('the ISO 3382 / STI tab gives each parameter by band with its broadband value, and the STI', async () => {
  const { page } = await open();
  // the quick estimate has no arrival directions, so the lateral and stage-support values are left as a dash
  await page.getByRole('button', { name: 'Quick estimate' }).click();
  await page.getByText(/statistical estimate from the standard equations/).waitFor({ timeout: 5000 });
  await page.getByRole('button', { name: 'ISO 3382 / STI' }).click();
  const sti = page.getByTestId('sti');
  await sti.waitFor();
  assert.match(await sti.innerText(), /Speech Transmission Index\s+0\.\d\d\s+(Bad|Poor|Fair|Good|Excellent)/i);
  // by default the STI's speech is what the sources give at the receiver
  assert.match(await page.getByTestId('iso').innerText(), /the sources' own levels and spectra \(\d+\.\d dB\(A\) of speech there\),\s+male weighting and NC 30 background noise/);
  assert.match(await page.getByTestId('broadband-t30S').innerText(), /^\d\.\d\d$/);
  assert.equal(await page.getByTestId('broadband-c80Db').innerText() !== '–', true);
  assert.equal(await page.getByTestId('broadband-lf80').innerText(), '–');
  assert.equal(await page.getByRole('columnheader', { name: 'Broadband' }).count(), 1);
  assert.equal(await page.locator('[data-testid=iso] tbody tr').count(), 29);
  // the source sound power (flat, 90 dB overall by default) gives the levels; the bass ratio and %ALcons are there; the echo criterion needs a run
  assert.match(await page.getByTestId('broadband-splDb').innerText(), /^\d+\.\d$/);
  assert.match(await page.getByTestId('broadband-splADb').innerText(), /^\d+\.\d$/);
  assert.match(await page.getByTestId('broadband-bassRatio').innerText(), /^\d\.\d\d$/);
  assert.match(await page.getByTestId('alcons').innerText(), /%ALcons \d+\.\d/);
  // STIPA, RaSTI and the SII come with the STI, from the same room
  assert.match(await page.getByTestId('stipa').innerText(), /STIPA 0\.\d\d/);
  assert.match(await page.getByTestId('rasti').innerText(), /RaSTI 0\.\d\d/);
  assert.match(await page.getByTestId('sii').innerText(), /SII [01]\.\d\d/);
  assert.equal(await page.getByTestId('broadband-echoCriterionSpeech').innerText(), '–');
  assert.equal(await page.getByTestId('broadband-directToReverberantDb').innerText(), '–', 'the direct sound on its own needs a run');
  assert.equal(await page.getByTestId('broadband-iaccEarly').innerText(), '–', 'IACC needs the arrival directions of a run');

  // a full run has them all
  await page.getByRole('button', { name: /Run simulation/ }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  await page.getByTestId('sti').waitFor();
  assert.match(await page.getByTestId('broadband-lf80').innerText(), /^0\.\d\d$/);
  assert.match(await page.getByTestId('broadband-stageSupportEarlyDb').innerText(), /^-?\d+\.\d$/);
  // the direct sound alone: G of it, and against all that follows it
  const gDirect = Number(await page.getByTestId('broadband-directStrengthGDb').innerText());
  const ratio = Number(await page.getByTestId('broadband-directToReverberantDb').innerText());
  assert.ok(Number.isFinite(gDirect) && Number.isFinite(ratio));
  assert.ok(gDirect < Number(await page.getByTestId('broadband-strengthGDb').innerText()), 'the direct sound is part of G');
  // IACC of a head facing the source: the early sound, led by the direct sound, more alike at the two ears than the late; BQI is 1 - IACC E3
  const [early, late, bqi] = await Promise.all(['iaccEarly', 'iaccLate', 'binauralQualityIndex'].map(async (k) => Number(await page.getByTestId(`broadband-${k}`).innerText())));
  assert.ok(early > late && late >= 0 && early <= 1, `IACC E3 ${early}, L3 ${late}`);
  assert.ok(Math.abs(bqi - (1 - early)) < 0.011, `BQI ${bqi}`);
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('the plan map is drawn, and a click on it moves the receiver', async () => {
  const { page } = await open();
  await page.getByRole('button', { name: /Calculate map/ }).click(); // the map's own view opens when it is in
  const svg = page.locator('svg[aria-label="Plan map"]');
  await svg.waitFor({ timeout: 60000 });
  const box = await svg.boundingBox();
  await page.mouse.click(box.x + box.width * 0.5, box.y + box.height * 0.5);
  await page.waitForTimeout(500);
  const receiver = await page.locator('input[type=number]').evaluateAll((els) => els.slice(3, 6).map((e) => Number(e.value)));
  assert.notDeepEqual(receiver, [9, 4, 1.2]);
  assert.ok(receiver[0] > 0 && receiver[0] < 12 && receiver[1] > 0 && receiver[1] < 8);
  await page.close();
});

test('a narrow window folds the panel away and nothing runs off the right edge', async () => {
  const { page } = await open({ width: 560, height: 800 });
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  assert.ok(overflow <= 0, `the page is ${overflow}px wider than the window`);
  assert.equal(await page.getByText('Parameters').count(), 0, 'the panel starts folded');
  await page.locator('aside button').first().click();
  await page.getByText('Parameters').waitFor();
  await page.close();
});

test('selecting the source and dragging its X arrow moves it along the length only', async () => {
  const { page } = await open();
  await page.getByText('Show rays').click();
  await page.waitForTimeout(1000);
  const box = await page.locator('canvas').boundingBox();
  const read = () => page.locator('input[type=number]').evaluateAll((els) => els.slice(0, 3).map((e) => Number(e.value)));
  const start = await read();
  // from the starting camera the source sits left of centre, and once clicked its red X arrow points to the right
  // (the room shares the main area with the charts, so the source is at 407, 157 in the 934 x 337 canvas of a 1280 x 800 window)
  await page.mouse.click(box.x + 407, box.y + 157);
  await page.waitForTimeout(600);
  await page.mouse.move(box.x + 431, box.y + 166);
  await page.mouse.down();
  for (let i = 1; i <= 12; i++) await page.mouse.move(box.x + 431 + i * 5, box.y + 166 + i, { steps: 2 });
  await page.mouse.up();
  await page.waitForTimeout(600);
  const end = await read();
  assert.ok(end[0] > start[0] + 1, `x went from ${start[0]} to ${end[0]}`);
  assert.equal(end[1], start[1]);
  assert.equal(end[2], start[2]);
  await page.close();
});

test('a second source can be added, given a level and a delay, and removed', async () => {
  const { page } = await open();
  await page.getByRole('button', { name: '+ Add', exact: true }).click();
  await page.getByRole('button', { name: 'Source 2' }).waitFor();
  assert.equal(await page.getByLabel('Source delay in ms').count(), 0, 'the delay is set in the sources window');
  const popup = await sourcesWindow(page);
  await popup.getByLabel('Source delay in ms').fill('20');
  assert.equal(await popup.getByLabel('Source delay in ms').inputValue(), '20');
  await page.getByTestId('source-summary').getByText(/20 ms delay/).waitFor();
  // its position is its own: source 2's fields, not the first source's
  assert.equal(await page.getByLabel('Source 2 y').count(), 1);
  await page.getByRole('button', { name: 'Run simulation' }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  await page.getByRole('button', { name: 'Remove this source' }).click();
  assert.equal(await page.getByRole('button', { name: 'Source 2' }).count(), 0);
  await page.close();
});

test('a line array has its own controls and the simulation runs with it', async () => {
  const { page } = await open();
  const popup = await sourcesWindow(page);
  await popup.getByRole('button', { name: 'Line array' }).click();
  await popup.getByLabel('Elements', { exact: true }).fill('12');
  await popup.selectOption('select[aria-label="Array shading"]', 'chebyshev');
  await popup.getByLabel('Side lobes held below').waitFor();
  await page.getByRole('button', { name: 'Run simulation' }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  await page.close();
});

test('a measured pattern is uploaded and shown as a polar plot; a bad file is explained', async () => {
  const { page } = await open();
  assert.equal(await page.locator('input[type=file][accept*=csv]').count(), 0, 'the pattern is chosen in the sources window');
  const popup = await sourcesWindow(page);
  await popup.getByRole('button', { name: 'Loudspeaker' }).click();
  await page.waitForTimeout(1200); // the rays are asked for again a moment after the change: choosing a pattern before uploading one must not raise an error
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  const lines = ['azimuth_deg,elevation_deg,level_db'];
  for (let el = -90; el <= 90; el += 30) for (let az = 0; az < 360; az += 30) lines.push(`${az},${el},${-12 * (1 - Math.cos(az * Math.PI / 180) * Math.cos(el * Math.PI / 180))}`);
  const input = popup.locator('input[type=file][accept*=csv]');
  await input.setInputFiles({ name: 'junk.csv', mimeType: 'text/csv', buffer: Buffer.from('not a table') });
  await popup.getByRole('alert').filter({ hasText: 'junk.csv' }).waitFor();
  await input.setInputFiles({ name: 'lobe.csv', mimeType: 'text/csv', buffer: Buffer.from(lines.join('\n')) });
  await popup.locator('svg[aria-label^="Polar pattern of lobe.csv"]').waitFor();
  assert.ok((await input.getAttribute('accept')).includes('.cf2'), 'a CLF distribution file can be picked');

  // All files… offers every file, read by what it holds: a CF2 is explained, and a pattern with an odd extension is read
  const any = popup.getByLabel('Pattern file of any type');
  assert.equal(await any.getAttribute('accept'), null);
  await any.setInputFiles({ name: 'Speaker.CF2', mimeType: 'application/octet-stream', buffer: Buffer.from([0x43, 0x46, 0x32, 0, 1, 0, 0, 0, 255, 127, 0, 16, 2, 0, 0, 5]) });
  await popup.getByRole('alert').filter({ hasText: 'CLF Viewer' }).waitFor();
  await any.setInputFiles({ name: 'lobe2.pattern', mimeType: 'application/octet-stream', buffer: Buffer.from(lines.join('\n')) });
  await popup.locator('svg[aria-label^="Polar pattern of lobe2.pattern"]').waitFor();
  await page.close();
});

async function mapOf(mode) {
  const { page } = await open();
  const popup = await settingsWindow(page);
  await popup.getByRole('button', { name: mode, exact: true }).click();
  await page.getByRole('button', { name: /Calculate map/ }).click(); // the map's own view opens when it is in
  page.settings = popup;
  return page;
}

test('a Common Loudspeaker Format text file is read as a source pattern, named by its model', async () => {
  const { page } = await open();
  const popup = await sourcesWindow(page);
  await popup.getByRole('button', { name: 'Loudspeaker' }).click();
  const tab = String.fromCharCode(9);
  const lines = ['<CLF1>', '<VERSION>' + tab + '1', '<MODELNAME>' + tab + 'Horn 9050', '<MANUFACTURER>' + tab + 'Test Audio', '<MINBAND>' + tab + '125', '<MAXBAND>' + tab + '8000',
    '<RADIATION>' + tab + '<Fullsphere>', '<BALLOON-SYMMETRY>' + tab + '<full>', '<BALLOON-ARC-ORDER>' + tab + '<default>', '<BALLOON-REF>' + tab + '<relative>' + tab + '<SIGN>' + tab + '<reversed>'];
  for (const band of [125, 250, 500, 1000, 2000, 4000, 8000]) {
    lines.push('<BAND>' + tab + band);
    for (let arc = 0; arc <= 90; arc += 10) lines.push(Array.from({ length: 19 }, (_, c) => (c * 10 * (0.1 + 0.1 * Math.sin(arc * Math.PI / 180) ** 2)).toFixed(2)).join(tab));
  }
  lines.push('<CLF1END>');
  await popup.locator('input[type=file][accept*=csv]').setInputFiles({ name: 'horn.tab', mimeType: 'text/plain', buffer: Buffer.from(lines.join('\r\n')) });
  await popup.locator('svg[aria-label^="Polar pattern of Test Audio Horn 9050"]').waitFor();
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('a section is drawn in the cutting plane with the room outline', async () => {
  const page = await mapOf('Section');
  await page.locator('svg[aria-label="Section map"]').waitFor({ timeout: 90000 });
  assert.ok(await page.locator('svg[aria-label="Section map"] line').count() >= 4, 'the room outline');
  assert.match(await page.getByTestId('map-stats').innerText(), /min .* mean .* max/);
  await page.close();
});

test('a height matrix shows one height at a time', async () => {
  const page = await mapOf('Height matrix');
  await page.locator('svg[aria-label="Plan map"]').waitFor({ timeout: 90000 });
  const heights = page.getByRole('group', { name: 'Height shown' }).getByRole('button');
  assert.equal(await heights.count(), 2);
  await heights.nth(1).click();
  assert.equal(await heights.nth(1).getAttribute('aria-pressed'), 'true');
  await page.close();
});

test('a map follows the octave band chosen in the sidebar without being calculated again', async () => {
  const page = await mapOf('Plan');
  await page.locator('svg[aria-label="Plan map"]').waitFor({ timeout: 90000 });
  assert.equal(await page.settings.locator('select[aria-label="Map band"]').count(), 0, 'the map has no band of its own');
  const first = await page.getByTestId('map-stats').innerText();
  await page.getByRole('button', { name: '125 Hz', exact: true }).click();
  const second = await page.getByTestId('map-stats').innerText();
  assert.match(second, /125 Hz/);
  assert.notEqual(first, second);
  await page.close();
});

test('the map shows any ISO 3382 parameter or the STI, in an octave band or broadband, without being calculated again', async () => {
  const page = await mapOf('Plan');
  await page.locator('svg[aria-label="Plan map"]').waitFor({ timeout: 90000 });
  let runs = 0;
  page.on('request', (r) => { if (r.url().includes('/jobs/map')) runs++; });
  const stats = () => page.getByTestId('map-stats').innerText();
  assert.match(await stats(), /^C80 · 1k Hz/);
  await page.getByLabel('Map parameter').selectOption('T20');
  assert.match(await stats(), /^T20 · 1k Hz/);
  await page.getByRole('group', { name: 'Map band' }).getByRole('button', { name: '250', exact: true }).click();
  assert.match(await stats(), /^T20 · 250 Hz/);
  await page.getByRole('button', { name: 'Broadband', exact: true }).click();
  assert.match(await stats(), /^T20 · broadband · min \d/);
  await page.getByLabel('Map parameter').selectOption('LF');
  assert.match(await stats(), /^LF · broadband · min 0\./);
  await page.getByLabel('Map parameter').selectOption('SPL');
  assert.match(await stats(), /^SPL · broadband · min \d+\.\d+ · mean \d+\.\d+ · max \d+\.\d+ dB$/);
  await page.getByLabel('Map parameter').selectOption('Alcons');
  assert.match(await stats(), /^Alcons · broadband/);
  await page.getByLabel('Map parameter').selectOption('STI');
  assert.match(await stats(), /^STI · broadband · min 0\.\d+ · mean 0\.\d+ · max 0\.\d+/);
  assert.equal(await page.getByRole('group', { name: 'Map band' }).getByRole('button', { name: '250', exact: true }).isDisabled(), true, 'the STI has no octave bands');
  await page.getByLabel('Map parameter').selectOption('SII');
  assert.match(await stats(), /^SII · broadband · min [01]\.\d+ · mean [01]\.\d+ · max [01]\.\d+/);
  assert.equal(runs, 0, 'the choice is made on the map already calculated');
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('a map can be given its own scale and contour lines, kept for each parameter', async () => {
  const page = await mapOf('Plan');
  await page.locator('svg[aria-label="Plan map"]').waitFor({ timeout: 90000 });
  const scale = page.getByTestId('map-scale');
  const ends = async () => (await scale.locator(':scope > span').allInnerTexts()).slice(0, 2);
  const bottom = page.getByLabel('Scale bottom'), top = page.getByLabel('Scale top'), step = page.getByLabel('Contour step');
  assert.match(await bottom.getAttribute('placeholder'), /^-?\d+\.\d\d$/, 'the box shows the scale the map has of itself');
  assert.equal(await page.getByTestId('map-contours').count(), 0, 'no lines to begin with');

  await bottom.fill('-5'); await top.fill('10');
  assert.deepEqual(await ends(), ['10.00', '-5.00']);
  await step.fill('0.001');
  await page.getByText(/a step of 0\.001 gives over 50 lines/).waitFor();
  assert.equal(await page.getByTestId('map-contours').count(), 0, 'too fine a step draws none');
  await step.fill('1');
  await page.getByTestId('map-contours').waitFor();
  assert.ok((await page.getByTestId('map-contours').getAttribute('d')).split('M').length > 3, 'some lines are drawn');
  assert.match(await scale.innerText(), /lines every 1/);
  const labels = await page.getByTestId('contour-label').allTextContents();
  assert.ok(labels.length > 0 && labels.every((t) => /^\d+$/.test(t)), `each line's level, in whole dB (${labels})`);
  assert.equal(new Set(labels).size, labels.length, 'each level once');

  // another parameter has a scale of its own; coming back finds this one as it was set
  await page.getByLabel('Map parameter').selectOption('T20');
  assert.equal(await bottom.inputValue(), '');
  assert.equal(await page.getByTestId('map-contours').count(), 0);
  await page.getByLabel('Map parameter').selectOption('C80');
  assert.equal(await bottom.inputValue(), '-5');
  assert.deepEqual(await ends(), ['10.00', '-5.00']);

  // a bottom above the top is pointed out, and the map's own scale used
  await bottom.fill('20');
  await page.getByText('The bottom must be below the top').waitFor();
  assert.notDeepEqual(await ends(), ['10.00', '20.00']);
  await page.getByRole('group', { name: 'Map scale' }).getByRole('button', { name: 'Auto' }).click();
  assert.equal(await top.inputValue(), '');
  assert.equal(await page.getByTestId('map-contours').count(), 0);
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('Overall in the octave band panel gives the overall decay curve, impulse response and map, and a band chip takes it back', async () => {
  const { page } = await open();
  await page.getByRole('button', { name: 'Quick estimate' }).click();
  await page.getByText(/statistical estimate from the standard equations/).waitFor({ timeout: 5000 });
  const overall = page.getByRole('button', { name: 'Overall', exact: true });
  assert.equal(await overall.getAttribute('aria-pressed'), 'false');
  await page.getByRole('button', { name: 'Decay curve' }).click();
  await page.locator('.recharts-line').first().waitFor();
  assert.equal(await page.locator('.recharts-line').count(), 8, 'the eight bands');
  await overall.click();
  assert.equal(await overall.getAttribute('aria-pressed'), 'true');
  await page.waitForFunction(() => document.querySelectorAll('.recharts-line').length === 9, null, { timeout: 5000 }); // the eight bands and the overall curve
  await page.getByRole('button', { name: 'Impulse response' }).click();
  await page.locator('.recharts-line').first().waitFor();
  assert.equal(await page.locator('main .text-rose-300').count(), 0);

  // the map follows it
  await page.getByRole('button', { name: /Calculate map/ }).click();
  await page.getByTestId('map-stats').waitFor({ timeout: 90000 });
  assert.match(await page.getByTestId('map-stats').innerText(), /broadband/);
  assert.equal(await page.getByRole('button', { name: 'Broadband', exact: true }).getAttribute('aria-pressed'), 'true');
  await page.getByRole('group', { name: 'Map band' }).getByRole('button', { name: '500', exact: true }).click();
  assert.match(await page.getByTestId('map-stats').innerText(), /500 Hz/);
  assert.equal(await overall.getAttribute('aria-pressed'), 'false', 'a band takes the overall choice back');
  await page.close();
});

test('the source sound power is set in the sources window and gives the sound pressure level', async () => {
  const { page } = await open();
  const level = async () => {
    await page.getByRole('button', { name: 'Quick estimate' }).click();
    await page.getByRole('button', { name: 'ISO 3382 / STI' }).click();
    return Number(await page.getByTestId('broadband-splDb').innerText());
  };
  const before = await level();
  const popup = await sourcesWindow(page);
  const spectrum = popup.getByLabel('Source spectrum');
  await spectrum.selectOption('speech'); // the STI's male speech shape at the same overall level
  assert.equal(await popup.getByLabel('Lw at 63 Hz').inputValue() === await popup.getByLabel('Lw at 1k Hz').inputValue(), false, 'no longer flat');
  const speech = await level();
  assert.ok(Math.abs(speech - before) < 3, `the overall power is kept when a noise or STI shape is chosen, so the level is much the same (${before} then ${speech})`);
  await popup.getByLabel('Lw at 1k Hz').fill('110');
  assert.equal(await spectrum.inputValue(), 'custom');
  assert.ok((await level()) > speech + 3, 'a louder 1 kHz band raises the level');
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('a standard speech spectrum sets the voice level, and every source has its own spectrum', async () => {
  const { page } = await open();
  const popup = await sourcesWindow(page);
  await popup.getByLabel('Source spectrum').selectOption('ansi-normal');
  // ANSI/ASA S3.5 normal effort, 1 m in front of the mouth: its octave bands add to 62.9 dB, so an omnidirectional source has Lw = 62.9 + 10 lg(4 pi) = 73.9 dB
  const voiceLw = Number((await popup.getByTestId('power-summary').innerText()).match(/Lw (\d+\.\d)/)[1]);
  assert.ok(voiceLw > 73.5 && voiceLw < 74.3, `Lw ${voiceLw}`);
  assert.equal(await popup.getByLabel('Lw at 250 Hz').inputValue(), String(Math.round((57.2 + 10 * Math.log10(4 * Math.PI)) * 10) / 10), 'the standard\'s 57.2 dB at 250 Hz');
  const normal1k = Number(await popup.getByLabel('Lw at 1k Hz').inputValue());
  await popup.getByLabel('Source spectrum').selectOption('ansi-shout');
  assert.ok(Number(await popup.getByLabel('Lw at 1k Hz').inputValue()) > normal1k + 20, 'shouting is much louder at 1 kHz');
  await popup.getByLabel('Source spectrum').selectOption('ansi-normal');

  // a second source starts from the first one's spectrum, but is its own: changing it leaves the first alone
  await page.getByRole('button', { name: '+ Add', exact: true }).click();
  await popup.getByText('Level and spectrum of source 2').waitFor();
  assert.equal(await popup.getByLabel('Source spectrum').inputValue(), 'ansi-normal');
  await popup.getByLabel('Source spectrum').selectOption('flat');
  await page.getByRole('button', { name: 'Source 1' }).click();
  await popup.getByText('Level and spectrum of source 1').waitFor();
  assert.equal(await popup.getByLabel('Source spectrum').inputValue(), 'ansi-normal');
  assert.match(await page.getByTestId('source-summary').innerText(), /Normal voice/);

  // both are in the exports
  await page.getByRole('button', { name: 'Run simulation' }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  const { readFile } = await import('node:fs/promises');
  const [csv] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Results CSV' }).click()]);
  const rows = (await readFile(await csv.path(), 'utf8')).trim().split('\r\n');
  const lw = rows.filter((r) => r.includes('Sound power level Lw'));
  assert.ok(lw.some((r) => r.startsWith('Source 1,') && r.includes('Normal voice')), 'source 1 with its spectrum');
  assert.ok(lw.some((r) => r.startsWith('Source 2,') && r.includes('Flat')), 'source 2 with its own');
  assert.ok(lw.some((r) => r.startsWith('All sources,')), 'and the two added');

  // the Excel workbook: a sheet each for the results, the sources and the setup, with numbers as numbers
  const { unzipSync, strFromU8 } = await import('fflate');
  const [xlsx] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Results XLSX' }).click()]);
  assert.equal(xlsx.suggestedFilename(), 'results.xlsx');
  const parts = unzipSync(new Uint8Array(await readFile(await xlsx.path())));
  const text = (name) => strFromU8(parts[name]);
  assert.match(text('xl/workbook.xml'), /name="Results"[\s\S]*name="Sources"[\s\S]*name="Setup"[\s\S]*name="Decay"[\s\S]*name="Impulse"/);
  assert.match(text('xl/worksheets/sheet1.xml'), /T30/);
  assert.match(text('xl/worksheets/sheet1.xml'), /<c r="G2"[^>]*><v>-?\d/, 'a band value is a number cell, not text');
  assert.match(text('xl/worksheets/sheet2.xml'), /Normal voice[\s\S]*Flat[\s\S]*added as energies/);
  assert.match(text('xl/worksheets/sheet3.xml'), /Calculation/);
  assert.match(text('xl/worksheets/sheet4.xml'), /Receiver 1 1k Hz \(dB\)/, 'the decay curves');
  assert.match(text('xl/worksheets/sheet5.xml'), /Receiver 1 overall \(dB\)/, 'the impulse responses');
  const [report] = await Promise.all([page.waitForEvent('popup'), page.getByRole('button', { name: 'Report', exact: true }).click()]);
  await report.getByText(/Sources \(sound power level/).waitFor();
  assert.match(await report.locator('body').innerText(), /Source 2[\s\S]*Flat/);
  await report.close();
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('an export describes the run that gave the results, and the header says when the inputs have changed since', async () => {
  const { readFile } = await import('node:fs/promises');
  const { page } = await open();
  await page.getByRole('button', { name: 'Run simulation' }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  assert.equal(await page.getByTestId('stale').count(), 0);
  const popup = await sourcesWindow(page);
  await popup.getByLabel('Source spectrum').selectOption('ansi-shout'); // after the run
  await page.getByTestId('source-summary').getByText(/Shout voice/).waitFor();
  await page.getByTestId('stale').waitFor();
  const [csv] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Results CSV' }).click()]);
  const rows = (await readFile(await csv.path(), 'utf8')).trim().split('\r\n');
  const lw = rows.find((r) => r.startsWith('Source 1,') && r.includes('Sound power level Lw'));
  assert.match(lw, /Flat/, 'the spectrum the run was made with');
  await page.close();
});

test('a project saved before the sources had their own power still loads', async () => {
  const { readFile } = await import('node:fs/promises');
  const { page } = await open();
  await page.getByRole('button', { name: '+ Add', exact: true }).click();
  const [saved] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Save project' }).click()]);
  const project = JSON.parse(await readFile(await saved.path(), 'utf8'));
  // as such a project was: no power anywhere, the second source 6 dB below the first
  delete project.settings.lwBandsDb; delete project.settings.lwShape; delete project.settings.stiSignal;
  for (const e of project.extras) { delete e.lwBandsDb; delete e.lwShape; e.gainDb = -6; }
  await page.getByRole('button', { name: 'Remove this source' }).click();
  await page.getByLabel('Project file').setInputFiles({ name: 'old.room3d.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(project)) });
  await page.getByRole('button', { name: 'Source 2' }).click();
  // the default is 90 dB flat, so 6 dB below it is 84 dB
  assert.match(await page.getByTestId('source-summary').innerText(), /Lw 84\.0 dB/);
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  // its STI is still a talker's, as it was when it was saved
  await page.getByRole('button', { name: 'Quick estimate' }).click();
  await page.getByRole('button', { name: 'ISO 3382 / STI' }).click();
  assert.match(await page.getByTestId('iso').innerText(), /a talker at 60 dBA at 1 m/);
  await page.close();
});

test('a source can be muted or soloed, and with every source muted nothing is calculated', async () => {
  const { page } = await open();
  // the estimate's level once the page shows it: the click only asks for it, and under load the old figure can still be up when it is read
  const level = async () => {
    const [response] = await Promise.all([
      page.waitForResponse((r) => r.url().endsWith('/api/statistical') && r.request().method() === 'POST'),
      page.getByRole('button', { name: 'Quick estimate' }).click(),
    ]);
    const want = (await response.json()).broadband.splDb;
    await page.getByRole('button', { name: 'ISO 3382 / STI' }).click();
    const cell = page.getByTestId('broadband-splDb');
    for (let i = 0; i < 100 && !(Math.abs(Number(await cell.innerText()) - want) <= 0.06); i++) await page.waitForTimeout(50);
    return Number(await cell.innerText());
  };
  const one = await level();
  await page.getByRole('button', { name: '+ Add', exact: true }).click();
  const two = await level();
  assert.ok(two > one + 2, `a second source as loud adds about 3 dB (${one} then ${two})`);
  await page.getByRole('button', { name: 'Mute this source' }).click(); // source 2
  assert.match(await page.getByTestId('source-summary').innerText(), /muted/);
  assert.ok(Math.abs((await level()) - one) < 0.2, 'muted, it is left out');
  await page.getByRole('button', { name: 'Source 1', exact: true }).click();
  await page.getByRole('button', { name: 'Mute this source' }).click();
  await page.getByRole('alert').filter({ hasText: 'Every source is muted' }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Run simulation' }).isDisabled(), true);
  // a solo is heard even when muted, and leaves out the rest
  await page.getByRole('button', { name: 'Solo this source' }).click();
  assert.ok(Math.abs((await level()) - one) < 0.2, 'source 1 alone');
  assert.equal(await page.getByRole('button', { name: 'Run simulation' }).isDisabled(), false);
  await page.close();
});

test('a level can be entered at 1 m in front or as sensitivity and power, and a talker\'s pattern turns it into its sound power', async () => {
  const { page } = await open();
  const popup = await sourcesWindow(page);
  const summary = popup.getByTestId('power-summary');
  await popup.getByRole('button', { name: 'Talker' }).click();
  await popup.getByRole('button', { name: 'Level at 1 m' }).click();
  await popup.getByLabel('Source spectrum').selectOption('ansi-normal');
  assert.equal(await popup.getByLabel('Lp at 1 m at 1k Hz').inputValue(), '53.5', 'the standard\'s level 1 m in front at 1 kHz');
  // with the measured talker's directivity index of 1.4 dB at 1 kHz, Lw = 53.5 + 11.0 - 1.4 = 63.1 dB, within 0.1 dB of ODEON's sound power for normal speech
  await popup.waitForFunction(() => !document.querySelector('[data-testid="power-summary"]').textContent.includes('working out'));
  await popup.getByRole('button', { name: 'Sound power', exact: true }).click();
  assert.ok(Math.abs(Number(await popup.getByLabel('Lw at 1k Hz').inputValue()) - 63.0) <= 0.15, 'the talker\'s sound power at 1 kHz');
  // a loudspeaker: 96 dB at 1 W and 1 m with 100 W is 116 dB at 1 m on axis
  await popup.getByRole('button', { name: 'Sensitivity + power' }).click();
  await popup.getByLabel('Sensitivity (dB SPL, 1 W, 1 m)').fill('96');
  await popup.getByLabel('Power fed to it (W)').fill('100');
  await popup.getByText(/level 1 m in front, on axis 116\.0 dB/).waitFor();
  assert.match(await page.getByTestId('source-summary').innerText(), /set by sensitivity and power/);
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('the spectra include the IEC voices, measured and Pearsons speech, and ISO 3382-3 speech, and a spectrum of my own can be saved and picked again', async () => {
  const { page } = await open();
  const popup = await sourcesWindow(page);
  const spectrum = popup.getByLabel('Source spectrum');
  await spectrum.selectOption('iec-female'); // the IEC female spectrum at ISO 9921's normal effort, 60 dBA 1 m in front
  assert.match(await page.getByTestId('source-summary').innerText(), /IEC female, normal/);
  // measured sound power of normal speech (Hopkins et al. 2024): 63.4 dB at 1 kHz for the unisex average
  await spectrum.selectOption('hopkins-unisex');
  assert.equal(await popup.getByLabel('Lw at 1k Hz').inputValue(), '63.4');
  // Pearsons' male normal voice is 50.8 dB at 1 kHz 1 m in front: an omnidirectional source needs 50.8 + 11.0 = 61.8 dB
  await spectrum.selectOption('pearsons-male-normal');
  assert.equal(await popup.getByLabel('Lw at 1k Hz').inputValue(), '61.8');
  // ISO 3382-3's level 1 m in front of a talker is 53.5 dB at 1 kHz: an omnidirectional source needs 53.5 + 11.0 = 64.5 dB
  await spectrum.selectOption('iso3382-3-axis');
  assert.equal(await popup.getByLabel('Lw at 1k Hz').inputValue(), '64.5');
  await spectrum.selectOption('iso3382-3'); // a sound power the standard gives: 63.0 dB at 1 kHz
  assert.equal(await popup.getByLabel('Lw at 1k Hz').inputValue(), '63');
  await popup.getByLabel('Lw at 2k Hz').fill('70');
  assert.equal(await spectrum.inputValue(), 'custom');
  await popup.getByRole('button', { name: 'Save as my spectrum…' }).click();
  await popup.getByLabel('Name of my spectrum').fill('Loud fan');
  await popup.getByRole('button', { name: 'Save', exact: true }).click();
  assert.equal(await spectrum.locator('option:checked').innerText(), 'Loud fan');
  await spectrum.selectOption('flat');
  await spectrum.selectOption({ label: 'Loud fan' });
  assert.equal(await popup.getByLabel('Lw at 2k Hz').inputValue(), '70', 'its levels come back');
  await page.close();
});

test('the STI can be for a talker instead of the sources, and says which', async () => {
  const { page } = await open();
  const popup = await settingsWindow(page);
  await popup.getByRole('button', { name: 'A talker' }).click();
  await popup.getByLabel('Speech level at 1 m').waitFor();
  await page.getByRole('button', { name: 'Quick estimate' }).click();
  await page.getByRole('button', { name: 'ISO 3382 / STI' }).click();
  assert.match(await page.getByTestId('iso').innerText(), /a talker at 60 dBA at 1 m/);
  await popup.getByRole('button', { name: "The sources' own levels" }).click();
  assert.equal(await popup.getByLabel('Speech level at 1 m').count(), 0);
  await page.getByRole('button', { name: 'Quick estimate' }).click();
  await page.getByTestId('iso').getByText(/the sources' own levels/).waitFor(); // once the new estimate is in
  await page.close();
});

test('the surfaces can be hidden in either 3-D room, and the map view starts without them', async () => {
  const page = await mapOf('Plan');
  await page.locator('svg[aria-label="Plan map"]').waitFor({ timeout: 90000 });
  const toggle = page.getByRole('button', { name: 'Show surfaces' });
  assert.equal(await toggle.getAttribute('aria-pressed'), 'false', 'the map view starts with the surfaces hidden');
  await toggle.click();
  assert.equal(await toggle.getAttribute('aria-pressed'), 'true');
  await page.getByRole('tab', { name: '3-D and results', exact: true }).click();
  assert.equal(await toggle.getAttribute('aria-pressed'), 'true', 'the working room shows them');
  await toggle.click();
  assert.equal(await toggle.getAttribute('aria-pressed'), 'false');
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('a map above a surface is drawn for the chosen surface', async () => {
  const page = await mapOf('Above a surface');
  await page.locator('svg[aria-label="Plan map"]').waitFor({ timeout: 90000 });
  assert.equal(await page.settings.getByLabel('Surface to map above').inputValue(), 'Floor');
  assert.match(await page.getByTestId('map-stats').innerText(), /min .* mean .* max/);
  await page.close();
});

test('a project is saved to a file and loaded back, with its model and settings', async () => {
  const { page } = await open();
  const heading = page.getByRole('heading', { level: 1 });
  await page.getByLabel('Length', { exact: true }).fill('20');
  await page.getByText(/20 × 8 × 4 m/).waitFor();
  const [roomDownload] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Save project' }).click()]);
  assert.match(roomDownload.suggestedFilename(), /\.room3d\.json$/);
  const roomFile = await roomDownload.path();
  await page.getByLabel('Length', { exact: true }).fill('30');
  await page.getByText(/30 × 8 × 4 m/).waitFor();
  await page.getByLabel('Project file').setInputFiles(roomFile);
  await page.getByText(/20 × 8 × 4 m/).waitFor();

  // an imported model travels inside the file, so it comes back even though the engine is asked to read it afresh
  await page.locator('input[type=file][accept*=".dae"]').setInputFiles({ name: 'Sketch.dae', mimeType: 'model/vnd.collada+xml', buffer: Buffer.from(dae) });
  await heading.filter({ hasText: 'Sketch.dae' }).waitFor();
  const [modelDownload] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Save project' }).click()]);
  assert.match(modelDownload.suggestedFilename(), /^Sketch\.room3d\.json$/);
  const modelFile = await modelDownload.path();
  await page.getByRole('button', { name: 'Use box room' }).click();
  await heading.filter({ hasText: 'Room Acoustics' }).waitFor();
  await page.getByLabel('Project file').setInputFiles(modelFile);
  await heading.filter({ hasText: 'Sketch.dae' }).waitFor();
  await page.getByText(/10\.0 × 6\.0 × 3\.0 m · 180 m³/).waitFor();

  await page.getByLabel('Project file').setInputFiles({ name: 'junk.json', mimeType: 'application/json', buffer: Buffer.from('{"hello":1}') });
  await page.getByRole('alert').or(page.getByText(/not a Room Acoustics 3D project/)).first().waitFor();
  await page.close();
});

test('a measured pattern comes back from a saved project', async () => {
  const { page } = await open();
  const popup = await sourcesWindow(page);
  await popup.getByRole('button', { name: 'Loudspeaker' }).click();
  const lines = ['azimuth_deg,elevation_deg,level_db'];
  for (let el = -90; el <= 90; el += 30) for (let az = 0; az < 360; az += 30) lines.push(`${az},${el},${-12 * (1 - Math.cos(az * Math.PI / 180) * Math.cos(el * Math.PI / 180))}`);
  await popup.locator('input[type=file][accept*=csv]').setInputFiles({ name: 'lobe.csv', mimeType: 'text/csv', buffer: Buffer.from(lines.join(String.fromCharCode(10))) });
  await popup.locator('svg[aria-label^="Polar pattern of lobe.csv"]').waitFor();
  const [saved] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Save project' }).click()]);
  const file = await saved.path();
  await popup.getByRole('button', { name: 'Horn-like' }).click();
  await popup.locator('svg[aria-label^="Polar pattern of lobe.csv"]').waitFor({ state: 'detached' });
  await page.getByLabel('Project file').setInputFiles(file);
  await popup.locator('svg[aria-label^="Polar pattern of lobe.csv"]').waitFor();
  await page.waitForTimeout(1200); // the rays are asked for again with the pattern's new id: that must not raise an error
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('the listener has a head to turn, and loudspeakers are an option with a speaker angle', async () => {
  const { page } = await open();
  await page.getByRole('button', { name: 'Listen', exact: true }).click();
  await page.getByRole('button', { name: 'Loudspeakers' }).click();
  assert.equal(await page.getByLabel('Speaker angle').inputValue(), '30');
  await page.getByText(/crosstalk cancellation/).waitFor();
  await page.getByRole('button', { name: 'Headphones' }).click();
  assert.equal(await page.getByLabel('Speaker angle').count(), 0);
  await page.close();
});

test('further receivers get their own results side by side, and come back from a saved project', async () => {
  const { page } = await open();
  await page.getByRole('button', { name: '+ Add receiver' }).click();
  assert.equal(await page.getByTestId('extra-receiver').count(), 1);
  await page.getByRole('button', { name: /Run simulation/ }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  await page.getByRole('button', { name: 'Receivers', exact: true }).click();
  await page.getByTestId('receivers').waitFor();
  const first = Number(await page.getByTestId('receiver-0-c80Db').innerText());
  const second = Number(await page.getByTestId('receiver-1-c80Db').innerText());
  assert.ok(Number.isFinite(first) && Number.isFinite(second), 'both receivers have a C80');
  assert.ok(Number(await page.getByTestId('receiver-1-sti').innerText()) > 0, 'the second receiver has an STI');
  // the octave-band table is for the receiver picked
  await page.getByRole('button', { name: 'ISO 3382 / STI' }).click();
  await page.getByRole('group', { name: 'Receiver shown' }).getByRole('button', { name: 'Receiver 2' }).click();
  await page.getByTestId('iso').waitFor();
  // the receiver picked stays picked on the charts, and the decay curve and impulse response are its own
  const decayAt = async () => page.locator('.recharts-line-curve').first().getAttribute('d');
  await page.getByRole('button', { name: 'Decay curve' }).click();
  await page.locator('.recharts-line-curve').first().waitFor();
  assert.equal(await page.getByRole('button', { name: 'Receiver 2', exact: true }).getAttribute('aria-pressed'), 'true');
  const decayTwo = await decayAt();
  await page.getByRole('button', { name: 'Receiver 1', exact: true }).click();
  await page.waitForTimeout(400);
  assert.notEqual(await decayAt(), decayTwo, 'receiver 1 has its own decay curve');
  await page.getByRole('button', { name: 'Impulse response' }).click();
  await page.locator('.recharts-line-curve').first().waitFor();
  await page.getByRole('button', { name: 'ISO 3382 / STI' }).click();
  await page.getByRole('group', { name: 'Receiver shown' }).getByRole('button', { name: 'Receiver 2' }).click();

  const [saved] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Save project' }).click()]);
  const file = await saved.path();
  await page.getByRole('button', { name: 'Remove receiver 2' }).click();
  assert.equal(await page.getByTestId('extra-receiver').count(), 0);
  await page.getByLabel('Project file').setInputFiles(file);
  await page.getByTestId('extra-receiver').waitFor();
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('a line of four receivers from one source gives the ISO 3382-3 open-plan office results', async () => {
  const { page, problems } = await open();
  // before a full run there is nothing to show; then the receivers are the positions and each single number is worked out
  for (let i = 0; i < 3; i++) await page.getByRole('button', { name: '+ Add receiver' }).click();
  await page.getByRole('button', { name: /Run simulation/ }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  await page.getByRole('button', { name: 'Open-plan office' }).click();
  await page.getByTestId('open-plan').waitFor();
  assert.equal(await page.getByTestId('open-plan-why').count(), 0, 'four receivers from one source are enough');
  for (const key of ['spatialDecayDb', 'speechAt4mDbA', 'nearestSti', 'backgroundDbA'])
    assert.ok(Number.isFinite(parseFloat(await page.getByTestId(`open-plan-${key}`).innerText())), `${key} is worked out`);
  assert.equal(await page.getByRole('rowheader', { name: /^Receiver \d$/ }).count(), 4);
  // the receivers added beside the first are all about 7 m away, too close together to measure a decay from, and it says so
  await page.getByTestId('open-plan-narrow').waitFor();
  assert.deepEqual(problems, []);
  await page.close();
});

test('Receivers in a line puts eight receivers in a line away from the source, wide enough for the open-plan decay', async () => {
  const { page, problems } = await open();
  // the default 12 m room with the source at x = 2 m: from 2 m out to the receiver margin 0.6 m short of the far wall, at the receiver's height
  await page.getByRole('button', { name: 'Receivers in a line' }).click();
  assert.equal(await page.getByTestId('extra-receiver').count(), 7);
  const xs = [];
  for (const name of ['Receiver 1', ...Array.from({ length: 7 }, (_, i) => `Receiver ${i + 2}`)]) {
    xs.push(await page.getByLabel(`${name} x`, { exact: true }).inputValue());
    assert.equal(await page.getByLabel(`${name} y`, { exact: true }).inputValue(), '4');
    assert.equal(await page.getByLabel(`${name} z`, { exact: true }).inputValue(), '1.2');
  }
  assert.deepEqual(xs.map(Number), [4, 5.06, 6.11, 7.17, 8.23, 9.29, 10.34, 11.4]);
  await page.getByRole('button', { name: /Run simulation/ }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  await page.getByRole('button', { name: 'Open-plan office' }).click();
  await page.getByTestId('open-plan').waitFor();
  assert.equal(await page.getByRole('rowheader', { name: /^Receiver \d$/ }).count(), 8);
  assert.equal(await page.getByTestId('open-plan-narrow').count(), 0, 'the line spans more than a doubling of distance');
  assert.ok(parseFloat(await page.getByTestId('open-plan-spatialDecayDb').innerText()) > 0, 'speech falls with distance');
  assert.deepEqual(problems, []);
  await page.close();
});

test('a line of receivers gives the ISO 14257 workroom curves, DL2 and DLf', async () => {
  const { page, problems } = await open();
  await page.getByRole('button', { name: 'Receivers in a line' }).click();
  await page.getByRole('button', { name: /Run simulation/ }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  await page.getByRole('button', { name: 'Workroom' }).click();
  await page.getByTestId('workroom').waitFor();
  assert.equal(await page.getByTestId('workroom-why').count(), 0);
  for (const curve of ['normalised', 'source', '125', '4000']) {
    assert.ok(Number.isFinite(parseFloat(await page.getByTestId(`workroom-dl2-middle-${curve}`).innerText())), `DL2 of the ${curve} curve`);
    assert.ok(Number.isFinite(parseFloat(await page.getByTestId(`workroom-dlf-middle-${curve}`).innerText())), `DLf of the ${curve} curve`);
    assert.ok(Number.isFinite(parseFloat(await page.getByTestId(`workroom-dlfr-middle-${curve}`).innerText())), `DL'f at 10 m of the ${curve} curve`);
  }
  // the near, middle and far regions each have a column group; the far one, beyond 16 m, has no receivers in a 12 m room
  for (const region of ['near', 'middle', 'far']) await page.getByTestId(`workroom-region-${region}`).waitFor();
  assert.equal(await page.getByTestId('workroom-dl2-far-normalised').innerText(), '–');
  assert.equal(await page.getByRole('rowheader', { name: /^Receiver \d$/ }).count(), 8);
  // in the default 12 m room the receivers in the 5 to 16 m middle region reach only 9.4 m, less than a doubling, and it says so
  await page.getByTestId('workroom-narrow').waitFor();
  assert.equal(await page.getByTestId('workroom-regions').count(), 0, 'the typical d1 and d2 need no note');

  // d1 and d2 set to suit the room move the regions, and the results say so
  const settings = await settingsWindow(page);
  await settings.getByLabel('Workroom d1').fill('3');
  await settings.getByLabel('Workroom d2').fill('8');
  await settings.close();
  await page.getByRole('button', { name: /Run simulation/ }).click();
  await page.getByTestId('workroom-regions').filter({ hasText: 'd1 = 3 m and d2 = 8 m' }).waitFor({ timeout: 90000 });
  assert.match(await page.getByTestId('workroom-region-far').innerText(), /Far \(from 8 m\)/);
  assert.deepEqual(problems, []);
  await page.close();
});

test('results and a map are saved as CSV, the responses as WAV, and a printable report opens', async () => {
  const { readFile } = await import('node:fs/promises');
  const { page } = await open();
  await page.getByRole('button', { name: '+ Add receiver' }).click();
  await page.getByRole('button', { name: /Run simulation/ }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });

  const [csv] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Results CSV' }).click()]);
  assert.equal(csv.suggestedFilename(), 'results.csv');
  const rows = (await readFile(await csv.path(), 'utf8')).trim().split('\r\n');
  assert.match(rows[0], /^Receiver,x \(m\),y \(m\),z \(m\),Parameter,Unit,63 Hz,125 Hz.*,8k Hz,Broadband$/);
  assert.ok(rows.some((r) => r.startsWith('1,') && r.includes(',T30,s,')), 'a T30 row for receiver 1');
  assert.ok(rows.some((r) => r.startsWith('2,') && r.includes(',T30,s,')), 'and for receiver 2');
  assert.ok(rows.some((r) => r.includes("STI (male weighting, the sources' own levels")), 'the STI, and what it was for');
  assert.ok(rows.some((r) => r.includes('Speech level at the receiver,dB(A)')), 'the speech that reached the receiver');
  assert.ok(rows.some((r) => r.startsWith('1,') && r.includes(',STIPA,')) && rows.some((r) => r.startsWith('2,') && r.includes(',RaSTI,')), 'STIPA and RaSTI for each receiver');
  assert.ok(rows.some((r) => r.startsWith('2,') && r.includes('ISO 3382-3 speech level Lp,A,S')), 'the ISO 3382-3 speech level at each receiver');
  assert.ok(rows.some((r) => r.startsWith('ISO 3382-3,') && r.includes('Spatial decay rate of speech D2,S')), 'and its single numbers');
  assert.ok(rows.some((r) => r.startsWith('2,') && r.includes('ISO 14257 sound distribution Lp - LW')), 'the ISO 14257 sound distribution at each receiver');
  assert.ok(rows.some((r) => r.startsWith('ISO 14257,') && r.includes('at least 3')), 'and why it has no DL2 or DLf');

  const [popup] = await Promise.all([page.waitForEvent('popup'), page.getByRole('button', { name: 'Report', exact: true }).click()]);
  await popup.getByRole('heading', { name: 'Receiver 2', exact: false }).waitFor();
  // a section for each receiver, then the open-plan office and workroom ones, which with two receivers say why they have no single numbers
  assert.equal(await popup.getByRole('heading', { level: 2 }).count(), 4);
  await popup.getByRole('heading', { name: 'Workroom (ISO 14257)' }).waitFor();
  await popup.getByRole('heading', { name: 'Open-plan office (ISO 3382-3)' }).waitFor();
  assert.match(await popup.locator('body').innerText(), /needs at least 4 receivers/);
  assert.match(await popup.locator('body').innerText(), /Speech Transmission Index/);
  assert.match(await popup.locator('body').innerText(), /STIPA 0\.\d\d; RaSTI 0\.\d\d/);
  assert.equal(await popup.locator('svg[role=img]').count(), 8, 'four charts for each of the two receivers');
  await popup.close();

  // the map, once calculated
  await showMap(page);
  await page.getByRole('button', { name: 'Calculate map' }).click();
  await page.locator('svg[aria-label="Plan map"]').waitFor({ timeout: 90000 });
  const [mapCsv] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Map CSV' }).click()]);
  // the workbook carries the map too, once there is one
  await page.getByRole('tab', { name: '3-D and results', exact: true }).click();
  const { unzipSync, strFromU8 } = await import('fflate');
  const [book] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Results XLSX' }).click()]);
  assert.match(strFromU8(unzipSync(new Uint8Array(await readFile(await book.path())))['xl/workbook.xml']), /name="Map"/);
  await showMap(page);
  assert.equal(mapCsv.suggestedFilename(), 'map.csv');
  const mapRows = (await readFile(await mapCsv.path(), 'utf8')).trim().split('\r\n');
  assert.match(mapRows[0], /^x \(m\),y \(m\),z \(m\),T30 \(reverberation time\) broadband \(s\),/);
  assert.ok(mapRows.length > 20, 'a row per map point');

  // the impulse response and the sound, as stereo 16-bit WAV files
  await page.getByRole('tab', { name: '3-D and results', exact: true }).click();
  await page.getByRole('button', { name: 'Listen', exact: true }).click();
  await page.getByRole('button', { name: 'Calculate response' }).click();
  const saveAs = page.getByRole('group', { name: 'Save as WAV' });
  await saveAs.waitFor({ timeout: 90000 });
  const [wav] = await Promise.all([page.waitForEvent('download'), saveAs.getByRole('button', { name: 'Impulse response' }).click()]);
  const bytes = await readFile(await wav.path());
  assert.equal(bytes.toString('latin1', 0, 4), 'RIFF');
  assert.equal(bytes.toString('latin1', 8, 12), 'WAVE');
  assert.equal(bytes.readUInt16LE(22), 2, 'stereo');
  assert.equal(bytes.readUInt16LE(34), 16, '16 bit');
  assert.equal(bytes.readUInt32LE(40), bytes.length - 44, 'the data chunk fills the rest of the file');
  const [sound] = await Promise.all([page.waitForEvent('download'), saveAs.getByRole('button', { name: 'Sound as played' }).click()]);
  assert.equal(sound.suggestedFilename(), 'auralised-sound.wav');
  assert.ok((await readFile(await sound.path())).length > bytes.length, 'the sound is longer than the impulse response');
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('a calculation preset sets the rays and image-source order, and the reverberation chart has every global estimate', async () => {
  const { page } = await open();
  const popup = await settingsWindow(page);
  const presets = popup.getByRole('group', { name: 'Calculation preset' });
  assert.equal(await presets.getByRole('button', { name: 'Standard' }).getAttribute('aria-pressed'), 'true', 'the usual settings are the starting point');
  await presets.getByRole('button', { name: 'Detailed' }).click();
  assert.equal(await presets.getByRole('button', { name: 'Detailed' }).getAttribute('aria-pressed'), 'true');
  assert.equal(await presets.getByRole('button', { name: 'Standard' }).getAttribute('aria-pressed'), 'false');
  assert.equal(await popup.getByLabel('Rays traced').inputValue(), '200000');
  assert.equal(await popup.getByLabel('Image-source order').inputValue(), '3');
  await presets.getByRole('button', { name: 'Draft' }).click();
  assert.equal(await popup.getByLabel('Rays traced').inputValue(), '20000');
  assert.equal(await popup.getByLabel('Image-source order').inputValue(), '1');

  await page.getByRole('button', { name: 'Quick estimate' }).click();
  await page.locator('.recharts-legend-item-text', { hasText: 'Millington' }).waitFor();
  for (const name of ['Sabine', 'Eyring', 'Arau-Puchades', 'Fitzroy']) await page.locator('.recharts-legend-item-text', { hasText: name }).waitFor();
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('the reverberation chart says why the estimates part company', async () => {
  const { page } = await open();
  await page.getByRole('button', { name: 'Quick estimate' }).click();
  await page.getByText(/statistical estimate from the standard equations/).waitFor({ timeout: 5000 });
  assert.match(await page.getByTestId('rt-note').innerText(), /Fitzroy .* arithmetic mean/);
  await page.getByRole('button', { name: 'Clarity' }).click();
  assert.equal(await page.getByTestId('rt-note').count(), 0);
  await page.close();
});

test('a surface can be an audience area with an edge effect, or open to the outside', async () => {
  const { page } = await open();
  const popup = await surfacesWindow(page);
  const head = (name) => popup.locator('button[aria-pressed]').filter({ hasText: new RegExp(name, 'i') });
  const alpha = async (name) => Number(/α (\d\.\d+)/.exec(await head(name).innerText())[1]);

  const before = await alpha('floor');
  await popup.getByLabel('floor is an audience area').check();
  assert.equal(await alpha('floor'), before, 'with no exposed edge nothing changes');
  await popup.getByLabel('Exposed edge of floor').fill('40');
  assert.ok((await alpha('floor')) > before, `an exposed edge adds absorption (${before} then ${await alpha('floor')})`);
  await popup.getByLabel('floor is an audience area').uncheck();
  assert.equal(await alpha('floor'), before);

  await popup.getByLabel('walls is open').check();
  assert.equal(await alpha('walls'), 1);
  assert.equal(await popup.getByLabel('walls is an audience area').count(), 0, 'an opening is not also an audience area');
  await page.getByRole('button', { name: 'Quick estimate' }).click();
  await page.getByRole('button', { name: 'Reverberation' }).waitFor();
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('the Reflections tab lists the paths at the receiver, and a picked row draws its route', async () => {
  const { readFile } = await import('node:fs/promises');
  const { page } = await open();
  await page.getByRole('button', { name: 'Reflections', exact: true }).click();
  await page.getByTestId('reflection-row').first().waitFor({ timeout: 30000 });
  const order = page.getByRole('group', { name: 'Reflection order' });
  await order.getByRole('button', { name: '1', exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll('[data-testid=reflection-row]').length === 6);
  const rows = page.getByTestId('reflection-row');
  // earliest first: the delays rise down the table
  const delays = (await rows.evaluateAll((els) => els.map((e) => Number(e.children[3].textContent))));
  assert.deepEqual([...delays].sort((a, b) => a - b), delays);
  assert.ok(delays.every((d) => d > 0));
  await rows.first().click();
  assert.equal(await rows.first().getAttribute('aria-selected'), 'true');
  await rows.first().click();
  assert.equal(await rows.first().getAttribute('aria-selected'), 'false');

  const [csv] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Reflections CSV' }).click()]);
  const text = (await readFile(await csv.path(), 'utf8')).trim().split('\r\n');
  assert.match(text[0], /^Order,Surfaces,Path length \(m\),Delay after direct \(ms\),63 Hz/);
  assert.equal(text.length, 7);
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

test('a reflector coverage map is a parameter once a reflector surface is chosen in Settings', async () => {
  const { page } = await open();
  const popup = await settingsWindow(page);
  await popup.getByLabel('Reflector surface').selectOption('Ceiling');
  await popup.getByLabel('Reflector window from').waitFor();
  await page.getByRole('button', { name: /Calculate map/ }).click();
  await page.locator('svg[aria-label="Plan map"]').waitFor({ timeout: 90000 });
  await page.getByLabel('Map parameter').selectOption('Reflector');
  assert.match(await page.getByTestId('map-stats').innerText(), /^Reflector · broadband · min -?\d+\.\d+ · mean -?\d+\.\d+ · max -?\d+\.\d+ dB$/);
  assert.equal(await page.locator('main .text-rose-300').count(), 0);
  await page.close();
});

/** A measured-looking impulse response as a 16-bit mono WAV at 48 kHz: 50 ms of silence, a direct sound, then a noise decay falling 60 dB in t60 seconds. */
function decayWav(t60) {
  const rate = 48000, n = 2 * rate;
  let seed = 3;
  const random = () => ((seed = (seed * 1103515245 + 12345) % 2147483648) + 0.5) / 2147483648;
  const gauss = () => Math.sqrt(-2 * Math.log(random())) * Math.cos(2 * Math.PI * random());
  const data = Buffer.alloc(2 * n);
  for (let i = 0; i < n; i++) {
    const t = i / rate - 0.05;
    let v = (t >= 0 ? 0.05 * gauss() * 10 ** (-3 * t / t60) : 0) + 1e-5 * gauss();
    if (i === 0.05 * rate) v = 0.9;
    data.writeInt16LE(Math.round(Math.max(-1, Math.min(1, v)) * 32767), 2 * i);
  }
  const head = Buffer.alloc(44);
  head.write('RIFF', 0); head.writeUInt32LE(36 + data.length, 4); head.write('WAVEfmt ', 8);
  head.writeUInt32LE(16, 16); head.writeUInt16LE(1, 20); head.writeUInt16LE(1, 22); head.writeUInt32LE(rate, 24); head.writeUInt32LE(2 * rate, 28);
  head.writeUInt16LE(2, 32); head.writeUInt16LE(16, 34); head.write('data', 36); head.writeUInt32LE(data.length, 40);
  return Buffer.concat([head, data]);
}

test('a measured impulse response is analysed and set beside the simulated receiver, band by band, in JNDs', async () => {
  const { page, problems } = await open();
  await page.getByRole('button', { name: 'Measured', exact: true }).click();
  const panel = page.getByTestId('measured');
  const file = page.locator('input[type=file][accept*=".wav"]');
  await file.setInputFiles({ name: 'Hall.wav', mimeType: 'audio/wav', buffer: decayWav(0.8) });
  await panel.getByTestId('measured-info').waitFor();
  assert.match(await panel.getByTestId('measured-info').innerText(), /Hall\.wav · 48\.0 kHz · 1\.95 s from the direct sound, which comes 50 ms in/);
  const t30 = (await panel.getByTestId('measured-t30S').locator('td').allInnerTexts()).slice(1, 9).map(Number);
  for (const b of [3, 4, 5]) assert.ok(Math.abs(t30[b] - 0.8) < 0.08, `T30 at band ${b} is about 0.8 s (${t30})`);
  assert.match(await panel.innerText(), /Press Run simulation/);

  // with a simulation, each parameter has its simulated row and the difference in JNDs, and the analysis stays while other tabs are seen
  await page.getByRole('button', { name: /Run simulation/ }).click();
  await page.locator('text=/to compute/').waitFor({ timeout: 90000 });
  await page.getByRole('button', { name: 'Reverberation', exact: true }).click();
  await page.getByRole('button', { name: 'Measured', exact: true }).click();
  const differences = (await panel.getByTestId('difference-t30S').locator('td').allInnerTexts()).slice(1, 9);
  assert.ok(differences.every((d) => /^[+-]?\d+\.\d$|^–$/.test(d)), `differences in JNDs (${differences})`);
  assert.match(await panel.getByTestId('difference-t30S').locator('td').last().innerText(), /^\d+\.\d$/, 'the mean difference');

  // a file that is not a WAV is refused with the reason, and the last analysis stays
  await file.setInputFiles({ name: 'Notes.wav', mimeType: 'audio/wav', buffer: Buffer.from('not a recording at all') });
  assert.match(await panel.getByRole('alert').innerText(), /WAV/);
  assert.match(await panel.getByTestId('measured-info').innerText(), /Hall\.wav/);
  assert.deepEqual(problems.filter((p) => !/400|Bad Request/.test(p)), []);
  await page.close();
});

test('the theory page opens from the app, with every formula typeset and the references listed', async () => {
  const { page } = await open();
  const [theory] = await Promise.all([page.waitForEvent('popup'), page.getByRole('link', { name: 'Theory and references' }).click()]);
  const problems = [];
  theory.on('pageerror', (e) => problems.push(e.message));
  theory.on('console', (m) => { if (m.type() === 'error') problems.push(m.text()); });
  await theory.reload();
  await theory.getByRole('heading', { name: '20. References' }).waitFor();
  const formulas = await theory.locator('.eq, .m').count();
  assert.ok(formulas > 200, `the page has its formulas (${formulas})`);
  assert.equal(await theory.locator('.eq math, .m math').count(), formulas, 'every formula is typeset');
  assert.ok(await theory.locator('ol.refs li').count() > 40, 'the references are listed');
  assert.deepEqual(problems, []);
  await theory.close();
  await page.close();
});

test('the user guide opens from the app, with its contents and a link to the theory page', async () => {
  const { page } = await open();
  const [guide] = await Promise.all([page.waitForEvent('popup'), page.getByRole('link', { name: 'User guide' }).click()]);
  const problems = [];
  guide.on('pageerror', (e) => problems.push(e.message));
  guide.on('console', (m) => { if (m.type() === 'error') problems.push(m.text()); });
  await guide.reload();
  await guide.getByRole('heading', { name: '14. Good to know' }).waitFor();
  // every entry in the contents leads to a section of the page
  const targets = await guide.locator('nav a').evaluateAll((links) => links.map((a) => a.getAttribute('href')));
  assert.equal(targets.length, 14);
  for (const t of targets) assert.equal(await guide.locator(t).count(), 1, `${t} is on the page`);
  assert.equal(await guide.getByRole('link', { name: 'Theory and references' }).first().getAttribute('href'), '/theory.html');
  assert.deepEqual(problems, []);
  await guide.close();
  await page.close();
});

test('the Third octaves tab works out the 23 bands from 50 Hz to 8 kHz at the receiver on request', async () => {
  const { page, problems } = await open();
  await page.getByRole('button', { name: 'Third octaves', exact: true }).click();
  const panel = page.getByTestId('third-octaves');
  await panel.getByRole('button', { name: 'Calculate third octaves' }).click();
  const table = panel.getByTestId('third-octave-table');
  await table.waitFor({ timeout: 180000 });
  assert.equal(await table.locator('thead th').count(), 24, 'a column for each of the 23 bands');
  const t30 = (await table.locator('tbody tr').first().locator('td').allInnerTexts()).map(Number);
  assert.ok(t30.slice(8, 20).every((t) => t > 0.1 && t < 10), `T30 from 250 Hz to 4 kHz (${t30})`);
  assert.ok(await panel.getByRole('button', { name: 'Third octaves CSV' }).isVisible());
  assert.deepEqual(problems, []);
  await page.close();
});
