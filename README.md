# Beutetresor (loot safe)

During the raid, automatically moves **dog tags** and **keys** into the
secure container as soon as they show up in the backpack, vest or pockets.

## Requirements

- SPT 4.1.6 (EFT 0.16.9). BepInEx ships with SPT.

## Installation

1. Download `Beutetresor-1.3.0.zip` from the [Releases](https://github.com/Kaanori/spt-beutetresor/releases) page.
2. Extract it **into your SPT game folder** (the folder that contains `EscapeFromTarkov.exe`).
   The ZIP already has the right layout; the mod ends up in `BepInEx/plugins/`.
   On Windows, make sure "Extract All" does not add an extra subfolder.
3. Restart the game.

## Uninstall

Delete the mod's files from `BepInEx/plugins/` (settings: `BepInEx/config/` for client mods) and restart the game.

## How it works

Every `Pruefabstand` seconds (default 1 s) the player's inventory is
scanned. Detection works via the item components `DogtagComponent` and
`KeyComponent`, not via template lists. That way keys from mods and future
additions are covered without maintenance. Keycards carry the same
`KeyComponent` and are covered too.

If there is no room in the secure container, the item stays where it is
and is retried on the next pass.

## Settings (F12)

| Section | Switches |
|---|---|
| 1. Allgemein (general) | **Eingeschaltet** (enabled) |
| 2. Was (what) | **Erkennungsmarken** (dog tags), **Schluessel** (keys) |
| 3. Woher (from where) | **Rucksack** (backpack), **Weste** (vest), **Taschen** (pockets) |
| 4. Takt (interval) | **Pruefabstand** (check interval) (0.25-10 s) |
| 5. Anzeige (display) | **Meldung im Spiel** (in-game message), **Ins Protokoll schreiben** (write to log) |

## Why not an existing mod

"Transfer loot into a container automatically" (CactusPie) does it with
Ctrl+click and needs a container marked with `@loot` in the safe — so
semi-automatic and with setup effort. Here it happens by itself.

## Building from source

Needs the .NET 10 SDK and an SPT 4.1.6 installation to build against.

```bash
./build.sh             # Linux: build only
./build.sh --install   # build and copy into the game folder
```
```powershell
.\build.ps1            # Windows
.\build.ps1 -Install
```

The game folder is set in `Directory.Build.props` (Windows default `C:/SPT`)
or via the environment variable `SPT_DIR`.

## Support

Bugs and questions: [GitHub issues](https://github.com/Kaanori/spt-beutetresor/issues).

## License

MIT — see [LICENSE](LICENSE).
