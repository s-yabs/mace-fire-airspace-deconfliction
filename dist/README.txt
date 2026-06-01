MACE Fire Airspace Deconfliction Plugin

Install:
1. Close MACE.
2. Copy MaceFireAirspace.dll to:
   C:\Users\Public\Documents\MACE\Plugins
3. Start MACE.
4. Open System Settings > Plugins and enable Fire Airspace Deconfliction.
5. Open the plugin from Info/Status Windows.

Current prototype behavior:
- Lists Call For Fire data and keeps the latest populated CFF data visible.
- Tracks active fire airspace from MACE weapon fire/detonation events.
- Draws active fire airspace overlays on the MACE map using map primitives.
- Shows persistent airspace/deconfliction records in the Conflicts tab.
- Allows operator-configurable horizontal NM and vertical ft separation defaults.

Notes:
- This is an advisory simulation/training prototype.
- The airspace geometry currently uses a 1 km gun/target radius and 1000 ft either side of the gun-target line.
- Use only the DLL in the public third-party plugin folder; do not install to Program Files.
