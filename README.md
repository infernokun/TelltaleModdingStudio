# Telltale Modding Studio

A modern desktop app for modding Telltale games: browse the contents of `.ttarch` /
`.ttarch2` archives, extract just the assets you want, swap in replacements
(character models, textures, animations…), and build a minimal patch archive the
game loads over its originals — no full rebuild needed.

Built on [ttarchext](https://aluigi.altervista.org/papers.htm#ttarchext) by Luigi
Auriemma, which does the actual byte-level extraction/rebuilding (for learning
purposes only). This project wraps it in a workflow that makes asset swapping
practical.

## How the swap workflow works

1. **Pick your game** — this selects the per-game encryption key ttarchext uses.
2. **Open an archive** — the app sniffs the header (`ECTT`/`ZCTT`/`NCTT`/`3ATT` vs
   classic version numbers) and auto-detects the right format version, then lists
   the contents *without* extracting anything.
3. **Find your asset** — filter by name or category (Model, Skeleton, Animation,
   Texture, …) and extract just that file if you want to inspect or edit it.
4. **Add swaps** — point each target asset at your replacement file. Save the whole
   set as a `.ttmod` project you can reopen later.
5. **Build patch** — the app stages *only* your swapped files and builds a tiny
   `0.ttarch` / `0.ttarch2`. Drop it in the game's archive folder; the game reads
   it as a patch that overrides the originals.

Plus: in-app DDS texture preview (DXT1/3/5 and 32-bit RGBA) so you can eyeball a
texture before you ship it.

## Repository layout

| Path | What it is |
|---|---|
| `src/TelltaleModding.Core` | Cross-platform .NET 8 library: game database, archive header detection, async ttarchext wrapper, listing parser, mod-project model, minimal-patch builder, DDS decoder |
| `src/TelltaleModding.App` | Avalonia 11 desktop UI (MVVM) |
| `tests/TelltaleModding.Core.Tests` | xUnit suite, including detection against real sample archives |
| `engine/ttarchext` | The ttarchext CLI (source + Windows exe) — the extraction/build engine |
| `samples/` | Real `.ttarch2` archives used by the tests |
| `legacy/winforms` | The original WinForms GUI, kept for reference |

## Building

```bash
dotnet build TelltaleModding.sln
dotnet test                      # runs the Core test suite (works on any OS)
dotnet run --project src/TelltaleModding.App
```

Everything builds and runs cross-platform. The engine comes in two flavours:

- **`ttarchext.exe`** (shipped) — the Windows binary. Required for
  Oodle-compressed archives (lowercase `zCTT`/`eCTT` tags), because Oodle
  decompression works by loading the game's own `oo2core` DLL.
- **Native build** — `make -C engine/ttarchext` (needs `zlib1g-dev`) produces a
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
  patch — decoded DDS/OGG files are not valid archive members.
- If the game crashes loading a rebuilt v7+ archive, rebuild with ttarchext's
  compatibility mode (`-x`).
- ttarchext is © Luigi Auriemma, GPL-2.0. Game archives and their contents belong
  to their respective owners; mod responsibly.
