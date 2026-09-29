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

- [x] Enable/disable mod does not trigger regereration of profile.json and Plugins.txt. Must restart Wildpinkler to make profile.json update properly
- [x] Restore "HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps" after debugging
- [ ] What does "remove tool" do? How are global tools added? Automatically for associated games??
- [x] Mod install
   - [x] Long delay between clicking add mod on large archive (BHUNP) and dialog shows up with just spinning donut as indicator. Improve/cancel?
   - [x] Installing fomod files from large archive (BHUNP) takes a very long time!
   - [x] Cancel fomod installation of large archive (BHUNP) does not close the dialog (or maybe after a long time?)
- [ ] Tool output overlay should be visible in the load order only when:
      1. The tool is added to the profile
      2. The tool has the "capture tool output" setting enabled
      Regardless of whether the tool is enabled in the tools pane list or not. For as long as the tool is added to the profile the output overlay shall retain its place in the load order and can be enabled/disabled independently from the tool. If the tool is removed from the profile the user shall be asked whether to keep the tool output overlay or not. If the "capture tool output" is turned off the output overlay shall be removed. (Hidden)?
- [ ] Diabling/enabling mods with tools does not remove and bring back tools or overlays correctly.
- [ ] "Chose roles" menu item does nothing
- [ ] Toolbutton menus in profile page does nothing
- [x] Overlay for tools doesn't work. Overlay branch should be added to view automatically.
- [x] No scrollbar in add mods dialog
- [ ] Local ollama does not work
- [ ] Why are there assistant "what can assistant do" settings? Dropdown in assistant pane should suffice?
- [ ] When LOOT is running... Bad message!
- [ ] Renaming a mod drops dependencies (only when depending??)
- [ ] Download does not resume automatically after restarting wildpinkler (or ui not updated)
- [ ] Status in mod list not properly updated when mod finished download.
- [ ] Check! Game launcher is mutually exclusive (only one mod at a time (at most) can launch)

## Refactoring

- [ ] Rename: Custom views -> game views
- [ ] Rethink the profile folder structure. Views could be forced to be named uniquely -> profiles/id/views/…  (custom\skyrim-se\Documents) maybe simply overlay/<view name>?
- [ ] Log levels in log? (command line and launch info is "verbose")

## Features

- [ ] Excluded: dependency-extraction internals, download resume, other backlog items, persistence/recipe schema changes, spec.md updates.
- [ ] Tool icon: Excluded: cache-policy changes, DPI scaling, per-tool custom icons, logging beyond the existing InfoBar pattern.
- [x] Bodyslide, FNIS and others - not a tool and not a game! Make all executables visible as toolbuttons using their icons
- [ ] Tools shall be mutually exclusive launchable, including actual game
- [ ] Add mod definitions like game and tool definitions.
- [ ] Icon
- [ ] LOOT and/or manual load order editing. Offer LOOT execution in warning dialog.
- [ ] Show archive tree in the "choose destination" dialog when installing mods (done?)
- [ ] Reveal folder for installed mod
- [ ] Dependencies (general + version-specific) editing in better graph. Tree view is probably better...
- [ ] Data pre-filled in the "add mod" dialog (is there a setting in the game definition?)
- [ ] Install mod with dependencies?
- [ ] Mod categories (Animation ...)
- [ ] Remove all migration code and make all current schemas version 1
- [ ] Dead code, vulnerabilities, code smells, optimizations, adherence to design reference ...

## Mods

- [ ] Bathsheba Body?
