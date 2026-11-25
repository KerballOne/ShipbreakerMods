# TheExpanse

A companion mod for the MCRN Corvette ships in Hardspace: Shipbreaker (the Tachi, the Rocinante and the Beratnas Gas Hauler). It handles things that come from how those ships are designed and built, and makes their ghost-ship version properly haunted. All of it also applies to any other custom ship.

- **Heavy parts come free gently.** The ships' hull panels and large objects are built from a 3D model, so neighboring parts overlap, and the game threw them off at speed when cut or yanked free. They are eased apart instead.
- **Big rooms take longer to pressurize.** The game fills and empties every room in the same few seconds. With this mod, a room takes as long as the ship sets for it, and the regulator's hiss and vent effect last as long as the change does.
- **Ghost ships are more haunted.** On top of the game's own haunting, the ship rolls, lights flicker and black out, the nodes radiate, and the growth disappears with the last node.
- **The reactor has to be shut down.** The ship's Solomon Epstein torch drive burns a core of fusion plasma in the reactor chamber, held by eight plasma injectors. Take them off in the wrong order and it lashes out with fire.

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

## Reactor control

The MCRN Corvette is driven by a Solomon Epstein torch drive. Its core, a ball of fusion plasma, burns in the reactor chamber, held in a magnetic bottle by the eight plasma injectors around it. Each injector feeds the core through a beam from its nozzle, and a larger beam comes down from the nozzle above.

Each injector pushes on the core from its side. With all eight connected the pushes cancel. The core pulses on a steady beat; it shrinks as injectors are removed, and a short, harmless flare bursts from each gap on every beat. A disconnected injector's beam goes out, so you can see which ones are already off.

- **Levels.** The core's light shows how unbalanced it is: blue in balance, then orange (level 1), red (level 2, unstable: a fire cloud wraps the core) and purple (level 3), brighter at each level. A warning beep in your helmet sounds while it is unstable.
- **Balance.** Opposite gaps cancel each other out. One gap at a time is level 1, and so is a pair that is not quite opposite. Two gaps side by side are level 2, three are level 3. So take the injectors off like the nuts on a car wheel: opposite sides, then the pair in between.
- **Escalation.** Left off balance, the core gets worse by itself: every 45 seconds it goes up a level. A disconnect that evens the balance out takes a step back and restarts the wait; a wrong one leaves the clock running. In the last 5 seconds before each step, the warning beep speeds up to four a second.
- **Level 3: the donut flare.** Held at level 3 for 5 seconds, the core throws out a ring of plasma in the plane of the injectors and destroys all eight. It cannot be undone. Then the game's own meltdown of the reactor above the core starts: get out. When that reactor explodes, the core detonates with it.
- **Timing the handle.** Like the game's breakers: three lights on each handle's console light red one by one on the core's beats and go dark again. Use the handle while they are dark; the dark moment narrows as the core gets more unstable. At any other moment the plasma flashes back along the handle and sets you on fire as it finishes turning. Cutting the purple pipe is not timed. A caution sign on each console stays, unlit, once its injector is out.
- **Shutdown modes.** Each ship picks one of the modes switched on in the options page, at random:
  - *Balance*: no set order; keep it balanced.
  - *Memorize*: two nav terminals on the engineering deck (two of the four, picked at random per ship) flash the order, one injector a second, on a diagram of the ring marked DRSL, VNTR, PORT and STBD, seen looking aft; a map pin marks the terminal's own side. Disconnect them in that order.
  - *Resonance*: the lights of the injector to release next tick exactly on the core's beat; the others tick between beats. The terminals show the core's beat as a wave.

  In Memorize and Resonance the order (any order) is the only rule; the balance does not count. A wrong injector is a mistake: the core flashes red and goes up a level (no fire), the one you owed stays next, and a clock starts: every 45 seconds until a correct one, it goes up a level too. A correct one brings the core back to level 0 and stops the clock; with no mistakes there is no clock.
- **Disconnecting an injector.** Pull its handle, or cut the purple pipe that runs from it up to the reactor's lid (freeze the pipe first, as with any pipe). Either one switches it off. With all eight disconnected, the plasma fades out.
- **Pulling the reactor** while the plasma still burns detonates the core: the reactor's own explosion, at twice its reach.
- **Hazards.** The plasma is radioactive within a few meters, and touching it sets you on fire. Holding the reactor, an injector or its pipe, you hear the core through your suit; a controller rumbles with each beat.

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

**[Reactor]** (MCRN Corvette)
- **ReactorControl** (default true): the live reactor, "Live Reactor" in the options page. Off: the reactor is cold, with no core, no light and nothing to shut down.
- **BallSize** (2): diameter of the core in balance, in meters. It swells to twice this in each pulse.
- **PulseSeconds** (1.5): how long one beat of the core takes; steady whatever the balance.
- **RadiationRadius** (4), **RadiationDamage** (4): how far the radiation reaches from the core's surface, in meters, and the health lost per second right at the surface, as a percentage of full health.
- **HeatRadius** (0.5): coming this close to the core sets you on fire.
- **UnstableAt** (1.05): imbalance above which the plasma is unstable (level 2). One gap is 1; two side by side are 1.85.
- **PlayerDamage** (true): the core's radiation, its fire, a flashback, the donut flare and the detonation hurt you. Off: it never hurts you, though a detonation still throws you.
- **PlasmaInjectorReleaseTiming** (true): the handle lights, signs and timed pulls.
- **ModeBalance**, **ModeMemorize**, **ModeResonance** (all true): the shutdown modes a ship may pick from. **ResonanceOffset** (0.5): how far off the beat the other injectors' lights tick, in beats; smaller is harder. **ResonanceSpread** (0.12): how much the others' offsets differ. **ResonanceRingHint** (false): the screen rings the injector to release next.
- **Terminals** (empty), **ScreenDorsal** (+Z), **ScreenBrightness** (4), **ScreenPosition**, **ScreenRotation**, **ScreenSize** (for placing the screen): which nav terminals show the shutdown screen (their numbers from the log; empty: any two of the four on the engineering deck, at random per ship), which of the ship's axes is dorsal on the diagram, and how bright the screen is.
- **Haptics** (true): a controller rumbles with each pulse while you are near the core.
- **ShipDamage** (100): how hard the core hits the ship, in percent. At 100 the donut flare destroys the injectors, and the core detonates with twice the reactor's reach (200: four times). 0: the donut destroys nothing, nothing melts down and the core goes out without a blast.
- **Escalation** (true), **EscalateSeconds** (45): plasma left off balance goes up a level by itself every EscalateSeconds.
- **Meltdown** (true), **MeltdownAt** (2.4), **MeltdownSeconds** (5): level 3 starts at MeltdownAt; held that long, the donut flare comes, and with Meltdown on the game's meltdown follows.
- **Brightness** (10), **LightLumens** (30000), **BallDepth** (3): the stand-in core's glow (used only if the game's fire cannot load), the light the plasma casts, and where the core sits if the ship's reactor marker is missing.

**[Drive]**
- **DrivePlume** (default false): the MCRN Corvette's drive plume. Not finished; it will come with flight control.

**[Debug]**
- **TestTools** (default false): for testing only. Shows how many nodes are left and the reactor's balance on screen. **KillHalfNodesKey** (F8) destroys half of the nodes; **ReactorRightKey** (F11) and **ReactorWrongKey** (Shift+F11) treat the best or the worst injector as disconnected.

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
