using System;
using BepInEx.Configuration;
using UnityEngine;

namespace UCHGamePerformance
{
    // 3.1.0: Settings / "Uncap FPS" (Feature Engine/UncapFpsSetting), off by default, live toggle. Same effect as the
    // advanced Engine/UncapFrameRate (cap 1000, VSync off), which stays as it is; either one on = uncapped. Re-applied
    // after GameState.Start (the game's 300 FPS line) and after SaveFileData.ApplySettings (the game writes its saved
    // VSync option there). Off puts back the game's own cap and VSync as they were last set by the game; if the advanced
    // Engine/UncapFrameRate or Engine/TargetFrameRate is active, off hands back to it instead. While Fix screen tearing is
    // on this setting writes nothing (VSync belongs to the fix and a cap is ignored under VSync); it applies as soon as
    // the fix goes off. Started after the advanced engine options, so its GameState.Start postfix runs after theirs.
    internal static class UncapSettingHooks
    {
        internal static readonly Feature Uncap = new Feature("Engine/UncapFpsSetting");
        internal static ConfigEntry<bool> Entry;
        private static bool installed, broken, applied, skipLogged;
        private static int prevTarget, prevSync = -1, pendingSync = -1;

        internal static void Start()
        {
            if (Entry == null) return;
            Entry.SettingChanged += Changed;
            Uncap.Start(Entry.Value, Install, null);
        }

        private static void Install()
        {
            Uncap.Restore = RestoreValues;
            Uncap.Patch(Reflect.Method(typeof(GameState), "Start"), typeof(UncapSettingHooks), null, "AfterGameState", null, null);
            Uncap.Patch(Reflect.DeclaredMethod(typeof(SaveFileData), "ApplySettings"), typeof(UncapSettingHooks), null, "AfterApplySettings", null, null);
            installed = true;
            Apply(false);
        }

        // gameWroteSync: the game has just written its own VSync option (ApplySettings), so take it as the value to restore.
        private static void Apply(bool gameWroteSync)
        {
            if (ScreenTearingHooks.Fix.Active)
            {
                if (!skipLogged)
                {
                    skipLogged = true;
                    Feature.Log.LogInfo("Engine/UncapFpsSetting: Fix screen tearing is on, Uncap FPS leaves the FPS cap and VSync alone until the fix is off");
                }
                return;
            }
            int target = Application.targetFrameRate, sync = QualitySettings.vSyncCount;
            if (!applied)
            {
                prevTarget = target;
                prevSync = sync;
            }
            else
            {
                if (target != UncapHooks.Target) prevTarget = target;
                if (gameWroteSync || sync != 0) prevSync = sync;
            }
            pendingSync = -1;
            Application.targetFrameRate = UncapHooks.Target;
            ScreenTearingHooks.SetVSync(0);
            applied = true;
        }

        private static void RestoreValues()
        {
            if (!applied) return;
            applied = false;
            // An advanced frame-rate option that is on gets the frame rate back instead of the game's own values.
            if (EngineHooks.Fps != null && EngineHooks.Fps.Active)
            {
                EngineHooks.ApplyTarget();
                return;
            }
            Application.targetFrameRate = prevTarget;
            if (prevSync < 0) return;
            if (ScreenTearingHooks.Fix.Active) pendingSync = prevSync;   // the fix owns VSync now; put it back when the fix goes off
            else QualitySettings.vSyncCount = prevSync;
        }

        private static void AfterGameState()
        {
            if (!Uncap.Active) return;
            try { Apply(false); }
            catch (Exception e) { Broke(e); }
        }

        private static void AfterApplySettings()
        {
            if (!Uncap.Active) return;
            try { Apply(true); }
            catch (Exception e) { Broke(e); }
        }

        // Called by ScreenTearingHooks right after Fix screen tearing went off live (its own values are back by then).
        internal static void TearingOff()
        {
            try
            {
                if (Uncap.Active) Apply(false);
                else if (pendingSync >= 0)
                {
                    if (EngineHooks.Fps == null || !EngineHooks.Fps.Active) QualitySettings.vSyncCount = pendingSync;
                    pendingSync = -1;
                }
            }
            catch (Exception e) { Broke(e); }
        }

        // Live toggle from the settings page (Barnyard sets BoxedValue at once); no restart needed.
        private static void Changed(object sender, EventArgs e)
        {
            try
            {
                if (Entry.Value)
                {
                    if (Uncap.Active || broken) return;
                    if (!installed) { Uncap.Start(true, Install, null); return; }
                    Uncap.Active = true;
                    Apply(false);
                    Feature.Log.LogInfo("Engine/UncapFpsSetting on (live): " + Status());
                }
                else
                {
                    if (!Uncap.Active) return;
                    Uncap.Active = false;
                    RestoreValues();
                    Feature.Log.LogInfo("Engine/UncapFpsSetting off (live): " + Status());
                }
            }
            catch (Exception ex) { Broke(ex); }
        }

        private static void Broke(Exception e)
        {
            broken = true;
            Uncap.Fail(e);
        }

        internal static string Status()
        {
            return "target=" + Application.targetFrameRate + " vsync=" + QualitySettings.vSyncCount + " active=" + Uncap.Active + " applied=" + applied + " prev=" + prevTarget + "/" + prevSync + " tearingFix=" + ScreenTearingHooks.Fix.Active;
        }
    }
}
