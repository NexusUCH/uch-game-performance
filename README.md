# UCH Game Performance 2.2.0

One performance mod for vanilla Ultimate Chicken Horse. It replaces UCH Game Performance 1.x and includes the ideas of BetterPerformance and LevelLoadingOptimizer, so you don't need those anymore (keeping them installed is fine too). Requires **BepInEx 5**; Harmony is already included with BepInEx. No other plugin or extra DLL is required.

Gains show mostly on busy maps (many moving blocks/projectiles). No specific FPS increase is promised; FPS caps can hide gains.

## Install

1. Close UCH.
2. Remove any older UCH Game Performance DLL, including copies in differently named folders.
3. Download `UCHGamePerformance.dll` from [Releases](../../releases) and put it in `BepInEx/plugins/UCH Game Performance/`.
4. Start UCH. Read the startup lines in `BepInEx/LogOutput.log`: each feature reports `requested / applied / bypassed(reason)`.

This version targets vanilla UCH. It warns if other gameplay plugins are loaded and does not negotiate ownership with them. Use it on its own for reliable results. The separate optional benchmark tool can also be used; it is not required or bundled.

Only the explicitly supported game build applies optimizations. Other game builds keep all features bypassed. The mod writes only its own BepInEx config and log messages. It does not change game files, the registry, physics timing, hitboxes, projectile rotation, or gameplay layers.

## What it does and config options

Config: `BepInEx/config/com.nexus.uch.gameperformance.cfg`. **Restart UCH after every change.** Defaults below apply to a fresh config; existing 1.x CPU settings are preserved. Old F9/recorder and old graphics entries are unused. There is no menu or recording code in this mod.

Every row is a separate switch unless a value is shown. Section/key spelling matches the config.

| Section / key | Default | What changes |
|---|---|---|
| General / Enabled | On | Master switch. Off = the mod applies nothing and the game runs vanilla (restart). Handy for before/after comparisons. |
| Engine / ReuseCollisionCallbacks | On | Reuses short-lived collision callback objects. |
| Engine / IncrementalGC | On | Sets the budget of incremental garbage collection, only if the game already uses it. |
| Engine / IncrementalGCSliceMilliseconds | 1 ms | Budget, from 0.1 to 10 ms. Appears when incremental-GC tuning is requested on a supported build. |
| Engine / TargetFrameRate | 0 | 0 leaves FPS and VSync alone. Positive values set a cap and disable VSync at startup. |
| Engine / HighProcessPriority | Off | Optional Windows High process priority. |
| CPU / ReuseFixedUpdateWait | On | Reuses a fixed-step coroutine wait object. |
| CPU / SkipEmptyBlockLoops | On | Avoids starting block loops whose action does nothing. |
| CPU / PhysModEmptyCache | On | Reuses empty modifier arrays in base methods; block-specific logic stays intact. |
| CPU / ColliderArrayCache | On | Reuses collider arrays while checking live membership and order on every use. |
| CPU / IdleColliderDisable | On | Turns off idle collision-check behaviours and re-enables them when needed. Unity can still deliver trigger callbacks to disabled behaviours. |
| Projectiles / ComponentCache | On | Caches rigidbodies and the settings object; current settings values remain live. |
| Projectiles / ColliderArrayCache | On | Reuses live-validated collider arrays when firing. |
| Projectiles / SkipIgnoredPairs | On | Avoids repeating an already active collision-ignore setting. |
| Projectiles / DebrisPooling | On, partial | Reuses arrow/ball miss debris instead of creating and destroying it per hit; each reuse resets position, rotation, scale, sprites, animation and velocity. Player-hit debris, debris with sounds (airplane) and the projectile-explosion modifier stay vanilla. |
| CPU / AttractorRadiusOnChange | On | Writes the magnet collider radius only when it differs. |
| CPU / MultipieceUpdateGate | On | Runs hover/destruction work when relevant inputs or part lifecycle change. |
| CPU / MoverMassCache | On, partial | Caches attachment-based mass ratios. Distance/radius-based ratios remain vanilla. |
| CPU / WaterScratchArrays | On, partial | Reuses splash snapshots. Water physics queries remain vanilla, with the same complete hit set and order. |
| Projectiles / SkipInactiveNetworkEvents | On, partial | Skips network handling for reset, inactive pooled projectiles. Keeps pause/scoreboard updates and active projectile destruction. |
| CPU / LevelFullnessCount | On | Counts the creative/free-play level fullness from registered blocks instead of searching the whole scene (~7 ms on a full level, repeated every 21 network messages). Checked against the vanilla count on every scene load and every 32nd count; any difference turns it off. |
| CPU / EventScratchLists | On | Reuses cleanup lists, including separate storage for nested events. |
| CPU / QuietHotPathLogs | On | Removes only three known placement/pool messages, including their string construction. Other logging stays enabled. |
| Loading / HierarchyPathCache | On, partial | Reuses parsed paths within a load pass. Root lookups stay live so hierarchy changes are respected. |
| Loading / PlacedBlocksHashSet | On | Speeds up placed-block membership tests; preserves the original ordered list. |
| Experimental / PhysicsMultithreading | On | Enables Unity 2D physics jobs with consistency sorting, spreading the physics step over CPU cores; does not change simulation mode or worker counts. Biggest measured gain on busy maps; turn off if anything looks different. |
| Experimental / SmoothSyncAllocations | Off, partial | Removes one overwritten temporary allocation per send. Other sync allocations remain vanilla. |
| Graphics / SlowMotionScreenEffect | Vanilla | `Vanilla`, `SkipWhenIdle` (plain blit with neutral shader inputs), or `Remove` (always plain blit). Active slowdown timing stays vanilla. Cosmetic opt-in. |
| Experimental / StaticBlocks | Off; unavailable | Remains vanilla; complete safe motion/contact invalidation is unproven. |
| Experimental / QuickSaverRepair | Off; unavailable | Remains vanilla; blanket layer/body changes would change gameplay state. |

