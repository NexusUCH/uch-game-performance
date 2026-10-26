using System;
using System.Collections.Generic;

namespace UCHGamePerformance
{
    // 2.5.0: every barbed-wire / ice / glue block carries an AttachmentIndicator (the placement hint sprite). Its
    // Update polls the block's parts every frame until one of them is placed; from that moment it sets isOn=false and
    // returns immediately on every later call, forever (nothing in the game sets isOn back to true, and nothing reads
    // the component's enabled state; it has no other Unity messages besides Start, which has run by then). Unity
    // still pays one native call per indicator per frame: about 900 per frame on Eternity. After the vanilla Update
    // has made its final decision, the component is switched off, which is observably the same as that early return.
    internal static class IndicatorHooks
    {
        internal static readonly Feature Idle = new Feature("CPU/IdleAttachmentIndicators");
        private static readonly List<AttachmentIndicator> Owned = new List<AttachmentIndicator>();
        private static long switchedOff;

        internal static void Install()
        {
            Idle.Restore = Restore;
            Idle.Patch(Reflect.DeclaredMethod(typeof(AttachmentIndicator), "Update"), typeof(IndicatorHooks), null, "AfterUpdate", null, null);
        }

        private static void AfterUpdate(AttachmentIndicator __instance)
        {
            if (!Idle.Active) return;
            try
            {
                if (__instance.isOn || !__instance.enabled) return;
                __instance.enabled = false;
                Owned.Add(__instance);
                switchedOff++;
                if ((Owned.Count & 2047) == 0) Prune();
            }
            catch (Exception e) { Idle.Fail(e); }
        }

        private static void Prune()
        {
            int write = 0;
            for (int i = 0; i < Owned.Count; i++) if (Owned[i] != null) Owned[write++] = Owned[i];
            Owned.RemoveRange(write, Owned.Count - write);
        }

        private static void Restore()
        {
            for (int i = 0; i < Owned.Count; i++) if (Owned[i] != null) Owned[i].enabled = true;
            Owned.Clear();
        }

        internal static string Status() { return "switchedOff=" + switchedOff + " tracked=" + Owned.Count; }
    }
}
