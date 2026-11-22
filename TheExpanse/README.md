# TheExpanse

Changes to ships in Hardspace: Shipbreaker, for large custom ships. It was made for the Tachi, and works with any ship.

- **Big rooms take longer to pressurize.** The game fills and empties every room in the same few seconds. With this mod, a room takes as long as its ship's author set it to.
- **Heavy parts come free gently.** On custom ships, large parts that were thrown off at speed when cut or yanked free are eased apart instead.

Centering and straightening ships in the bay, and moving the jacks clear of them, were part of this mod up to version 0.5.0. They are now in the BiggerBays mod.

## Room pressure times

Every room in a ship has a pressurization time and a decompression time set by its author, but the game ignores them and changes every room's pressure in about 5 seconds. This mod makes those times count, so a large room can take as long as its size deserves.

- Only rooms set to longer than the game's own time are affected. Everything else, including every ship that leaves them at the default, behaves as before.
- Rooms connected by open doors change pressure together and take the longest time among them.
- A hull breach still vents at the game's normal speed.

## Gentle separation

Every part collides as the convex shape around its mesh. On custom ships built from large, hollow or curved hull pieces, that shape reaches into neighboring parts. While the parts are joined this does not matter. The moment one is cut or yanked free, the game pushes the overlapping parts apart at up to 10 meters per second, which throws a part of several tonnes faster than a tether can hold.

With this on, that push is limited by the part's mass:

- parts of 5 tonnes or more are eased apart at 0.3 meters per second;
- parts up to 100 kg keep the game's own speed, so small pieces still pop free;
- parts in between fall on a smooth slide (about 2.4 m/s at 500 kg, 1.3 m/s at 1 tonne).

Only the push between overlapping parts changes. The grapple, tethers, explosions and decompression move parts as before. It applies while a custom ship is in the bay; the game's own ships are left alone.

## Settings file

Generated on first run at:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.TheExpanse.cfg
```

The file is read again each time a ship starts loading, so you can edit it while the game is running and the change applies to the next ship.

**[General]**
- **Enabled** (default true): turns the whole mod on or off.

**[Rooms]**
- **UsePressureTimes** (default true): make each room's own pressurization and decompression time count.
- **ExtendPressureEffects** (default true): keep an atmosphere regulator's hiss and vent effect going for as long as a slowed pressure change lasts, by starting them again at even intervals. The spoken line is not repeated.
- **PressureEffectSeconds** (default 2): how many seconds one play of the hiss and vent effect covers. Lower it if you notice pauses between repeats.

**[Parts]**
- **GentleSeparation** (default true): on custom ships, limit how fast overlapping parts are pushed apart when they separate, by mass.

## For ship authors

To make a room slow to pressurize or decompress, set **Pressurization Time** and **Decompression Time** on its room container, in seconds. Values of about 5 or less make no difference, since the mod never makes a room faster than the game does.

## Notes

- Settings from earlier versions for centering ships, jacks, size limits, ship variants and part masses are no longer used and can be deleted from the settings file.
- For Mars in the sky, your own menu and loading pictures, and music during a shift, see the Themes mod.

## Installation

1. Download the latest 64-bit (x64) version of BepInEx 5 (not 6 or above) from [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the same folder as Shipbreaker.exe
3. Run the game, load the main menu and quit
4. Extract the mod into `BepInEx\plugins\`, so you end up with `BepInEx\plugins\TheExpanse\me.kerballone.TheExpanse.dll`

## Support

Questions, bugs, or feedback? Join the Shipbreaker Discord: [discord.gg/shipbreakergame](https://discord.gg/shipbreakergame)
