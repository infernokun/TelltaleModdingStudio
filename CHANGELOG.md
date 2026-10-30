# Changelog

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
