Install Powershell - Works better with copilot?:

winget install Microsoft.PowerShell

Check version:

$PSVersionTable.PSVersion

Windows Powershell:

Major  Minor  Build  Revision
-----  -----  -----  --------
5      1      26100  9444 

Powershell

Major  Minor  Patch  PreReleaseLabel BuildLabel
-----  -----  -----  --------------- ----------
7      6      6                      

    "terminal.integrated.profiles.windows": {
        "PowerShell": {
            "source": "PowerShell",
            "icon": "terminal-powershell"
        }
    },
    "terminal.integrated.shellIntegration.enabled": true,
    "terminal.integrated.defaultProfile.windows": "PowerShell"
