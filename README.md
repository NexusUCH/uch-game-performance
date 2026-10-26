# UCH Game Performance 2.7.2

One performance mod for vanilla Ultimate Chicken Horse. It replaces UCH Game Performance 1.x, BetterPerformance and LevelLoadingOptimizer, so you don't need those anymore (keeping them installed is fine too). Requires **BepInEx 5**; Harmony is already included with BepInEx. No other plugin or extra DLL is required.

Gains show mostly on very full maps (many moving blocks/projectiles). No specific FPS increase is promised; FPS caps can hide gains.

## Install

1. Close UCH.
2. Remove any older UCH Game Performance DLL, including copies in differently named folders.
3. Download `UCHGamePerformance.dll` from [Releases](../../releases) and put it in `BepInEx/plugins/UCH Game Performance/` (or drop the whole folder from the release zip into `BepInEx/plugins`).
4. Start UCH. Read the startup lines in `BepInEx/LogOutput.log`: each feature reports `requested / applied / bypassed(reason)`.

Other mods can stay installed; nothing has to be removed or disabled. LevelLoadingOptimizer works fine alongside this mod (also if your mod manager, e.g. Barnyard, cannot turn it off). BetterPerformance does some of the same things, so turn it off if Barnyard allows it; you do not need it with this mod. The mod logs which other plugins are loaded but never changes them. The separate optional benchmark tool can also be used; it is not required or bundled.

Only the explicitly supported game build applies optimizations. Other game builds keep all features bypassed. The mod writes only its own BepInEx config and log messages. It does not change game files, the registry, physics timing, hitboxes, projectile rotation, or gameplay layers.

## Benchmarks

All numbers: local free play, FPS cap lifted, only this mod plus the measurement plugin loaded, Ryzen 5 5600X + RTX 4060.

Heavy competitive map (UCH level code **800M-VDMW**, 2,848 blocks), 30 s per run, two runs each:

| | Avg FPS | Frames slower than 8.33 ms (per 30 s) | Worst frame |
|---|---|---|---|
| Vanilla | 67.6 | ~1,770 | ~41 ms |
| 2.2.0 level (2.3 features off) | 206.4 | ~1,795 | ~22 ms |
| **2.3.0** | **271.9** | **~347** | **~16 ms** |

"Eternity" (level code **8VKK-7G4K**, 3,984 blocks, 2,404 of them active, 1,700 barbed-wire pieces, 760 block animations). FPS cap lifted to 1000 and VSync off (Engine/UncapFrameRate for the mod runs, set by the test bridge for vanilla), fresh game start per run, 15 s warm-up, 20 s recording, invincible test character at the start, same session, 2026-10-26:

| | Avg FPS | Median frame | 99th percentile frame | Worst frame | Frames slower than 8.33 ms (per 20 s) |
|---|---|---|---|---|---|
| Vanilla | 108.2 | 12.1 ms | 15.7 ms | 27.6 ms | 1,201 |
| **2.7.2** run 1 | **214.0** | **3.4 ms** | **10.2 ms** | **19.8 ms** | **446** |
| **2.7.2** run 2 | **224.2** | **3.3 ms** | **9.7 ms** | **20.3 ms** | **315** |

