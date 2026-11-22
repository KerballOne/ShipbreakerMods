# Bigger Bays

A work bay that grows to fit the ship. A mod for Hardspace: Shipbreaker.

Big custom ships do not fit the work bay. They clip the side walls, run past the Master Jack, and sit far from the furnaces and processors.

This mod sizes the bay to the ship. Each time a ship loads it is measured, and the bay is changed only as far as that ship needs. A ship that fits leaves the bay exactly as the game has it, so the tutorial and story play as normal.

## What it does

- **Wider bay.** If the ship is close to the side walls, both sides of the bay move apart just far enough to leave room beside it. The back wall and the barge widen to match.
- **Longer bay.** If the ship is long, the bay grows by 76 meters. Each side gets a second furnace and processor, so there is always one close to where you are cutting. The barge grows with it.
- **Brighter bay.** The bay's spotlights can be given more range and brightness, and are raised further when the bay is widened so the middle stays lit.
- **Custom ships are placed for you.** A custom ship posed at an angle is turned square to the bay, centered, and set a fixed distance from the back wall.
- **Jacks move clear.** For a custom ship, the Master Jack is pushed just past the ship's far end, with its kiosk, hab door and your start point. The five light jacks are raised above the ship.

The second furnace and processor work like the originals: salvage thrown into them is paid or scrapped the same way.

## Which ships get what

- **Every ship, the game's own included:** the bay is widened or lengthened if the ship needs it, and the spotlight settings apply.
- **Custom ships only:** straightening, centering and moving the jacks. The game's own ships are never moved.

## Saved ships

The bay a ship was given is remembered, so loading a save gives you the same bay again, however much of the ship is left.

## Settings

Open the game's Options menu and pick the **BAYS** tab. It works from the title screen and the pause menu. Light settings change as you move the sliders; everything else applies to the next ship you load.

**Bay**
- **Bigger Bay** (default on): resize the bay for ships that need it.
- **Side Clearance Meters** (default 20): the room kept between the ship and each side wall. Raise it for a wider bay, lower it for a tighter one.
- **Length Trigger Meters** (default 25): how far a ship has to reach past the stock processor before the bay is made longer. Lower it to lengthen the bay for shorter ships.

**Lights**
- **Bay Lights** (default on): adjust the bay's spotlights.
- **Range Multiplier** (default 1): how far the spotlights reach.
- **Brightness Multiplier** (default 1): how bright they are.

**Custom Ships**
- **Center In Bay** (default on): center custom ships and move the jacks clear of them.
- **Straighten** (default on): turn custom ships square to the bay first.
- **Back Wall Gap Meters** (default 15): the gap between the back wall and the ship.
- **Height Offset Meters** (default 0): sit ships higher or lower in the bay.
- **Jack Clearance Meters** (default 5): the gap kept between the ship and the jacks.

The same settings are in the settings file, which is read again whenever you save it:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.BiggerBays.cfg
```

Three settings are only in the file:

- **StraightenRatio** (default 1.5): lower it toward 1.1 if a ship that is clearly crooked is not being straightened.
- **HelmetRangeMultiplier** and **HelmetIntensityMultiplier** (default 1): the range and brightness of your helmet light.

## Good to know

- The bay gets one extra furnace and processor per side, however long the ship is.
- Made for the [MCRN Corvette](https://www.nexusmods.com/hardspaceshipbreaker/mods/41) (the Tachi, the Rocinante and the Beratnas Gas Hauler), and works with any ship.

## Installation

1. Download the latest 64-bit (x64) version of BepInEx 5 (not 6 or above) from [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the same folder as Shipbreaker.exe
3. Run the game, load the main menu and quit
4. Extract the mod into `BepInEx\plugins\`, so you end up with `BepInEx\plugins\BiggerBays\me.kerballone.BiggerBays.dll`

## Support

Questions, bugs, or feedback? Join the Shipbreaker Discord: [discord.gg/shipbreakergame](https://discord.gg/shipbreakergame)
