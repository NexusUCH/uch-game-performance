using System;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.7.1: Engine/UncapFrameRate, off by default. The owner's benchmark setting as a plain switch: cap 1000, VSync off,
    // re-applied after GameState starts exactly like Engine/TargetFrameRate (same EngineHooks, same pinned GameState.Awake
    // postfix). An explicit Engine/TargetFrameRate wins; this option is then reported as bypassed.
    internal static class UncapHooks
    {
        internal const int Target = 1000;
        internal static readonly Feature Uncap = new Feature("Engine/UncapFrameRate");

        internal static void Install()
        {
            if (EngineHooks.Fps != null && EngineHooks.Fps.Active) throw new NotSupportedException("Engine/TargetFrameRate is set");
            EngineHooks.Target = Target;
            EngineHooks.Fps = Uncap;
            int previousTarget = Application.targetFrameRate, previousSync = QualitySettings.vSyncCount;
            Uncap.Restore = delegate { Application.targetFrameRate = previousTarget; QualitySettings.vSyncCount = previousSync; };
            Uncap.Patch(Reflect.Method(typeof(GameState), "Awake"), typeof(EngineHooks), null, "AfterGameState", null, null);
            Application.targetFrameRate = Target;
            QualitySettings.vSyncCount = 0;
        }

        internal static string Status()
        {
            return "target=" + Application.targetFrameRate + " vsync=" + QualitySettings.vSyncCount + " active=" + Uncap.Active;
        }
    }
}
