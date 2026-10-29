# Telltale Modding Studio

A desktop app for exploring and modding Telltale games. Point it at an installed
game (e.g. *The Walking Dead: The Telltale Definitive Series* or *Tales from the
Borderlands*) and it inventories the whole game: every character with their
outfit parts, textures and animations, and every file in its archives. Swap in
replacements and build a minimal patch archive the game loads over its originals.

Archives are read natively (`.ttarch2`: chunked zlib/Oodle, Blowfish-encrypted or
plain). [ttarchext](https://aluigi.altervista.org/papers.htm#ttarchext) by Luigi
Auriemma (for learning purposes only) is still used to build patches, to decrypt
the games' resource descriptions, and to read classic `.ttarch` archives.

**Download:** the Windows build is on the [Releases](../../releases) page. Unzip it
and run `TelltaleModding.App.exe`; keep the `engine` folder next to it.

## Screenshots

Click a picture for the full size.

<table>
  <tr>
    <td align="center" width="33%"><a href="docs/screenshots/characters.png"><img src="docs/screenshots/characters.png" width="280" alt="Characters: outfits and the game's look (Clementine)"></a><br><sub>Characters: outfits and the game's look (Clementine)</sub></td>
    <td align="center" width="33%"><a href="docs/screenshots/animations.png"><img src="docs/screenshots/animations.png" width="280" alt="Animations played on the model"></a><br><sub>Animations played on the model</sub></td>
    <td align="center" width="33%"><a href="docs/screenshots/face.png"><img src="docs/screenshots/face.png" width="280" alt="Face tab: moods, mouth shapes, gestures"></a><br><sub>Face tab: moods, mouth shapes, gestures</sub></td>
  </tr>
  <tr>
    <td align="center" width="33%"><a href="docs/screenshots/characters_tftb.png"><img src="docs/screenshots/characters_tftb.png" width="280" alt="Fiona, Tales from the Borderlands"></a><br><sub>Fiona, Tales from the Borderlands</sub></td>
    <td align="center" width="33%"><a href="docs/screenshots/characters_got.png"><img src="docs/screenshots/characters_got.png" width="280" alt="Asher, Game of Thrones"></a><br><sub>Asher, Game of Thrones</sub></td>
    <td align="center" width="33%"><a href="docs/screenshots/characters_wau.png"><img src="docs/screenshots/characters_wau.png" width="280" alt="Bigby in the Graphic black look"></a><br><sub>Bigby in the Graphic black look</sub></td>
  </tr>
  <tr>
    <td align="center" width="33%"><a href="docs/screenshots/dialogue.png"><img src="docs/screenshots/dialogue.png" width="280" alt="Dialogue with a lip-synced 3D speaker"></a><br><sub>Dialogue with a lip-synced 3D speaker</sub></td>
    <td align="center" width="33%"><a href="docs/screenshots/conversation.png"><img src="docs/screenshots/conversation.png" width="280" alt="Conversation trees (Talks)"></a><br><sub>Conversation trees (Talks)</sub></td>
    <td align="center" width="33%"><a href="docs/screenshots/scenes.png"><img src="docs/screenshots/scenes.png" width="280" alt="Scenes rebuilt with their own lights"></a><br><sub>Scenes rebuilt with their own lights</sub></td>
  </tr>
  <tr>
    <td align="center" width="33%"><a href="docs/screenshots/scripts.png"><img src="docs/screenshots/scripts.png" width="280" alt="Decompiled Lua scripts"></a><br><sub>Decompiled Lua scripts</sub></td>
    <td align="center" width="33%"><a href="docs/screenshots/assets.png"><img src="docs/screenshots/assets.png" width="280" alt="Asset browser and texture preview"></a><br><sub>Asset browser and texture preview</sub></td>
    <td align="center" width="33%"><a href="docs/screenshots/home.png"><img src="docs/screenshots/home.png" width="280" alt="Game library"></a><br><sub>Game library</sub></td>
  </tr>
</table>

## Workspaces

- **Home**: your game library. Add a game's install folder, or one found in your
  Steam libraries. Opening a game indexes its archives once (a few seconds) and
  caches the index under `%LOCALAPPDATA%\TelltaleModdingStudio\catalogs`.
- **Characters**: every character the game defines. The default outfit comes from
  the character's own `.prop` (its mesh list and per-mesh visibility); toggle parts
  in the Outfit tab. Pick a clip in the Animations tab to play it, with scrubbing,
  speed and looping. The Face tab poses the face over the playing clip (a
  mood's expression, mouth shapes, head and face gestures, single facial bones)
  and lip-syncs the character's voice lines. The skeleton button draws the bones inside the
  model. Textures are matched through each mesh's materials (including materials
  they inherit), and when a file exists in several archives the game's own
  resource priorities decide which copy is used (patches over episodes over
  seasons). **Export…** saves the
  character as a `.glb` (glTF 2.0) for Blender and other tools: the dressed, textured
  mesh, its skeleton and skin, and the clip that's playing.
- **Scenes**: every location in the game, rebuilt from its `.scene` file: each
  object placed with its transform, textured, in an orbit/fly-around viewport. Toggle
  object kinds or single objects, and export what's shown to `.glb`.
- **Dialogue**: every subtitle line in the game, matched to its voice recording.
  Filter by episode, speaker or language, search what's said, click a line to hear
  it (Continuous plays a scene through), and save lines as their original MP3/Ogg
  or as WAV, or export the whole filtered list with a transcript. The speaker
  appears beside the list in 3D, lip-synced: each line's phoneme track drives the
  character's mouth poses, in the mood its direction tags name (e.g. *smugA*).
  **Talks** shows each scene's conversations as the game's dialogue tree (lines,
  choice menus and their options, branches and jumps) and can play a conversation
  through, offering the choices as buttons. **In conversation** jumps from a line
  to its place in the tree. **Music** lists the soundtrack by episode and music bank. The Characters
  workspace links straight to a character's lines.
- **Scripts**: the game's Lua scripts. Compiled scripts are decompiled to readable
  Lua (or shown as an annotated instruction listing), with a function outline,
  syntax highlighting and search (Ctrl+F). Search script names or what's inside
  them, and save the decompiled source or the original file.
- **Assets**: search every file in the game by name or episode (e.g.
  `WalkingDead203 .d3dtx`), preview textures/models, play sound files (voice lines,
  music, FMOD banks), export files (textures as PNG or DDS, sounds as WAV), or send
  one to your mod as a replacement. **Export results…** saves every match at once,
  converted to everyday formats (PNG, MP3/Ogg, decompiled Lua, subtitle transcripts)
  unless *Convert* is off.
- **Mod**: the files you replace and with what, saved as a `.ttmod` project.
  **Build patch** stages *only* your swapped files into a tiny `0.ttarch` /
  `0.ttarch2`; drop it in the game's archive folder and the game reads it as a
  patch that overrides the originals.
- **Settings** (the gear, top right): default look and camera speeds, background,
  playback, dialogue and subtitle language, script view, export folder and
  conversion, scene texture detail. Changes apply at once and are saved with the
  library. It also clears the saved game indexes and opens the crash log
  (`%LOCALAPPDATA%\TelltaleModdingStudio\crash.log`).

### 3D viewports

The Characters, Scenes and Dialogue viewports share their controls: drag to orbit,
middle-drag (or Shift+drag) to pan, scroll to dolly, double-click or **F** to
frame. Click the view, then **W/A/S/D** fly and **Q/E** lower and raise the camera;
hold the right button to look around while flying (the wheel then sets the
speed). The switch at the top picks how models are shaded, for every viewport at
once:

- **Studio**: even, neutral lighting for inspecting textures.
- **Game**: the open game's own look: its outlines, light model, ink lines and,
  where the game uses it (the Definitive Series), graphic black. **Engine default
  intensity** sets how far that game's black shadows reach.
- **Graphic black**: the game's look with solid black shadows, for any game.
  **Graphic black intensity** sets how far they reach (0% turns them off).

Voice and sound files are FMOD sound banks holding MP3 or Vorbis audio. Vorbis
banks omit their codec setup, which the app reads from the FMOD library the game
installs next to its executable, so those games' sounds only play from an install
folder.

Playback poses the skeleton from the clip alone: the engine's runtime IK and
layering are not reproduced, so foot-planted or layered clips can differ from
the game.

## Repository layout

| Path | What it is |
|---|---|
| `src/TelltaleModding.Core` | Cross-platform .NET 8 library: native `.ttarch2` reader (`Archives`), game inventory: install detection, archive priorities, characters, outfits, animations (`Catalog`), mesh/skeleton/animation/texture decoders (`Meshes`, `Textures`), model assembly, animation playback, face rig and lip sync (`Scene`), glTF export (`Export`), FMOD sound decoding (`Audio`), subtitle databases and their voice lines (`Dialogue`), Lua bytecode decompiler (`Scripts`), ttarchext wrapper, mod-project model, minimal-patch builder |
| `src/TelltaleModding.App` | Avalonia 11 desktop UI (MVVM) |
| `tests/TelltaleModding.Core.Tests` | xUnit suite, including tests against real sample files and (when present) installed games |
| `tests/TelltaleModding.App.Tests` | Headless UI tests: render every workspace to PNG, and drive the app end-to-end against installed games |
| `engine/ttarchext` | The ttarchext CLI (source + Windows exe), the extraction/build engine |
| `samples/` | Game files the tests read (archives, meshes, textures, skeletons, animations, props, resource descriptions). Only a few are in the repository; tests whose sample is missing are skipped |
| `legacy/winforms` | The original WinForms GUI, kept for reference |

## Building

```bash
dotnet build TelltaleModding.sln
dotnet test                      # runs the Core test suite (works on any OS)
dotnet run --project src/TelltaleModding.App
```

Everything builds and runs cross-platform. The engine comes in two flavours:

- **`ttarchext.exe`** (shipped): the Windows binary. Required for
  Oodle-compressed archives (lowercase `zCTT`/`eCTT` tags), because Oodle
  decompression works by loading the game's own `oo2core` DLL.
- **Native build**: `make -C engine/ttarchext` (needs `zlib1g-dev`) produces a
  Linux/macOS `ttarchext` that handles encrypted, zlib-compressed, and plain
  archives. This repo carries a small portability fix for it (the original
  Blowfish code assumed 32-bit `unsigned long`).

The app picks whichever flavour runs on your OS automatically, or you can point
the `TTARCHEXT_PATH` environment variable at a specific binary. When only the
`.exe` is present on a non-Windows host, engine actions are disabled with an
explanatory status message.

With a native engine present, `dotnet test` also runs end-to-end integration
tests: listing and extracting from the real sample archives and round-tripping a
built patch. CI does this on every push.

## Notes

- Don't enable the "-m" meta decode option for files you plan to rebuild into a
  patch; decoded DDS/OGG files are not valid archive members.
- If the game crashes loading a rebuilt v7+ archive, rebuild with ttarchext's
  compatibility mode (`-x`).
- ttarchext is © Luigi Auriemma, GPL-2.0. Game archives and their contents belong
  to their respective owners; mod responsibly.
