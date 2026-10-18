# CombatQOL

Combat quality-of-life mod for **Tainted Grail: The Fall of Avalon**. Works on both game builds: the default one (IL2CPP, BepInEx 6) and the `mono` beta branch (BepInEx 5).

- **Melee:** longer reach (length/width multipliers), swings pass through walls, max targets per swing, no hit-stop freeze on enemies and no bouncing off walls, soft aim assist and a light magnet toward the target (melee weapons only).
- **Bows & thrown weapons:** wider hit zone that only helps near misses; a narrow "crit arrow" check keeps weak spots honest — a near miss never becomes a headshot and a real headshot is never stolen.
- **Magic:** wider hit zone for spell projectiles.
- **Feedback:** hitmarker at the crosshair (hit / weak spot / crit / crit on weak spot) and floating "Crit!" / "Weak spot!" labels.
- **In-game panel (F10):** tabs Melee / Bows / Magic / General, everything live and saved to the config; optional hit-zone drawing.
- UI follows the game language (all 12 game languages + Korean, Ukrainian). Nothing is written to saves.

## Install
Pick the file for your game build. If the game folder has `GameAssembly.dll`, it is **IL2CPP** (the default Steam/GOG build); otherwise it is **Mono** (Steam → Properties → Betas → `mono`).

| Game build | Loader | File |
|---|---|---|
| IL2CPP (default) | [BepInEx 6 IL2CPP](https://builds.bepinex.dev/projects/bepinex_be) x64, tested with 6.0.0-be.789 | `CombatQOL-x.y.z-IL2CPP.zip` |
| Mono (`mono` beta) | [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) x64, tested with 5.4.23.5 | `CombatQOL-x.y.z-Mono.zip` |

1. Install the loader into the game folder and start the game once.
2. Download the matching zip from [Releases](../../releases) and extract it into the game folder — the dll goes to `BepInEx\plugins\CombatQOL\`.
3. In game press **F10**. Config: `BepInEx\config\leon.tg.combatqol.cfg`.

To uninstall, delete `BepInEx\plugins\CombatQOL`.

## Build
.NET SDK 6+. One source builds both runtimes (`net6.0` → IL2CPP, `netstandard2.1` → Mono); each one is built when its references are found:
IL2CPP needs a game folder with BepInEx 6 started once (`BepInEx\interop`), Mono needs a Mono-branch game folder with BepInEx 5.
Copy `local.props.example` to `local.props` and set the paths, then run `./build.ps1` (zips go to `dist\`; `-Deploy` also copies the matching dll into the game).

License: MIT.
