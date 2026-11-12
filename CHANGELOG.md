# Changelog

## 1.5.0 (2026-11-12)

### Home

- Faster start: added **Look for new games on start** option and optimize detection. **Scan this PC** still checks every game folder.
- Steam installed outside Program Files is found through the Windows registry.

### Fixes

- Exported bones have their real names (about 19,500 known, from RandomTBush's list). Set in `%LocalAppData%\TelltaleModdingStudio\BoneNames.txt`.
- Minor fix for cases where some feet and shoes are mis-shaped in animations.

## 1.4.0 (2026-10-31)

### Dialogue

- **Follow camera:** follows the speaker through each line, widening for gestures and moving with crouches and turns.
- **Game camera:** shows a line from the game's own camera when it frames the speaker's face.

### Fixes

- About a third of Definitive Series mouths that stayed still now lip-sync (new phonemes, mood-only mouth shapes, shapes from the wrong season, shared shapes).
- Bonnie no longer talks with a fearful open mouth.
- Eyes and mouths no longer drawn black on characters with shared eye and mouth parts.
- Michonne's animations play.
- A line voiced over another character shows the speaker.
- Performances follow the chore's strength and timing for each clip.
- Performances mix like the game's animation mixer: clip priority, body part masks and correctly turned gesture offsets.
- The Dialogue speaker moves at the display's frame rate and stays in time with the sound.

## 1.3.1 (2026-10-30)

### Fixes

- The Dialogue speaker uses the model the scene stages for the line.
- The speaker's idle, resting face and mouth shapes come from the character's `.prop`.
- Definitive Series characters no longer look shiny or plastic.
- Normal maps in newer games are read with the right channel layout.
- Studio rim lights only light the silhouette.

## 1.3.0 (2026-10-30)

### Dialogue

- **Performance:** the speaker plays the game's body language for each line.
- Lines are matched to their chores through dialog files, scene chores and shared recordings.
- Short subtitle names ("JACK") are matched to full character names.

### Characters

- Hologram Handsome Jack is drawn blue, self-lit and see-through, as in the game.
- Scene-specific character tints are left out.
- Materials can set their own outline color.

### Scenes

- Characters and objects in scenes use their own settings (glow, see-through glass, decals). Invisible helpers are hidden.
- A scene with nothing visible at the start shows the characters you switch on.

### Assets

- **Chores** are shown as a timeline per character.
- Chore data is named for what it does ("look at Clementine (eyes)").
- **Export results** writes chores as text timelines.

### Window and layout

- **Updates:** checks GitHub for a newer release on start and from the status bar.
- Window controls moved into the app header.
- New status bar: version, open game and Check for updates.
- Side panels can be resized on every page.
- Layout fixes at small window sizes and for text-only tabs.

### Performance

- Model, texture and scene caches are capped (about 24 GB down to 1.3 GB browsing Definitive Series characters).
- Idle CPU use down from about 15% to 2-4%.

### Fixes

- No more green and blue haze on some Season 4 characters.

## 1.2.0 (2026-10-30)

- New logo and app icon.
- The README shows the logo in light and dark versions.

## 1.1.0 (2026-10-30)

### Linux support

- Linux (x86-64) builds as a `.tar.gz`.
- Sound through PulseAudio or PipeWire.
- Steam libraries found on Linux, including Flatpak and Snap Steam.
- Oodle-compressed archives open on Linux, using the game's own Oodle library.
- Native `ttarchext` for Linux.

### Fixes

- The 3D view no longer stays blank on Linux Mesa drivers.
- 3D view setup failures go to the crash log.

### Releases

- One `SHA256SUMS.txt` for both builds, with build provenance for each.
- The Windows zip ships only `engine/ttarchext`.

## 1.0.0 (2026-10-29)

First release, for Windows.

- **Home**: add your installed Telltale games, or let it find them in Steam.
- **Characters**: every character with their outfits, animations and facial expressions. Export to `.glb` for Blender.
- **Scenes**: walk around any location in the game, lit like the game.
- **Dialogue**: read and hear every line, watch it lip-synced, and follow dialogue trees. Includes the soundtrack.
- **Scripts**: the game's Lua scripts, decompiled.
- **Assets**: browse, preview and export any file.
- **Mod**: swap in your own files and build a patch the game loads.
- SHA-256 checksums and build provenance for each release.
