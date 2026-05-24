# QuickCutscene

Adds a skip prompt to unskippable story cutscenes in Hardspace: Shipbreaker, letting you move through the narrative at your own pace without breaking progression.

Skipping does **not** break story progression. Completion markers are posted normally — the game advances exactly as if you had watched the full sequence.

## What gets skipped

- After-shift story sequences (videos, speech, scripted events) that play in the hab at specific story beats
- The four Player Action Tracker (PAT) gated morning sequences: data drive discovery, Calysia anti-union, Rhodes arrives, and Lynx union clampdown

## Controls

An on-screen hint appears whenever a cutscene is skippable. The hint automatically switches between keyboard and controller prompts based on your last input.

| Input | Default |
|---|---|
| Keyboard | F6 |
| Controller | B (Xbox) / Circle (PlayStation) |

## Configuration

The config file is generated on first run at:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.QuickCutscene.cfg
```

```ini
[General]

## Enable or disable the plugin (requires restart).
# Setting type: Boolean
# Default value: true
Enabled = true

## Keyboard shortcut to skip the current cutscene.
# Setting type: KeyboardShortcut
# Default value: F6
SkipKey = F6

## Controller button to skip the current cutscene.
# Setting type: InputControlType
# Default value: Action2
ControllerSkipButton = Action2

## Log verbose debug info to BepInEx/LogOutput.log.
# Setting type: Boolean
# Default value: false
DebugPrint = false
```

### SkipKey examples

Use any [Unity KeyCode](https://docs.unity3d.com/ScriptReference/KeyCode.html) name. Modifier combinations are supported:

```ini
SkipKey = F6
SkipKey = Delete
SkipKey = LeftControl + F6
```

### ControllerSkipButton examples

Common values:

| Value | Xbox | PlayStation |
|---|---|---|
| `Action1` | A | Cross |
| `Action2` | B | Circle |
| `Action3` | X | Square |
| `Action4` | Y | Triangle |
| `Start` | Menu | Options |

```ini
ControllerSkipButton = Action2
ControllerSkipButton = Start
```

## Compared to NoStory

[NoStory](https://gitlab.com/faerbit/hsb-nostory) blocks cutscenes entirely, which prevents the story from continuing. QuickCutscene lets them start and gives you the choice to skip — progression is always preserved.

## Installation

1. Download the latest 64 bit (x64) version of BepInEx 5 (5.4.23.2 at time of writing, not 6 or above!) from https://github.com/BepInEx/BepInEx/releases
2. Extract into the same folder as `Shipbreaker.exe`
3. Run the game, load the main menu and quit
4. Extract the mod to `BepInEx\plugins\`, so you should have `.\BepInEx\plugins\QuickCutscene\me.kerballone.QuickCutscene.dll`
5. Config is located in `BepInEx\config\me.kerballone.QuickCutscene.cfg` (will be autopopulated on first run with the plugin installed)
