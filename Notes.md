# Powershell

Install Powershell - Works better with copilot:

winget install Microsoft.PowerShell

Check version:

$PSVersionTable.PSVersion

## Windows Powershell

~~~
Major  Minor  Build  Revision
-----  -----  -----  --------
5      1      26100  9444 
~~~

## Powershell

~~~
Major  Minor  Patch  PreReleaseLabel BuildLabel
-----  -----  -----  --------------- ----------
7      6      6                      
~~~

## Settings

~~~

    "terminal.integrated.profiles.windows": {
        "PowerShell": {
            "source": "PowerShell",
            "icon": "terminal-powershell"
        }
    },
    "terminal.integrated.shellIntegration.enabled": true,
    "terminal.integrated.defaultProfile.windows": "PowerShell"
~~~

# Ollama

ollama run logicbeat/qwen3.8-27B_GSQ_RCO
ollama ps

ollama create qwen3.8-27B_GSQ_RCO-128k -f ./Modelfile
ollama run logicbeat/qwen3.8-27B_GSQ_RCO

http://127.0.0.1:11434

128kB: 131072

## Modelfile

~~~
FROM logicbeat/qwen3.8-27B_GSQ_RCO
PARAMETER num_ctx 131072
~~~

# Documentation

Press Win + R, type %userprofile%\Documents\My Games\Skyrim Special Edition, and press Enter. [1] (https://www.eneba.com/hub/games/game-guides/how-to-make-skyrim-fullscreen/)

Open SkyrimPrefs.ini in Notepad. [1] (https://www.eneba.com/hub/games/game-guides/how-to-make-skyrim-fullscreen/)

Find the [Display] section and change the values:

bFull Screen=1
bBorderless=0
iSize W= (set to your monitor's width, like 1920)
iSize H= (set to your monitor's height, like 1080) [1] (https://www.eneba.com/hub/games/game-guides/how-to-make-skyrim-fullscreen/), [2] (https://help.bethesda.net/app/answers/detail/a_id/26054/~/im-unable-to-launch-the-elder-scrolls-v%3A-skyrim-in-fullscreen.-i-unchecked-the)

Save the file. [1] (https://www.eneba.com/hub/games/game-guides/how-to-make-skyrim-fullscreen/), [2] (https://help.bethesda.net/app/answers/detail/a_id/26054/~/im-unable-to-launch-the-elder-scrolls-v%3A-skyrim-in-fullscreen.-i-unchecked-the)

# Change git author and email:

git rebase -r 6a8b51fbf9cb1dea6a35cceb5cbb04833c8f86ab --exec 'git commit --amend --no-edit --reset-author'

# Backlog

## Bugs

- [ ] Add Mods dialog (in Profile page) - width jumps when scrolling in mod list. (cosmetic)
- [ ] Add mod with dependencies should be offered as part of the add mod workflow, not a separate button (cosmetic)
- [ ] Don't step the tool output overlay folder if there are no new files written.
- [ ] Disabling/enabling mods with tools does not remove and bring back tools or overlays correctly. (see above)
- [ ] Toolbutton menus in profile page does nothing
- [ ] "Chose roles" menu item does nothing
- [ ] Data pre-fill is indistinguishable from a remembered path, need to be obvious to user
- [ ] Non-fomod installer; make it easier to understand whats going on. Fomod installer; put path selection behind "advanced" button or similar. Is alwayd plugin folder??
- [ ] Remove tool menu item does nothing in ui, unclear whether the operation does anything at all. Trashcan works!
- [ ] 500ms timer + scanning archive is instantaneous - only make the extract (and analysis) of each file part of progress. Don't show progress until a reasonable time passed (and some work remains to be done) to avoid flickering windows.
- [ ] Why are there assistant "what can assistant do" settings? Dropdown in assistant pane should suffice?
- [ ] When LOOT is running... Bad message! (cosmetic)
- [ ] Renaming a mod drops dependencies (only when depending??)
- [ ] Download does not resume automatically after restarting wildpinkler (or ui not updated)
- [ ] Status in mod list not properly updated when mod finished download.
- [ ] Check! Game launcher is mutually exclusive (only one mod at a time (at most) can launch)
- [ ] Insufficient logging
- [ ] Profiles files must be named "readbly" - i.e. tool-id rather than a guid.
- [ ] game tab in profile (move working directory override there)
- [ ] CVEs on ImageSharp 3.1.12 suppressed, alternative needed!
- [ ] Can the deployment be upgraded from .Net8 to something more modern to allow native image decoding instead of ImageSharp?
- [ ] Rename: Custom views -> game views
- [ ] Rethink the profile folder structure. Views could be forced to be named uniquely -> profiles/id/views/…  (custom\skyrim-se\Documents) maybe simply overlay/<view name>?

profiles/<uuid>/
├── overlay-branches/
│   ├── profile/                            # hard-coded, must not be an allowed game or tool name
│   │   └── GameInstall/                    # view name
│   │       ├── pandora-behaviour-engine/   # tool name (id)
│   │       │   ├── v7/                     # output version
│   │       │   └── v8/
│   │       ├── bodyslide/                  # tool name (id)
│   │       │   └── v8/
│   │       └── overlay/                    # profile overlay branch (hard-coded, must not be an allowed tool name)
│   ├── skyrim-se/                          # game name (id)
│   │   ├── Documents/                      # view name
│   │   └── LocalAppData/                   # view name
│   ├── loot/                               # tool name (may not exist if no view defined)
│   └── bethini-pie/
├── profile-skyrim-se.json                  # game launch file
└── profile-loot.json                       # tool launch file

- [x] Installing a mod always triggers reordering of load order (plugin.txt) and sorting warning, even though no new plugins were added. Ordering warning should only appear if the load order changes through a plugin addition, plugin removal does not change order. Also, now behaviour seems to be that adding a mod always forces a reordering of plugins in the load order. Adding a new mod shall not change the ordering at all and the new plugin (if any) should be put last in the load order and ordering of all other plugins remain same as before. 
- [x] Tool output isn't enabled when created, output goes to general overlay instead.
- [x] Enable/disable mod does not trigger regereration of profile.json and Plugins.txt. Must restart Wildpinkler to make profile.json update properly
- [x] Restore "HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps" after debugging
- [x] Mod install
   - [x] Long delay between clicking add mod on large archive (BHUNP) and dialog shows up with just spinning donut as indicator. Improve/cancel?
   - [x] Installing fomod files from large archive (BHUNP) takes a very long time!
   - [x] Cancel fomod installation of large archive (BHUNP) does not close the dialog (or maybe after a long time?)
   - [x] Infobar for text and cancel button appears in very strange place (right of "reveal folder" button), mangling the UI layout.
   - [-] Installing same mod twice?
   - [x] Install mod dialog have tiny space for selectable options. Dialog overall also seems very small for the amount of information it handles. Include support for fomod screens (and dialog redesign to more modern style)
   - [x] Installing files, unpacking the files after "hashing archive" phase, is very slow. Example 83kB in 41 seconds. This must be improved!
   - [x] After "cancelling" phase the dialog shows the text "canceled." but the dialog never closes and the app is stuck in a modal dialog that cant be closed. 
   - [x] Path doubling (Data\Data) in fomod installs? Maybe in all installs??
   - [x] Data folder not pre-filled in non-fomod installer
- [x] Tool output overlay shall be added to the load order only when: The tool is added to the profile and the tool has the "capture tool output" setting enabled while there is no output overlay branch in the load order (first time and after manually removing the output overlay branch). When the output overlay branch is added it shall be enabled in the load order.
       Once the output overlay is added to the load order it stays until manually removed, at which point the tool "capture tool output" setting shall automatically be disabled.
       For as long as the output overlay branch is part of the profile it shall retain its place in the load order and can be enabled/disabled independently from the tool. If the tool is removed from the profile or the "capture tool output" is turned off, the user shall be asked whether to keep the tool output overlay in the load order or not. Disabling the tool does not affect the status of the output overlay branch at all.
       When a tool is called the corresponding output capture overlay is temporarily put at the very top of the load order so all tool output is correctly captured. Once the tool exits the general profile overlay is put back on top to act as a catch-all when the game is run.
- [x] What does "remove tool" do? How are global tools added? Automatically for associated games??
- [x] Overlay for tools doesn't work. Overlay branch should be added to view automatically.
- [x] No scrollbar in add mods dialog
- [-] Local ollama does not work
- [x] Bodyslide thinks it can't write to directory
- [x] tool workdir might not be proper.
- [x] sorting load order doesn't remove warning

## Refactoring

- [ ] Log levels in log? (command line and launch info is "verbose")

## Features

- [ ] Notes, tags (for grouping and filtering. "animation", "lighting", "immersion" multiple tags allowed ...) #capability (animation fex)
- [ ] Mod wishlist with conflicts and clustering... (visuals?)
- [ ] Merge tool output from several tools in one branch? Doable??
- [ ] Dropdown to select active tool output version in view
- [ ] Tools shall be mutually exclusive launchable, including actual game
- [ ] Save mod installation choices (+ log) for later automatic reinstallation (modlist)
- [ ] Excluded: dependency-extraction internals, download resume, other backlog items, persistence/recipe schema changes, spec.md updates.
- [ ] Tool icon: Excluded: cache-policy changes, DPI scaling, per-tool custom icons, logging beyond the existing InfoBar pattern.
- [ ] Add mod definitions like game and tool definitions.
- [ ] Dependencies (general + version-specific) editing in better graph. Tree view is probably better...
- [ ] Data pre-filled in the "add mod" dialog (is there a setting in the game definition?)
- [ ] Install mod with dependencies?
- [ ] Mod categories (Animation ...)
- [ ] Remove all migration code and make all current schemas version 1
- [ ] Dead code, vulnerabilities, code smells, optimizations, adherence to design reference ...
- [x] ledger logging in wildpinkler (set pipe and level in ui, send info to launched binary + show logs/include in wp log)
- [x] plugins.txt editing in profile (tab)
- [x] Manual plugin ordering enable/disable as tab in profilepage. LOOT and/or manual load order editing.
       Offer LOOT execution in warning dialog. Sorting warning does not go away when sorting
- [x] Bodyslide, FNIS and others - not a tool and not a game! Make all executables visible as toolbuttons using their icons
- [x] Icon
- [x] Show archive tree in the "choose destination" dialog when installing mods (done?)
- [x] Reveal folder for installed mod and tool output

## Mods

- [ ] Bathsheba Body?
- [ ] Disguise

## Tools

### Global

- [x] LOOT : 
- [x] Bethini Pie : https://www.nexusmods.com/site/mods/631


# Resolve issues

- [x] No progress bar for download in row. 
- [x] Once downloaded the mod does not shift section (still in "to download"). Later when i excluded a mod it moved.
- [x] "Already present" and "ready to install" sections overlap! In fact sections might be useless and can be replaced by sorting?
- [x] "obsoleted by", dialog does not work - no content in mod library list!
- [x] No automatic folding (matching) on initial scan - only use user cues for operations through actions. (Superseded: on a new plan every persisted decision is shown only as a recommendation; once the user *confirms* one (via the recommendation or the manual action) it is sticky and auto re-applied on subsequent rescans of the same plan — see "Dependency Plan State Analysis.md" → "Sticky decisions".)
- [x] Two distinct plan states both read "Installed" (library-only vs. profile-installed). Unified per-row status: Unidentified → To download → Ready → Installed (plus Conflicting); "Installed" now strictly means in the target profile, "Ready" means archive on disk but not installed here. Set-aside states (Excluded/Folded/Obsoleted) remain badges with dimmed rows.- [x] Reintroduce sections (expandable) and make sure rows move when changing state.
- [x] "obsoleted by" and matches using "name heuristics" are remembered between planning sessions and if there are possible matches for mods there shall be an indicator on the row and an action that the user can take to investigate and possibly accept the suggestion to replace (or fold in) the mod in the plan. (Done: accepted identify/satisfy/replace decisions are remembered and offered as a "Recommended:" row action with the previous target preselected; explicit confirmation still required — see "Dependency Plan State Analysis.md".)
- [ ] "Install" button only actually installed one mod (and no dependencies)

- [ ] what does the checkboxes mean anyway?
- [ ] conflicts with mods in profile not in dependencies. add all mods in profile to page for resolution?


- [ ] Claims SkyUI needs updates but no update available.
- [ ] Bodyslide v6.0.0 presents as new mod instead of upgrade from v5.8.2
- [ ] VR address library is not for Skyrim SE (but for Skyrim VR) and should thus not be included in the mod list

- [ ] Add mod/add mods with dependencies should be one action with an offer to resolve dependncies
- [ ] rethink "remove last plan install"
