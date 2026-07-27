# NewtonianPhysics

Makes movement in Hardspace: Shipbreaker harder and more realistic: no more instant, on-demand braking. Once you're drifting, you stay drifting - you have to actively counter your own momentum instead of just holding a button to stop.

That means leaning on the tools you actually have for controlling your movement:

- **Reverse-thrusting** - point yourself the other way and burn off your velocity the hard way, like real thrusters.
- **The Grapple Gun** - pull yourself toward (or push off) parts to redirect your drift.
- **Hand-grabbing** - grab something solid to stop dead or change direction.
- **[MagBoots](https://www.nexusmods.com/hardspaceshipbreaker/mods/31)** - magnetically walk the hull instead of fighting momentum in the open. (Separate mod - link goes to its own page.)

On top of that, this mod adds real recoil to the Cutter and Grapple Gun, so firing them has a consequence: cutting kicks you back, and pushing or throwing something shoves you the other way based on how heavy it is - just like it would in zero-g.

## Newtonian movement

**NoBrakes** is on by default: it disables your air brake completely. Vanilla lets you cancel all your momentum instantly on demand, which trivializes movement in a way that doesn't feel like actual zero-g. With it off, you have to plan your stops.

Vanilla also has a few quieter ways of slowing you back down over time, even with NoBrakes on - these are off by default too, so drifting and tumbling both behave like real Newtonian motion: constant until something actually stops you.

- **PlayerLinearDrag** - vanilla quietly brakes you back down to a crawl over time whenever you're drifting and not actively thrusting, grabbing, or grappled. Off by default, so you keep drifting at a constant velocity instead.
- **PlayerRotationDrag** - vanilla damps out any tumble/spin you pick up whenever you're not actively steering. Off by default, so spin persists indefinitely too - this automatically pauses while you're hand-grabbing something or locked in place with MagBoots, and picks back up right where vanilla would once you let go.
- **ObjectDrag** - vanilla applies drag to loose parts and debris so they settle down over time. Off by default, so objects drift and spin forever once set in motion, same as the player.

## Recoil

On by default. Adds real kickback to tools that had none:

- The saw Cutter kicks you back the instant you fire, instead of waiting for the cut to finish.
- The Scalpel/single-laser gives a steady push for as long as the beam is firing.
- Pushing or throwing anything with the Grapple Gun kicks you back based on how close and how heavy it is - a light object barely reacts, a massive or immovable one kicks like pushing off a wall. Only within a limited range; anything farther away gives no kickback.
- If you've grappled something too heavy to actually throw, the normal kickback you get from trying can also be scaled up or down.

**BrakeBreak** (1 second by default) also briefly disables your air brake after any recoil hit, so the kick actually moves you instead of being cancelled out the instant it happens. This has no extra effect if NoBrakes is already on.

## Settings

The config file is generated on first run at:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.NewtonianPhysics.cfg
```

Every setting has a plain-language description in the file itself - open it in any text editor to see what each one does and tweak it to taste.

## Installation

1. Download the latest 64-bit (x64) version of BepInEx 5 (not 6 or above) from [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the same folder as Shipbreaker.exe
3. Run the game, load the main menu and quit
4. Extract the mod into `BepInEx\plugins\`, so you end up with `BepInEx\plugins\NewtonianPhysics\me.kerballone.NewtonianPhysics.dll`
5. The config file appears at `BepInEx\config\me.kerballone.NewtonianPhysics.cfg` the first time you run the game with the plugin installed

## Compatibility

Works entirely on your own client, via forces on your own player and tools. It doesn't touch save data or ship data, so it's safe to add or remove mid-save. Fully standalone - MagBoots is not required, though the two are designed to complement each other.
