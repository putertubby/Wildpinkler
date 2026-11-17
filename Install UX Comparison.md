# Mod Installation UX: Wildpinkler vs. GitHub Mod Managers

Comparison of the installation UX and workflow for **FOMOD mods** and **pure archives**,
focused on source/destination selection and installation options. Competitors analyzed from
their GitHub repositories: [Vortex](https://github.com/Nexus-Mods/Vortex),
[Mod Organizer 2](https://github.com/modorganizer/ModOrganizer2) (community mirror),
and [Stellar Mod Loader](https://github.com/lVlyke/stellar-mod-loader).

---

## Wildpinkler (this workspace)

### FOMOD mod (`FomodInstallWizardDialog`)

1. **Progress dialog** opens first: "Analyzing '{modName}'…" with an indeterminate bar.
2. Archive is scanned; if a FOMOD is detected, the progress dialog closes and the **FOMOD wizard** takes over:
   - **Header**: mod name, author, version, description, optional module image (preloaded in the background).
   - **Step-by-step wizard**: one step per dialog page (Next/Back navigation). Each step shows groups of radio/checkbox option rows with thumbnails. Flags accumulate: choices in earlier steps control visibility/usability of later steps.
   - **Summary step**: read-only recap of all committed selections + a **destination path text box** (pre-filled with the game's `PluginDataFolder`, e.g. `Data`), validated as a safe relative path. A "remember path" checkbox.
   - **Warnings bar**: surfaces FOMOD `moduleDependencies` (e.g. minimum game version) as non-blocking warnings.
3. After the wizard closes: progress dialog reopens → **Hashing archive** → **Extraction** (determinate bar with byte progress) → **Scanning plugins** → done.
4. The install is recorded in the `ModInstallation` store with a **recipe** (full selections + destination) for replay.

### Pure archive (`ModDestinationDialog`)

1. **Progress dialog**: "Analyzing…" → archive layout is scanned (top-level directories).
2. **Destination dialog** opens:
   - **Source root dropdown**: pre-selected to the "suggested root" (deepest common parent if the archive is wrapped in a single folder). Info bar explains: "Content appears inside 'X'. That folder will be omitted."
   - **Destination text box**: relative path under the install root (pre-filled with the remembered `LastInstallPath` or the game's `PluginDataFolder`).
   - **Remember path** checkbox.
3. Progress dialog reopens → **Hashing** → **Extraction** (whole archive from source root into destination) → **Scanning plugins** → done.
4. The install is recorded with a `ManualInstallationRecipe { SourceRoot, Destination }`.

---

## Vortex (Nexus Mods)

### FOMOD mod

1. **Download** from the in-app Nexus web view → file lands in `Downloads/`.
2. User clicks **"Install"** in the Downloads view.
3. A **modal** shows the FOMOD steps on a **single scrollable page** (not a step-by-step wizard). Each group renders as a list of checkboxes (or radios for single-choice). No thumbnails. Flag logic (visibility/usability) is respected.
4. At the **bottom** of the modal: an **"Install to path"** text field (default empty = game Data folder) and an **OK** button. No "remember path".
5. Clicking OK runs extraction with a progress bar **in the same modal**.
6. On success the mod is added to the profile and **deployed** (hardlink/junction) into the game folder.
7. **No replay/recipe**: uninstall + re-install means re-walking the wizard. The archive is kept in `Downloads/`, so re-install is possible, but selections are not persisted.

### Pure archive

1. Same entry point: click **Install** in Downloads.
2. A modal shows a **file tree** of the archive. The user can pick a **sub-folder** as the install root (clicking a folder highlights it), or install the whole archive.
3. Below: an **"Install to path"** text field (default empty = Data folder).
4. Extract + deploy. No remembered path, no "remember" checkbox.

---

## Mod Organizer 2 (MO2)

### FOMOD mod

1. **Install** button (or drag-and-drop) → a separate **wizard window** (not a modal in the main window).
2. **Step-by-step**: one page per FOMOD step. Each page shows all groups as checkbox/radio rows. No thumbnails by default (optional in settings).
3. **Back/Next** navigation. The final page is a **summary** listing all selected options.
4. A **"Destination path"** field is present at the **bottom of every page** (default empty); the value persists through Back/Next.
5. On **Finish**: extraction runs with a progress bar. The mod folder is created as `mods/<ModName>/`.
6. **No recipe/replay**: delete + re-install means re-walking the wizard.
7. **Overwrite dialog**: if files already exist (previous install or another mod), MO2 offers a conflict/overwrite dialog for per-file choice.

### Pure archive

1. Install → extraction into `mods/<ArchiveName>/` directly. **No destination dialog at all**; the mod folder name is the archive filename.
2. If the archive has a single root folder, MO2 extracts into that folder (same idea as Wildpinkler's "suggested root").
3. **No path choice** for pure archives — everything goes into `mods/<Name>/`.

---

## Stellar Mod Loader

- Same lineage as Vortex: single scrollable FOMOD modal, "Install to path" field at the bottom.
- **Pure archive**: file-tree picker plus a destination field.
- Cross-platform (Windows/Linux); otherwise functionally equivalent to Vortex's install UX.

---

## Comparison table

| Aspect | **Wildpinkler** | **Vortex** | **MO2** |
|---|---|---|---|
| FOMOD navigation | Step-by-step wizard (Next/Back), one step per page | Single scrollable page | Step-by-step wizard (separate window) |
| FOMOD thumbnails | Yes, background-preloaded 128px previews | No (optional in some builds) | No (optional) |
| FOMOD destination path | On the summary step; pre-filled with game `PluginDataFolder` | Bottom of modal; default empty (Data folder) | Bottom of every page; default empty |
| FOMOD remember path | "Remember path" checkbox | No | No |
| FOMOD warnings | Warnings bar for module dependencies (min game version), non-blocking | Version gate check (skip/error) | Version gate check |
| FOMOD recipe/replay | Full recipe persisted; same selections re-install instantly without the wizard | Re-install = re-walk wizard | Re-install = re-walk wizard |
| Progress display | Separate progress dialog with phases (Analyzing → Hashing → Extracting → Scanning) + cancel + elapsed time | Progress bar in same modal | Progress bar in same dialog |
| Pure archive source root | Dropdown with suggested root + info bar | File tree; click a folder | Single-root auto-detection |
| Pure archive destination | Text box, pre-filled, relative path | Text field, default empty | No choice (always `mods/<Name>/`) |
| Pure archive remember path | Checkbox | No | N/A |
| Install reuse (hash-based) | Same archive + same selections → existing folder reused, no re-extraction | Always re-extracts | Always re-extracts |
| Extraction staging | Staging folder → atomic move | Direct extraction | Direct extraction |
| Conflict/overwrite prompt | None (extraction into isolated folder) | Overwrite dialog | Overwrite dialog |
| Cancel | Cancel button in progress dialog | Cancel in modal | Cancel |
| Batch install | Batch progress ("N of M") | One at a time | One at a time |

---

## Pros (Wildpinkler)

1. **Recipe/replay** — the most differentiating feature. Once a FOMOD is installed with specific selections, re-install (e.g. after an archive update or profile migration) replays the exact selections without showing the wizard. No competitor offers this.
2. **Hash-based install reuse** — if the archive and selections are unchanged, the install folder is reused instantly (no re-extraction). Faster than Vortex/MO2, which always re-extract.
3. **Thumbnails in the FOMOD wizard** — background-preloaded, so option previews appear instantly; Vortex/MO2 don't have this by default.
4. **Remember path for both FOMOD and pure archive** — reduces repetitive typing; absent in Vortex/MO2.
5. **Atomic extraction (staging → move)** — cancellation or failure leaves no partial files in the install folder; competitors can leave partial state.
6. **Rich phase-based progress** — the user always knows the current phase (Analyzing/Hashing/Extracting/Scanning), with elapsed time and a cancel button.
7. **Safe-path validation** — destination and source root are validated as safe relative paths (no `..`, no absolute paths), preventing directory traversal.
8. **Batch install support** — progress dialog shows "N of M" for batch installs.
9. **Non-blocking FOMOD warnings** — module dependencies (min game version) surface as warnings rather than hard-failing, which is more forgiving.

## Cons (Wildpinkler)

1. **FOMOD destination is buried in the summary step** — not visible during the selection steps. Vortex keeps it at the bottom of the modal (always visible) and MO2 repeats it on every page. The user doesn't see where they're installing until the very end. (Matches the open backlog item in Notes.md: *"Fomod installer; put path selection behind 'advanced' button or similar."*)
2. **No overwrite/conflict detection** — extraction into an isolated folder (`mod-installs/<modId>/`) means no file-level conflicts, but the user can't see which files would overwrite game files or other mods' files. Vortex and MO2 show an overwrite dialog.
3. **Pure archive: no visual file preview** — the destination dialog offers a directory dropdown but no file tree. Vortex shows a full archive file tree, making it easier to decide where to place things.
4. **Modal-dialog churn** — the flow opens a progress dialog, closes it, opens the wizard/destination dialog, closes it, and reopens the progress dialog (WinUI 3 single-modal-per-XamlRoot limitation). Vortex/MO2 keep everything in one modal.
5. **No per-file destination override in the FOMOD UI** — the destination base is a single path; per-file destinations come from the FOMOD XML. (Same as competitors, but worth noting as a flexibility limit.)
6. **No progress indication inside the wizard itself** — the wizard steps are instant; extraction progress only appears after the wizard closes. Vortex/MO2 show progress in the same dialog.
7. **Destination is a free-text field** — a folder picker or a combobox of known subfolders (e.g. `Data/SKSE`, `Data/textures`) would reduce input errors. (Competitors use free text too, so this is not a competitive disadvantage.)
8. **No "update existing install" prompt** — installing with a *different* selection than an existing install silently creates a second install folder; there is no offer to replace/update the existing one. Vortex/MO2 surface overwrite conflicts instead.
9. **Pure archive: no "add into existing mod folder" path** — the destination dialog always creates a new install; adding files to an existing mod requires the unmanaged-folder path.
10. **No archive content preview before install** — directory list only, not the files themselves (Vortex: full file tree).
