# Themes

Reskins Hardspace: Shipbreaker from folders of your own files. Drop pictures and music into the mod's folders and the game uses them in place of its own:

- **Menu:** the picture behind the main menu.
- **Loading:** a picture on the loading screen.
- **Planet:** the planet in the sky outside the work bay and the hab.
- **Music:** your own tracks during a shift.

Each part is separate. If a folder is empty, that part of the game stays exactly as it ships.

The mod comes with a Mars theme to start from: four menu pictures, thirteen loading pictures and Mars in place of the Earth. It comes with no music.

## The folders

Everything lives beside the mod's DLL, in `BepInEx\plugins\Themes\`:

```
Themes\
  Menu\          main menu pictures
  Loading\       loading screen pictures
  Planet\        planet pictures
  Audio\Shift\   music for shifts
```

Add, replace or delete files in these folders to change what you get. File names don't matter.

### Menu

- PNG or JPG.
- One picture is picked at random each time you start the game.
- The game's own picture is 2048 x 1159. A picture of that shape appears exactly where the original was.

### Loading

- PNG or JPG.
- A different picture is picked at random for each loading screen.
- By default the whole picture is shown, centred, on a black background. See **Zoom** and **Stretch** below if you would rather fill the screen.
- The tooltip, loading bar and prompt cover the bottom third of the screen, so keep the subject in the upper two thirds.
- The loading screen's old-monitor look (scanlines, glow, film grain) is applied to your picture too.
- The LYNX logo in the middle of the screen is hidden while a picture is showing.

### Planet

- PNG with transparency. One picture is picked at random each time you start the game.
- Lay the picture out like the game's own Earth:
  - square, 4096 x 4096;
  - the planet as a full disc in the centre, about 3200 pixels across;
  - lit from the upper left, with a solid black night side at the lower right;
  - transparent outside the disc, with a soft edge.
- The glow around the planet is recoloured to suit Mars, and the Moon is hidden. Both can be changed in the settings.

### Music

- MP3 or WAV, in `Audio\Shift\`.
- Tracks are shuffled while you are in a shift: every track plays once before any is heard again. They are silent in the hab, the menus and cutscenes.
- The music pauses when the game is paused or loses focus, and resumes from the same place.
- Your place in a track is kept between shifts, so a long track carries on where it left off.
- One track runs into the next without a gap. A single track loops.
- Press `]` to skip to the next track and `[` to go back to the previous one. Both keys can be changed in the settings.
- It follows the game's own Music and Master volume sliders.
- The game's own shift music is silenced while your tracks are in use.

Tracks added while a shift is running are picked up without restarting.

## Going back to the stock game

Empty a folder to put that part of the game back to normal. To keep your files in place, set **Enabled** to `false` for that part in the settings file.

## Settings file

Generated on first run at:

```
Hardspace Shipbreaker\BepInEx\config\me.kerballone.Themes.cfg
```

Edit it while the game is closed.

**[Menu]**
- **Enabled** (default true): use the pictures in the Menu folder.
- **Folder** (default `Menu`): the folder to read them from.

**[Loading]**
- **Enabled** (default true): use the pictures in the Loading folder.
- **Folder** (default `Loading`): the folder to read them from.
- **Zoom** (default 0): how far a picture is enlarged when its shape differs from your screen's. 0 shows the whole picture. 1 enlarges it until the screen is covered and crops what overflows.
- **Stretch** (default false): pull each picture to the screen's shape so it covers everything with nothing cropped. Pictures of a different shape are distorted. When on, Zoom is ignored.
- **BlackBackground** (default true): plain black behind the picture. Turn it off to see the game's teal background beside it.
- **HideLogo** (default true): hide the LYNX logo while a picture is showing.

**[Planet]**
- **Enabled** (default true): use the pictures in the Planet folder.
- **Folder** (default `Planet`): the folder to read them from.
- **GlowColor** (default `D9804D21`): colour of the glow around the planet, as RRGGBBAA. The last pair is its strength. The game's own Earth glow is `1EB1FF63`.
- **HideMoon** (default true): hide the Moon while the planet is replaced.

**[Music]**
- **Enabled** (default true): play the tracks in the music folder.
- **Folder** (default `Audio/Shift`): the folder to read them from.
- **Volume** (default 1): loudness of your music, from 0 to 1, on top of the game's own sliders.
- **GameMusicVolume** (default 0): how much of the game's own shift music stays while yours is in use. 0 silences it. 1 leaves it at full volume underneath.
- **FadeSeconds** (default 3): how long your music takes to fade in when a shift starts and out when it ends.
- **PreviousTrackKey** (default `LeftBracket`, the `[` key): go back to the previous track. With no earlier track it restarts the current one.
- **NextTrackKey** (default `RightBracket`, the `]` key): skip to the next track.

The keys use Unity key names, such as `PageUp` or `F8`. Add modifiers with `+`, such as `RightBracket + LeftAlt`. Set a key to `None` to turn it off.

## Notes

- Menu and planet pictures are chosen once per game start. Restart the game to get a different one.
- Music is Windows only. The game's own sound system can't play loose files, so the mod plays them through Windows directly, using the `NAudio.dll` that ships with it. Keep that file beside the mod's DLL.
- Because the music doesn't go through the game's sound system, it has no in-game reverb and is not positioned in space.
- OGG files are not supported.

## Installation

1. Download the latest 64-bit (x64) version of BepInEx 5 (not 6 or above) from [github.com/BepInEx/BepInEx/releases](https://github.com/BepInEx/BepInEx/releases)
2. Extract into the same folder as Shipbreaker.exe
3. Run the game, load the main menu and quit
4. Extract the mod into `BepInEx\plugins\`, so you end up with `BepInEx\plugins\Themes\me.kerballone.Themes.dll` with `NAudio.dll` and the `Menu`, `Loading`, `Planet` and `Audio` folders beside it

## Support

Questions, bugs, or feedback? Join the Shipbreaker Discord: [discord.gg/shipbreakergame](https://discord.gg/shipbreakergame)
