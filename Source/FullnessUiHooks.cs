using System;
using System.Collections.Generic;
using System.Reflection;
using GameEvent;
using HarmonyLib;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.4.0: the free-play "level fullness" meter (FreeplayFullMessage) handles every PiecePlacedEvent: it updates two
    // UI texts and recounts the whole level every 21st event. While QuickSaver.RestoreSaveables places all blocks of a
    // level in one frame that is thousands of text updates nobody can see (1.45 s on a 10,000-block level). Those
    // updates are held back during the restore and the meter is recounted once right after it - the same full recount
    // the meter itself does every 21st event, so the shown value is the exact level fullness.
    internal static class FullnessUiHooks
    {
        internal static readonly Feature Defer = new Feature("Loading/DeferFullnessMeter");
        private static readonly List<FreeplayFullMessage> meters = new List<FreeplayFullMessage>();
        private static readonly FieldInfo ops = Reflect.Field(typeof(FreeplayFullMessage), "opsSinceLastCount");
        private static int restoring;
        private static long skipped, refreshed;

        internal static void Install()
        {
            if (ops == null || ops.FieldType != typeof(int)) throw new MissingFieldException("FreeplayFullMessage.opsSinceLastCount");
            Defer.Restore = delegate { restoring = 0; meters.Clear(); };
            Defer.Patch(Reflect.DeclaredMethod(typeof(FreeplayFullMessage), "Awake"), typeof(FullnessUiHooks), null, "Registered", null, null);
            Defer.Patch(Reflect.DeclaredMethod(typeof(FreeplayFullMessage), "OnDestroy"), typeof(FullnessUiHooks), null, "Unregistered", null, null);
            Defer.Patch(Reflect.DeclaredMethod(typeof(FreeplayFullMessage), "handleEvent"), typeof(FullnessUiHooks), "BeforeMeterEvent", null, null, null);
            Defer.Patch(Reflect.DeclaredMethod(typeof(QuickSaver), "RestoreSaveables"), typeof(FullnessUiHooks), "BeforeRestore", null, null, "AfterRestore");
        }

        private static void Registered(FreeplayFullMessage __instance) { if (__instance != null && !meters.Contains(__instance)) meters.Add(__instance); }
        private static void Unregistered(FreeplayFullMessage __instance) { meters.Remove(__instance); }

        private static bool BeforeMeterEvent(GameEvent.GameEvent e)
        {
            if (!Defer.Active || restoring == 0 || e == null || e.GetType() != typeof(PiecePlacedEvent)) return true;
            skipped++;
            return false;
        }

        private static void BeforeRestore(out bool __state)
        {
            __state = Defer.Active;
            if (__state) restoring++;
        }

        private static Exception AfterRestore(bool __state, Exception __exception)
        {
            if (__state && restoring > 0 && --restoring == 0)
            {
                try
                {
                    for (int i = meters.Count - 1; i >= 0; i--)
                    {
                        FreeplayFullMessage meter = meters[i];
                        if (meter == null) { meters.RemoveAt(i); continue; }
                        // Next event becomes the meter's own full recount (opsSinceLastCount > 20), which then redraws it.
                        ops.SetValue(meter, 20);
                        meter.handleEvent(new PiecePlacedEvent(0, null));
                        refreshed++;
                    }
                }
                catch (Exception e) { Defer.Fail(e); }
            }
            return __exception;
        }

        internal static string Status() { return "skipped=" + skipped + " refreshed=" + refreshed + " meters=" + meters.Count; }
    }
}
