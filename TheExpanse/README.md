# TheExpanse

A companion mod for the MCRN Corvette ships in Hardspace: Shipbreaker (the Tachi, the Rocinante and the Beratnas Gas Hauler). It handles things that come from how those ships are designed and built, and makes their ghost-ship version properly haunted. All of it also applies to any other custom ship.

- **Heavy parts come free gently.** The ships' hull panels and large objects are built from a 3D model, so neighboring parts overlap, and the game threw them off at speed when cut or yanked free. They are eased apart instead.
- **Big rooms take longer to pressurize.** The game fills and empties every room in the same few seconds. With this mod, a room takes as long as the ship sets for it, and the regulator's hiss and vent effect last as long as the change does.
- **Ghost ships are more haunted.** On top of the game's own haunting, the ship rolls, lights flicker and black out, the nodes radiate, and the growth disappears with the last node.
- **The reactor has to be shut down.** The ship's Solomon Epstein torch drive burns a core of fusion plasma in the reactor chamber, held by eight plasma injectors. Take them off in the wrong order and it lashes out with fire.
- **The ship flies.** Sit in a command chair and fly it out of the bay under the torch drive, then get up and walk its decks while it burns.

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

## Flight control

Sit in any command chair on the ship (near it, press Interact) and you are at the controls; Interact again gets you up. The view goes outside the ship.

**Controls** (keyboard and mouse / controller):

