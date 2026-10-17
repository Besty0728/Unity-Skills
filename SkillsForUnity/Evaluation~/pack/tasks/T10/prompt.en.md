<!-- usk-eval prompt; task T10; lang en -->
R1. Move every material (.mat) under `{inc}` (all subfolders included) into `{mat}/<upper-case first letter of the file name>/`, keeping the file name (for example `moss.mat` goes to `{mat}/M/moss.mat`); create the destination folder if it does not exist.
R2. If the destination already holds a file of that name (whether it was there before or you moved it in), use the first free name among `<original name>_2.mat`, `<original name>_3.mat`, ...; process the materials in ascending ordinal order (by character code) of their original full paths.
R3. Every material must keep its GUID, and the renderers in the scene that use these materials must keep their references: only move the materials; do not create, copy, delete or overwrite any material file.
R4. Leave non-material files where they are; do not touch the assets that were already under `{mat}` or any asset outside `{inc}`.
R5. Delete the subfolders of `{inc}` that are empty after the moves (delete `{inc}` itself too if it is empty, keep it if it is not).
R6. In the answer, report the original path and the new path of every material you moved.
