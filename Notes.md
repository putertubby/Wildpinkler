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
- [ ] Overlay for tools doesn't work. Overlay branch should be added to view automatically.
- [ ] "Chose roles" menu item does nothing
- [ ] Check! Game launcher is mutually exclusive (only one mod at a time (at most) can launch)
- [ ] No scrollbar in add mods dialog
- [ ] Local ollama does not work
- [ ] Why are there assistant "what can assistant do" settings? dropdown should suffice?
- [ ] When LOOT is running... Bad message!
- [ ] Renaming a mod drops dependencies (only when depending??)
- [ ] Download does not resume automatically after restarting wildpinkler (or ui not updated)
- [ ] Status in mod list not properly updated when mod finished download.

## Refactoring

- [ ] Rename: Custom views -> game views
- [ ] Rethink the profile folder structure. Views could be forced to be named uniquely -> profiles/id/views/…  (custom\skyrim-se\Documents) maybe simply overlay/<view name>?
- [ ] Log levels in log? (command line and launch info is "verbose")

## Features

- [ ] Tool icon: Excluded: cache-policy changes, DPI scaling, per-tool custom icons, logging beyond the existing InfoBar pattern.
- [ ] Bodyslide, FNIS and others - not a tool and not a game! Make all executables visible as toolbuttons using their icons (mutually exclusive launchable, including actual game)
- [ ] Add mod definitions like game and tool definitions.
- [ ] Icon
- [ ] LOOT and/or manual load order editing. Offer LOOT execution in warning dialog.
- [ ] Show archive tree in the "choose destination" dialog when installing mods
- [ ] Reveal folder for installed mod
- [ ] Dependencies (general + version-specific) editing in better graph. Tree view is probably better...
- [ ] Data pre-filled in the "add mod" dialog (is there a setting in the game definition?)
- [ ] Install mod with dependencies?
- [ ] Mod categories (Animation ...)
- [ ] Remove all migration code and make all current schemas version 1
- [ ] Dead code, vulnerabilities, code smells, optimizations, adherence to design reference ...

Bathsheba Body?