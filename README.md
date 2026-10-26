# UCH Game Performance 2.7.4

A performance mod for **Ultimate Chicken Horse**. It makes full, busy levels run and load much faster without changing how the game plays: same physics, same hitboxes, same timing, same rules. Every optimization is a separate switch in the config, and each one reports in the log whether it is active.

It replaces the older UCH Game Performance 1.x, BetterPerformance and LevelLoadingOptimizer; you don't need those alongside it (keeping them installed is fine).

## What to expect

Measured on "Eternity" (level code **8VKK-7G4K**: about 4,000 blocks, 2,400 of them active, 1,700 barbed-wire pieces, 20-40 arrows in the air at any time), one of the heaviest community levels. Local free play, FPS cap lifted, fresh game start per run, 20 s recording each. Full details and the exact setup are in the [Eternity test report](docs/eternity-test-report-2.7.2.md) (measured on 2.7.2; later versions only fix bugs).

| | Average FPS | Typical frame | Worst 1 % of frames | Frames slower than 8.33 ms (per 20 s) |
|---|---|---|---|---|
| Vanilla | 108 | 12.1 ms | 15.7 ms | 1,201 |
| **With the mod** | **214 - 224** | **3.3 - 3.4 ms** | **9.7 - 10.2 ms** | **315 - 446** |

| | Portal entry to level ready | Longest freeze while loading |
|---|---|---|
| Vanilla | 29.8 s | 26.0 s |
| **With the mod** | **5.7 - 5.9 s** | **2.0 - 2.1 s** |

Roughly twice the frame rate, a frame 3.6x shorter, and a level that loads in 6 s instead of 30. The gain grows with how full a level is: a small level that already runs at several hundred FPS will not change much, a level that stutters will. Numbers are from one PC (Ryzen 5 5600X, RTX 4060); the ratio should carry over, the absolute FPS will not. Real rounds with players and online games were not benchmarked.

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) for UCH if you don't have it yet (Harmony comes with it; nothing else is required).
2. Close UCH.
3. Remove any older UCH Game Performance DLL, including copies in differently named folders.
4. Download `UCHGamePerformance.dll` from [Releases](../../releases) and put it in `BepInEx/plugins/UCH Game Performance/` (or drop the whole folder from the release zip into `BepInEx/plugins`).
5. Start UCH. Open `BepInEx/LogOutput.log`: every option prints one line like `requested=True / applied=True / bypassed(none)`.

Other mods can stay installed; nothing has to be removed or disabled. LevelLoadingOptimizer works fine alongside this mod (also if your mod manager cannot turn it off). BetterPerformance does some of the same things, so turn it off if Barnyard allows it; you do not need it with this mod. The mod logs which other plugins are loaded but never changes them.

Only the supported game build gets optimizations. On any other game build every option stays bypassed and the game runs vanilla. The mod writes only its own config file and its log lines; it does not change game files or the registry.

## Does it change gameplay?

No. The mod never touches the physics step, the maximum time step, solver settings, gravity, collision layers, hitboxes or projectile rotation. Where it takes a shortcut, it takes it only when the result is provably the same, and several options check themselves while the game runs and switch themselves off (with a log line) if anything looks different from what they expect.

Checks done before each release, on the level above: hazards (spikes, saws, gloves, axes, flipping blocks, doors, shooters) end exactly the same way for a test character with and without the mod; arrows fire on the same physics step; nothing that is out of view can affect a player or an arrow differently than in vanilla.

If you ever want to compare, set `General / Enabled` to `false` in the config: the mod stays installed but applies nothing.

## What it optimizes

In plain words; the exact list of switches follows below.

