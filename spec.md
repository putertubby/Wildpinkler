# Wildpinkler

*Wildpinkler* is a native windows 11 mod manager for games like Skyrim. It works by loading an existing shim dll called `uufs64.dll` that installs hooks to implement a virtual (union) file system (VFS) in sub-processes started through a `CreateProcess` Win32 call.

## Application type

- A native Windows 11 application allowing hooking for virtual file system implementation in spawned sub-processes.
- The application is built as a C++ native core paired with a C# UI shell: the C++ core owns the `uufs64.dll` hook and all virtual file system resolution logic; the C# shell owns the UI, mods store, task queue, and remote site connectors.
- The UI shell is implemented in C# using WinUI 3 (Windows App SDK).
- The native core is implemented in C++ and shared between the hook DLL and the VFS query interface, ensuring a single implementation of the union/merge semantics.
- C# and C++ components communicate through a defined interop boundary (P/Invoke or IPC); the VFS merge logic is never duplicated in managed code.

### Build Environment Installation

Building Wildpinkler requires a mixed C++/C# MSBuild toolchain. A default Windows 11 install provides only `winget` and Windows PowerShell 5.1; everything below must be installed explicitly. Run the commands from an elevated PowerShell prompt.

#### Required tools

| Tool | Purpose |
| --- | --- |
| Git | Source control |
| Visual Studio 2022 (Community or Build Tools) | MSBuild, MSVC v143 C++ toolset, .NET SDK, Windows SDK |
| MSVC v143 x64 toolset + Windows 11 SDK | Compiles `uufs64.dll`, the VFS core and the query DLL |
| .NET 8 SDK | Builds the C# interop library and WinUI 3 shell |
| Windows App SDK C# build tooling | WinUI 3 project system and XAML compiler |

#### Installation

```powershell
winget install --id Git.Git --exact --source winget --accept-package-agreements --accept-source-agreements

winget install --id Microsoft.VisualStudio.2022.Community --exact --source winget `
  --accept-package-agreements --accept-source-agreements `
  --override "--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.NativeDesktop --add Microsoft.VisualStudio.Workload.ManagedDesktop --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --add Microsoft.VisualStudio.Component.Windows11SDK.22621 --add Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs --includeRecommended"
```

On build agents, replace `Microsoft.VisualStudio.2022.Community` with `Microsoft.VisualStudio.2022.BuildTools` (same `--override` argument); the workloads and components are identical.

If the .NET SDK is needed outside Visual Studio (for example on a build agent that only installs Build Tools):

```powershell
winget install --id Microsoft.DotNet.SDK.8 --exact --source winget --accept-package-agreements --accept-source-agreements
```

The WinUI 3 shell is built self-contained (`WindowsAppSDKSelfContained`), so the Windows App SDK runtime is *not* required on build machines. It is only needed on machines that run a framework-dependent build:

```powershell
winget install --id Microsoft.WindowsAppRuntime.1.6 --exact --source winget --accept-package-agreements --accept-source-agreements
```

#### Verification

Open a **new** PowerShell window so `PATH` changes take effect, then:

```powershell
git --version
dotnet --list-sdks
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
```

All three commands must succeed and the last must print an installation path. Then build the solution:

```powershell
.\build.ps1 -Configuration Debug
```

### Architecture

