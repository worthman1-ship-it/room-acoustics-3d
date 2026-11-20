# Benchmark: BRAS seminar room (CR2)

The engine against measurements in a real room, from the **Benchmark for Room Acoustical Simulation** (BRAS, TU Berlin and RWTH Aachen,
CC BY-SA 4.0, DOI [10.14279/depositonce-6726.3](https://doi.org/10.14279/depositonce-6726.3)), the database behind the round robin of
Brinkmann et al. (JASA 2019). CR2 is a seminar room at RWTH Aachen: 146 m³, 202.5 m² of plaster, concrete, windows, floor and ceiling.

## Method

- **Geometry:** `CR2.skp` (geometry only), written to OBJ by `tools/bras/export.rb` in SketchUp 2015 (every face as triangles, in metres, a
  group per material). The three radiators are closed boxes standing in the room and go in as free-standing screens of the concrete finish.
  The room comes out at 146.4 m³ (the database gives 146.1 m³) and 202.5 m².
- **Surfaces:** the database's third-octave absorption and scattering, each octave the mean of its three third octaves. The *fitted* set was
  matched by the database's authors to the measured reverberation times with Eyring's formula; the *initial* set is their estimate before that.
  Eyring with the octave values here gives back the measured T20 to within 0.05 s, so the data went in as intended.
- **Sources and receivers:** the dodecahedron at its two positions (taken as omnidirectional), five receivers at 1.23 m; 19.5 °C, 41.7 %.
- **Measured values:** the database's T20, EDT, C80 and D50 in third octaves, the mean over the 10 source-receiver pairs, each octave the mean of
  its three third octaves. The simulated values are the mean over the same 10 pairs.
- **Just-noticeable differences** (ISO 3382-1 Annex A): reverberation time and EDT 5 %, C80 1 dB, D50 5 points.

`tools/bras/Program.cs` runs the engine and `tools/bras/compare.py` makes the tables (see `tools/bras/README.md`).

## Results

With the fitted surfaces, the database's own scattering and the app's defaults (60 000 rays, image sources to order 2, 2 s response, scattering
by the edges' diffraction on), measured → simulated:

| Octave | T20 | EDT | C80 | D50 |
|---|---|---|---|---|
| 125 Hz | 1.34 → 1.36 s (+2 %) | +3 % | −0.1 dB | +6.1 |
| 250 Hz | 1.51 → 1.49 s (−2 %) | +2 % | −0.3 dB | −1.7 |
| 500 Hz | 2.00 → 2.01 s (+1 %) | +1 % | +0.2 dB | +0.2 |
| 1 kHz | 1.94 → 1.97 s (+1 %) | +3 % | 0.0 dB | +0.9 |
| 2 kHz | 1.76 → 1.79 s (+2 %) | +1 % | −0.2 dB | −1.7 |
| 4 kHz | 1.56 → 1.61 s (+3 %) | +2 % | +0.1 dB | +0.8 |
| 8 kHz | 0.94 → 0.92 s (−2 %) | −2 % | 0.0 dB | +0.2 |

Mean error in JNDs over the seven octaves, as the engine was improved:

| Run | T20 | EDT | C80 | D50 |
|---|---|---|---|---|
| As first run (times from the emission, no edge scattering) | 1.4 | 1.2 | 0.9 | 1.0 |
| Times from the direct sound | 1.4 | 1.2 | 0.3 | 0.4 |
| Times from the direct sound, scattering by the edges' diffraction (**now**) | **0.3** | **0.4** | **0.1** | **0.3** |
| The same, 200 000 rays and a 3 s response (before edge scattering) | 1.5 | 1.2 | — | — |
| The same with the initial (unfitted) surfaces | 5.3 | 5.4 | 1.7 | 1.4 |

### Scattering

The same room, T20 simulated against measured, with the database's scattering or one coefficient on every surface, before and after the edges'
diffraction was added (both with times from the direct sound):

| Scattering | 125 Hz | 250 Hz | 500 Hz | 1 kHz | 2 kHz | 4 kHz | 8 kHz | Mean \|T20\| error | Mean \|EDT\| error |
|---|---|---|---|---|---|---|---|---|---|
| Database's values | +11 % | +14 % | +10 % | +7 % | +4 % | +4 % | −2 % | 7.3 % | 6.1 % |
| 0.05 | +12 % | +15 % | +12 % | +9 % | +7 % | +7 % | −1 % | 9.0 % | 6.8 % |
| 0.1 | +8 % | +8 % | +6 % | +6 % | +4 % | +5 % | −1 % | 5.4 % | 5.1 % |
| 0.2 | +5 % | +3 % | +4 % | +4 % | +3 % | +4 % | −2 % | 3.2 % | 4.0 % |
| 0.3 | +4 % | +2 % | +3 % | +3 % | +3 % | +3 % | −2 % | 2.9 % | 3.2 % |
| 0.5 | +4 % | +1 % | +3 % | +3 % | +3 % | +3 % | −2 % | 2.7 % | 3.2 % |
| 1 (all diffuse) | +6 % | +2 % | +3 % | +4 % | +3 % | +4 % | −2 % | 3.2 % | 3.7 % |
| **With the edges' diffraction:** | | | | | | | | | |
| Database's values | +2 % | −2 % | +1 % | +2 % | +2 % | +3 % | −2 % | **1.8 %** | **1.9 %** |
| 0.3 | +3 % | 0 % | +2 % | +3 % | +2 % | +3 % | −2 % | 2.1 % | 2.6 % |
| 1 (all diffuse) | +6 % | +2 % | +3 % | +4 % | +3 % | +4 % | −2 % | 3.2 % | 3.7 % |

## What it shows

- **The defaults are converged:** more rays and a longer response change nothing, so the differences are the model's, not noise.
- **The input data matters most**, as the round robins found: the database's own first estimate of the surfaces is off by about five JNDs in
  reverberation, the fitted set by well under one.
- **The engine's scattering behaves as theory says.** From about s = 0.2 the decay stops changing: the sound field is diffuse, and the decay is
  about 3 % slower than Eyring's (which the fitted absorption reproduces, so 3 % over the measurement). Kuttruff's correction for the spread of
  path lengths between reflections puts a diffuse room's decay 1–2 % over Eyring's at this absorption (ᾱ ≈ 0.055) in a room this flat.
- **The surfaces' own scattering is not enough on its own.** The database gives 0.05 for most surfaces up to 1 kHz, the surfaces' roughness.
  With only that, the field is not diffuse: a box of hard, smooth walls keeps sound in grazing paths along the floor and ceiling, which reflect
  less often, and the decay ran 7–14 % long below 2 kHz. The measured room decays as a diffuse one (the fitted absorption was matched with
  Eyring's formula, and Eyring's matches).
- **The edges' diffraction supplies the rest.** A finite surface met at a grazing angle, far from where the sound came from and is going, is too
  small to reflect it specularly; what it does not reflect is scattered (Rindel 1986; see
  `EdgeScattering`). That is exactly the sound the smooth room kept in grazing paths. With it, the database's own scattering puts every band
  within 3 % (a mean 0.3 JND), and the room needs no scattering coefficient raised by hand. It does not over-scatter: with s = 0.3 or more
  the result hardly moves, since a reflection already scattered cannot be scattered more.
- **The early sound matches:** C80 within 0.3 dB in every band, D50 within 2 points from 250 Hz up. Before the timing fix below it was
  0.5–1.4 dB and 4–8 points low in every band. D50 at 125 Hz is 6 points high: a room this small has few modes in that band (its Schroeder
  frequency is about 230 Hz), which no geometrical method models.

## Found on the way

- **Slivers in SketchUp's export.** This room's walls have triangles with three corners in a line along their top edges, which join them to a
  ceiling drawn in pieces. The engine dropped them for having no area, which opened the surface there, so the room could not be built. They are
  now folded into the triangle across their long edge (`SurfaceMesh.Faces`), and the model imports as a closed room.
- **Parameters timed from the emission.** C50, C80, D50, the centre time, LF80, LFC80 and LJ counted their 50 and 80 ms from the moment the
  source sounded, not from the arrival of the direct sound as ISO 3382-1 says, so at 2–6 m their early windows closed 6–18 ms short, and the
  decay curve began with a flat stretch at 0 dB that the EDT's fit took in. Everything is now timed from each band's first arrival
  (`RoomAcousticsCalculator`); measured responses were already cut at the direct sound.
- **No scattering by diffraction.** Added as above (`EdgeScattering`, `Room3D.SurfaceSizesM`): each surface's size is 4 × area / perimeter of
  the faces in one plane, sharing edges and a finish, and it applies to the rays and the image sources alike. `RayTraceOptions.EdgeDiffraction`
  turns it off.
