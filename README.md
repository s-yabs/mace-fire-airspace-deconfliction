# MACE Fire Airspace Deconfliction Prototype

This is a first-pass MACE plugin for Call For Fire airspace deconfliction.

What it does now:

- Implements `BSI.MACE.PlugInNS.IMACEPlugIn`.
- Subscribes to `IMission.CallForFire.CallForFireEvent`.
- Lists Call For Fire mission data exposed by MACE: battery, target, round, number of rounds, gun-target line, max ordinate, time of flight, and status.
- Subscribes to `IMission.WeaponFire` and creates a temporary fire airspace volume when a matching battery shoots.
- Uses `WeaponDetonation` and mission time to expire the volume after time of flight plus a small buffer.
- Draws aimed and active fire airspace using MACE map primitives.
- Scans active aircraft-like entities and reports advisory/conflict status when an aircraft is inside or near active fire airspace.
- Tracks the MACE Call For Fire form/mission slot so aimed and executing events update the same row.

Important assumptions:

- The lateral buffer is a placeholder rule. Replace it with the approved safety template for the munition, weapon, charge, and local training rules.
- The volume is a rectangular corridor along the gun-target line with 1 km endpoint rings and vertical limits from ground/target MSL to max ordinate MSL.
- Aircraft deconfliction is advisory only until validated against the MACE scenario data model and your range control procedures.

Build:

```powershell
dotnet build .\MaceFireAirspace.csproj -c Release
```

Install for local MACE testing as a third party plugin:

```powershell
Copy-Item .\bin\Release\net481\MaceFireAirspace.dll 'C:\Users\Public\Documents\MACE\Plugins\'
```

Package:

The distributable zip is published in GitHub Releases as `MaceFireAirspacePlugin.zip`.
