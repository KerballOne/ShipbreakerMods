# BayLights 1.1: whitelist (built, needs one in-game check)

Requested by KerballOne on 21 November 2026 and built the same day. This file is the hand-over for whoever continues.

## What changed

Version 1.0 walked every light and multiplied it by type, so any light on any ship was changed. Version 1.1 changes four named groups and nothing else.

| Group | Which lights | Config section |
|---|---|---|
| Helmet | The Spot light named `Helmet Spotlight` | `Helmet` |
| Bay | Spot lights that are not inside a ship, with a cone at or below `MaxAngle` or a default name (`Spot Light...`, `Point Light...`) | `BayLights` |
| Sodium | Lights inside a ship whose own name or a parent's name matches `NamePatterns` (default `*Sodium*`) | `SodiumLights` |
| Custom | Lights matching a rule in `Rules`, each rule with its own multipliers | `Custom` |

Every other light keeps the values it was authored with.

- **Inside a ship** means some object above the light carries the game's `ModuleDefinition` or `RootModuleDefinition` component. The check is by type name, so the mod still has no reference to the game DLL. Code: `IsInsideShip()`.
- **Name patterns** use `*` for any text and are not case sensitive. A pattern without `*` must match the whole name. Code: `ToRegex()`, `NameMatches()`.
- **Custom rules** are `NamePattern|IntensityMultiplier|RangeMultiplier`, separated by semicolons, for example `*Console*|2|1.5; PRF_Light_Emergency*|3|2`. The first matching rule wins, and a custom rule wins over the built-in groups. A malformed rule is skipped with a warning in the log.
- Each group has an `Enabled` switch.
- The blacklist setting `ShipLights / ExcludeNameContains` is gone. It is not needed: a light nobody named is not touched.
- `General / LogLights` now works. It lists every light after each scene load with its type, path, scene, range and intensity before and after, and the group it fell into.

## What 1.0 did that 1.1 does not

1.0 multiplied every Point light and every remaining Spot light on a ship by 5, with double range. 1.1 leaves those alone. Ship interiors lit mainly by such lights will look darker than under 1.0. To get any of them back, name them in a `Custom` rule.

1.0 also treated any narrow Spot light as a bay light, including ones on a ship. 1.1 does not.

## Moving an existing config

`python migrate_config.py` (in this folder) rewrites the config file in the game's BepInEx `config` folder and keeps a `.bak` copy. It copies the old Area multipliers and `DisableShadows` value into `SodiumLights`, removes the old `ShipLights` section and adds an empty `Custom` section. It was run on the owner's config on 21 November 2026.

## Still to check in game

None of this has been run in the game yet. With `LogLights = true`, load a ship and read `BepInEx/LogOutput.log`:

1. **Bay lights** show `| bay` and their range and intensity changed. If none do, the bay's lights are inside something with a `ModuleDefinition` and `IsInsideShip()` needs another test.
2. **Sodium fixtures** show `| sodium`. If they show `| untouched (ship)`, look at the logged path for the fixture's real name and set `SodiumLights / NamePatterns` to match. The default assumes the fixture or a parent has "Sodium" in its name.
3. **A hand-tuned light**, for example the small light on a custom AI node, shows `| untouched (ship)` with the same values before and after.
4. The summary line reads `N bay, N helmet, N sodium, N custom-rule lights changed; N lights left untouched`.

Then set `LogLights = false`. It writes one line per light on every scene load.

## Things to keep

- `_boosted` remembers which lights were already multiplied, so a second scene load does not multiply them again.
- Lights are changed about 3 seconds after the light count stops changing, up to 30 seconds after a scene load.
- The mod reads its config once at start-up.
