# Wildpinkler

## Overview

Wildpinkler is used to modify, or _mod_ games on Windows 10 or 11 using a virtual file system. Currently the bundled virtual file system `uufs64` only supports x64 binaries but that may change in the future. Modification is done by downloading modifications, _mods_, either by using one of the _custom protocol handler_ URIs for direct "mod manager" download, e.g. `nxm:` for [NexusMods](https://www.nexusmods.com/), or by manually download archives.

The archives are then added to the Wildpinkler mod database and installed/extracted to a managed folder referenced from a _profile_. A profile is the name of a modification project for a game. A game may have multiple profiles associated at any time. The managed mod installation/extraction folders are then organized in a merged view that overlays the original game installation.

Each mod makes up one _branch_ in the merged view. When a file is opened in the path overlaied by the merged view each branch will be searched in priority order. The first matching file found "wins" the arbitration. This way files can be overridden, or even deleted, "virtually" without affecting the original game installation.

The top, _upper_ or _overlay_, branch is always the branch used when performing destructive file oprations such as writing to, or deleting, files. The bottom, _lower_ or _underlay_ branch serves as the foundational data blueprint and should never be writted to.

When the game is launched the game merged view has the general structure below. Branches are organized in order by _priority_. The top priority, _upper_ or _overlay_, and bottom priority, _lower_ or _underlay_, branches are fixed but the branches in between can be rearranged by the user to provide the desired resulting merged view content.

I.e. if the game is installed in C:\Game, with two mods installed (X and Z) with one install each (Y and W), and one tool generating otuput (U), a profile merged view is created with the mount point `C:\Game` and the following general structure:

1. `...\AppData\Local\Wildpinkler\profiles\<profileId>\overlay`
1. `...\AppData\Local\Wildpinkler\mod-installs\<modIdX>\<installationIdY>`
1. `...\AppData\Local\Wildpinkler\profiles\<profileId>\tool-output\<toolIdU>-<version>`
1. `...\AppData\Local\Wildpinkler\mod-installs\<modIdZ>\<installationIdW>`
1. `C:\Game`.

The profile folder structure is detailed below. The top directories are always the same but sub-directories in the `custom` and `tool-output` folders vary depending on game and tool definitions as well as the tools installed in the profile.

```
profile-folder/
├── custom/                 # Root directory for game- or tool specific branches used in custom views.
├── overlay/                # Game installation directory overlay used to catch spurious writes.
├── tool-output/            # Root directory for tool output branches.
├── profile.json            # Game launch profile, including merged view branch priority.
└── profile.<toolId>.json   # Tool launch profile(s)
```

When launching a tool the tool profile will contain a very similar merged view as the game view. The most obvious difference is that the overlay branch is replaced with a new version of the tool output. The structure will look something like this in our example:

1. `...\AppData\Local\Wildpinkler\profiles\<profileId>\tool-output\<toolIdU>-<new version>`
1. `...\AppData\Local\Wildpinkler\mod-installs\<modIdX>\<installationIdY>`
1. `...\AppData\Local\Wildpinkler\profiles\<profileId>\tool-output\<toolIdU>-<old version>`
1. `...\AppData\Local\Wildpinkler\mod-installs\<modIdZ>\<installationIdW>`
1. `C:\Game`.

Where `<toolIdU>-<new version>` is a new folder receiving the new tool output. Once the tool is finished running the new tool output branch replaces the old in the merged view.

In addition to the profile merged view, _custom_ merged views can be defined in game and tool definitions. These views live side-by-side with the profile view and cover mountpoints outside of the game installation. Note that these views may reference relative branches, in which case the relative branch `foo\bar` resolves to `...\custom\<gameId>\foo\bar` or `...\custom\<toolId>\foo\bar` depending on whether the branch is part of a game or a tool definition.

### Notes

- When installing a mod, if a folder containing an installation using the exact same installation choices already exists, the archive is never extracted but the existing folder directly referenced instead.

- `tool-output` and `custom` sub-folders are owned by the profile and never shared with other profiles.

- Wildpinkler is responsible for managing all sub-directories in the `custom` and `tool-output` folders. That means creating and deleting directories as required as well as clearing content on user request. All sub-directories are owned by, and local to, the profile.

- Wildpinkler must scan the game and tool definitions for relative paths (references into the _custom_ sub directory) and create sub-directories as required to provide the requested branches.

- Relative paths must be expanded into absolute paths when creating the game and tool profile launch (json) files.

- Wildpinkler will do garbage collection from time to time to remove unreferenced folders.

- All profiles are generated as the same time to avoid discrepancies.

### Custom Protocol Handlers

Remote sites are connectors compiled into Wildpinkler rather than records the user creates. Each one
lives in its own assembly behind a shared contract, so "Nexus Mods" is a replaceable implementation
and nothing outside it knows about Nexus. Nexus Mods ships in the box.

A connector may claim a URI scheme; Nexus claims `nxm:`. Turn the association on from Settings, and
the "Mod Manager Download" button on a mod page hands the link to Wildpinkler. Before anything is
downloaded the link is resolved into a preview - mod name, author, summary, file name, version, size,
category, upload date - and matched against a local game through the `remoteGameKeys` map on the game
definition, so an unrecognised game is reported while abandoning the download is still cheap. Confirm
the pre-filled record and the transfer starts; the confirmation can be turned off.

A link Wildpinkler cannot download - a collection, or a malformed URI - is refused with an
explanation rather than silently ignored. So are an expired link, a link created under a different
account, and a non-premium account with no site-issued download key, since Nexus only issues those
from the mod page.

If an `nxm:` link does not reach the download confirmation, try it once with Wildpinkler closed and
once with Wildpinkler already running. Startup, activation, and preview failures are recorded in
`%LOCALAPPDATA%\Wildpinkler\diagnostics.log`. The log does not include the link or its query values;
do not share a complete `nxm:` link because its query can contain short-lived download credentials.

Downloads appear on their own Downloads page with per-transfer progress, cancel and retry, mirror
fallback and checksum verification. An archive added by hand can be identified through the site's
checksum lookup and filled in with the same metadata, one request at a time and only when asked.

Wildpinkler evaluates dependencies extracted from FOMOD metadata and plugin masters, plus constraints
entered by hand. The result is advisory: unresolved requirements, order violations, conflicts and
game-version mismatches are shown before launch but can be acknowledged.

### Mod lists

A Wildpinkler mod list is a portable recipe stored as `*.wpmodlist.json`. It records the game
definition and executable-version requirement, exact remote file identities and archive hashes,
ordered enabled state, launcher designation, structured FOMOD or manual installation choices,
portable profile overrides and tool prerequisites. It never contains local database ids, install
paths, credentials, short-lived download URLs, profile overlay content, saves, settings or generated
tool output.

Use **Export mod list** from a selected profile to add an immutable revision to the local catalog and
optionally write a shareable copy. The export is graded:

- **Reproducible** - every archive is identifiable and every installation recipe is replayable.
- **Guided** - one or more private files, folders, settings or tool steps require user action.
- **Unavailable** - required content has no verifiable archive or acquisition instructions.

Incomplete requirements remain in the export instead of being silently omitted. The **Mod lists**
page imports, searches, filters, inspects and removes catalog revisions. **Create profile** first
builds a preflight plan without publishing a profile or moving bytes. Resume then reuses matching
archives/installations, downloads exact remote files where the account permits it, verifies provider
MD5 and manifest SHA-256, replays structured FOMOD/manual recipes, and pauses on private archives,
unmanaged folders, changed installers, missing tools or tracked-manual steps.

Every task transition is saved to `%LOCALAPPDATA%\Wildpinkler\mod-list-builds.json`. Closing the app
does not publish a partial profile; on restart interrupted automatic work becomes ready to resume and
interrupted user/tool work returns as action required. Tool invocations target registered tool
definitions only and require one explicit consent decision per build. Blocking dependency or game
version failures prevent publication; load-order/conflict advisories require acknowledgment. The
profile is added to the Profiles page only after validation and commit. Discard removes only its
unpublished profile folder; verified archives and shared installations remain reusable.

Wildpinkler mod lists are independent of Nexus Collections. Nexus collection links remain recognised
and refused; Wildpinkler does not import that format or redistribute mod archives.

## Clarifications

This section resolves the questions the Overview above leaves open, and is authoritative wherever another section disagrees with it.

### Storage layout

Everything Wildpinkler manages lives under `%LOCALAPPDATA%\Wildpinkler`:

```
%LOCALAPPDATA%\Wildpinkler\
├── archives/                                  # Downloaded mod archives
├── mod-installs/<modId>/<installationId>/     # Shared, deduplicated mod installations
├── mod-lists/<listId>/<revision>.wpmodlist.json # Validated immutable manifest catalog
├── mod-list-builds.json                       # Persistent resumable build journal
├── profiles/<profileId>/                      # One managed folder per profile
├── games.json, tools.json, mods.json, profiles.json, mod-installations.json
```

A mod installation is global and shared: it is keyed by the mod, source archive SHA-256 and exact
installation choices, so two profiles that install the same archive identically reference the same
folder instead of extracting it twice. FOMOD records retain selected step/group/plugin names and the
`ModuleConfig.xml` hash; manual records retain source root and destination. It is never written to by
a running game - only the profile overlay is.

Everything below a profile folder is the opposite: owned by exactly that profile and never shared.

### Overlay, upper, scratch

The profile's `overlay` folder is the merged view's *upper* directory in UnionFS terms - the single writable branch that receives copy-on-write copies, whiteouts and any spurious writes the game makes into its install folder. "Overlay", "upper" and "scratch" all refer to this same folder; the UI and the code call it the overlay.

### Relative paths in a merged view

A merged view declared by a game or tool definition may use relative paths, and the two halves resolve differently:

- A relative **mount path** is relative to the owning entity's install folder, because it names a location that already exists in the game's or tool's own tree - `Data` under a game definition means `<game install path>\Data`.
- A relative **branch** is relative to `<profile folder>\custom\<gameId|toolId>`, because a branch is content the profile owns and Wildpinkler has to create. A branch `foo\bar` in a game definition resolves to `<profile folder>\custom\<gameId>\foo\bar`.

Anything that expands to an already-rooted path - typically because it was built from a read-only system folder variable such as `${Documents}` - is used verbatim and neither rule applies. Wildpinkler creates every resolved `custom` branch folder before a launch, since a union filesystem cannot mount a branch that does not exist.

### Tool output versions

Tool output is per profile and versioned: `<profile folder>\tool-output\<toolId>-<version>`. Enabling a tool creates version 1 and inserts it into the profile merged view directly below the overlay, so tool output overrides mods.

Launching that tool creates the *next* version and mounts it as the top branch of the tool's own merged view, in place of the profile overlay - so a run never mixes its output with the previous run's, and never pollutes the overlay. When the tool exits, the new version is promoted: the profile's branch is repointed at it and the previous version becomes garbage. "Clear output" on the Tools tab deletes every version for that tool and restarts at version 1.

A mod designated as a game launcher (see [Mods as game launchers](#mods-as-game-launchers)) is the exception: it starts the game itself through the profile's single Game launch target, so it reuses the profile merged view unchanged - no overlay swap, no output version.

A tool definition can also declare that it produces no output at all (`"producesOutput": false`), for a tool that only changes settings - LOOT, for example, which writes its own configuration outside the game folder. Enabling such a tool creates no `tool-output` folder and adds nothing to the profile merged view; it becomes a launch target like any other, and its writable merged views write straight to the branches the definition declares.

### Garbage collection

A cleanup pass removes managed directories nothing references any more:

- tool-output versions other than the current one and the one a run is currently writing,
- `custom\<ownerId>` folders for a game or tool the profile no longer uses,
- mod installations no profile's merged view references, along with their record.

A folder that is still locked by a running process is simply skipped and collected on a later pass. Cleanup is available on demand from the Profiles page; deleting a profile removes its whole folder outright and needs no collection.

### Profile-scoped configuration

A profile can carry its own variables and merged views, and a per-profile tool binding can carry variable and merged-view overrides. These are honored by the launch configuration exactly as described under [Variables](#variables) and [Merged views](#merged-views), but they are not editable from the Profiles page: that surface is about the merged view, its mods and its tools. Existing values are preserved untouched through every profile edit.

### All profiles are generated at the same time

"Generated at the same time" means the game profile and every enabled tool profile are resolved from one snapshot of the profile's state and written together, so `profile.json` and each `profile.<toolId>.json` can never disagree about load order, variables or merged views.

## UI Style

Wildpinkler UI is designed according to the "Fluent Design System" (specifically Fluent 2) and implemented in WinUI 3 using standard controls whenever possible. The app shall have productivity visual density and style. Detailed guidelines can be found in [UI Design Reference](./UI%20Design%20Reference.md).

Managed test design guidelines are documented in [Test Design Reference](./Test%20Design%20Reference.md).

The managed test project is `tests/Wildpinkler.App.Tests`. Build the solution with `build.ps1`, then run its xUnit tests with the Visual Studio test runner when the standalone .NET SDK does not provide the Windows App SDK packaging tasks:

```powershell
.\build.ps1 -Configuration Debug
& "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe" .\build\test-bin\x64\Debug\Wildpinkler.App.Tests.dll
```

## Distribution

Wildpinkler is distributed as an unpackaged x64 desktop application. The release pipeline produces both an Inno Setup installer and a portable ZIP archive from the same staged application directory. It remains framework-dependent: users must have the x64 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) installed. The Windows App SDK runtime is not a user prerequisite because the application deploys its Windows App SDK files locally.

Creating a distribution requires the .NET SDK pinned in `global.json` (9.0, `rollForward: latestFeature`), the Visual Studio C++/MSBuild toolchain used by `build.ps1`, and [Inno Setup 6](https://jrsoftware.org/isinfo.php). The pin keeps the analyzer rule set identical on every machine; a newer SDK band would surface new diagnostics as build errors because `TreatWarningsAsErrors` is on. It also requires an externally built x64 VFS asset bundle. For now that bundle is separate from this repository and must contain at least:

```text
vfs-assets/
  uufs64ldr.exe
  uufs64.dll
  <any loader or shim DLL/resource dependencies>
```

Build the versioned artifacts by supplying the release version and the asset-bundle directory:

```powershell
.\build-distribution.ps1 -Version 0.1.0 -VfsAssetsPath C:\releases\uufs64\0.1.0\win-x64
```

The command builds the Release solution, publishes the framework-dependent `win-x64` app, adds the external loader and shim, and verifies the staged payload. Its outputs are written under `build\distribution\<version>\artifacts\`:

- `Wildpinkler-<version>-win-x64.zip`
- `Wildpinkler-<version>-win-x64-setup.exe`
- `Wildpinkler-<version>-win-x64.sha256`

`uufs64ldr.exe` and `uufs64.dll` are copied beside `Wildpinkler.App.exe`, which is required by the application's launch contract. The installer detects the x64 .NET 8 Desktop Runtime and opens the official download page when it is unavailable. Uninstall preserves `%LOCALAPPDATA%\Wildpinkler` by default and asks before deleting it.

The external `uufs64ldr.exe` is the lifetime owner for every launched target. After accepting the target, configuration and optional Steam arguments, it must remain running until the injected target process tree has exited. This includes a launcher or Steam bootstrap process that exits after spawning the actual game or tool. It must keep VFS injection active in relevant descendants, return a nonzero exit code when the target run fails, and clean up only after that final completion. Wildpinkler observes this loader process asynchronously: it remains usable for other profiles but locks mount-changing operations on the running profile. Closing Wildpinkler does not stop the loader or its target.

When the loader or shim moves into this repository, replace the external asset-acquisition input with that project’s build output while retaining the same staged layout and installer/archive workflow.

## Variables

Variables are local to the entity that defines them: a game definition, a tool definition, a profile,
and a per-profile tool override each have their own, completely isolated set. There is no chaining or
inheritance between entities - a variable defined in a game definition is never visible to a tool
definition, a profile, or another game, and vice versa. A variable exists purely to shorten the merged views (see below) declared by that same entity; once a string leaves an entity (for example, a game definition's merged view is carried into the profile's effective configuration), every `${variable}` placeholder in it has already been fully expanded using only that entity's own variables - nothing is left for a later entity to resolve.

The one exception is the small set of read-only, system-provided known-folder variables and the
Wildpinkler-computed variables below: they are seeded automatically wherever they are meaningful,
since they are constants rather than something any entity authors, and follow identical naming
(`PascalCase`) and non-overridable rules. A user-defined variable is always `kebab-case` and local
to the single entity that defines it.

Built-in variables (both the system folders below and a profile's own computed ones) are never baked
into the exported uufs64 configuration as literal text - a local/user variable is expanded completely, but a built-in stays a literal `${name}` placeholder there, resolvable through the exported `variables` map. This keeps an exported config portable if the profile folder or install path later moves; only local, human-authored text is ever inlined. A user-defined variable itself never appears in the exported `variables` map, though - see [uufs64 configuration file shape](#uufs64-configuration-file-shape) for exactly what that map contains.

==NOTE: Eventually there will be protection against circular variable definitions in uufs64 but right now there is none. Just don't do it!==

### Supported built-in, read-only, system variables

Available in every entity's scope, everywhere:

|Variable|Known folder|Description|
|--|--|--|
|Documents|FOLDERID_Documents| Identifies the standard "Documents" (or "My Documents") user file system directory in Windows Vista and later operating systems.|
|LocalAppData|FOLDERID_LocalAppData|Pointing to a user's non-roaming application data directory.|
|LocalAppDataLow|FOLDERID_LocalAppDataLow| Pointing to application data for non-roaming programs running with low security privileges.|
|Profile|FOLDERID_Profile|Points directly to the root directory of the current user's Windows profile.|
|RoamingAppData|FOLDERID_RoamingAppData|Points to a user's roaming application data directory.|

### Profile-scope computed built-ins

A profile (and a per-profile tool override) additionally sees a small, deliberately minimal set of
variables computed by Wildpinkler itself, on top of its own user-defined variables. A game or tool
definition does not see these - it only ever sees its own variables plus the system folders above,
since neither an install path nor a profile folder exists yet at that portable, shareable stage.

|Variable|Description|
|--|--|
|InstallPath|Root directory of the modded game.|
|ProfilePath|The profile's own managed directory.|

## Profiles

A profile is an entity managed by Wildpinkler that contains information about a game and installed mods. When instantiated a profile manifests as a Wildpinkler-managed directory with the structure below. `profile.json` is a configuration file for uufs64 specifying the virtual file system to inject using `uufs64ldr.exe`; every enabled tool gets its own `profile.<toolId>.json` next to it, because a tool may need different merged views and write access than the game does.

```
profile-folder/
├── custom/
│   └── <gameId|toolId>/    # Where that entity's relative merged-view branches resolve to
├── overlay/                # The merged view's writable upper branch (spurious writes, logs, saves)
├── tool-output/
│   └── <toolId>-<version>/ # One folder per tool run; the current version is a profile branch
│
├── profile.json            # Game launch profile, including merged view branch priority
└── profile.<toolId>.json   # Tool launch profile(s)
```

Mod installations are not part of this folder - they are shared between profiles and live under `mod-installs/`:

```
mod-installs/
├── <mod1Id>/
└── <mod2Id>/
```

### uufs64 configuration file shape

Each exported config has exactly two top-level keys, `variables` and `mountpoints`:

```jsonc
{
  "variables": {
    "InstallPath": "C:\\Games\\Skyrim Special Edition", // profile-computed placeholder a mountpoint may reference - never a FOLDERID_* system folder or a user-defined variable
    "workingDirectory": "${InstallPath}"
  },
  "mountpoints": [
    {
      "name": "GameInstall",
      "root": "${InstallPath}",
      "branches": ["C:\\Users\\...\\profiles\\<profileId>\\overlay", "..."],
      "writable": true
    }
  ]
}
```

`variables` never carries a profile's/tool's/game's own user-defined variables - those are only ever inputs used to build the `mountpoints` list at export time, per [Variables](#variables). It also never carries a `FOLDERID_*` system folder (`Documents`, `LocalAppData`, `LocalAppDataLow`, `Profile`, `RoamingAppData`) - uufs64 already knows those internally, so exporting them would be redundant. It carries only the profile-computed built-in placeholder names (`InstallPath`, `ProfilePath`) a mountpoint's `root`/`branches` may still reference (see [Merged views](#merged-views)), plus the fixed `workingDirectory` key. The target executable, its arguments and the game's Steam app id are passed directly to `uufs64ldr.exe` as `--target`/`--args`/`--steamid` instead of being duplicated into the config.

`workingDirectory` is the launched executable's identity *as seen through the virtual file system* - i.e. resolved against the mounted install root, never against wherever the file actually happens to sit on disk. This only diverges from the real on-disk path for a mod designated as the game's launcher (see [Mods as game launchers](#mods-as-game-launchers)): its executable physically lives inside that mod's own installed folder, but once its branch is mounted at the game's install root, the game sees it at `InstallPath` plus its path relative to the mod's own folder. Wildpinkler itself still starts the real on-disk executable through `uufs64ldr.exe --target`; only the exported config's `workingDirectory` describes the virtual identity.

## Tools

A tool is a launchable program tied to one or more games and enabled per profile — LOOT, FNIS or BodySlide, for example. A tool can never start the game itself; an executable that does (SKSE and similar) is installed as a mod instead and designated as that mod's game launcher (see [Mods as game launchers](#mods-as-game-launchers)). Like a game, a tool is split into a portable *tool definition* (`<toolId>.wptool.json`, shareable, no local paths) and a local *tool entry* (name, install path, launch arguments). Enabling a tool that produces output creates `tool-output/<toolId>-<version>/` and inserts it into the profile merged view directly below the profile overlay, so tool output overrides mods. A tool that only changes settings declares `"producesOutput": false` and gets neither.

A tool's merged views and all paths within them must use variables like `${GameInstallPath}` or `${ToolInstallPath}` to resolve to absolute folders. When you enable a tool in a profile, you can toggle "Capture tool output" to organize that tool's writes into a separate priority folder, or disable it to write directly to the views it declares.

## Mods as game launchers

Installing a mod scans its installed folder for executables. If any are found, the user is offered the choice of designating one of them as that mod's *game launcher* - an alternate way to start the game (SKSE and similar loaders are installed and designated this way, not as tools). A mod's launcher designation can be changed later from its load-order row's "more" menu.

While the owning mod is enabled, its designated launcher replaces the executable of the profile's single Game launch target and reuses the profile's own merged views unchanged - no overlay swap, no output version. Only one mod launcher may be designated in a profile; designating another clears the previous designation.

## Merged views

A merged view has a friendly, purely cosmetic `Name` (not required to be unique - a colliding name is
numbered where views are listed together, e.g. "GameData", "GameData (2)") and a `MountPath`, which is
the real identity used to merge/override a view across layers: a game definition's, a tool
definition's, a profile's and a per-profile tool override's views are combined by matching mount path
(after full resolution to an absolute path), not by name - a later layer's view at the same mount path
replaces the earlier one outright. Both the mount path and every branch are, by default, relative to
the owning entity's install/tool folder (auto-prefixed later); either may instead be built from a
read-only system folder variable (see Variables), in which case it stays absolute. A profile's own
views may reference its computed built-ins (`InstallPath`, `ProfilePath`) in addition
to its own variables.

Two merged views may legitimately nest - for example a `GameData` view mounted inside a `GameRoot`
view's own subtree. The more specific (deeper) mount path takes precedence for its own subtree; the
shallower view still covers everything else. The UI shows a quick "N branches" flyout per view instead
of requiring a separate dialog to inspect the branch stack, and hovering a view's name shows its
resolved mount path as a tooltip.

## Terminology

In a Union Virtual File System (UnionFS), the standard, widely accepted terminology for these components is:

- Merged View (or Union Path): The single, unified directory tree presented to the user.
- Branches (or Layers): The individual directories being combined to create the merged view.

Here is how the specific paths and priorities are named in uufs64:

### The Component Paths

- Merged Directory (merged): The final path where the unified file system is mounted and accessed.
- Upper Directory (upperdir): The high-priority, writable layer. Changes made in the merged view are physically written here.
- Lower Directory (lowerdir): The low-priority, read-only layer (or layers). It acts as the base template.

### Key Concepts

- Priority: Layers are stacked in a specific order. If a file exists in both layers, the version in the upper layer hides (or "overlays") the version in the lower layer.
- Copy-on-Write (CoW): If you modify a file that only exists in the lower layer, the system automatically copies it to the upper layer first, then applies your changes.
- Whiteouts: If you delete a file belonging to a read-only lower layer, the system creates a special hidden file (a "whiteout") in the upper layer to hide it from the merged view.

## Authentication

Nexus Mods authenticates with a personal API key, generated on the
[API access page](https://www.nexusmods.com/users/myaccount?tab=api) and pasted into Settings. Keys
are held in the credential store, never in `remote-sites.json`. "Validate" confirms the account name,
whether it is premium, and how much of the request budget is left.

Wildpinkler identifies itself on every request as the Nexus acceptable use policy requires, and
throttles itself to a burst allowance that recovers one request per second.

Single sign-on is wired up behind the same interface but stays disabled: the websocket handshake
needs an application slug that only Nexus staff can issue.

## Assistant

The assistant pane answers questions about your setup and can run the same actions you can, through
the same confirmation gate and audit journal. It never has a private set of operations.

Out of the box it is pointed at a local [Ollama](https://ollama.com/download) server on
`http://localhost:11434/v1`, which costs nothing and needs no key. Install Ollama and run
`ollama pull qwen3:8b` to use it. Settings also carries one-click presets for OpenRouter, Groq,
OpenAI and Azure OpenAI, plus a custom option for any other OpenAI-compatible server such as LM
Studio. Those providers need your own API key; no shared key ships with Wildpinkler.

Keys are encrypted with DPAPI for your Windows user, in the same store as the site credentials, and
are kept in a separate slot per provider so switching does not discard the previous one. They never
appear in `app-settings.json` or in the logs.

Reading actions run on their own. Anything that changes your setup is proposed in the conversation
and waits for you to allow it.

Three modes decide how much it may do, switchable from the pane's options menu or Settings:
**chat only**, where no actions are offered to the model at all; **ask before every action**; and
**look things up freely**, the default, where reading runs unattended and changes still wait for you.

What you type, and the names and paths that actions return about your games, profiles and mods, are
sent to whichever provider you choose. Wildpinkler asks you to confirm that once for each provider
host, before the first question it sends there. The local Ollama default sends nothing anywhere.

