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

## Newtonian movement (optional)

On by default: your air brake is disabled entirely. Braking on demand is a bit overpowered for a zero-g game, so turning it off makes movement more realistically Newtonian — you keep drifting unless something actually stops you, like recoil, the Grapple Gun pulling you, grabbing something by hand, or MagBoots.

## Battery

Mag boots draw a limited battery, in minutes, that only drains while you're locked to a surface. It recharges to full at the start of every shift.

Choose how it's shown on screen: a segmented gauge, a live percentage, a countdown timer, or turn it off entirely. Run it down to zero and the boots stop working (shown as NO POWER) until your next shift.

## HUD hint

A small on-screen hint in the corner shows your current status and toggle key, styled to match the game's own prompts and translated to your controller's real button name if you're on a gamepad.

- **White** — off, ready to attach
- **Yellow** — locking on (snapping into position)
- **Green** — locked and walking
- **Red** — error, no valid surface found below you
- **Gray** — no power, battery depleted

Its position and size can both be tweaked in the config — position as a percentage offset from the center of your screen, size as a scale multiplier (1 is default, 2 is twice as big).

## Recoil (optional)

On by default. Gives extra kickback from the Cutter and the Grapple Gun:

- The saw Cutter kicks you back the instant you fire, instead of waiting for the cut to finish.
- The Scalpel/single-laser gives a steady push for as long as the beam is firing.
- Pushing or throwing anything with the Grapple Gun kicks you back based on how close and how heavy it is — a light object barely reacts, a massive or immovable one kicks like pushing off a wall. Only within a limited range; anything farther away gives no kickback.
- If you've grappled something too heavy to actually throw, the normal kickback you get from trying can also be scaled up or down.
- If a hit is strong enough, mag boots will let go of the surface instead of always holding on.
- Recoil also briefly disables your air brake (1 second by default) so a kick actually moves you instead of being cancelled out the instant it happens.

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
