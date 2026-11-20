# Room Acoustics 3D

A room acoustics simulator: ray tracing and image sources in a 3-D room, with the ISO 3382 parameters, the Speech Transmission
Index, maps, listening and reports. It runs on your own machine as a web app (a React page with an orbitable 3-D room) on a C# engine.

What it does, in brief:

- **Rooms:** a box with sliders, or a model imported from DXF, DWG, OBJ, STL, COLLADA (SketchUp), IFC or 3D Studio; a model that is not closed is
  checked, its holes filled and repaired, or taken as outdoors; groups split, renamed, merged and deleted; corners moved, faces drawn and surfaces
  deleted in the 3-D view.
- **Surfaces:** a built-in library of about 90 materials from published tables (BB93, EN 12354-6 and others), plus your own library (`%APPDATA%\RoomAcoustics3D\materials.json`) if you keep one, scattering, absorption by angle of incidence (optional), audience areas with their edge effect, open surfaces,
  free-standing screens and partitions with doors.
- **Sources:** up to eight, omnidirectional, horn-like, line arrays, measured patterns (CSV, CLF text and binary .CF2, EASE 3 and 4 .lob balloons, BYU's CIF CSV), a talker or a radiating
  surface, each with its own sound power spectrum, level, delay and aim; up to eight receivers.
- **Results:** T30, T20, EDT, C50, C80, D50, Ts, G, LF, LFC, LJ, stage support, IACC, SPL, STI, STIPA, RaSTI, SII and %ALcons by band with their
  single-number values; third octaves; the Sabine, Eyring, Millington, Arau-Puchades and Fitzroy estimates; ISO 3382-3 open-plan offices and ISO 14257
  workrooms; the reflections at a receiver; a measured impulse response compared band by band.
- **Maps:** plans, sections, height matrices and maps above a surface of every parameter in every band.
- **Listening:** binaural (a spherical head or a measured SOFA HRTF), loudspeaker playback and Ambisonics to third order.
- **Output:** projects saved to one file, CSV and Excel results, a printable report, PNG pictures and WAV files.
- **AI assistants:** the engine is an MCP server at `http://localhost:5080/mcp` (load or import a room, search materials, simulate, estimate); in
  Claude Code, `claude mcp add --transport http room-acoustics http://localhost:5080/mcp`.

The engine's methods, formulas and references are on the in-app **Theory and references** page (`web/public/theory.html`), and how to use it on the
**User guide** page (`web/public/guide.html`).

## The code

| Folder | What it is |
|---|---|
| `src/RoomAcoustics.Core` | The engine: `Engine` (ray tracer, image sources, diffraction, receiver index, directivity, maps, auralisation, Ambisonics), `Geometry` (model readers, checks, repairs and edits), `Materials` (absorption database, statistical estimates), `Speech` (STI, SII, open-plan offices, workrooms), `Signal` (WAV) |
| `src/RoomAcoustics.Api` | An ASP.NET Core minimal API over the engine, on `http://localhost:5080`; it also serves the built page |
| `src/RoomAcoustics.Tray` | A tray launcher (`RoomAcoustics.exe`): starts the engine, opens the page, Quit stops it |
| `web/` | The React front end (Vite, Tailwind, three.js, Recharts), its browser tests (`web/e2e`), and the user guide and theory pages (`web/public`) |
| `tests/` | `RoomAcoustics.Core.Tests` (the engine) and `RoomAcoustics.Api.Tests` (the API, in memory) |
| `examples/` | The example models the page offers |
| `packaging/` | What goes into the deployable package besides the program (its README) |
| `tools/` | The icon and example-model generators (Python), and the BRAS benchmark (`tools/bras`) |
| `docs/` | See below |

| Document | What it is |
|---|---|
| `docs/ARCHITECTURE.md` | How the program is built: the engine, the API (every endpoint), the front end, the tests and the known limits |
| `docs/BENCHMARK_BRAS_CR2.md` | The engine against the measured BRAS seminar room |

## Building, testing, running

Needs the .NET 10 SDK (Windows for the tray launcher) and Node.js 20 or later to build the page (the scripts also look for a portable Node in
`%USERPROFILE%\tools`).

```
dotnet build RoomAcoustics3D.slnx
dotnet test tests/RoomAcoustics.Core.Tests
dotnet test tests/RoomAcoustics.Api.Tests
./run-app.ps1          # builds the page if needed and runs everything on http://localhost:5080 (-Rebuild rebuilds the page, -NoBrowser leaves the browser alone)
./run-web.ps1          # development: the engine on 5080 and Vite on 5173
cd web; npm run e2e    # the browser tests, against a running app (E2E_URL picks another address)
```

## Making a deployable version

```
./publish-app.ps1                  # publish\RoomAcoustics3D-<version>-win-x64 and its .zip: needs no .NET installed
./publish-app.ps1 -FrameworkDependent   # a smaller package for machines that have the .NET 10 runtime
```

The package is a folder (and a zip of it) to copy to any 64-bit Windows 10 or 11 machine: run `RoomAcoustics.exe` in it. It holds the tray launcher,
the engine, the built page with the user guide and theory pages, the example models and a README. The version is set in `Directory.Build.props`.

This repository was split out of the AcousticCalc suite, which keeps the other calculators.

## Licence

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](LICENSE.md): personal use, research, teaching, and use by charities,
schools and public bodies. Commercial use needs a separate licence from the author.

Required Notice: Copyright (c) 2026 Richard King