- **Loading.** Full levels used to freeze for many seconds when a level starts, because each block did some work that scanned the whole level again. Those scans are answered from a cache or done once: screen-wrap lookups, event sign-ups, the fullness meter, the beehive bee objects, the log file writes and a compression step that only matters online.
- **Physics.** During the play phase, blocks that cannot move (plain blocks, wire, ice, glue, and the fixed base of shooters, gloves, spikes, cannons, doors and similar) get a static physics body, which the physics step does not simulate. Players, arrows and moving blocks collide with them exactly as before. Everything is switched back before anything can pick a block up, and a watchdog reverts any block that moves anyway.
- **Block updates.** Unity called every block's per-step routine one by one, thousands of calls per physics step. The mod runs the same routine for all of them from one loop, in the same order and at the same moment of the frame.
- **Animations out of view.** On a full level most block animations are off screen, yet Unity evaluated all of them every frame. Purely visual animations are not evaluated while nothing of them is visible. Gameplay animations keep their state machines and events running (shots fire at the same moment from the same spot) and only skip drawing while off screen; a block whose animation moves a collider stays fully live whenever any player or projectile could reach it.
- **Projectiles and small things.** Reused arrays and objects instead of allocating them per shot or per hit, idle scripts switched off, a few per-frame scene searches replaced by the lists the game already keeps, and Unity's multithreaded 2D physics jobs turned on.

## Options

Config file: `BepInEx/config/com.nexus.uch.gameperformance.cfg`. **Restart UCH after every change.** All optimizations are on by default; the two frame-rate options and the process priority are off. Section / key spelling matches the config.

<details>
<summary>Full list of switches (click to expand)</summary>