No global logger suppression, event pruning, built-in profiler, hardware-specific worker tuning, or blanket debris lifecycle replacement is included. Experimental graphics/physics options require a visual and gameplay check on your maps. The screen-effect options retain the camera render-texture path; they replace the material blit rather than disabling the effect component.

## Comparing results

Use the same map, players, graphics settings and actions with and without the mod. Keep menus closed, allow warm-up, and repeat runs. FPS caps can hide throughput gains.

## Building from source

See [CONTRIBUTING.md](CONTRIBUTING.md). Short version: install UCH + BepInEx 5, then run `.\build.ps1` in PowerShell.

## Uninstall

Close UCH and remove `BepInEx/plugins/UCH Game Performance`. You can optionally delete its config file. No game files need restoring. Restart UCH to return to vanilla behavior.

## Credits

Nexus: combined implementation and original CPU optimizations. **Bojack**: BetterPerformance and LevelLoadingOptimizer code/ideas, reused with permission. The revised implementation preserves vanilla logging outside the three specific hot paths and fixes cache invalidation rather than inheriting permanent collider/root caches.

2.0.1 fixes the pause-menu and mode-change softlock caused by event listeners
being removed after dispatch. CPU/EventScratchLists remains On by default.
The separate UCH Perf Test 1.0.1 package reduces census searches during capture.

2.1.0: Experimental/PhysicsMultithreading is now On by default (A/B on a dense stress map: +17-22% average FPS, 75-87% fewer frames below 120 FPS, no visible gameplay difference). New General/Enabled master switch for before/after testing.

2.2.0: Projectiles/DebrisPooling is now active for miss debris with a known lifecycle (arrow and tennis-ball debris). New CPU/LevelFullnessCount removes a ~7 ms hitch that creative/free play hit every 21 network messages.
