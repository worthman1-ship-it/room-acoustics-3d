# How Room Acoustics 3D is built

The physics (ray tracing, image sources, diffraction, the ISO 3382 parameters, the STI, the material library) is in C#, in `RoomAcoustics.Core`,
with its tests. The interface is a web page, where three.js gives orbit controls, picking and animation cheaply. The two meet at a small JSON API that
also serves the built page, so one program on `http://localhost:5080` is the whole application. A tray launcher starts it.

```
 browser (web/)                              this machine, localhost:5080
 React + Vite + Tailwind                     ASP.NET Core minimal API (src/RoomAcoustics.Api)
 React Three Fiber (the 3-D rooms) <--JSON--> SimulationService, ModelService, BalloonService, HrtfService, MeasuredService, JobService
 Recharts (charts)                             -> RoomAcoustics.Core: EnergyRayTracer, ImageSourceEnumerator, Room3D, AcousticMap,
 user guide and theory pages (web/public)         RoomAcousticMetrics, SpeechTransmissionIndex, the model readers and edits, ...
```

The methods and formulas are on the theory page (`web/public/theory.html`), which must match the engine's code; how to use the program is in the
user guide (`web/public/guide.html`).

## The engine (`src/RoomAcoustics.Core`)

| Folder | What it holds |
|---|---|
| `Engine` | `Room3D` (faces, a bounding-volume hierarchy, screens), `EnergyRayTracer` (rays and image sources, octave bands, scattering, edge scattering, screen and corner diffraction, interaural cells), `ImageSourceEnumerator`, `ReceiverIndex` (many receivers from one set of rays), `AcousticMap`, `ReceiverGrid`, `RoomSection`, `RoomVolume`, `RoomAcousticMetrics`, `BroadbandMetrics`, `InterauralCoherence`, `ThirdOctaveSimulation`, `ReflectionList`, `RoomLevels`, `EchoCriterion`; sources (`SourceDirectivity`, `BalloonDirectivity`, `ClfReader` (CLF text and .CF2), `EaseReader` (EASE .lob), `CoherentLineArray`, `TalkerDirectivity`, `ExtendedSource`); listening (`RoomAuralisation`, `HeadShadow`, `MeasuredHrtf`, `Ambisonics`); measured responses (`MeasuredImpulseResponse`, `DecayAnalysis`) |
| `Geometry` | The model readers (`CadMeshReader` for DXF and DWG, `ObjMeshReader`, `StlMeshReader`, `ColladaMeshReader`, `IfcMeshReader`, `ThreeDsMeshReader`) into a `SurfaceMesh`; `MeshCheck`, `HoleFill`, `ModelRepair`, `SurfaceSplit`, `MeshEdit` (shape edits), `AudienceEdge` |
| `Materials` | `AbsorptionMaterial` (the database), `Surface`, `AngleDependentAbsorption`, `ReverberationCalculator` and `StatisticalEstimate` (the global formulas and the quick estimate) |
| `Speech` | `SpeechTransmissionIndex` (STI, STIPA, RaSTI), `SpeechIntelligibilityIndex`, `OpenPlanOffice` (ISO 3382-3), `Workroom` (ISO 14257) |
| `Signal` | `WavReader` |

Positions are in metres in the engine's frame: x along the length, y across, z up. The page converts to three.js's y-up frame.

## The API (`src/RoomAcoustics.Api`, port 5080)

It listens on `localhost` only (`appsettings.json`). Requests and responses are JSON in camel case; the records are in `Contracts.cs`. Input is
checked before any work starts, and a bad request (a source outside the room, a ray count beyond the limit, an unreadable file) is a **400** with the
engine's own message, which the page shows.

| Method and path | Purpose |
|---|---|
| `GET /api/health` | Is the engine up, and its version. |
| `GET /api/bands` | The eight octave centres, 63 Hz to 8 kHz. |
| `GET /api/materials` | Every absorption material: id, name, category, eight coefficients (null where unreported). |
| `GET /api/examples`, `POST /api/model/example?name=&space=` | The example models (the `examples` folder) and loading one. |
| `POST /api/model/import?name=&scale=&yUp=&space=` | A model file's bytes as the body (DXF, DWG, OBJ, STL, COLLADA, IFC, 3DS; up to 512 MB). Returns its surface groups with triangles for drawing, bounds, volume, a suggested source and receiver, an IFC file's rooms, and, if it is not a closed surface, why and a geometry check. Models are kept in memory (the last eight) and referred to by id; every edit below makes a new id. |
| `POST /api/model/fill` | Close holes the geometry check found. |
| `POST /api/model/repair` | Repair a model that is not closed (fill, join, cut T-junctions, make furniture screens or boxes), with a line for each step. |
| `POST /api/model/split` | Split a surface group into its flat surfaces. |
| `POST /api/model/group` | Rename a group, merge it into another or delete it. |
| `POST /api/model/geometry` | Move a corner, draw a face or delete a flat surface (`MeshEdit`); corners and faces are found by position, to 1 mm. |
| `POST /api/simulate`, `POST /api/jobs/simulate` | A full run: a box room or a model (a finish per group, screens), up to eight sources and eight receivers, rays, image-source order, air, the STI's speech and noise, absorption by angle, the workroom regions. Returns every receiver's parameters by band and broadband, decay curves, impulse responses, the STI block, the global estimates, and the open-plan office and workroom results. |
| `POST /api/statistical` | The same result shape from the standard equations alone, at once (the quick estimate). |
| `POST /api/jobs/thirdoctave` | The 23 third-octave bands at the main receiver. |
| `POST /api/map`, `POST /api/jobs/map`, `GET /api/map/metrics` | A plan, section, height matrix or map above a surface: every parameter at every point (up to 3 000), by band and broadband, and the names of the parameters. |
| `POST /api/jobs/auralise` | The binaural impulse responses at the listener (a spherical head, or a measured HRTF by id), with ILD, ITD and IACC; one source at a time when asked. |
| `POST /api/jobs/ambisonic` | An Ambisonic (AmbiX) impulse response at the listener, first to third order. |
| `GET /api/jobs/{id}`, `DELETE /api/jobs/{id}` | A job's status, progress (0 to 1) and result; cancel it. Finished jobs are kept ten minutes. |
| `POST /api/rays` | Ray paths for the 3-D picture, flagging those that reach the receiver. |
| `POST /api/reflections` | The specular paths at the receiver up to an order, with delays and levels. |
| `POST /api/directivity/index` | A pattern's on-axis directivity index, for entering a level at 1 m or a sensitivity. |
| `POST /api/balloon/import?name=` | A measured source pattern (CSV balloon, CLF text, CLF binary .CF2, EASE 3 or 4 .lob, BYU CIF CSV), told apart by what the file holds, not its name: an id, and its polar cuts. Any other binary file is a 400 saying what to do instead. |
| `POST /api/hrtf/import?name=` | A measured HRTF as a SOFA file: an id. |
| `POST /api/measured?name=` | A measured impulse response (WAV): its ISO 3382 parameters by band. |

