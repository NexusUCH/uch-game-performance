# UCH Game Performance 2.7.2 - Eternity test report

Date: 2026-10-26. Author: nexus. Map: "Eternity", UCH level code **8VKK-7G4K** (3,984 placed blocks, 2,404 of them active blocks, about 1,700 barbed-wire pieces, 289 gameplay animations, 17 black holes, 2 ferris wheels, 20-40 arrows in flight at any time).

## Setup

- Machine: Ryzen 5 5600X, RTX 4060, Windows 11, 1920x1080.
- Game: Ultimate Chicken Horse, current Steam build, BepInEx 5. Local free play, one keyboard player, invincible test character standing at the start of the level (nobody plays; the level runs its shooters, gloves, saws and wheels on its own).
- Profile: only this mod, the measurement plugin (UCH Perf Test) and the test bridge loaded. No other mods.
- FPS cap lifted to 1000 and VSync off for every arm. In the mod arms the mod's own `Engine/UncapFrameRate` option did that; in the vanilla arm the test bridge set it after the level was ready (the game's own cap of 300 was observed first, see below).
- Fresh game start for every arm, 15 s warm-up after the level reached the play phase, then one 20 s recording with the game window focused.
- Vanilla = the mod's master switch `General/Enabled = false`: the DLL is loaded but applies nothing.
- Order: 2.7.2 (run 1), vanilla, 2.7.2 (run 2). Same session, same machine state, nothing else running.

## Results

| Arm | Avg FPS | Median frame | 95th percentile | 99th percentile | Worst frame | Frames slower than 8.33 ms (per 20 s) |
|---|---|---|---|---|---|---|
| Vanilla | 108.2 | 12.07 ms | 14.41 ms | 15.73 ms | 27.6 ms | 1,201 |
| **2.7.2 run 1** | **214.0** | **3.39 ms** | **8.89 ms** | **10.24 ms** | **19.8 ms** | **446** |
| **2.7.2 run 2** | **224.2** | **3.29 ms** | **8.62 ms** | **9.66 ms** | **20.3 ms** | **315** |

Roughly twice the average frame rate, a median frame 3.6x shorter, and a third to a quarter of the slow frames. The two mod runs differ by about 5 %, which is the session noise on this machine; treat the gain as "about 2x", not as an exact factor.

## Level load time

Same setup, separate pass: fresh game start per arm, the level downloaded by code into a treehouse portal, a 60 s recording running across the load, the stopwatch from portal entry to "level ready" (the placement cursor exists and the level scene is active). Order: 2.7.2, vanilla, 2.7.2.

| Arm | Portal entry to level ready | Longest frame during the load | Frames longer than 250 ms |
|---|---|---|---|
| Vanilla | 29.8 s | 25.95 s (one freeze) | 1 |
| **2.7.2 run 1** | **5.9 s** | **2.12 s** | **1** |
| **2.7.2 run 2** | **5.7 s** | **1.96 s** | **1** |

Vanilla freezes for about 26 s while the level starts; with the mod the same start is a single frame of about 2 s. The main contributor is Loading/ScreenWrapLookupCache: Eternity's projectile and hockey-shooter blocks asked the scene for the screen-wrap object 846 times during this one start, and the mod answered all of them from one scene search (`searches=1 answeredFromCache=846`). Loading/NoGCDuringLevelStart, Loading/DeferFullnessMeter, Loading/ListenerMembershipSet and Loading/BeehiveEarlyBeeDisable remove the rest of the per-block startup cost. These are the 2.3.1 to 2.4.2 options; 2.7.x does not change them, and the numbers match the 30.3 s vs 6.5 s measured on 2.5.1.

## What the mod was doing during the runs (its own status counters)

- Static bodies: 3,435 of the level's blocks ran with a Static physics body during the play phase (288 of them active-block roots: shooters, gloves, spikes, cannons, teleporters and so on). The pose watchdog handed 377 bodies back to the physics step because they really moved (wire pieces on moving blocks); that is the safety net working, not a change in behaviour.
- Central block tick: 3,609 plain, wire, ice and glue blocks ticked from the mod's loop instead of one native per-step call each (882 invisible parent blocks among them).
- Off-screen gameplay animations: 223-229 of 289 tracked animations paused their pose writes while outside the camera view; 88-97 of the 145 collider-moving ones at any moment. Ferris wheels skipped 2,552 of 2,652 per-step rotations while out of view and caught up on return.
- Projectile interest grid: 23-39 interest points per frame, horizon 0.083 s, acceleration allowance up to 1.09 units next to the black holes (the review fix of 2.7.1/2.7.2 in action).
- `Engine/UncapFrameRate`: `requested=True / applied=True`, frame-rate target 1000 and VSync 0 confirmed by the test bridge before any test command touched them. In the vanilla arm the bridge read the game's own cap, 300, first.
- No exception, no feature switched itself off, in any arm.

## Behaviour checks

- Off-screen rule probe (six samples across the two mod runs, 20-40 arrows in flight): no paused animation and no paused ferris wheel had a player or a projectile path within its collider reach. 0 violations.
- Hazard sequence (checked on 2.7.0, whose hazard code 2.7.2 does not change): a mortal test character placed on spikes, a saw, a glove, an axe, a flipping block, a door and in front of a shooter ends exactly the same way with and without the mod.
- Arrow first-shot timing (checked on 2.7.0, no round-start or event code changed since): 42 launchers, identical decision step with and without the mod.
- Gameplay settings untouched: physics step, max time step, solver iterations, gravity, layers and hitboxes are never written by the mod (verified offline by the build's 52,289 IL assertions).

## Earlier versions on the same map

Other sessions, with the game's own 300 FPS cap, so only the step from one row to the next is comparable:

| Version | Avg FPS | Median frame |
|---|---|---|
| 2.4.2 | 115.5 | 9.6 ms |
| 2.5.1 | 175.6 - 181.5 | 3.6 - 3.9 ms |
| 2.6.0 | 203.6 | 3.3 ms |
| 2.6.2 -> 2.7.0 (one busier back-to-back session) | 132.4 -> 192.3 | 5.4 -> 3.6 ms |

## Limits of this report

- One map, one machine, one session. Numbers on other hardware will differ; the relative gain should hold on any machine where the physics step and the block scripts are the bottleneck, which is what Eternity stresses.
- Nobody played during the recordings. Real rounds add player input, deaths and camera moves; those were not benchmarked.
- Online play is not benchmarked.
