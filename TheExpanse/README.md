# TheExpanse

A companion mod for the MCRN Corvette ships in Hardspace: Shipbreaker (the Tachi, the Rocinante and the Beratnas Gas Hauler). It handles things that come from how those ships are designed and built, and makes their ghost-ship version properly haunted. All of it also applies to any other custom ship.

- **Heavy parts come free gently.** The ships' hull panels and large objects are built from a 3D model, so neighboring parts overlap, and the game threw them off at speed when cut or yanked free. They are eased apart instead.
- **Big rooms take longer to pressurize.** The game fills and empties every room in the same few seconds. With this mod, a room takes as long as the ship sets for it, and the regulator's hiss and vent effect last as long as the change does.
- **Ghost ships are more haunted.** On top of the game's own haunting, the ship rolls, lights flicker and black out, the nodes radiate, and the growth disappears with the last node.

## Room pressure times

Every room in a ship has a pressurization time and a decompression time set by its author, but the game ignores them and changes every room's pressure in about 5 seconds. This mod makes those times count, so a large room can take as long as its size deserves.

- Only rooms set to longer than the game's own time are affected. Everything else, including every ship that leaves them at the default, behaves as before.
- Rooms connected by open doors change pressure together and take the longest time among them.
- A hull breach still vents at the game's normal speed.
- An atmosphere regulator's hiss and vent effect are only about two seconds long. During a slowed change they are started again at even intervals, so they last until the pressure has finished changing. The spoken line plays once.

## Gentle separation

Every part collides as the convex shape around its mesh. On custom ships built from large, hollow or curved hull pieces, that shape reaches into neighboring parts. While the parts are joined this does not matter. The moment one is cut or yanked free, the game pushes the overlapping parts apart at up to 10 meters per second, which throws a part of several tonnes faster than a tether can hold.

With this on, that push is limited by the part's mass:

- parts of 5 tonnes or more are eased apart at 0.3 meters per second;
- parts up to 100 kg keep the game's own speed, so small pieces still pop free;
- parts in between fall on a smooth slide (about 2.4 m/s at 500 kg, 1.3 m/s at 1 tonne).

Only the push between overlapping parts changes. The grapple, tethers, explosions and decompression move parts as before. It applies while a custom ship is in the bay; the game's own ships are left alone.

## Ghost ships

The game's own haunting is kept as it is: doors, room pressure and electricity. On a custom ghost ship this mod adds to it. Like the game's haunting, it gets worse as you destroy nodes and stops with the last one.

- **The ship rolls.** Every few minutes the ship turns slowly about its long axis for about 20 seconds, pushing loose parts and you with it. Rolls come up to 3 times as often and turn up to 5 times as fast as nodes are destroyed.
- **Lights flicker and black out.** Every minute or few, the lights around one spot of the ship either flicker for a few seconds or go dark for 10 to 30 seconds. More often as nodes are destroyed.
- **The nodes are radioactive.** Close to a node you lose health as radiation, falling off with distance. Radiation resistance counts. Larger nodes reach further.
- **The helmet light stutters** near a node, more the closer you get.
- **The growth goes with the last node.** When the last node is destroyed, the growth painted across the hull, walls and floors disappears. It stays gone on a ship loaded from a save with no nodes left.

## Settings file

Generated on first run at:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.TheExpanse.cfg
```

Saving the file while the game is running applies the change within a second.

**[General]**
- **Enabled** (default true): turns the whole mod on or off.

**[Rooms]**
- **UsePressureTimes** (default true): make each room's own pressurization and decompression time count.
- **ExtendPressureEffects** (default true): keep an atmosphere regulator's hiss and vent effect going for as long as a slowed pressure change lasts, by starting them again at even intervals. The spoken line is not repeated.
- **PressureEffectSeconds** (default 2): how many seconds one play of the hiss and vent effect covers. Lower it if you notice pauses between repeats.

**[Parts]**
- **GentleSeparation** (default true): on custom ships, limit how fast overlapping parts are pushed apart when they separate, by mass.

**[Haunting]** (custom ghost ships only)
- **RollShip** (default true): the ship rolls now and then.
- **RollDegreesPerSecond** (2), **RollSeconds** (20): speed and length of a roll.
- **RollMinInterval** (180), **RollMaxInterval** (480): wait between rolls, in seconds.
- **RollStartShare** (1): rolls happen while the share of nodes left is at or below this. 0.5 = only once half are destroyed.
- **RollFrequencyRamp** (3), **RollSpeedRamp** (5): with the last node left, rolls are this many times as frequent and as fast. 1 = no change.
- **LightFlicker** (true), **LightBlackout** (true): the two light hauntings.
- **LightMinInterval** (60), **LightMaxInterval** (180), **LightFrequencyRamp** (3): their timing, as for rolls.
- **LightRoomRadius** (12): how far around the chosen spot lights are affected, in meters.
- **FlickerSeconds** (4), **BlackoutMinSeconds** (10), **BlackoutMaxSeconds** (30): how long they last.
- **HelmetFlicker** (true), **HelmetFlickerRadius** (10): the helmet light stutters within this many meters of a node.
- **NodeRadiation** (true): nodes damage your health as radiation.
- **NodeRadiationDamage** (3): health lost per second right at a node, as a percentage of full health. Doses of several nodes add up.
- **NodeRadiationRadius** (6): how far a node's radiation reaches, in meters.
- **NodeRadiationFalloff** (1): 0 = the same dose everywhere inside, as the game's own radiation; 1 = falls evenly to nothing at the edge; 2 or more = strong only close to the node.
- **RemoveGrowthAfterLastNode** (true): the growth disappears with the last node.

**[Debug]**
- **TestTools** (default false): shows how many nodes are left on screen, and **KillHalfNodesKey** (F8) destroys half of them. For testing only.

## For ship authors

To make a room slow to pressurize or decompress, set **Pressurization Time** and **Decompression Time** on its room container, in seconds. Values of about 5 or less make no difference, since the mod never makes a room faster than the game does.

## Notes

- Settings left in the settings file by earlier versions, other than the ones listed above, are no longer used and can be deleted.
- For Mars in the sky, your own menu and loading pictures, and music during a shift, see the Themes mod.

## Installation

1. Download the latest 64-bit (x64) version of BepInEx 5 (not 6 or above) from [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the same folder as Shipbreaker.exe
3. Run the game, load the main menu and quit
4. Extract the mod into `BepInEx\plugins\`, so you end up with `BepInEx\plugins\TheExpanse\me.kerballone.TheExpanse.dll`

## Support

Questions, bugs, or feedback? Join the Shipbreaker Discord: [discord.gg/shipbreakergame](https://discord.gg/shipbreakergame)