`/mcp` is the engine as a Model Context Protocol server (streamable HTTP, stateless; `McpTools.cs`, on the official `ModelContextProtocol.AspNetCore`
SDK), for an AI assistant: `list_examples`, `load_example`, `import_model` (a file path on this machine), `find_materials`, `simulate` and
`quick_estimate` (both take the `/api/simulate` request). Each calls the same service as its endpoint; models come back without their triangles, and
results without their decay curves and impulse responses. A request with an `Origin` other than localhost is refused (403), as `import_model` reads files.

Uploaded patterns (the last sixteen) and HRTFs (the last four) are kept in the engine's memory only. A saved project carries its measured patterns and uploads them again; an HRTF has to be uploaded again after the engine restarts.

## The page (`web/`)

React with Vite and Tailwind; `@react-three/fiber` and `drei` for the two 3-D rooms (the work view and the map view, both kept mounted); Recharts for
the charts. `App.tsx` holds the state and talks to the engine through `api.ts`; the Sources and receivers, Surfaces and Settings panels open in browser
windows of their own (`PopOut.tsx`). `project.ts` saves and loads a project (`.room3d.json`, with the model file, every edit made to it, and measured
patterns inside); `export.ts` and `xlsx.ts` make the CSV, the workbook and the report in the browser. The user's own materials, favourites and spectra
are kept in the browser's local storage. `npm run build` makes `web/dist`, which the API serves.

## Running and packaging

| Script | What it does |
|---|---|
| `run-app.ps1` | Builds `web/dist` if needed and runs the API, which serves the page, on `http://localhost:5080`; opens the browser once the engine answers. |
| `run-web.ps1` | Development: the API on 5080 and the Vite dev server on 5173 (it proxies `/api`). |
| `publish-app.ps1` | The deployable package (see the README): `RoomAcoustics.exe` (the tray launcher) beside `RoomAcoustics.Api.exe`, the page in `wwwroot`, the examples and a README, self-contained unless `-FrameworkDependent`, and a zip of it. |

The tray launcher (`src/RoomAcoustics.Tray`) starts the engine hidden, opens the page once it answers and keeps an icon in the notification area
(Open, Quit); a second copy just opens the page; it warns if the engine stops (another program on port 5080, say).

## Tests

| Suite | What it covers |
|---|---|
| `tests/RoomAcoustics.Core.Tests` (595) | The engine: geometry, reading and editing models, the ray tracer and image sources against analytic answers, diffraction, maps, metrics, STI, listening, the standards' worked examples. About 1.5 minutes in Release, much longer in Debug. |
| `tests/RoomAcoustics.Api.Tests` (88) | The API on an in-memory server: every endpoint, its limits and error answers, jobs, the MCP tools through an MCP client, and the built page being served. |
| `web/e2e` (76) | `npm run e2e`: the page driven in the system's Chrome or Edge with `playwright-core` (nothing is downloaded) against a running app (`E2E_URL` picks another address). |

`npx tsc --noEmit` type-checks the page. `tools/bras` runs the engine against the measured BRAS seminar room (`docs/BENCHMARK_BRAS_CR2.md`).

## Known limits

- The engine is geometrical: no room modes or wave effects at low frequencies. Diffraction is modelled at screens' free edges, round the room's own
  corners (the direct sound and image paths of up to two reflections) and as scattering at surface edges; the ray-traced sound does not bend.
- Image sources multiply as faces to the power of the order, so a model of many faces is held to a lower order (the header says so).
- A room must be one closed surface, or be taken as outdoors; free-standing furniture becomes screens or boxes through the repair.
- The quick estimate ignores the room's shape and source patterns, and takes every source's direct sound as arriving together.
- The talker pattern has no data more than 53 degrees below the horizontal, so its index at 4 and 8 kHz is about 1 dB under the published one.
- The loudspeaker playback is a simple crosstalk canceller for a listener midway between the speakers; the default head is a sphere.
- The workroom regions' conventional distances for DL'fr are the standard's 4, 10 and 30 m whatever d1 and d2 are set to.
- Shape editing moves corners by typed coordinates (no dragging) and starts from an imported model (no drawing a room from nothing).