- **Side thrusters:** W A S D, Space up, C or Shift down / left stick, A up, B down.
- **Turn:** move the mouse / right stick. **Roll:** Q and E / bumpers (or the stick clicks).
- **Main drive:** mouse wheel / right and left triggers. **Z** full throttle, **X** (D-pad down) cut.
- **Brake** (thrusters against the drift): Left Ctrl, or Q and E together / both stick clicks.
- **Hold attitude** (thrusters stop any spin you did not ask for): T / D-pad up, on by default.
- **Flip and burn:** G / D-pad left (`[Flight] FlipKey`). The ship turns tail-first, burns down to a crawl and stops on its thrusters. Press again, or touch the throttle, to cancel.
- **Weapon wheel:** hold R / Y, point at a slice, let go (as the suit's tool wheel), or press 1-4: HELM, PDC, TORP, RAIL.
- **Views:** V / D-pad right: outside, nose, seat. Outside, hold the right mouse button / View button to orbit and zoom.

**Weapons** (pick them on the weapon wheel; they fire with the left mouse button / RT; holding the right mouse button / View to look around never fires):

- **PDC** (point defense cannons): the mouse / right stick moves a red reticle and the ship holds its attitude. Each of the six turrets turns its truss, body and barrel onto the point under the reticle, but only while it can see it; a segment per turret under the reticle shows which are on target. Hold to fire tracers: rounds carry the ship's speed, spark where they hit, and heat what they hit.
- **TORP** (torpedoes): both tube doors open when selected. A purple box cursor, with two rows of segments for the torpedoes left in the top and bottom tubes. Each press launches one, locked on the point under the cursor, from the tube on the target's side: it eases out on its thrusters, burns clear, turns, lights its blue drive and flies there (round the ship when the target is behind it), then explodes the way the game's own explosives do. The torpedo last in that tube's tray disappears; when the tray is empty, the one in the tube goes.
- **RAIL** (rail gun): the flight controls stay; a blue sight marks where the nose points. Hold to charge, and it fires: the rail gate's flash at the muzzle, then its beam along the nose. The slug goes through up to four parts and stops at the station; each shot pushes the ship back a little. It reloads in five seconds.

All of it is tunable in **[Weapons]** of the settings file (rates, ranges, damage, sizes and speeds of the effects).

**The HUD** shows the drive and thruster gauges, the speed, a prograde / retrograde bubble, the turn rate, the acceleration in m/s² and g, and the flip and burn's progress. In the outside views a thin hexagon marks the work bay, with its distance; off screen it sits on the edge with an arrow.

**The drive plume** burns out of the engine bell with the throttle: a white-hot core with shock diamonds inside a blue sheath that fans out and tapers to a point at full throttle.

**On foot while under way.** Get up and the ship stops where it is, for real, while you walk around: the flight goes on (its speed, a burn, a flip and burn) and the ship catches up when you sit down again. So everything aboard behaves as in the bay: the grapple, the cutter, grabbing, the decks.

- **Artificial gravity.** With the drive burning, you and the loose things aboard are pulled toward the stern. Things fall to the decks and land softly.
- **Deck boots.** Under the pull your boots lock onto the decks by themselves: walk with the move keys, run with Shift (L3) to toggle, crouch with thrust down. Space (A) jumps: the boots let go for a moment, holding it lifts you a little, and they lock on again as you come down. Your suit's thrusters stay off meanwhile.
- **Your suit's HUD** shows, top left, the distance to the Hab, the ship's speed and its acceleration.
- **Far out** (past 30 km from the bay) the ship parks 30 km out, in the direction it has flown, where positions are still precise; nothing looks different and it catches up when you sit down.
- If the MagBoots mod is installed, it is paused while the drive pulls aboard.

**Setup mode: fly other ships.** Turn on **Setup Mode** (FLIGHT rows of the EXPANSE options tab), then place demo charges as markers (they don't go off, and stick anywhere on the ship):

- **Alt+1**: an RCS thruster block at each charge (jets fire with the side thrusters and turning).
- **Alt+2**: a drive plume at each charge, sized to the face it sits on (replaces the drives set before).
- **Alt+3**: a seat where you stand, facing where you look. With no seat set, the command chairs are used.
- **Alt+Backspace**: remove the block, drive or seat you aim at.

The fittings are saved per ship class in `ships.json` next to the mod's DLL (hand-editable: `rcsSize`, `noBlocks` / `noBlock` for jets straight out of the hull) and come back on every ship of that class, resumed saves included. A ship with no drive set keeps its own (the MCRN Corvette's plume). Turn Setup Mode off to fly.

**Space.** The work area's limits are lifted for the flight, the planet, Moon and Sun stay put in the sky however far you go, and the game's space dust is off (see **[Space]**). The bay's light jacks are let go when the ship first moves, so the hull pushes them aside; the Master Jack (the Hab) stays put and stops the ship like a wall.

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

**[Flight]**
- **FlightControl** (default true): sit in a command chair to fly the ship. Also in the options menu.
- **SitDistance** (2.5), **SeatOffset**, **SeatYaw**: how close to a chair you sit down, and where and which way you sit.
- **DriveAcceleration** (5), **ThrusterAcceleration** (1), **TurnAcceleration** (10), **MaxTurnRate** (20), **MouseSensitivity** (1): how the ship flies.
- **CameraDistance** (0): how far the outside view starts; 0 = 1.3 times the ship's length.
- **AboardPull** (1): out of the seat, how strongly the drive's push pulls you toward the stern; 0 = no artificial gravity.
- **StandHeight** (1.6): how high above the deck the deck boots hold you, in meters.
- **ParkingSphere** (30): out of the seat, the ship parks no farther than this many km from the bay; 0 = always where it is.
- **OutsideViewFog** (false): the game's fog in the outside and nose views; through the bay it darkens the whole view.
- **FreeJacks** (true), **LightJackMass** (20): the bay's light jacks are let go, with this weight in tonnes, when the ship first moves. The Master Jack (the Hab) always stays put.

**[Space]**
- **ExtendedLimits** (true): room to fly: nothing is destroyed for straying from the work area while the ship is under way, the pilot has no speed cap, and (without the NewtonianPhysics mod) the player's work-area warning is lifted and the background is kept right far from the bay.
- **WorkAreaRadiusMultiplier** (0): how far from the work areas things may go before the game warns, hurts or destroys; 1 = stock, 0 = no limit.
- **FixedPlanets** (true): far from the bay the planet, Moon and Sun keep their distance whichever way you fly.
- **FlatEarthMode** (false), **AnglePinning** (false), **TriggerDistance** (250): how the background cards are kept far from the bay (without NewtonianPhysics).
- **SpaceDust** (false): the game's drifting specks and speed streaks.
- **DebugPrint** (false): extra lines in the log.

**[Drive]**
- **DrivePlume** (default false): a test burn of the plume without flying, at **TestThrottle** (0.25). In flight the plume follows the throttle.
- **Length** (75), **MouthDiameter** (0 = the bell's), **Spread** (1.4), **Brightness** (1), **Color**, **Turbulence** (0.6), **Sheath** (0.6), **Core** (4), **CoreDiameter** (3), **FlowSpeed** (300), **ShockDiamonds** (4), **Streak** (1), **LightLumens** (150000): the plume's look, each explained in the settings file.

**[Debug]**
- **TestTools** (default false): for testing only. Shows how many nodes are left and the reactor's balance on screen. **KillHalfNodesKey** (F8) destroys half of the nodes; **ReactorRightKey** (F11) and **ReactorWrongKey** (Shift+F11) treat the best or the worst injector as disconnected.
- **PhysicsCostTestKey** (empty = off): for measuring, logs where the physics time goes over 5 seconds.

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