- **VFS core engine (C++)** — shared resolution logic used by both the hook and the query path; single source of truth for union/merge semantics.
- **Hook shim (`uufs64.dll`, C++)** — installs hooks in sub-processes launched via `CreateProcess` to implement the virtual file system at runtime.
- **VFS query interface (C++, native ABI or helper host)** — exposes VFS enumeration and per-file origin metadata to the UI without requiring a live hooked game process.
- **Process launcher (C++/C#)** — wraps `CreateProcess` and loads/configures the shim DLL for game launches.
- **UI shell (WinUI 3, C#)** — native Windows 11 UI; consumes the VFS query interface and mods store.
- **Mods (C#)** — local, searchable store of mod metadata (id, game, name, version, side-by-side versions).
- **Background task queue (C#)** — executes downloads, deletions, and scans asynchronously, decoupled from the UI.
- **Remote site connectors (C#, pluggable)** — per-site API/download/version-check logic; Nexus is included by default.

## User interface

- The UI is built with WinUI 3 (Windows App SDK) in C#, using native Fluent Design (Mica/Acrylic, rounded corners, light/dark theme) for a modern, minimalistic, contemporary Windows 11 look and feel.
- The UI includes a virtual file system view showing the merged VFS content (tree/searchable list) for the active profile.
- Each file in the virtual file system view expands to show origin details — contributing mod, version, archive/real path, and override precedence — sourced from the native VFS query interface.

## Functionality

### Games

- Total games list to be managed manually in the games panel, add, edit, delete games.
- Games that are associated with profiles can't be deleted. Offer option to delete all profiles when a game with associated profiles is requested to be deleted.
- Each game has:
  - a unique id
  - a descriptive name, not required to be unique but should warn about name collisions
  - the complete path to the installation directory
  - a reference to a game definition
  - optional launch arguments
- The game data is stored in a json structure

#### Game definitions

A *game definition* is the portable, machine-independent part of a game: it never contains local paths and can be shared between users. The local *game entry* (name, install path, launch arguments) references a definition by id; definition properties are never copied into the entry. New games must select a definition. The executable path is resolved from the local install path and the definition's relative executable path.

- Definitions are stored one per file as `<definitionId>.wpgame.json`, loaded from two layers:
  - built-in, shipped with the application in `Assets\GameDefinitions` (read-only, never overwritten by an import)
  - user, in `%LOCALAPPDATA%\Wildpinkler\game-definitions` (overrides a built-in with the same id)
- A definition has: schema version, definition id (filename-safe slug), name, definition version, author, description, steam id, executable path relative to the install folder, detection markers, virtual file system variables (name to relative path, the source of the `TOOL:DATA` style references used by profiles), merged views, and suggested launch arguments.
- A *merged view* is a named union-filesystem mount, independent of the variables above: an ordered list of one or more branch paths (highest priority first, overlaying the branches after it) plus an `IsWritable` flag marking whether the view gets a writable upperdir. A branch path is always absolute and may start with a `{VARIABLE}` placeholder instead of a literal drive letter; placeholders are not resolved at definition-edit time (the full variable set includes app-predefined variables plus ones a user can later add or override at the profile or tools level), so a branch is only checked for being non-empty and within the text length cap.
- Definitions are imported and exported from the Games panel. A definition file is untrusted input and is validated on read: the id must match `^[a-z0-9][a-z0-9._-]{0,63}$`, every relative-path field must be relative and resolve inside the install folder (no rooted, UNC, `..` or drive-qualified paths), variable names must match `^[A-Za-z][A-Za-z0-9_]{0,31}$`, merged-view names must be such an identifier or a `${identifier}` reference, and names must be unique within their own collection. A merged view must declare at least one branch, and file size, variable, marker and merged-view/branch counts are capped. Suggested launch arguments are presented to the user for confirmation and never applied silently.
- A definition can be edited from the Games panel: name, description, author, steam id, executable, detection markers, variables and merged views. The same validation rules apply on save, duplicate variable and merged-view names are rejected, and the definition version is increased so the change can be detected. Editing a built-in definition writes a personal copy to the user layer; the built-in file is never modified.
- A single invalid definition file is skipped with a warning and never prevents the remaining definitions from loading.
- A game whose definition is missing keeps working; the missing definition is flagged in the UI.
- Definitions are not uufs64 configuration files. They contribute variables and merged views that the profile export uses when generating the uufs64 configuration.


==TBD==

### Profiles

A profile is a modded instance of a game. Managed folders containing installed mods and output from tools are arranged in order of priority where a files in a higher priority folder overrides files with the same names in lower priority folders. The managed folder structure is later exported as a json file configuring the uufs64 virtual filesystem which is hooking Win32 functions when a game is launched from within wildpinkler (or from outside Wildpinkler using the config file explicitly).

- A profile is always associated with exactly one game.
- A profile has the following properties:
  - a unique Id
  - a descriptive name, not required to be unique but should warn about name collisions
  - a reference to a game, note that game properties are owned by the game and must not be copied. The game referene is set when the profile is created and is constant.
  - an ordered set of references to unmanaged and managed folders
- The game installation folder is automatically added to the profile as the lowest (bottom) priority folder when a profile is created. Note that the game folder is not managed by wildpinkler, just purely referenced. The priority of this folder can't be changed.
- An empty non-shared managed scratch folder used for save games and other files written by the game or tools is automatically created and added as the highest (top) priority folder when a profile is created. The priority of this folder can't be changed.
- The profile directory is created under `%LOCALAPPDATA%\Wildpinkler\profiles\<profileId>` and contains `data`, `logs`, `saves`, `settings` and `tools` subdirectories. `data` is the scratch folder referenced by the load order.
- The load order can be reordered by dragging a row or, from a row's actions menu, with `Alt+Up` and `Alt+Down`. The two pinned folders are always forced back to the top and bottom regardless of how a move was attempted, and cannot be removed.
- Deleting a profile permanently removes its managed directory.

#### Launch targets

A profile is launched through a *launch target*. The game is the default target; every enabled tool adds one more. Each target is exported as its own uufs64 configuration file inside the profile directory: `profile.json` for the game and `profile.<toolId>.json` for a tool. This is what lets a tool use different merged views, different variables and write access that the game does not need.

- A launch target resolves to an executable, arguments, a working directory, a flat set of resolved variables (the profile's own scope - system folders, computed built-ins, and the profile's/binding's own variables; a game or tool definition's variables were only ever local inputs to building its own merged views and never propagate this far), and a list of merged views with every mount path and branch expanded to an absolute path.
- The profile's load order is exported as one merged view mounted at the game's own install path, whose branches are the load-order folders in priority order and which is writable, so writes land in the pinned scratch folder rather than the game directory. This view is repinned last, so nothing else can occupy that mount path.
- The game definition's own merged views (saves, settings, ...) and any enabled tool's own merged views are added; a later layer's view at the same fully-resolved mount path replaces an earlier one outright rather than merging branch lists. The final list is ordered deepest-mount-path-first, since a nested view (e.g. a tool's `Data` view inside the game's own root) must be considered more specific than its shallower parent.
- A game/tool definition's mount path and branches are absolute paths, either rooted or expressed through variables like `${GameInstallPath}`. Each entity (game definition, tool definition, profile, per-profile tool override) resolves its own merged views from its own isolated variable scope - there is no chaining between them. A profile's (and a per-profile tool override's) views are absolute, built from its own variables plus the read-only system folders and its own read-only computed built-ins - a deliberately minimal set following the exact same naming (`PascalCase`) and non-overridable rules as the system folders: `InstallPath` (the modded game's root) and `ProfilePath` (the profile's own managed directory).
- A tool cannot know its own per-profile output folder in its portable definition, so instead of requiring a variable for it, the resolver automatically inserts that folder as the new highest-priority branch of any writable view the tool declares.
- `${...}` placeholders are expanded recursively within their own entity's scope only. An undefined placeholder, a reference cycle, or nesting deeper than 32 levels is rejected with a specific message rather than being passed on to uufs64, which has no such protection.
- Exporting a target's merged views bakes every local/user variable in fully, but a built-in variable (a system folder, or one of the profile's own computed built-ins) is never baked in as literal text - the resolved absolute path is symbolized back into its `${name}` placeholder wherever it is a prefix of a branch or mount path, resolvable through that same target's exported `variables` map. This keeps the exported config valid even if the profile folder or install path later moves.
- The exported uufs64 configuration file has exactly two top-level keys: `variables` and `mountpoints`. `variables` never contains a profile's/tool's/game's own user-defined variables (those are only ever inputs to building `mountpoints` above), and never contains a `FOLDERID_*` system folder (uufs64 already knows those internally) - it carries only the profile-computed built-in placeholder names (`InstallPath`, `ProfilePath`) still referenced by a `${name}` in a mountpoint's `root`/`branches`, plus the fixed `workingDirectory` key. Each entry in `mountpoints` has `name`, `root`, `branches` and `writable`, one per merged view in the target's already deepest-mount-path-first order.
- `workingDirectory` describes the launched executable's identity as seen through the virtual file system - resolved against the mounted install root, never against wherever the executable actually sits on disk. For the ordinary game executable and every tool this is identical to the real path, but a mod designated as the game's launcher (see [Mods as game launchers](#mods-as-game-launchers)) physically lives inside that mod's own installed folder; once its branch is mounted at the game's install root, the exported `workingDirectory` reflects where the game sees it there instead. The real on-disk executable is still what Wildpinkler actually starts, passed via `--target` (with `--args` and `--steamid` when non-empty), not through the config.
- Launching exports the target's configuration and then starts `uufs64ldr.exe --target <executable> [--args <arguments>] [--steamid <id>] <configPath>`.

### Tools

A tool is a launchable program tied to one or more games and enabled per profile; LOOT, FNIS and BodySlide are the motivating examples. A tool is split the same way a game is: a portable *tool definition* that can be shared, and a local *tool entry* that holds machine-specific paths. A tool is not a game and not a mod; it is an additional launch target of a profile. A tool can never start the game itself - see [Mods as game launchers](#mods-as-game-launchers) for how SKSE and similar loaders are handled instead.

- Total tools list is managed manually in the tools panel: add, edit, delete tools. Deleting a tool that profiles use also removes it from those profiles, after confirmation.
- Each tool entry has: a unique id, a descriptive name (warned about but not required to be unique), an install path, optional launch arguments, and a reference to a tool definition. New tools must select a definition, exactly as games must.

#### Tool definitions

- Definitions are stored one per file as `<toolId>.wptool.json`, loaded from the same two layers as game definitions: built-in in `Assets\ToolDefinitions` and user in `%LOCALAPPDATA%\Wildpinkler\tool-definitions`. Built-ins for LOOT, FNIS and BodySlide ship with the application.
- A definition has: schema version, definition id, name, definition version, author, description, supported game definition ids (empty means any game), executable path relative to the tool's install folder, an optional working directory, suggested launch arguments, detection markers, variables and merged views.
- Tools declare their own merged views explicitly. All paths in tool definitions must use variables like `${GameInstallPath}` or `${ToolInstallPath}` to resolve to absolute folders, identical to the rules for game definitions.
- Validation shares the game definition rules for the id, name, version, text lengths, variable names and merged views. Tool-specific rules: every supported game id must be a valid definition id and at most 64 may be declared; the executable is required and must be a safe relative path; detection markers must be safe relative paths.
- Definitions are imported, exported and edited from the Tools panel with the same rules as game definitions: a personal copy is written to the user layer when a built-in is edited, the definition version is increased on save, and a single invalid file is skipped with a warning.

#### Mods as game launchers

- Installing a mod (FOMOD or manual) scans its installed folder for executables. If any are found, the user is offered a picker to designate one of them as that mod's game launcher; the designation is optional and can be changed later from the mod's load-order row.
- While the owning mod branch is enabled, its designated launcher replaces the executable of the profile's single `Game` launch target. It reuses the profile's own merged views unchanged (no overlay swap, no output version, no merged views of its own).
- Only one mod launcher may be designated in a profile; designating another clears the previous designation. Disabling the designated mod falls back to the game's own executable until another enabled launcher is designated.

#### Tools in a profile

- A profile lists the tools whose definition supports its game, each with a toggle and an expandable per-profile configuration: a launch arguments override and variable overrides.
- Enabling a tool creates `tools/<toolId>` inside the profile directory and inserts it into the load order directly below the pinned scratch folder, above every mod folder, so tool output overrides mod files. It is an ordinary movable folder afterwards.
- Disabling a tool removes its folder from the load order but keeps the directory and its contents on disk.
- Enabled tools appear in the Launch split button's flyout on the Profiles panel; the button's own action launches the game.

### Mods

- Mods are compressed archive files, potentially including fomod format installation information.
- Mods can either be downloaded from remote systems, such as [nexus](https://www.nexusmods.com/) for example, or added manually as local files.
- When a mod is added it is assigned a unique id (if not already existing) and added to the local, searchable list of known mods including metadata such as supported game, mod name, mod version etc.
- Adding a new version of a mod will not replace older versions but live side-by-side with older versions.
- The user can remove mods manually using the UI, selecting mods and finally confirm the delete action.
- The user can initiate a scan to find unused mods using the UI. Unused mods are mods that are currently not installed in any profile. All unused mods will be added to the selection during the scan.
- Operations like deletions and downloads should be added to a queue when initiated by the user and executed in the background to allow the suer to move on.
- A *remote site* is a connector compiled into the application, not a user-authored record. Each one
  is a `IRemoteSiteProvider` in its own assembly (`Wildpinkler.Remote.Nexus` for Nexus Mods),
  registered once in a `RemoteSiteRegistry`. The Settings page therefore lists the sites this build
  supports and lets the user configure each one's credential, enabled state and download method; it
  cannot add or remove a site, because a site without a connector cannot work.
- Every connector reports its own capabilities (protocol links, update checks, tracked mods,
  checksum lookup, browser fallback, game catalog) and maps its own failures onto a shared
  `RemoteErrorKind` with a user-facing remedy, so the UI never has to know which site failed.
- Downloads from remote sites will be done either using an API or by clicking link on webpages for example. The method to be used is defined by the remote site settings.
- Credentials are per site and stored through the credential store, never in `remote-sites.json`.
  Nexus currently authenticates with a personal API key; its websocket single sign-on is implemented
  behind `IRemoteCredentialProvider` but reports itself unavailable until Nexus issues Wildpinkler an
  application slug.
- When a mod is downloaded from a remote site that supports the operation the mod manager will include a user-initiaded function to look for new versions and automatically download these.

### Mod installation and association

Mods are installed and associated with a profile directly from the Profiles panel's load order editor
- there are no separate "Mod installation"/"Mod association" panels.

- From a profile's load order editor, "Add mod..." opens a picker over the known mods list (filtered
  to the profile's game, with an option to show every mod), excluding mods already associated with
  that profile. A mod without a local archive cannot be selected.
- If the mod is a fomod, all installation choices (every wizard step's group/plugin selections) are
  collected first, one visible step at a time; a step's visibility and a plugin's conditional files
  can depend on earlier steps' choices. Only after every choice is made is a physical folder searched
  for or created.
- If the mod is not a fomod, the user is instead asked for a single destination folder, relative to
  the mod's own managed install folder, under which the archive's content is placed.
- Installation choices (the fomod selections, or the manual destination path) are hashed into a
  reuse signature and stored, together with the managed installation folder, in a local installations
  database. If an existing installation for the same mod has an identical signature, its folder is
  reused and no new extraction happens; several profiles can then reference the same folder. If the
  choices differ, a new folder is created and only the relevant files are copied into it.
- The resulting folder is added to the profile's load order as an ordinary movable, toggleable
  folder, associated with the mod. Toggling it off removes it from the merged content without losing
  its position in the load order or requiring reinstallation; removing it drops the association
  entirely (a mod's known-mods entry tracks which profiles currently reference it).
- Archive extraction and fomod parsing follow the full fomod XML schema (nested dependency
  composites, flag- and file-state-based conditions, install-step/group/plugin ordering,
  conditional file installs); a dependency node the parser cannot understand is treated as always
  satisfied rather than blocking the install, and the user is warned.

### Protocol links

A connector may claim a custom URI scheme; Nexus claims `nxm:`. The association is written under
HKCU and preserves whatever handler it displaced, so turning it off hands the scheme back rather than
deleting it. Activation is single-instance: a second launch redirects into the running process.

A link is always parsed into one of four outcomes, so an unrecognised link produces a message instead
of a silently ignored click:

| Link | Outcome |
| --- | --- |
| `nxm://<game>/mods/<id>/files/<id>?key&expires&user_id` | Downloadable |
| `nxm://<game>/mods/<id>` | Downloadable; resolves to the mod's primary file |
| `nxm://<game>/collections/<slug>/revisions/<n>` | Recognised, refused with an explanation |
| anything else | Refused with an explanation |

Before any bytes move, the link is resolved into a preview: mod and file metadata from the site, plus
the local game the site's game key maps to (`remoteGameKeys` on the game definition). The user
confirms that pre-filled record in a dialog, which also warns when no local game or profile matches,
since that is the point at which the download is still worth abandoning. The confirmation is
suppressible.

Three conditions are refused up front rather than as a late, opaque error: an expired `expires`, a
`user_id` belonging to a different account than the signed-in one, and a non-premium account with no
site-issued download key (which Nexus only ever issues from the mod page).

A locally added archive can be identified by its MD5 through the site's checksum lookup and filled in
with the same metadata. This is always user-initiated and costs one request per archive, because the
Nexus acceptable use policy prohibits bulk retrieval.

### Dependency management

Out of scope for now, and deliberately so: the Nexus v1 API exposes no requirements endpoint at all.
A resolver would have to combine FOMOD `moduleDependencies`, plugin master records and the v2 GraphQL
API, which is a separate problem from fetching a file. Mod entries already persist the fields such a
resolver would consume (`RemoteFileCategory`, `IsPrimaryFile`, `RequirementsRaw`).

Also out of scope: Nexus Collections beyond refusing them clearly, the v2 GraphQL API, browser
extensions and userscripts, and endorsing or tracking mutations from the UI.