Full Eternity test report (setup, counters, behaviour checks): [docs/eternity-test-report-2.7.2.md](docs/eternity-test-report-2.7.2.md). Roughly twice the frame rate and a median frame 3.6x shorter on this map. Earlier versions on the same map (other sessions, the game's own 300 FPS cap, so only the version-to-version steps are comparable): 2.4.2 115.5, 2.5.1 175.6-181.5, 2.6.0 203.6, 2.6.2 132.4 -> 2.7.0 192.3 (one busier back-to-back session).

Level loading on a 10,000-block level (2.4.2): worst frame 21 s -> 4.6 s, portal to ready 25 s -> 9.2 s. Full 2.3.0 test log: [docs/UCH-Game-Performance-Test-Log.xlsx](docs/UCH-Game-Performance-Test-Log.xlsx). Real party rounds and online play are not benchmarked.

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
| Engine / UncapFrameRate | Off | On lifts the FPS cap to 1000 and turns VSync off (the setting the benchmarks below use). Off keeps the game's own cap. TargetFrameRate wins when set. |
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
| Physics / StaticActiveBlocks | On | Extends the static bodies to the root of active blocks that never move themselves: arrow shooters, boxing gloves, pressure spikes, animal cannons, teleporters, springs, black holes, beehives, blowers, auto doors, lighters, swinging axes, saws, flipping blocks, punching plants, fire hydrants, hockey shooters and goal blocks (reviewed in the game code: none of them writes its own body's position or velocity; their animations move child colliders, which Unity re-syncs the same way under either body type, and the player and projectiles meet a non-moving body of either type identically). Blocks attached to such a root, like wire on a glove block, qualify too. Crumbling blocks, moving platforms, treadmills, boulders, coins, keys, bombs and the like stay as they are. Same live state checks, revert paths and watchdog. Needs Physics / StaticBlocks. |
| Graphics / CullInvisibleAnimators | On | On a full level most block animations are off screen (the camera follows the players), yet Unity evaluates every one of them every frame; on one 4,000-block level that was more than half the frame time. Purely visual animations (glue attach indicators, boxing-glove buttons, spring and black-hole sprites, hamster wheels, the goal flag) now use Unity's own culling mode: not evaluated while none of their sprites is visible, resumed as soon as one is. Only animators from a fixed, reviewed list qualify, and only if nothing below them has a collider or physics body and no animation clip triggers code. Everything that drives gameplay (shooters fire from their animation, gloves, doors, crumble blocks, saws, spikes, teleporters, cannons) is untouched. |
| Graphics / CullOffscreenAnimatorPoses | On | The animations left after the row above drive gameplay, and Unity still evaluated all of them every frame (about 300 on that level, 5-13 of them on screen). Animations whose clips only draw (arrow shooters, cannons, teleporters, beehives, blowers, fans, crumble blocks, treadmills, punching plants; the shot, the collider switch, the sound are animation events or script code, and the arrow spawns from a fixed point of the block) now skip their pose writes while the block is outside the camera view plus a 12-unit margin. Their state machines and animation events keep running exactly as before, so shots fire at the same moment from the same spot. The view is the game's own gameplay camera, in "local cam" and "all cam" alike; while no living player is inside it, nothing is skipped. The list of animations is fixed and reviewed; unknown blocks stay untouched. |
| Graphics / CullOffscreenMoverAnimators | On | Same mechanism for animations that move or switch a collider: boxing glove arm, saw blade, pressure spikes, flipping block, swinging axe, lighter flame and both door types. While such a block is outside the view (plus margin) its collider keeps the pose it had when it left the view, and it snaps to the live pose the moment the block is back in view. A block stays live while any player (local or remote) or any projectile (arrow, puck, cannon animal) is within reach of its collider (measured per block type, plus the distance the projectile can travel before the next check), so nothing can ever meet a frozen collider: an off-screen arrow is blocked or let through by a glove or saw exactly as in vanilla. Needs the row above. |
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

No global logger suppression, event pruning, built-in profiler, hardware-specific worker tuning, or blanket debris lifecycle replacement is included. Experimental graphics/physics options require a visual and gameplay check on your maps. The screen-effect options retain the camera render-texture path; they replace the material blit rather than disabling the effect component.

## Comparing results

Use the same map, players, graphics settings and actions with and without the mod. Keep menus closed, allow warm-up, and repeat runs. FPS caps can hide throughput gains. Detailed owner instructions, including the profile switcher and separate UCH Perf Test tool, are in **TESTING.md**.

## Building from source

See [CONTRIBUTING.md](CONTRIBUTING.md). Short version: install UCH + BepInEx 5, then run `.\build.ps1` in PowerShell.

## Uninstall

Close UCH and remove `BepInEx/plugins/UCH Game Performance`. You can optionally delete its config file. No game files need restoring. Restart UCH to return to vanilla behavior.

## Credits

Made by Nexus. Some early optimizations were inspired by Bojack's BetterPerformance / LevelLoadingOptimizer.
The event-listener membership idea (Loading / ListenerMembershipSet) comes from Triple's UCH Listener Fix.

2.0.1 fixes the pause-menu and mode-change softlock caused by event listeners
being removed after dispatch. CPU/EventScratchLists remains On by default.
The separate UCH Perf Test 1.0.1 package reduces census searches during capture.

2.1.0: Experimental/PhysicsMultithreading is now On by default (owner A/B on a dense stress map: +17-22% average FPS, 75-87% fewer frames below 120 FPS, no visible gameplay difference). New General/Enabled master switch for before/after testing.

2.5.0: three options for busy levels, found on "Eternity" (4,000 blocks, 1,700 barbed-wire pieces, 760 animations): Physics/StaticMultipieceBlocks (wire, ice and glue get static bodies too), Graphics/CullInvisibleAnimators (purely visual animations are skipped while off screen) and CPU/IdleAttachmentIndicators (finished attachment hints stop being called). Each is a separate switch and reports its own applied/bypassed line in the log.

2.6.0: off-screen gameplay animations. Graphics/CullOffscreenAnimatorPoses skips the pose writes of drawing-only gameplay animations (shooters, cannons, teleporters, beehives, blowers, fans, crumble blocks, treadmills, punching plants) outside the camera view; animation events and state machines keep running, so shot timing and spawn points are unchanged. Graphics/CullOffscreenMoverAnimators does the same for animations that move a collider (gloves, saws, spikes, flipping blocks, axes, lighters, doors): off screen the collider freezes and snaps back when the block comes into view (only off-screen projectiles can notice). CPU/FarFerrisWheels skips far-away ferris wheels' per-step rotation and applies the kept time at once when they come back (idea from a friend's FerrisCull plugin). Each is a separate switch. Lab A/B on Eternity: see the Benchmarks section on GitHub.

2.6.2: off-screen movers and ferris wheels can no longer change what happens to a projectile. 2.6.0 froze a glove, saw, spike, door or ferris wheel outside the view even while an arrow, puck or cannon animal was about to reach it, so an off-screen arrow could pass where vanilla would have blocked it. Now every player and every live projectile is placed in a 12-unit grid at every physics step, each with the distance it can travel before the next check, and a mover or wheel stays live while anything is within reach of its collider (reach measured per block type: glove 10, axe 9, saw 6, doors 5-6, lighter 5, spikes and flipping block 4 units). The check is an exact distance test over the neighbouring grid cells, so its cost does not grow with the number of projectiles in flight. Drawing-only animations keep the plain view rule, since they move no collider. The view is now read from the game's own gameplay camera, so the options also work with "local cam" in online games (remote players count as interest points), and a switch to "all cam" un-freezes everything in the same frame the view changes. (2.6.1 was a lab-only build of the same fix with a coarser grid.)

2.7.2: review fixes before the full release, plus one optional switch: Engine/UncapFrameRate (off by default) lifts the FPS cap to 1000 and turns VSync off, the setting all benchmarks here are taken with. The cap is applied right after the game sets its own 300 FPS cap in GameState.Start; the game has no GameState.Awake, so the Engine/TargetFrameRate option inherited from 2.0.0 had never attached (it reported "missing method" when switched on) and now attaches at the same place. 2.7.1 was a lab-only build of the same fixes whose uncap option still used the missing method. The travel-segment test of 2.7.0 assumed straight flight; a projectile's path can bend between two checks (cannon animals fall, a black hole pulls arrows in with up to 300 units/s^2, the blower pushes with up to 5), so the segment's radius now grows by the distance such an acceleration can move it within the horizon (0.5 x acceleration x horizon^2, with the black hole term only near a black hole). On a normal map that is about one unit next to a black hole and a few hundredths elsewhere, so nothing changes for FPS; on very full maps (over 400 reviewed animations, where a mover is checked every second frame) it closes a gap that could have let a black hole bend an arrow into a frozen off-screen collider. Also: the cell clamp for absurd velocities now keeps the cells around the projectile's current position; the block-destroy bookkeeping of CPU/CentralPlaceableTick is wrapped like every other hook and a part-type failure switches off only CPU/CentralPartTick.

2.7.0: more FPS without touching gameplay. The projectile test of 2.6.2 now uses each projectile's travel segment (where its velocity carries it before the next check) instead of a circle around it, about four times less area, so an arrow flying away from a glove or saw no longer keeps it live. New CPU/CentralPartTick: barbed-wire, ice and glue pieces and their invisible parent blocks (about 1,900 native per-step calls on a full wire level) are ticked from the mod's own loop like plain blocks already were; the parent's per-frame part check runs from that loop too. New Physics/StaticActiveBlocks: the root bodies of active blocks that never move themselves (shooters, gloves, spikes, cannons, teleporters, springs, black holes, beehives, blowers, doors, lighters, axes, saws, flipping blocks, punching plants, hydrants, hockey shooters, goal blocks) and the blocks attached to them get a Static body during play, like plain blocks since 2.3.0. Each is a separate switch.

2.5.1: the static-body watchdog now reverts a block only when its position or rotation really changed. Unity also flags a block's children as "changed" when the parent's body switches type, which made the 2.5.0 watchdog hand about 400 attached wire pieces back to the physics step at every play start for nothing.

2.2.0: Projectiles/DebrisPooling is now active for miss debris with a known lifecycle (arrow and tennis-ball debris). New CPU/LevelFullnessCount removes a ~7 ms hitch that creative/free play hit every 21 network messages.
