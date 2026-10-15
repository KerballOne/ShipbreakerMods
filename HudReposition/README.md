# HudReposition

Moves the part info panel in Hardspace: Shipbreaker. That's the panel describing the part you're looking at, along with its salvage destination label (PROCESSOR / BARGE / FURNACE). Use it to get the panel out of the middle of your view.

- Offsets are a percentage of the screen size, so they look the same at any resolution.
- By default, the panel is moved 8% down from its stock position.
- The salvage label moves with the panel. You can also give it an extra offset of its own.

## In-game settings

Open **Options** from the title screen or the pause menu and go to the **HUD** tab. There are two sections:

- **Part Info Panel:** horizontal and vertical offset. This moves the panel and the salvage label together.
- **Salvage Label (Extra Offset):** an extra horizontal and vertical offset for the salvage label only, on top of the panel offset. Leave both at 0 to keep it with the panel.

Each slider goes from -50 to 50 percent. Negative values move the panel left or down.

The panels move as soon as you change a slider. **Save** keeps the change. Leaving Options without saving puts the old position back. The tab works alongside other mods that add their own Options tabs, such as ShipbreakerTweaks and FreePlayPlus.

## Settings file

The same settings are in the config file, generated on first run at:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.HudReposition.cfg
```

**[PartInfoPanel]**
- **OffsetX** (default 0) / **OffsetY** (default -8): the panel offset, in percent of the screen.

**[SalvageLabel]**
- **OffsetX** / **OffsetY** (default 0): the salvage label's extra offset, in percent of the screen.

Edit the file while the game is closed. The game reads it at startup, and saving from the HUD tab overwrites it.

Version 1.0.0 stored each offset as a single `(x, y)` value. Those values are moved over to the new X/Y settings automatically the first time you run the new version.

## Installation

1. Download the latest 64-bit (x64) version of BepInEx 5 (not 6 or above) from [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the same folder as Shipbreaker.exe
3. Run the game, load the main menu and quit
4. Extract the mod into `BepInEx\plugins\`, so you end up with `BepInEx\plugins\HudReposition\me.kerballone.HudReposition.dll`

## Support

Questions, bugs, or feedback? Join the Shipbreaker Discord: [discord.gg/shipbreakergame](https://discord.gg/shipbreakergame)
