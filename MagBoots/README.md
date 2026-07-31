# MagBoots

Adds magnetic boots to Hardspace: Shipbreaker, letting you lock onto any suitable hull surface and walk it like it's got gravity, instead of free-floating everywhere.

Press the toggle key with a ship part below you. If the surface is flat enough and big enough, you'll snap into place above it and start walking. Keep moving and MagBoots keeps checking a step ahead of you, so you can walk over curves, seams, and corners without losing your footing.

## How it works

- Press the toggle key to attach. MagBoots checks straight down from you; if it finds a surface that's flat and big enough, you snap into place.
- While attached, movement input walks you along the surface instead of free-thrusting. You're held at a fixed distance off the hull.
- MagBoots checks a stride ahead of you as you walk, so you can climb and descend stairs, ramps, and ledges without losing your footing. Whether a step counts as "up" or "down" is decided by where you're looking - up or level means up, down means down.
- Look up and down freely — you just can't tip all the way over and stare straight into the deck. Pitching your view further (up or down) also shortens your stride, so tilting your view takes you into smaller, more careful steps; leveling back out returns you to full stride.
- While attached, looking around is instant and precise instead of the normal zero-g drift - turning your head doesn't keep spinning after you stop moving the mouse/stick, the way it does while free-floating. Goes back to normal zero-g drift the instant you detach.
- Hold or toggle the run key to move faster - battery drains faster while running too, proportional to the speed increase. Run only works while already attached; it can't be armed ahead of time and never carries over into your next attach.
- Your thrusters are fully suppressed while attached, so they don't fight MagBoots' hold, waste fuel, or rumble the controller for no reason. Refueling still works normally.
- Press the toggle key again to detach and go back to normal zero-g movement at any time.
- Holding the thrust-down input crouches you closer to the surface, for squeezing under low obstacles; release to stand back up.

## Battery

Mag boots draw a limited battery, in minutes, that only drains while you're locking on or locked to a surface. It recharges to full at the start of every shift.

Choose how it's shown on screen: a segmented gauge, a live percentage, a countdown timer, or turn it off entirely. Run it down to zero and the boots stop working (shown as NO POWER) until your next shift.

Standing still while locked on costs much less power than actually walking, and the initial snap-into-place costs more than either - so quickly tapping the toggle on and off isn't a way to dodge the battery cost.

## HUD hint

A small on-screen hint in the corner shows your current status, styled to match the game's own prompts. Below the status it shows two rows - POWER (attach/detach) and SPEED (run) - each with your key/button next to it, translated to your controller's real button name if you're on a gamepad. Each row's chip inverts to a solid white background when that action is currently active (attached, or running), so you can tell at a glance without reading the text.

- **White** — off, ready to attach
- **Yellow** — locking on (snapping into position)
- **Green** — locked and walking
- **Red** — error, no valid surface found below you
- **Gray** — no power, battery depleted

Its position and size can both be tweaked in the config — position as a percentage offset from the center of your screen, size as a scale multiplier (1 is default, 2 is twice as big). Turning on `ShowStride` in the config adds a small readout right of the hint showing your current stride distance, which shrinks in real time as you pitch your view.

## Settings

The config file is generated on first run at:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.MagBoots.cfg
```

Every setting has a plain-language description in the file itself — open it in any text editor to see what each one does and tweak it to taste. Settings are named so related ones sort together (e.g. everything about height starts with `Height_`, everything about a settle/catch-up window starts with `Settle_`), and are grouped into sections:

- **1 - General** — toggle key/button, run key/button, debug logging.
- **2 - Attach** — only the initial attach itself: surface size/angle requirements, standoff height, snap duration.
- **3 - Movement** — ordinary walking and looking around: stride length, move/run speed, corner smoothing, look-down limit, the pitch-based stride control, and PreciseRotation.
- **4 - Steps** — detecting and handling an actual step up or down while walking: step height thresholds, how fast height eases in, how a step-down waits for you to catch up, forward-facing allowances for taller/steeper footholds you're heading toward.
- **5 - Physics** — the spring/damper holding you to the surface, and how hard an impact has to be before you're knocked loose.
- **6 - Battery** — capacity and drain rates.
- **7 - HUD** — on-screen hint appearance.

A few highlights:

- **Distance_MaxStride** is your stride length — how far ahead MagBoots checks for the next foothold while walking.
- **Speed_Walk** is your walking speed while attached; **Speed_Run** is how fast you go while running. Actual speed may run a touch above whichever one you set.
- **PlayerHeight** is how far off the surface you float.
- **Pitch_MaxStride**/**Pitch_MinStride** control the pitch-based stride shortening: no effect within Pitch_MaxStride degrees of level, shrinking to a near-zero stride by Pitch_MinStride degrees, in either direction (looking up or down).
- **Rotation_Precise** turns on instant, drift-free looking while attached (on by default); **Rotation_PreciseSensitivity** is a separate multiplier just for that mode, since instant look can feel faster or slower than the drifting version at the same sensitivity.
- **Height_StepDown** is how far below your feet MagBoots will look for a surface to attach to or step down onto.
- **Height_StepUp** is how far above your current footing MagBoots will look for a surface to step up onto while already attached.
- **Settle_StepDown**/**Settle_StepUp** control how closely you need to catch up to a detected step (as a fraction of your stride) before MagBoots finishes the height (step down) or forward movement (step up) adjustment; **Timeout_Step** is the maximum wait either way, so you're never stuck waiting even if you never fully catch up.
- **Angle_MaxNormal** is how steep a surface can be for most footholds; **Angle_MaxNormalFwd** is a looser limit that only applies to the direction you're actually facing, so you can walk up steeper ramps you're heading toward.
- **BreakawayVelocity** is how hard you need to be hit before mag boots let go instead of holding on.

The config file also reloads live - edit and save it while the game is running and your changes apply immediately, no restart needed.

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
