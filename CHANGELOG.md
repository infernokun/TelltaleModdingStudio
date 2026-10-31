# Changelog

## 1.3.1 (2026-10-30)

### Fixes

- The Dialogue speaker uses the model the scene stages for the line, read from the scene's agent and its character `.prop`. Before, any character with the speaker's name could be picked, such as Clementine's Season 2 model in a Season 4 scene. Lines with no scene to go on prefer a model from the line's season.
- The speaker's idle, resting face and mouth shapes come from the character's `.prop` ("Walk Animation - Idle", "Walk Animation - Idle Face" and its phoneme table) instead of being picked by name. Characters whose `.prop` leaves one out keep the old choice.
- Characters in the Definitive Series no longer look shiny or plastic. Their specular maps keep gloss in the alpha channel, which the app ignored, so dull cloth and skin (gloss around 0.1 to 0.3) got a sharp highlight. Gloss now sets how sharp and how strong the highlight is, as in the game: matte clothes stay matte, and glass, metal, eyes and wet surfaces still shine.
- Normal maps in the Definitive Series, Tales from the Borderlands and Michonne are read with the channel layout each texture declares (sideways in alpha, up-down in blue). They were read the older games' way, so bumps caught the light in the wrong places.
- The studio light rig's rim lights only light the silhouette. In a fully wrapped look such as the Definitive Series' they spread a pale wash over the side of a character.

## 1.3.0 (2026-10-30)

### Dialogue

- **In-game performances:** with **Performance** on (next to **Speaker** in the player bar), the speaker plays the body language the game's chore plays with each line: turns, poses, gestures, glances and blinks, in time with the recording. Lines without one keep the idle, and the caption under the speaker says which you are seeing. Root motion is left out so the speaker stays in frame.
- Each line's chore is found through the scene's dialog file (the chore each exchange plays, whatever it is named), chores named after the scene, and chores embedded in other chores. Lines that reuse another line's recording are found by that recording. Between 63% (Tales from the Borderlands) and 90% (The Walking Dead Definitive Series) of voiced lines are placed in a chore; most of the rest were cut from the game or play without one.
- Speakers named by a short name in the subtitles ("JACK") are matched to their full name (Handsome Jack), and the version of a character made for the line's episode is preferred.

### Characters

- **Hologram Handsome Jack** (Tales from the Borderlands) is drawn as the game does: a character's `.prop` can tint, light and fade all of its meshes, so his grey textures become a blue, self-lit, see-through hologram. The plain Jack, whose textures the game never shipped, borrows them from a version of him with the same UV layout.
- A color set on an ordinary character only grades it for one scene's lighting, so it is left out (Carver and Clementine in Season Two episodes 3 and 4 look as before).
- Outlines take a material's own outline color where it sets one (hologram Jack's cyan), on characters and in scenes. Every other material keeps the look's outline color.

### Scenes

- Characters and objects placed in a scene follow the same settings as on their own: hologram Jack is blue, self-lit and see-through in Gortys Core Silo. Glowing signs and screens, see-through glass and decals in scenes follow their settings too, and invisible helper objects are left out.
- A scene where nothing shows at the start now displays the characters you switch on, instead of staying on "This scene places no visible meshes".

### Assets

- **Chores** (`.chore`) are shown as a timeline per character: when each animation, gesture, chore and voice line plays, with the line's text, and a bar showing where it falls in the chore. Chores from all the supported games are read.
- Data a chore carries itself is named for what it is: "look at Clementine (eyes)", "style idle", "eye movement", "camera blend" and so on.
- **Export results** with Convert on writes each chore as a readable text timeline (`.txt`), per character, with voice lines' text.

### Window and layout

- **Updates:** the app checks GitHub for a newer release when it starts (can be turned off in Settings) and from **Check for updates** in the status bar, and offers it in a popup.
- **Title bar:** minimize, maximize and close sit in the app's header, which is also the title bar (drag to move, double-click to maximize).
- **Status bar:** the version and update status on the left, the open game in the middle (click it to switch games), Check for updates on the right.
- Panels can be resized by dragging the gap between them on every page with side panels: Characters, Scenes, Dialogue (speaker, conversation list), Scripts and Assets (preview). Side panels share the width with the window, so nothing is cut off at the smallest window size.
- At small window sizes the viewport title shortens instead of sliding under the view tools, the app name no longer runs under the Home tab, and Dialogue's player sits on its own row so the seek bar keeps its width.
- Text-only tabs (Settings' look buttons, Dialogue's Lines/Talks/Music, Scripts) keep their labels centered in the highlight. The conversation tree's toolbar wraps instead of cutting off.
- The header's left, centre and right sections are aligned, and the open game is no longer repeated in the header.

### Performance

- Model, texture and scene caches are capped by size, so browsing many characters no longer keeps growing memory (Definitive Series characters: about 24 GB before, about 1.3 GB now).
- Hidden progress bars no longer redraw the window, which brings idle CPU use from about 15% to 2-4%.

### Fixes

- A green and blue haze on several Season 4 characters (packed specular maps read the wrong way) is gone.

## 1.2.0 (2026-10-30)

- New logo and app icon: round badges with lowercase letters, and a maroon badge with a wrench breaking out of its ring. The window, taskbar and header use the new badge.
- The README opens with the logo, in a light and a dark version that follow GitHub's theme.

## 1.1.0 (2026-10-30)

### Linux support

- Linux (x86-64) builds: each release now has a `.tar.gz` next to the Windows zip. Extract it and run `TelltaleModding.App`, with the `engine` folder and `libtelltale_oodle.so` kept next to it.
- Sound plays on Linux through PulseAudio or PipeWire. Without a sound server the app stays silent instead of failing.
- Steam libraries are found on Linux: the usual install, Flatpak and Snap Steam, and every library they list. Archive names match regardless of case.
- Oodle-compressed archives open on Linux. A small loader (`engine/oodle`, built as `libtelltale_oodle.so`) runs the game's own `oo2core_*_win64.dll`; nothing of Oodle is bundled.
- The Linux build ships a native `ttarchext` for classic `.ttarch` archives, resource descriptions and patch building.

### Fixes

- The 3D view stayed blank on Linux (Mesa drivers): a shader used `packed`, a reserved GLSL word that the Windows drivers allowed. Renamed, and a test now rejects reserved words in the shaders.
- A failure setting up the 3D view is now written to the crash log. Before, it left the view blank with no trace.

### Releases

- One `SHA256SUMS.txt` covers both builds, and each build has its own build provenance (`gh attestation verify <file> --repo infernokun/TelltaleModdingStudio`).
- The Windows zip ships only `engine/ttarchext`.

## 1.0.0 (2026-10-29)

First release, for Windows.

- **Home**: add your installed Telltale games, or let it find them in Steam.
- **Characters**: every character with their outfits, animations and facial expressions, in the Studio, Game and Graphic black looks. Export to `.glb` for Blender.
- **Scenes**: walk around any location in the game, lit like the game.
- **Dialogue**: read and hear every line, watch the character lip-sync it, and follow conversations as dialogue trees. Includes the soundtrack.
- **Scripts**: the game's Lua scripts, decompiled and readable.
- **Assets**: browse, preview and export any file (PNG, WAV, MP3/Ogg, Lua).
- **Mod**: swap in your own files and build a small patch the game loads.
- Each release has SHA-256 checksums and build provenance.
