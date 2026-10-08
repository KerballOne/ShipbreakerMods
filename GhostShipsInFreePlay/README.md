# GhostShipsInFreePlay

Play the ghost ships from Hardspace: Shipbreaker's career mode in FreePlay.

In career, the job board occasionally offers a ghost ship: the card with the scrambled thumbnail and the "???????????" owner. When you salvage one, it has AI nodes scattered through it and its own lighting. Stock FreePlay never gives you one of these. This mod adds a checkbox that does.

## How it works

- A **Ghost Ship** checkbox sits at the bottom center of the FreePlay ship selection screen. It's unchecked by default, and the game remembers its setting between sessions.
- With it checked, any FreePlay ship you launch loads as the career ghost variant, with AI nodes, ghost lighting and ghost skin.
- It's guaranteed every time. In career, a ghost ship only appears on a 15% roll; the checkbox always forces it.
- Ships that have a ghost variant in career get a **(GHOST)** tag after their name in the list while the checkbox is checked.
- Unchecked, FreePlay is completely stock, and the mod changes nothing.

## Controls

- **Mouse:** click the checkbox.
- **Keyboard:** press **G**.
- **Controller:** press **DPad Down**.

The hint on the checkbox switches between the key and the controller button automatically, depending on which you used last. PlayStation and Xbox controllers are both supported.

## Checking whether a ship is a ghost

Every time a ship finishes spawning, in FreePlay or career, one line is written to `BepInEx\LogOutput.log`:

```
[GhostStatus] Gecko_Science_Stargazer_ModuleConstructionAsset 'Ship Name' session=FreeMode ghost=YES (forced by checkbox) overrides=Properties_HazardLevel_8_Ghost
```

`ghost=YES` / `ghost=no` says whether the ship really is a ghost ship. `(forced by checkbox)` or `(rolled by the game)` says where it came from.

## Settings

The config file is generated on first run at:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.GhostShipsInFreePlay.cfg
```

- **ForceGhostShip:** the checkbox state. Normally changed from the checkbox itself.
- **IncludeCareerOnlyModules** (default on): keeps the parts the game normally leaves out of FreePlay ships, including the AI nodes. Turn it off and ghost ships will have no AI nodes.
- **ToggleKey** / **ToggleControllerButton:** the keyboard key (G) and controller button (DPad Down) that flip the checkbox.
- **GhostVariantTag:** the text shown after ship names. TextMeshPro rich text is supported.
- **OffsetFromBottomCenterX** / **OffsetFromBottomCenterY:** moves the checkbox from its bottom-center spot.
- **DebugLogging** (default off): extra troubleshooting detail in the log.

## Notes

- Ships without a (GHOST) tag still load with the ghost property when the box is checked. But career has no ghost variant for them, so they may not get AI nodes or ghost lighting.
- If you use mods that brighten or recolor ship lights, they will also affect the ghost lighting.

## Installation

1. Download the latest 64-bit (x64) version of BepInEx 5 (not 6 or above) from [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the same folder as Shipbreaker.exe
3. Run the game, load the main menu and quit
4. Extract the mod into `BepInEx\plugins\`, so you end up with `BepInEx\plugins\GhostShipsInFreePlay\me.kerballone.GhostShipsInFreePlay.dll`

## Support

Questions, bugs, or feedback? Join the Shipbreaker Discord: [discord.gg/shipbreakergame](https://discord.gg/shipbreakergame)
