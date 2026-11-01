using System;
using BepInEx.Configuration;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.15.0: Settings / "Fix screen tearing" (Feature Engine/ScreenTearingFix), off by default, live toggle.
    // On: VSync on (vSyncCount 1) with a one-frame queue (maxQueuedFrames 1); the previous values are remembered and put
    // back when the setting goes off. Re-applied after GameState.Start (the game's 300 FPS line; Unity ignores
    // targetFrameRate while VSync is on, so that stays as it is) and after SaveFileData.ApplySettings (the game applies
    // the saved VSync option there once the main user's save has loaded, which is after GameState.Start). While the fix
    // is on, Engine/UncapFrameRate and Engine/TargetFrameRate leave VSync alone (SetVSync). Display mode is not touched.
    // 3.1.0: Settings / Uncap FPS (UncapSettingHooks) writes nothing while the fix is on and is told when the fix goes off.
    internal static class ScreenTearingHooks
    {
        internal static readonly Feature Fix = new Feature("Engine/ScreenTearingFix");
        internal static ConfigEntry<bool> Entry;
        private static bool installed, broken, skipLogged;
        private static int prevSync = -1, prevQueue = -1;

        internal static void Start()
        {
            if (Entry == null) return;
            Entry.SettingChanged += Changed;
            Fix.Start(Entry.Value, Install, null);
        }

        private static void Install()
        {
            prevSync = QualitySettings.vSyncCount;
            prevQueue = QualitySettings.maxQueuedFrames;
            Fix.Restore = RestoreValues;
            Fix.Patch(Reflect.Method(typeof(GameState), "Start"), typeof(ScreenTearingHooks), null, "AfterGameState", null, null);
            Fix.Patch(Reflect.DeclaredMethod(typeof(SaveFileData), "ApplySettings"), typeof(ScreenTearingHooks), null, "AfterApplySettings", null, null);
            Apply(true);
            installed = true;
        }

        // remember: take the current values as "the game's own" unconditionally (setting turned on); otherwise only
        // when something else changed them since (a re-apply after the game wrote its own VSync option).
        private static void Apply(bool remember)
        {
            int sync = QualitySettings.vSyncCount, queue = QualitySettings.maxQueuedFrames;
            if (remember || sync != 1) prevSync = sync;
            if (remember || queue != 1) prevQueue = queue;
            QualitySettings.vSyncCount = 1;
            QualitySettings.maxQueuedFrames = 1;
        }

        private static void RestoreValues()
        {
            if (prevSync >= 0) QualitySettings.vSyncCount = prevSync;
            if (prevQueue >= 0) QualitySettings.maxQueuedFrames = prevQueue;
        }

        private static void AfterGameState()
        {
            if (!Fix.Active) return;
            try { Apply(false); }
            catch (Exception e) { Broke(e); }
        }

        private static void AfterApplySettings()
        {
            if (!Fix.Active) return;
            try { Apply(false); }
            catch (Exception e) { Broke(e); }
        }

        // Live toggle from the settings page (Barnyard sets BoxedValue at once); no restart needed.
        private static void Changed(object sender, EventArgs e)
        {
            try
            {
                if (Entry.Value)
                {
                    if (Fix.Active || broken) return;
                    if (!installed) { Fix.Start(true, Install, null); return; }
                    Fix.Active = true;
                    Apply(true);
                    Feature.Log.LogInfo("Engine/ScreenTearingFix on (live): " + Status());
                }
                else
                {
                    if (!Fix.Active) return;
                    Fix.Active = false;
                    RestoreValues();
                    // Back to what the frame-rate options would have set without the fix.
                    EngineHooks.ApplyTarget();
                    // 3.1.0: Settings / Uncap FPS waits while the fix is on; it applies (or puts its VSync back) now.
                    UncapSettingHooks.TearingOff();
                    Feature.Log.LogInfo("Engine/ScreenTearingFix off (live): " + Status());
                }
            }
            catch (Exception ex) { Broke(ex); }
        }

        // Engine/UncapFrameRate and Engine/TargetFrameRate write VSync only through this.
        internal static void SetVSync(int value)
        {
            if (Fix.Active)
            {
                if (!skipLogged)
                {
                    skipLogged = true;
                    Feature.Log.LogInfo("Engine/ScreenTearingFix on: UncapFrameRate/TargetFrameRate leave VSync alone");
                }
                return;
            }
            QualitySettings.vSyncCount = value;
        }

        private static void Broke(Exception e)
        {
            broken = true;
            Fix.Fail(e);
        }

        internal static string Status()
        {
            return "vsync=" + QualitySettings.vSyncCount + " maxQueued=" + QualitySettings.maxQueuedFrames + " active=" + Fix.Active + " prev=" + prevSync + "/" + prevQueue;
        }
    }
}