| Section / key | Default | What changes |
|---|---|---|
| General / Enabled | On | Master switch. Off = the mod applies nothing and the game runs vanilla (restart). Handy for before/after comparisons. |
| Engine / ReuseCollisionCallbacks | On | Reuses short-lived collision callback objects. |
| Engine / IncrementalGC | On | Sets the budget of incremental garbage collection, only if the game already uses it. |
| Engine / IncrementalGCSliceMilliseconds | 1 ms | Budget, from 0.1 to 10 ms. Appears when incremental-GC tuning is requested on a supported build. |
| Engine / TargetFrameRate | 0 | 0 leaves FPS and VSync alone. Positive values set a cap and disable VSync at startup. |
| Engine / UncapFrameRate | Off | On lifts the FPS cap to 1000 and turns VSync off (the setting the benchmarks above use). Off keeps the game's own cap. TargetFrameRate wins when set. |
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
| CPU / CentralBlockLoops | On | Runs every moving/active block's per-step action from one central loop instead of one coroutine per block, in the same order and at the same moment of the frame. Each block's first step after it appears still runs natively. Shooter fire timing does not use this loop. |
| CPU / CentralPlaceableTick | On | Plain blocks only need a tiny bit of bookkeeping each physics step, but Unity still makes one call per block. One early loop now does that bookkeeping for all of them; attached blocks still run the original method. |
| CPU / CentralPartTick | On | The same for barbed-wire, ice and glue pieces and their invisible parent blocks (about 1,900 native per-step calls on a full wire level). These types inherit the empty block action and declare no per-frame logic of their own except the parent's per-frame part check, which the mod's loop now runs once per frame for every parent block. Checked by reflection when the game starts; any surprise turns this option off. Needs CPU / CentralPlaceableTick. |
| CPU / IdleAttachmentIndicators | On | Every barbed-wire, ice and glue block carries a small attachment-hint script that keeps being called every frame after the block is placed, although from then on it returns immediately forever (about 900 calls per frame on a full barbed-wire level). It is switched off at exactly that point; nothing in the game reads or re-arms it. |
| CPU / AudioEmitterIdleSkip | On | Sound emitters re-check their position every frame. While an emitter has not moved and its sound environment is unchanged, that check is skipped; it would not have sent anything to the sound engine. |
| Loading / ScreenWrapLookupCache | On | When a level starts, every arrow/puck in the shooters' pools searched the whole scene for the screen-wrap area (about 13 ms each on a full level, 480+ times): a multi-second freeze right after loading. Repeated searches in the same frame now reuse one result; the result is identical because that object never changes while a level loads. |
| Loading / SkipLocalSnapshotCompress | On | When a level starts, the host compresses the whole level (about 1 s on a full level) just to send it to other players. In a local game there is nobody to send it to, so that step is skipped. Online lobbies, remote players and remote connections always get the normal compressed level. |
| Loading / ListenerMembershipSet | On | Every block and every pooled arrow signs up for a few game events when it is created, and each sign-up first scanned the whole sign-up list (one entry per block) for duplicates. On big levels that adds up quadratically: about 6 s on a 10,000-block level. Blocks and projectiles now check with a fast lookup that mirrors the list; the list itself, its order and who receives events are unchanged. |
| CPU / BufferedLogFile | On | The game copies every log line into its own log file (`Logs/uch-log-file-1.log`) by opening and closing that file once per line, about 0.6 ms each. A full level start writes thousands of lines (one per block), roughly 2 s. The text and its order stay the same; it is now written in batches every 0.25 s and when the game quits. |
| CPU / PhaseCharacterLookup | On | Every round phase change (and every free-play build/play switch) searched the whole scene for the players (~13 ms on a full level) just to refresh their size. The game already keeps a list of all players; that list is used instead. |
| CPU / QuietPlacementLog | On | The game writes a "Placing ..." log line for every block it places, thousands when a full level starts. Only this message is removed. |
| Loading / DeferFullnessMeter | On | While a level's blocks are being put in place, the free-play fullness meter redrew itself after every single block (about 1.5 s on a 10,000-block level). It now updates once, right after loading, with the exact value. |
| Loading / NoGCDuringLevelStart | On | Memory cleanup ran four times inside the level-start freeze frame. It is paused for that one frame and resumes right after. |
| Loading / BeehiveEarlyBeeDisable | On | Every beehive switches off its ~60 bee objects when it starts. In the level-start frame every freshly created component still has a queued Start, and each switch-off made Unity scan that whole queue: about 31 ms per hive, 16 s for 499 hives on a 10,000-block level (the biggest part of that level's 21 s freeze). The bee objects are now switched off the moment the hive is created, before anything is queued for them. The hive's own start and reset code runs unchanged, and the swarm is released exactly as before. Measured on that level: the worst frame is now 4.6 s instead of 21 s, portal to ready 9.2 s instead of 25 s. |
| Physics / StaticBlocks | On | During the play phase, plain placed blocks that cannot move (no moving parent, no animation, no logic) get a static physics body, which the physics step skips. Players, projectiles and moving blocks collide with them exactly as before. Every block is switched back before anything can pick up, move or re-attach it (phase changes, free-play mode switch, level load), and a watchdog switches back any block that moves anyway. |
| Physics / StaticBlocksAuditCache | On | Deciding which blocks qualify for a static body meant walking every block's parent chain and components at every play start (about 30 ms on a full level, i.e. one dropped frame per round). That decision is now remembered between play phases; the cheap checks (placed, picked up, disabled, body type) still run live, the full walk is repeated every 16th play start as a self-check, and any difference turns this option off. |
| Physics / StaticBlocksSliced | On | The static bodies are switched on over a few frames (300 blocks per frame) at play start instead of all in one frame, so the round starts without a hitch. Switching them back (phase change, pickup, level load) stays immediate. |
| Physics / StaticMultipieceBlocks | On | Extends Physics / StaticBlocks to barbed wire, ice and glue pieces and their invisible parent blocks, which the 2.4 rule skipped (about 1,900 of the 2,400 bodies still simulated every step on a full barbed-wire level). Same audit as plain blocks: no moving or non-plain block above them, no motion of their own, only known motionless components; their indicator animation may sit only on a child object without a collider. Same revert paths and watchdog. Pieces attached to moving, crumbling or animated blocks stay as they are. |
| Physics / StaticActiveBlocks | On | Extends the static bodies to the root of active blocks whose colliders never move: arrow shooters, animal cannons, teleporters, black holes, blowers and goal blocks (reviewed in the game code: none of them writes its own body's position or velocity or moves a collider by animation; the player and projectiles meet a non-moving body of either type identically). Blocks whose animation moves a collider (gloves, spikes, doors, lighters, axes, saws, flipping blocks, punching plants, hydrants, hockey shooters, and springs and beehives to be safe) keep their normal body: two Static bodies never notice each other, while a moving collider on a normal body always does, so a glove can still punch another glove's trigger. Blocks attached to a qualifying root, like wire on a shooter, qualify too. Crumbling blocks, moving platforms, treadmills, boulders, coins, keys, bombs and the like stay as they are. Same live state checks, revert paths and watchdog. Needs Physics / StaticBlocks. |
| Graphics / CullInvisibleAnimators | On | On a full level most block animations are off screen (the camera follows the players), yet Unity evaluates every one of them every frame; on one 4,000-block level that was more than half the frame time. Purely visual animations (glue attach indicators, boxing-glove buttons, spring and black-hole sprites, hamster wheels, the goal flag) now use Unity's own culling mode: not evaluated while none of their sprites is visible, resumed as soon as one is. Only animators from a fixed, reviewed list qualify, and only if nothing below them has a collider or physics body and no animation clip triggers code. Everything that drives gameplay (shooters fire from their animation, gloves, doors, crumble blocks, saws, spikes, teleporters, cannons) is untouched. |
| Graphics / CullOffscreenAnimatorPoses | On | The animations left after the row above drive gameplay, and Unity still evaluated all of them every frame (about 300 on that level, 5-13 of them on screen). Animations whose clips only draw (arrow shooters, cannons, teleporters, beehives, blowers, fans, crumble blocks, treadmills, punching plants; the shot, the collider switch, the sound are animation events or script code, and the arrow spawns from a fixed point of the block) now skip their pose writes while the block is outside the camera view plus a 12-unit margin. Their state machines and animation events keep running exactly as before, so shots fire at the same moment from the same spot. The view is the game's own gameplay camera, in "local cam" and "all cam" alike; while no living player is inside it, nothing is skipped. The list of animations is fixed and reviewed; unknown blocks stay untouched. |
| Graphics / CullOffscreenMoverAnimators | On | Same mechanism for animations that move or switch a collider: boxing glove arm, saw blade, pressure spikes, flipping block, swinging axe, lighter flame and both door types. While such a block is outside the view (plus margin) its collider keeps the pose it had when it left the view, and it snaps to the live pose the moment the block is back in view. A block stays live while any player (local or remote) or any projectile (arrow, puck, cannon animal) is within reach of its collider (measured per block type, plus the distance the projectile can travel before the next check), so nothing can ever meet a frozen collider: an off-screen arrow is blocked or let through by a glove or saw exactly as in vanilla. A boxing glove's fist is the one moving block part that can set off other blocks (glove triggers, cannons, doors, crumble blocks), so a glove is never frozen while its punch animation runs, wherever it is: chains of gloves work off screen exactly as in vanilla. Needs the row above. |
| CPU / FarFerrisWheels | On | A ferris wheel outside the view (12-unit margin plus the wheel's radius) skips its per-step rotation and keeps the skipped time; its first step back in view applies that time at once, so it stands exactly where it would have been (one rotation by the summed angle instead of many small ones). Phase changes and free-play mode switches drop the kept time. The wheel keeps turning normally while any player or any projectile is within its reach (its radius plus the distance the projectile can travel before the next physics step), so a platform always stands where vanilla would have it when something can touch it. |
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
| Experimental / QuickSaverRepair | Off; unavailable | Remains vanilla; blanket layer/body changes would change gameplay state. |

</details>

No global logger suppression, event pruning, built-in profiler, hardware-specific worker tuning, or blanket debris lifecycle replacement is included.

## Measuring it yourself

Use the same level, the same players and the same graphics settings with and without the mod (the `General / Enabled` switch), keep menus closed, give the level a few seconds of warm-up, and repeat each run. Lift the FPS cap (`Engine / UncapFrameRate = true`), otherwise the cap hides the difference. The separate UCH Perf Test plugin was used for the numbers above; it is optional.

## Uninstall

Close UCH and remove `BepInEx/plugins/UCH Game Performance`. You can delete its config file too. No game files need restoring.

## Building from source

See [CONTRIBUTING.md](CONTRIBUTING.md). Short version: install UCH + BepInEx 5, then run `.\build.ps1` in PowerShell.

## Credits

Made by Nexus.

- **Nn** for parts of the loading optimizer, the event-listener membership idea (UCH Listener Fix) and the ferris-wheel idea (FerrisCull) that this mod builds on.
- **Milena** for testing.
- Bojack's BetterPerformance inspired some of the early optimizations.
