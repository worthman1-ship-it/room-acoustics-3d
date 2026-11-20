# BRAS seminar room benchmark

Runs the engine on the BRAS seminar room (CR2) and compares it with the measurements; the results are in `docs/BENCHMARK_BRAS_CR2.md`.

1. From [DepositOnce](https://doi.org/10.14279/depositonce-6726.3) download `1_scene_descriptions-CR2.zip`, `3_surface_descriptions.zip` and
   `4_additional_data.zip` (about 850 MB) into a folder, the *BRAS root*. From them put:
   - `CR2.skp` (from `CR2 small room (seminar room)/Geometry`) in `<root>/CR2`,
   - the `mat_CR2_*.csv` files in `<root>/materials`, named `fitted_mat_CR2_*.csv` and `initial_mat_CR2_*.csv` after their folders,
   - the `Evaluation_CR2_*.txt` files (from `Room acoustic parameters/data`) in `<root>/additional`.
2. Copy `export.rb` into `<root>/work` and open the model with it in SketchUp (2015 reads it):
   `SketchUp.exe -RubyStartup <root>\work\export.rb <root>\CR2\CR2_RIR_Dodecahedron.skp`. It writes the room as OBJ (also `CR2.obj`, from the
   geometry-only model beside it) into `<root>/work` and closes SketchUp. Click past its Welcome window if it shows one.
3. Run the engine: `set BRAS_ROOT=<root>` then `dotnet run -c Release -- fitted 60000 2 bands 2.0`
   (surface set, rays, image-source order, `bands` or a fixed scattering coefficient, response length in seconds). It writes `<root>/work/sim_*.csv`.
4. Compare: `python compare.py <root> <root>/work/sim_fitted_bands_60000_2_2.csv`.
