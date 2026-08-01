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
- **PlayerRotationDrag** - vanilla damps out any tumble/spin you pick up whenever you're not actively steering. Off by default, so spin persists indefinitely too - this automatically pauses while you're hand-grabbing something or locked in place with MagBoots, and picks back up right where vanilla would once you let go. **This is off by default along with the others, but it's by far the hardest of the three to get used to - persistent tumble means an accidental spin never stops on its own, so expect to lean on hand-grabs, MagBoots, or your Grapple Gun a lot more to correct your orientation. Turn it back on in the config if you just want linear drift without dealing with tumble too.**
- **ObjectDrag** - vanilla applies drag to loose parts and debris so they settle down over time. Off by default, so objects drift and spin forever once set in motion, same as the player.
- **MaxVelocityMps** - caps how fast you can drift, in meters per second. Vanilla's default is 20; this mod raises it to 200 by default. Set to 0 for no cap at all - though ~200 m/s is a hard engine limit either way.
- **WorkAreaRadiusMultiplier** - scales how far you can roam from the game's designated work areas before the warning/danger zone (which can eventually teleport or hurt you) kicks in. 1 is vanilla, 2 doubles it, and so on. Off (0, unlimited roaming) by default.

## Rendering fixes

Vanilla's camera stops drawing anything past 2700m, and background objects (Earth, Moon, the Sun, distant station/gate cards) are placed and sized for how they look from near the work bay. Normally you'd never fly far enough for any of that to matter, but MaxVelocityMps and WorkAreaRadiusMultiplier above make it easy to range well past it.

- Camera draw distance is always raised well beyond 2700m, so distant structures no longer flatly vanish once you're far out.
- **TriggerDistance** (default 250m) - how far from the work bay you need to fly before any of the fixes below kick in. Close to the bay, vanilla's own placement already looks right.
- Earth, Moon, the Sun, and the planet glow stay pinned at a constant distance from you past that point, like real astronomical bodies, instead of visibly receding or approaching the way vanilla's fixed-position placement would once you're flying far enough to notice.
- **PinningMode** (`distance` by default, or `angle`) - how distant flat background cards (the rail gate card, the ring cards near it, and the village station/waystation cards) are kept from going edge-on/invisible at extended flight range. `distance` pins each card to a constant distance from you, same as the celestial bodies above. `angle` instead locks each card's rotation to whatever angle it had relative to you the moment it activated, letting its position drift like vanilla.
- **FlatEarthMode** (off by default) - disables all of the background pinning above and restores vanilla's recede/approach behavior, if you'd rather see it break down at range than have it artificially held in place.
- **StreamStretchMultiplier** (0/off by default) - the speed-streak effect you see while drifting fast is tuned for vanilla's ~20 m/s cap and maxes out almost immediately at this mod's higher speeds. 1 restores exactly how it behaves in vanilla; higher values let it keep stretching further as you go faster; 0 turns it off entirely.

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

The config file also reloads live - edit and save it while the game is running and your changes apply immediately, no restart needed.

## Installation

1. Download the latest 64-bit (x64) version of BepInEx 5 (not 6 or above) from [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the same folder as Shipbreaker.exe
3. Run the game, load the main menu and quit
4. Extract the mod into `BepInEx\plugins\`, so you end up with `BepInEx\plugins\NewtonianPhysics\me.kerballone.NewtonianPhysics.dll`
5. The config file appears at `BepInEx\config\me.kerballone.NewtonianPhysics.cfg` the first time you run the game with the plugin installed

## Compatibility

Works entirely on your own client, via forces on your own player and tools. It doesn't touch save data or ship data, so it's safe to add or remove mid-save. Fully standalone - MagBoots is not required, though the two are designed to complement each other.
