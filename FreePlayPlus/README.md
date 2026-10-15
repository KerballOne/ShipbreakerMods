# FreePlayPlus

More to do in Hardspace: Shipbreaker's FreePlay: career ghost ships, danger that actually matters, supplies you have to manage, and saves so big ships don't have to be finished in one sitting.

Formerly **GhostShipsInFreePlay**. See [Upgrading from GhostShipsInFreePlay](#upgrading-from-ghostshipsinfreeplay).

## Ghost ships

In career, the job board occasionally offers a ghost ship: the card with the scrambled thumbnail and the "???????????" owner. When you salvage one, it has AI nodes scattered through it and its own lighting. Stock FreePlay never gives you one of these. FreePlayPlus adds a checkbox that does.

- A **Ghost Ship** checkbox sits at the bottom center of the FreePlay ship selection screen. It's unchecked by default, and the game remembers its setting between sessions.
- With it checked, any FreePlay ship you launch loads as the career ghost variant, with AI nodes, ghost lighting and ghost skin.
- It's guaranteed every time. In career, a ghost ship only appears on a 15% roll; the checkbox always forces it.
- Ships that have a ghost variant in career get a **(GHOST)** tag after their name in the list while the checkbox is checked.

### Controls

- **Mouse:** click the checkbox.
- **Keyboard:** press **G**.
- **Controller:** press **DPad Down**.

The hint on the checkbox switches between the key and the controller button automatically, depending on which you used last. PlayStation and Xbox controllers are both supported.

## Health damage

Stock FreePlay makes you invulnerable. FreePlayPlus turns damage back on (on by default):

- You take health damage from impacts and hazards like in career: decompression, explosions, crushing, and leaving the play area.
- Suit repair kits found on the ship heal you.
- Oxygen is handled separately: see [Consumables](#consumables).

## Consumables

Stock FreePlay gives you unlimited oxygen, fuel, demo charges and tethers, so the kiosk has little to sell. FreePlayPlus makes each of them run out like in career (all on by default, each with its own setting):

- **Oxygen** drains as you work. Refill at the kiosk or in pressurised areas. With health damage on, running out suffocates you.
- **Thruster fuel** drains as you thrust.
- **Demo charges** start at career's carry amount.
- **Tethers** start at career's carry amount instead of 1000.

Restock at the kiosk. Oxygen, fuel and medkit purchases take **15 seconds** to fill (adjustable, or 0 for instant), so a supply run takes a moment. You pay up front; the refill only runs while the kiosk screen is open, so if you leave it pauses, and it picks up where it left off when you come back, without buying again. Pickups found on the ship still apply instantly.

### Expenses and NoCredit

The kiosk keeps a running total of what you've spent on the current ship: your **expenses**. It carries over when you save and load the ship, and the FreePlay **Shift Summary** shows it at the bottom right.

With **NoCredit** (on by default), you can only spend what the ship has earned you. The kiosk refuses a purchase that would take your expenses past the value you've salvaged from this ship so far, and shows that item as unaffordable. A fresh ship starts with nothing salvaged, so salvage something before your first supply run.

## Death ends the run

In career, dying costs a clone fee and you respawn on the same ship. FreePlay has no currency, so there's no cost to dying. With FreePlayPlus (on by default), acknowledging the death report takes you back to the **FreePlay ship select** instead of respawning, so you pick your next ship.

## Ship saves

Large ships, custom ones especially, can take more than one sitting. FreePlayPlus saves the ship you're working on so you can come back to it.

- **Autosaves** every time salvage passes another 10% (adjustable). The newest 3 are kept.
- **Manual save:** at the Hab door, the prompt's Cancel button now reads **Save**.
- **Exit save:** confirming Exit at the Hab door saves before leaving. Quitting from the pause menu, abandoning the shift, or dying does not save.
- Each ship keeps up to 12 saves; past that, the oldest is deleted.

On the FreePlay ship select, a ship with saves shows **(42.3% - 12m 34s)** (its latest save). Picking it opens the save list: date, type and salvaged % for each save, with **Load** and **Delete**, plus **New ship**, **Delete all** and **Close**. It works with mouse, keyboard and controller.

Loading restores the ship as it was, cuts included. Your own oxygen, fuel, health and tools start fresh, as on a new ship.

Saves are kept in `%USERPROFILE%\AppData\LocalLow\Blackbird Interactive\Hardspace_ Shipbreaker\FreePlayPlus\ShipSaves\<Ship_Name>\`, separate from the game's own saves.

### Custom ships

Saves work for custom ships (ModdedShipLoader and similar) too. Before a saved custom ship loads, FreePlayPlus prepares its parts the same way a fresh spawn does, so a big custom ship can take a moment on a **Preparing** screen. It also works around build quirks that would otherwise leave a loaded custom ship solid but unsalvageable (for example, parts carrying a script that doesn't exist at runtime), and makes sure custom ships show their own name and get the standard FreePlay starting upgrades.

## Checking whether a ship is a ghost

Every time a ship finishes spawning, in FreePlay or career, one line is written to `BepInEx\LogOutput.log`:

```
[GhostStatus] Gecko_Science_Stargazer_ModuleConstructionAsset 'Ship Name' session=FreeMode ghost=YES (forced by checkbox) overrides=Properties_HazardLevel_8_Ghost
```

`ghost=YES` / `ghost=no` says whether the ship really is a ghost ship. `(forced by checkbox)` or `(rolled by the game)` says where it came from.

## Settings

Most settings can be changed in game on the **FREEPLAY+** tab of the Options menu, from the title screen or the pause menu. Changes take effect as soon as you make them. Leaving the Options menu without saving puts the old values back. Settings marked "(next ship)" take effect when the next ship loads. The keybinds, the tag text, the checkbox offsets and debug logging can only be changed in the config file.

The config file is generated on first run at:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.FreePlayPlus.cfg
```

Settings from an older version are carried over to the current names automatically.

**[GhostShips]**
- **ForceGhostShip:** the Ghost Ship checkbox state. Normally changed from the checkbox itself.
- **IncludeCareerOnlyModules** (default on): keeps the parts the game normally leaves out of FreePlay ghost ships, including the AI nodes. Turn it off and ghost ships will have no AI nodes.

**[GhostShip_UI]**
- **ToggleKey** / **ToggleControllerButton:** the keyboard key (G) and controller button (DPad Down) that flip the Ghost Ship checkbox.
- **GhostVariantTag:** the text shown after ship names. TextMeshPro rich text is supported.
- **OffsetFromBottomCenterX** / **OffsetFromBottomCenterY:** moves the checkbox from its bottom-center spot.

**[Survival]**
- **HealthDamage** (default on): health damage in FreePlay, as above. Turn it off for stock invulnerability.
- **ExitToMenuOnDeath** (default on): a FreePlay death returns you to the ship select. Turn it off to respawn on the ship as stock.
- **ConsumeOxygen** / **ConsumeFuel** / **ConsumeDemoCharges** / **ConsumeTethers** (default on): each resource runs out as above. Turn one off to make it unlimited again.
- **RefillSeconds** (default 15): how long a kiosk oxygen, fuel or medkit purchase takes to fill while the kiosk is open. 0 = instant.
- **NoCredit** (default on): the kiosk refuses purchases that would take your expenses past the value salvaged from the ship. Turn it off to buy freely.

**[Saves]**
- **AutosavePoints** (default 10): autosave each time salvage passes another multiple of this percent. 0 = no autosaves.
- **Autosaves** (default 3): how many autosaves to keep per ship. Manual and exit saves are kept until you delete them.

**[Debug]**
- **DebugLogging** (default off): extra troubleshooting detail in the log.

## Notes

- Ships without a (GHOST) tag still load with the ghost property when the box is checked. But career has no ghost variant for them, so they may not get AI nodes or ghost lighting.
- If you use mods that brighten or recolor ship lights, they will also affect the ghost lighting.

## Upgrading from GhostShipsInFreePlay

1. Delete the old `BepInEx\plugins\GhostShipsInFreePlay` folder. If both are installed, both load.
2. Install FreePlayPlus as below.

Your settings carry over automatically: on first run, FreePlayPlus copies `me.kerballone.GhostShipsInFreePlay.cfg` to its own config file. The old file is left in place, and you can delete it.

## Installation

1. Download the latest 64-bit (x64) version of BepInEx 5 (not 6 or above) from [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the same folder as Shipbreaker.exe
3. Run the game, load the main menu and quit
4. Extract the mod into `BepInEx\plugins\`, so you end up with `BepInEx\plugins\FreePlayPlus\me.kerballone.FreePlayPlus.dll`

## Support

Questions, bugs, or feedback? Join the Shipbreaker Discord: [discord.gg/shipbreakergame](https://discord.gg/shipbreakergame)
