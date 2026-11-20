# TheExpanse

Makes room in the Hardspace: Shipbreaker work bay for custom ships that are bigger than the bay was built for. It was made for the Tachi, and works for any oversized ship.

- **Moves the jacks out of the way.** When a ship is too long or too tall for the bay, the Master Jack and the five light jacks are moved clear of it, so the ship no longer spawns through them.
- **Unlocks oversized ship variants.** A ship can include a larger variant that only appears while this mod is installed. The Tachi's Gas variant is set up this way.

The game's own ships fit the bay and are left alone.

## Moving the jacks

Each time a ship loads, the mod measures it. If it is over the size limit in any direction:

- The **Master Jack** is pushed further along the bay until it is clear of the ship's far end. Its kiosk and hab door go with it, and so does the spot where you start a shift and respawn.
- The **five light jacks** above the ship are raised together until the lowest one is clear of the ship's top.

A jack that already has enough room stays where it is. Everything is back in its normal place for the next ship.

## Oversized variants

Some custom ships come in more than one version. A ship's author can mark a larger version so the game never picks it by itself, and this mod then gives it a chance of appearing. That way the oversized version only turns up for players who have the mod that makes room for it.

By default the Tachi's Gas variant is unlocked and is as likely to appear as the standard Tachi.

## Settings file

Generated on first run at:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.TheExpanse.cfg
```

Edit it while the game is closed.

**[General]**
- **Enabled** (default true): turns the whole mod on or off.

**[Thresholds]**

The jacks are moved when the ship is larger than any of these, measured in metres along the bay.

- **MaxSizeX** (default 70): width.
- **MaxSizeY** (default 45): height.
- **MaxSizeZ** (default 100): length.

The ship's measured size is written to the BepInEx log on every load, which helps when choosing your own values.

**[Jacks]**
- **ClearanceMeters** (default 5): the gap kept between the ship and the jacks.
- **MovePlayerSpawnWithMasterJack** (default true): move your start and respawn point along with the Master Jack, so shifts still begin at its door.

**[Variants]**
- **Weights** (default `Tachi_Gas_Mount=1`): the variants to unlock and how likely each is. See below.

## For ship authors

To make a variant that only appears with this mod installed:

1. In the ship's module list, set the variant's entry to **Weight 0**. The game never picks an entry with no weight, so players without the mod only ever get the other entries.
2. Note the file name of the variant's module prefab, the one that entry spawns, without the `.prefab` ending. For the Tachi's Gas variant that is `Tachi_Gas_Mount`.
3. Add `name=weight` to the **Weights** setting. Separate several entries with commas.

The prefab's name is all that ties the setting to the ship, so rebuilding or reorganising the ship doesn't break it as long as that name stays the same. Give variant prefabs names that no other ship is likely to use. The name of the entry asset itself is also accepted.

The weight works like any other entry's weight in the same list. With the standard ship at weight 1, a variant at 1 appears half the time and a variant at 3 appears three times in four.

## Notes

- The size limits are meant to sit above the game's own ships. If one of those ever triggers a move, raise the limit for that direction.
- A ship saved in the middle of a shift is loaded back exactly as it was. If you save on an oversized variant and then remove the mod, that save still loads the variant, into a bay where the jacks are no longer moved.
- For very long ships, roughly 180 metres and up, the Master Jack ends up beyond the edge of the playable area.
- The scanner does not highlight the Master Jack or the light jacks while they are moved.
- For Mars in the sky, your own menu and loading pictures, and music during a shift, see the Themes mod.

## Installation

1. Download the latest 64-bit (x64) version of BepInEx 5 (not 6 or above) from [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the same folder as Shipbreaker.exe
3. Run the game, load the main menu and quit
4. Extract the mod into `BepInEx\plugins\`, so you end up with `BepInEx\plugins\TheExpanse\me.kerballone.TheExpanse.dll`

## Support

Questions, bugs, or feedback? Join the Shipbreaker Discord: [discord.gg/shipbreakergame](https://discord.gg/shipbreakergame)
