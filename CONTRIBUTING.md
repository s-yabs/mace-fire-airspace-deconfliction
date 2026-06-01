# Contributing

Thanks for helping improve the MACE Fire Airspace Deconfliction plugin.

## Access

This repository is public, so contributors can clone it and open issues or pull requests.

To get direct write access, send the repository owner your GitHub username so you can be added as a collaborator.

## Build

Build from the repository root:

```powershell
dotnet build .\MaceFireAirspace.csproj -c Release
```

## Local Install

Close MACE before replacing the DLL, then copy the release build:

```powershell
Copy-Item .\bin\Release\net481\MaceFireAirspace.dll 'C:\Users\Public\Documents\MACE\Plugins\' -Force
```

Restart MACE and enable `Fire Airspace Deconfliction` under System Settings > Plugins.

## Pull Requests

Keep changes focused and include a short note describing the MACE workflow tested, especially for Call For Fire form/mission behavior, Fire Plan execution, map overlays, and Active Airspace rows.
