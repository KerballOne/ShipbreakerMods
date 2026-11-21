# TheExpanse

Changes to the Hardspace: Shipbreaker work bay and to ships, for large custom ships. It was made for the Tachi, and works with any ship.

- **Brings large ships down into the bay.** Large custom ships are posed to spawn above the bay's jacks, so they can be played without this mod. An optional setting places each ship in the middle of the bay instead and moves the jacks out of its way.
- **Big rooms take longer to pressurize.** The game fills and empties every room in the same few seconds. With this mod, a room takes as long as its ship's author set it to.

## Centring the ship

This is off by default. Turn on **CenterShip** in the settings and each ship is placed for you, wherever its author posed it:

- centred across the bay, between the two side walls;
- centred on the bay's height;
- with its rear end 10 metres in front of the back wall.

The jacks are then moved clear of it:

- The **Master Jack** is pushed further along the bay until it is clear of the ship's far end. Its kiosk and hab door go with it, and so does the spot where you start a shift and respawn.
- The **five light jacks** above the ship are raised together until the lowest one is clear of the ship's top.

A jack that already has enough room stays where it is, and so does one the whole ship is posed above. Everything is back in its normal place for the next ship.

With the setting off, ships spawn where their author posed them and the jacks are never moved.

### Saved ships

A ship loaded from a save goes back where it was when you saved it. If you saved it centred, it loads centred. If you saved it before turning the setting on, it loads where it was and is not moved.

## Room pressure times

Every room in a ship has a pressurization time and a decompression time set by its author, but the game ignores them and changes every room's pressure in about 5 seconds. This mod makes those times count, so a large room can take as long as its size deserves.

- Only rooms set to longer than the game's own time are affected. Everything else, including every ship that leaves them at the default, behaves as before.
- Rooms connected by open doors change pressure together and take the longest time among them.
- A hull breach still vents at the game's normal speed.

## Settings file

Generated on first run at:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.TheExpanse.cfg
```

The file is read again each time a ship starts loading, so you can edit it while the game is running and the change applies to the next ship.

**[General]**
- **Enabled** (default true): turns the whole mod on or off.

**[Bay]**
- **CenterShip** (default false): place each ship in the middle of the bay and move the jacks to suit.
- **BackWallGapMeters** (default 10): the gap between the back wall and the end of the ship nearest to it.
- **AcrossOffsetMeters** (default 0): how far to one side of the bay's middle ships are placed. 0 centres them between the side walls.
- **BayCenterHeight** (default 0): the height ships are centred on. Raise or lower it to sit ships higher or lower in the bay.

**[Jacks]**
- **ClearanceMeters** (default 5): with CenterShip on, the gap kept between the ship and the jacks.
- **MovePlayerSpawnWithMasterJack** (default true): move your start and respawn point along with the Master Jack, so shifts still begin at its door.

**[Rooms]**
- **UsePressureTimes** (default true): make each room's own pressurization and decompression time count.

## For ship authors

A ship too large for the bay can be posed to spawn above the jacks, where it is playable without this mod. With **CenterShip** on, the mod works out the ship's size and brings it down into the bay, so no special setup is needed.

To make a room slow to pressurize or decompress, set **Pressurization Time** and **Decompression Time** on its room container, in seconds. Values of about 5 or less make no difference, since the mod never makes a room faster than the game does.

## Notes

- The ship's size is worked out before it spawns, from its parts' meshes and positions. The result is an estimate, so a centred ship can sit a little off. The BepInEx log reports how far after each load.
- For very long ships, roughly 180 metres and up, the Master Jack ends up beyond the edge of the playable area.
- The scanner does not highlight the Master Jack or the light jacks while they are moved.
- Settings from earlier versions for size limits, ship variants and part masses are no longer used and can be deleted from the settings file.
- For Mars in the sky, your own menu and loading pictures, and music during a shift, see the Themes mod.

## Installation

1. Download the latest 64-bit (x64) version of BepInEx 5 (not 6 or above) from [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the same folder as Shipbreaker.exe
3. Run the game, load the main menu and quit
4. Extract the mod into `BepInEx\plugins\`, so you end up with `BepInEx\plugins\TheExpanse\me.kerballone.TheExpanse.dll`

## Support

Questions, bugs, or feedback? Join the Shipbreaker Discord: [discord.gg/shipbreakergame](https://discord.gg/shipbreakergame)
