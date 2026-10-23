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
    [BepInPlugin(Guid, "UCH Game Performance", "2.2.0")]
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
            Logger.LogInfo("UCH Game Performance 2.2.0; vanilla target; all options require a restart.");
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
                ". This mod targets vanilla. Remove BetterPerformance + LevelLoadingOptimizer folders; this mod replaces them. Use alone for reliable results.");

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
            Deferred("Experimental/StaticBlocks", false, "Request static block conversion; unavailable pending proof of every mover/physics lifecycle.",
                "vanilla: body-type conversion can alter contacts; complete event-driven motion invalidation is unproven");
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
