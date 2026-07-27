# MagBoots

Adds magnetic boots to Hardspace: Shipbreaker, letting you lock onto any suitable hull surface and walk it like it's got gravity, instead of free-floating everywhere.

Press the toggle key with a ship part below you. If the surface is flat enough and big enough, you'll snap into place above it and start walking. Keep moving and MagBoots keeps checking a step ahead of you, so you can walk over curves, seams, and corners without losing your footing.

## How it works

- Press the toggle key to attach. MagBoots checks straight down from you; if it finds a surface that's flat and big enough, you snap into place.
- While attached, movement input walks you along the surface instead of free-thrusting. You're held at a fixed distance off the hull.
- Look up and down freely — you just can't tip all the way over and stare straight into the deck.
- Press the toggle key again to detach and go back to normal zero-g movement at any time.
- Holding the thrust-down input crouches you closer to the surface, for squeezing under low obstacles; release to stand back up.
- MagBoots only works during actual flight/salvage gameplay — it's fully inactive in the Hab, menus, loading screens, and cutscenes.

## Battery

Mag boots draw a limited battery, in minutes, that only drains while you're locking on or locked to a surface. It recharges to full at the start of every shift.

Choose how it's shown on screen: a segmented gauge, a live percentage, a countdown timer, or turn it off entirely. Run it down to zero and the boots stop working (shown as NO POWER) until your next shift.

Standing still while locked on costs much less power than actually walking, and the initial snap-into-place costs more than either - so quickly tapping the toggle on and off isn't a way to dodge the battery cost.

## HUD hint

A small on-screen hint in the corner shows your current status and toggle key, styled to match the game's own prompts and translated to your controller's real button name if you're on a gamepad.

- **White** — off, ready to attach
- **Yellow** — locking on (snapping into position)
- **Green** — locked and walking
- **Red** — error, no valid surface found below you
- **Gray** — no power, battery depleted

Its position and size can both be tweaked in the config — position as a percentage offset from the center of your screen, size as a scale multiplier (1 is default, 2 is twice as big).

## Settings

The config file is generated on first run at:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.MagBoots.cfg
```

Every setting has a plain-language description in the file itself — open it in any text editor to see what each one does and tweak it to taste. A few highlights:

- **AheadCastDistance** is your stride length — how far ahead MagBoots checks for the next foothold while walking.
- **MoveSpeed** is your walking speed while attached.
- **StandoffDistance** is how far off the surface you float.
- **BreakawayVelocity** is how hard you need to be hit before mag boots let go instead of holding on.

## Installation

1. Download the latest 64-bit (x64) version of BepInEx 5 (not 6 or above) from [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the same folder as Shipbreaker.exe
3. Run the game, load the main menu and quit
4. Extract the mod into `BepInEx\plugins\`, so you end up with `BepInEx\plugins\MagBoots\me.kerballone.MagBoots.dll`
5. The config file appears at `BepInEx\config\me.kerballone.MagBoots.cfg` the first time you run the game with the plugin installed

## Compatibility

MagBoots works entirely on your own client, via raycasts and forces on your own player. It doesn't touch save data or ship data, so it's safe to add or remove mid-save.

## See also

[NewtonianPhysics](https://github.com/KerballOne/ShipbreakerMods/tree/kb1/NewtonianPhysics) is a separate, optional companion mod that removes on-demand air braking and adds real recoil to the Cutter and Grapple Gun — it pairs well with MagBoots as an alternative way to stop and control your movement.
