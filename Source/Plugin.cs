using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Scripting;
using UnityEngine.SceneManagement;

namespace UCHGamePerformance
{
    [BepInPlugin(Guid, "UCH Game Performance", "2.5.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.nexus.uch.gameperformance";
        internal static readonly Feature Wait = new Feature("CPU/ReuseFixedUpdateWait");
        internal static readonly Feature EmptyLoops = new Feature("CPU/SkipEmptyBlockLoops");
        internal static readonly Feature PhysMods = new Feature("CPU/PhysModEmptyCache");
        internal static readonly Feature ColliderArrays = new Feature("CPU/ColliderArrayCache");
        internal static readonly Feature Idle = new Feature("CPU/IdleColliderDisable");
        internal static readonly Feature ProjectileComponents = new Feature("Projectiles/ComponentCache");
        internal static readonly Feature ShotColliders = new Feature("Projectiles/ColliderArrayCache");
        internal static readonly Feature IgnorePairs = new Feature("Projectiles/SkipIgnoredPairs");
        internal static readonly Feature Magnet = new Feature("CPU/AttractorRadiusOnChange");
        internal static readonly Feature Multipiece = new Feature("CPU/MultipieceUpdateGate");
        internal static readonly Feature Mass = new Feature("CPU/MoverMassCache");
        internal static readonly Feature Water = new Feature("CPU/WaterScratchArrays");
        internal static readonly Feature Events = new Feature("CPU/EventScratchLists");
        internal static readonly Feature Inactive = new Feature("Projectiles/SkipInactiveNetworkEvents");
        internal static readonly Feature Logs = new Feature("CPU/QuietHotPathLogs");
        internal static readonly Feature Paths = new Feature("Loading/HierarchyPathCache");
        internal static readonly Feature PlacedSet = new Feature("Loading/PlacedBlocksHashSet");
        internal static readonly Feature Smooth = new Feature("Experimental/SmoothSyncAllocations");
        internal static readonly Feature SlowMo = new Feature("Graphics/SlowMotionScreenEffect");
        internal static ScreenEffectMode ScreenMode;
        private bool started;

        public enum ScreenEffectMode { Vanilla, SkipWhenIdle, Remove }

        private bool Option(Feature feature, bool value, string description)
        {
            int slash = feature.Name.IndexOf('/');
            return Config.Bind(feature.Name.Substring(0, slash), feature.Name.Substring(slash + 1), value,
                description + " Restart the game after changing this option.").Value;
        }

        private void Enable(Feature feature, bool value, string description, Action action)
        {
            try { feature.Start(Option(feature, value, description), action, null); }
            catch (Exception e) { feature.Start(value, null, "config: " + e.Message); }
        }

        private void Deferred(string name, bool value, string description, string reason)
        {
            Feature feature = new Feature(name);
            try { feature.Start(Option(feature, value, description), delegate { }, reason); }
            catch (Exception e) { feature.Start(value, null, "config: " + e.Message); }
        }

        private void Awake()
        {
            Feature.Log = Logger;
            started = true;
            Logger.LogInfo("UCH Game Performance 2.5.0; vanilla target; all options require a restart.");
            // Master switch for A/B testing (UCH Perf Test can flip it by name); false leaves the game fully vanilla.
            if (!Config.Bind("General", "Enabled", true, "Master switch. false = this mod applies nothing and the game runs vanilla. Restart the game after changing this option.").Value)
            {
                Logger.LogWarning("General/Enabled=false: no features applied; the game runs vanilla.");
                return;
            }
            // The owner's vanilla-only decision supersedes the earlier compatibility requirements.
            List<string> extra = new List<string>();
            foreach (KeyValuePair<string, PluginInfo> item in Chainloader.PluginInfos)
                if (item.Key != Guid && item.Key != "com.nexus.uch.perftest") extra.Add(item.Key);
            if (extra.Count != 0) Logger.LogWarning("Non-vanilla plugins loaded: " + string.Join(", ", extra.ToArray()) +
                ". They can stay installed. LevelLoadingOptimizer is fine alongside this mod; BetterPerformance overlaps with it, so turn BetterPerformance off if your mod manager allows it.");

            EngineFeatures();
            Enable(Wait, true, "Reuse the fixed-step wait object without changing coroutine scheduling.", CpuHooks.InstallWait);
            Enable(EmptyLoops, true, "Skip coroutines whose block inherits the verified empty base action.", CpuHooks.InstallEmptyLoops);
            Enable(PhysMods, true, "Share empty physics-modifier arrays in base methods; overrides keep their own logic.", CpuHooks.InstallPhysMods);
            Enable(ColliderArrays, true, "Reuse collider arrays, checking live component membership on every use.", CacheHooks.InstallColliderArrays);
            Enable(Idle, true, "Disable idle collision-check behaviours and preserve their OnDisable cleanup.", CpuHooks.InstallIdle);
            Enable(ProjectileComponents, true, "Cache each projectile's rigidbody and modifier settings object, never modifier values.", ProjectileHooks.InstallComponents);
            Enable(ShotColliders, true, "Reuse validated collider arrays during shooting.", ProjectileHooks.InstallShotColliders);
            Enable(IgnorePairs, true, "Avoid repeating IgnoreCollision when that exact pair is already ignored.", ProjectileHooks.InstallIgnorePairs);
            Enable(DebrisHooks.Pooling, true, "Reuse projectile miss debris whose lifecycle is fully known (animation-event removal). Player-hit debris, debris with sounds and the explosion modifier stay vanilla.", DebrisHooks.Install);
            Logger.LogInfo("Projectiles/Rotation requested=transform / applied=vanilla transform write / bypassed(rb.rotation: interpolation, transform sync and visual equivalence unproven)");
            Enable(Magnet, true, "Update magnet collider radius only if its current value differs.", CpuHooks.InstallMagnet);
            Enable(Multipiece, true, "Run multipiece hover/destruction work when its inputs or part lifecycle change.", CpuHooks.InstallMultipiece);
            Enable(Mass, true, "Cache non-radius mover mass ratios between attachment/lifecycle changes. Radius-dependent ratios stay vanilla.", MassHooks.Install);
            Enable(Water, true, "Reuse water splash-key snapshots. Physics overlap queries retain vanilla's complete hit set and order.", CacheHooks.InstallWater);
            Enable(Events, true, "Reuse event cleanup lists with separate storage for nested event sends.", EventHooks.InstallScratch);
            Enable(Inactive, true, "Skip network-event handling for inactive pooled projectiles. Pause and scoreboard state still update.", EventHooks.InstallInactive);
            Enable(FullnessHooks.Fullness, true, "Count level fullness from registered placeables instead of a whole-scene search; checked against vanilla on every scene load and every 32nd count.", FullnessHooks.Install);
            // 2.3.0: both need a persistent coroutine host (this plugin object survives scene loads).
            ActDispatchHooks.Host = this;
            StaticBlockHooks.Host = this;
            Enable(ActDispatchHooks.Dispatch, true, "Run all block act loops from one central runner in the vanilla order and phase; each block's first step stays native.", ActDispatchHooks.Install);
            Enable(PlaceableTickHooks.Tick, true, "Run plain blocks' per-step bookkeeping from one early loop instead of one native FixedUpdate call per block; attached blocks still run the original method.", PlaceableTickHooks.Install);
            Enable(AudioHooks.Emitters, true, "Skip a sound emitter's per-frame position update while its transform and sound environment are unchanged; it would send nothing in that case.", AudioHooks.Install);
            Enable(SceneSearchHooks.Search, true, "Answer repeated screen-wrap lookups by projectiles/hockey shooters within one frame from a single scene search; removes a multi-second freeze after loading full levels.", SceneSearchHooks.Install);
            Enable(SnapshotHooks.Skip, true, "In local games (no online lobby, no remote player or connection) pass the level XML on uncompressed instead of LZMA-compressing it for nobody when a level starts.", SnapshotHooks.Install);
            GcHooks.Host = this;
            Enable(RoundHooks.Lookup, true, "On every phase start, refresh character sizes from the game's own character list instead of searching the whole scene (~13 ms on a full level).", RoundHooks.Install);
            Enable(PlacementLogHooks.Quiet, true, "Remove the per-block \"Placing ...\" log line (thousands per level start); other logging stays.", PlacementLogHooks.Install);
            Enable(FullnessUiHooks.Defer, true, "While a level's blocks are being restored, hold back the free-play fullness meter's per-block text updates and recount it once afterwards.", FullnessUiHooks.Install);
            Enable(GcHooks.Pause, true, "Pause automatic garbage collection for the level-start frame only; it resumes after the next frame.", GcHooks.Install);
            Enable(BeehiveHooks.EarlyBees, true, "When a level starts, each beehive's Start switches off its ~60 bee objects, and in that frame every new component still has a queued Start, so each switch-off makes Unity scan that queue (16 s for 499 hives on a 10,000-block level). The bee objects are switched off already when the hive awakes, before anything is queued for them; the hive's Start and Reset run unchanged.", BeehiveHooks.Install);
            Enable(ListenerHooks.Membership, true, "Blocks and projectiles check event-listener membership with a hash set mirroring each listener list instead of scanning the whole list per registration; list contents and order stay vanilla.", ListenerHooks.Install);
            Enable(LogFileHooks.Buffer, true, "The game appends each log line to its own log file by opening and closing the file per line (~0.6 ms each, thousands per level start). Same text and order, written in batches every 250 ms and on quit.", LogFileHooks.Install);
            Enable(StaticBlockHooks.Static, true, "During the play phase give motionless plain blocks a Static body (skipped by the physics step); reverted before anything can move them.", StaticBlockHooks.Install);
            Enable(StaticBlockHooks.AuditCache, true, "Remember each plain block's static-body audit (parent chain and components) between play phases instead of re-auditing every block at every play start (~30 ms on a full level). The cheap state checks still run live; the audit is repeated in full every 16th play start and any difference turns this option off.", StaticBlockHooks.InstallAuditCache);
            Enable(StaticBlockHooks.Sliced, true, "Switch the static bodies on over a few frames (300 per frame) at play start instead of all in one frame. Switching them back stays immediate.", StaticBlockHooks.InstallSliced);
            Enable(StaticBlockHooks.Wide, true, "Also give barbed wire, ice and glue pieces (and their invisible parent blocks) a Static body during the play phase when they pass the same audit as plain blocks: no moving or non-plain ancestor, no motion of their own, only known motionless components; their indicator animations may sit only on collider-free child objects. About 1,900 more static bodies on a full barbed-wire level.", StaticBlockHooks.InstallWide);
            Enable(AnimatorCullHooks.Cull, true, "Purely visual block animations (glue attach indicators, boxing-glove buttons, spring and black-hole sprites, hamster wheels, the goal flag) are not evaluated while none of their sprites is on screen (Unity's own animator culling). Only animators with no collider, no physics body and no animation event below them, from a fixed list of reviewed animations; everything that drives gameplay (shooters, gloves, doors, crumble blocks, saws, spikes, teleporters) stays untouched.", AnimatorCullHooks.Install);
            Enable(IndicatorHooks.Idle, true, "A barbed-wire, ice or glue block's attachment-indicator script keeps running every frame after the block is placed, although from then on it returns immediately forever. It is switched off at that point (about 900 per-frame calls on a full barbed-wire level).", IndicatorHooks.Install);
            Enable(Logs, true, "Silence only three known pool/placement hot-path messages at their call sites.", CpuHooks.InstallLogs);
            Enable(Paths, true, "Cache parsed hierarchy paths within each load pass. Resolve roots live to honor hierarchy mutations.", LoadingHooks.InstallPaths);
            Enable(PlacedSet, true, "Mirror placed-block membership with a hash set while the original ordered list remains authoritative.", LoadingHooks.InstallPlacedSet);
            Enable(Smooth, false, "Remove the overwritten temporary state allocation in the send constructor; packet generation remains unchanged.", ExperimentalHooks.InstallSmooth);
            try
            {
                ScreenMode = Config.Bind("Graphics", "SlowMotionScreenEffect", ScreenEffectMode.Vanilla,
                    "Vanilla: original screen effect. SkipWhenIdle: plain blit when the shader inputs are neutral. Remove: always plain blit. Restart required.").Value;
                SlowMo.Start(ScreenMode != ScreenEffectMode.Vanilla, ExperimentalHooks.InstallSlowMo, null);
                Logger.LogInfo("Graphics/SlowMotionScreenEffect mode=" + ScreenMode);
            }
            catch (Exception e) { SlowMo.Start(true, null, "config: " + e.Message); }
            Deferred("Experimental/QuickSaverRepair", false, "Request saved-block layer/body repair; unavailable pending a proven safe scope.",
                "vanilla: blanket layer/kinematic writes change gameplay state and conflict with the no-layer-change rule");
            SceneManager.sceneUnloaded += SceneUnloaded;
        }

        private void EngineFeatures()
        {
            Feature callbacks = new Feature("Engine/ReuseCollisionCallbacks");
            Enable(callbacks, true, "Reuse Unity collision callback objects; vanilla callbacks do not retain them.", delegate {
                bool previous = Physics2D.reuseCollisionCallbacks;
                callbacks.Restore = delegate { Physics2D.reuseCollisionCallbacks = previous; };
                Physics2D.reuseCollisionCallbacks = true;
            });
            Feature gc = new Feature("Engine/IncrementalGC");
            Enable(gc, true, "Set the work budget of already-enabled incremental garbage collection.", delegate {
                float slice = Config.Bind("Engine", "IncrementalGCSliceMilliseconds", 1f,
                    new ConfigDescription("Incremental GC work budget in milliseconds; applies only when incremental GC is already active. Restart required.", new AcceptableValueRange<float>(0.1f, 10f))).Value;
                if (!GarbageCollector.isIncremental) throw new NotSupportedException("incremental GC is not active; left vanilla");
                ulong previous = GarbageCollector.incrementalTimeSliceNanoseconds;
                gc.Restore = delegate { GarbageCollector.incrementalTimeSliceNanoseconds = previous; };
                GarbageCollector.incrementalTimeSliceNanoseconds = (ulong)(slice * 1000000d);
            });
            Feature priority = new Feature("Engine/HighProcessPriority");
            Enable(priority, false, "Opt in to High process priority; default leaves Windows scheduling alone.", delegate {
                using (Process process = Process.GetCurrentProcess())
                {
                    ProcessPriorityClass previous = process.PriorityClass;
                    priority.Restore = delegate { using (Process restore = Process.GetCurrentProcess()) restore.PriorityClass = previous; };
                    process.PriorityClass = ProcessPriorityClass.High;
                }
            });
            Feature fps = new Feature("Engine/TargetFrameRate");
            try { ConfigureTarget(fps); }
            catch (Exception e) { fps.Start(true, null, "config: " + e.Message); }
            Feature jobs = new Feature("Experimental/PhysicsMultithreading");
            Enable(jobs, true, "Unity 2D physics jobs with consistency sorting: spreads the physics step over CPU cores. Leaves simulation mode and worker counts alone.", delegate {
                PhysicsJobOptions2D previous = Physics2D.jobOptions;
                jobs.Restore = delegate { Physics2D.jobOptions = previous; };
                PhysicsJobOptions2D options = previous;
                options.useMultithreading = true;
                options.useConsistencySorting = true;
                Physics2D.jobOptions = options;
                Logger.LogInfo("Physics jobs use existing simulationMode=" + Physics2D.simulationMode + "; worker counts unchanged.");
            });
        }

        private void ConfigureTarget(Feature fps)
        {
            int target = Config.Bind("Engine", "TargetFrameRate", 0, new ConfigDescription(
                "0 leaves the game's FPS cap and VSync alone. A positive value sets that cap and turns VSync off after GameState starts. Restart required.", new AcceptableValueRange<int>(0, 10000))).Value;
            fps.Start(target > 0, delegate {
                EngineHooks.Target = target;
                EngineHooks.Fps = fps;
                int previousTarget = Application.targetFrameRate, previousSync = QualitySettings.vSyncCount;
                fps.Restore = delegate { Application.targetFrameRate = previousTarget; QualitySettings.vSyncCount = previousSync; };
                fps.Patch(Reflect.Method(typeof(GameState), "Awake"), typeof(EngineHooks), null, "AfterGameState", null, null);
                Application.targetFrameRate = target;
                QualitySettings.vSyncCount = 0;
            }, null);
        }

        private void OnDisable()
        {
            if (!started) return;
            SceneManager.sceneUnloaded -= SceneUnloaded;
            for (int i = Feature.All.Count - 1; i >= 0; i--) Feature.All[i].Stop();
            CacheHooks.Clear();
            LoadingHooks.Clear();
        }

        private void SceneUnloaded(Scene scene)
        {
            try { CpuHooks.PruneIdle(); }
            catch (Exception e) { Idle.Fail(e); }
            try { ActDispatchHooks.SceneUnloaded(); }
            catch (Exception e) { ActDispatchHooks.Dispatch.Fail(e); }
            try { AudioHooks.SceneUnloaded(); }
            catch (Exception e) { AudioHooks.Emitters.Fail(e); }
            try { StaticBlockHooks.SceneUnloaded(); }
            catch (Exception e) { StaticBlockHooks.AuditCache.Fail(e); }
        }

        private void OnEnable()
        {
            if (started) Logger.LogInfo("Feature state is startup-only; restart UCH after disabling/re-enabling the plugin.");
        }
    }

    internal static class EngineHooks
    {
        internal static int Target;
        internal static Feature Fps;
        internal static void ApplyTarget()
        {
            if (Fps == null || !Fps.Active) return;
            try { Application.targetFrameRate = Target; QualitySettings.vSyncCount = 0; }
            catch (Exception e) { Fps.Fail(e); }
        }
        private static void AfterGameState() { ApplyTarget(); }
    }
}
