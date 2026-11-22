# BayLights: change from blacklist to whitelist (to do)

Requested by KerballOne on 21 November 2026. Not started. This file is the hand-over for whoever picks it up.

## What the mod does now

`Plugin.cs`, `BoostLights()`: after each scene load it walks every `Light` in every loaded scene and multiplies range and intensity by light type.

| Lights | Config section | Defaults in code |
|---|---|---|
| Bay spotlights (Spot lights with a cone at or below `MaxAngle`) | `BayLights` | range x2, intensity x1 |
| Helmet light (a Spot light named `Helmet Spotlight`) | `Helmet` | range x2, intensity x1 |
| Every other Spot light | `ShipLights` | range x2, intensity x5 |
| Every Area light (the interior sodium lights) | `ShipLights` | range x2, intensity x5 |
| Every Point light | `ShipLights` | range x2, intensity x5 |

So any light on any ship is changed unless something stops it.

## The problem

A custom part with a small hand-tuned Point light (an accent light on an AI node, 3 cd, 1 m range) was multiplied by 5 with double range and looked blinding. The stop-gap added the same day is a blacklist:

- Config `ShipLights` / `ExcludeNameContains`, default `AI_Node_Crystal`.
- Comma-separated fragments. A light is skipped when its own name or any parent's name contains one.
- Code: `IsExcluded()` and the `continue` at the top of the loop in `BoostLights()`.

A blacklist means every new hand-tuned light has to be added to it. The owner wants the opposite.

## What is wanted

Only lights that are named in the config are changed. Everything else is left exactly as authored.

1. **Bay spotlights:** keep, as their own config section. They are identified by being bay lights, not by a name the user types.
2. **Helmet light:** keep, as its own config section.
3. **Interior sodium lights:** keep, as their own config section, separate from the other two. Today they are caught by "every Area light"; they should be identified as sodium lights specifically.
4. **All other lights:** not affected unless explicitly named in the config. The owner suggested name patterns with wildcards.
5. The blacklist setting `ExcludeNameContains` goes away once the whitelist exists.

## Suggested shape (not agreed, check with the owner)

- A section per built-in group (`BayLights`, `Helmet`, `SodiumLights`), each with its own multipliers and an on/off switch.
- One section for user-named groups, for example `Custom`: a list of patterns, each with its own range and intensity multiplier. Match against the light's name and its parents' names, as `IsExcluded()` does now, with `*` as a wildcard.
- Decide how to recognise a sodium light. Candidates: the light's type is Area and a parent's name contains the sodium fixture's prefab name (`PRF_Light_LowSodium_Industrial` in the ship project). Confirm in game with `LogLights = true`, which already logs every light.
- Defaults should reproduce today's look for bay, helmet and sodium lights, so existing users see no change there.

## Things to keep

- `_boosted` remembers which lights were already multiplied, so a second scene load does not multiply them again. Keep that.
- `DisableShadows` exists because boosted range made shadows flicker.
- The mod reads its config once at start-up; lights are changed about 3 seconds after the light count stops changing.

## How to check it

1. Set `LogLights = true` and load a ship.
2. The log lists every light with its type, name, range and intensity.
3. Confirm that bay, helmet and sodium lights changed, and that a light not named in the config has the same values as with the mod disabled.
