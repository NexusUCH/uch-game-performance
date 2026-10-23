using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace UCHGamePerformance
{
    internal static class MassHooks
    {
        private sealed class Entry
        {
            public Entry() { }
            internal long Epoch;
            internal float Mass, Influence, Maximum, Result;
            internal bool Ready;
        }
        private static long epoch;
        private static readonly ConditionalWeakTable<Placeable, Entry> Entries = new ConditionalWeakTable<Placeable, Entry>();

        internal static void Install()
        {
            Guards.Method(Reflect.DeclaredMethod(typeof(Placeable), "GetTotalMass"));
            Guards.Method(Reflect.DeclaredMethod(typeof(MultipieceBlock), "GetTotalMass"));
            Guards.Method(Reflect.DeclaredMethod(typeof(MultipiecePart), "GetTotalMass"));
            // All vanilla ChildPieces writes: Placeable's three mutators and AttachmentGroup.DestroyGroup.
            // Public Mass fields have no runtime writes in the audited game; Awake covers deserialization.
            foreach (MethodBase method in new MethodBase[] {
                Reflect.DeclaredMethod(typeof(Placeable), "AttachPiece"),
                Reflect.DeclaredMethod(typeof(Placeable), "DetachPiece"),
                Reflect.DeclaredMethod(typeof(Placeable), "DetachChildrenForPickup"),
                Reflect.DeclaredMethod(typeof(AttachmentGroup), "DestroyGroup"),
                Reflect.DeclaredMethod(typeof(MultipieceBlock), "Awake"),
                Reflect.DeclaredMethod(typeof(MultipieceBlock), "RemovePart"),
                Reflect.DeclaredMethod(typeof(Placeable), "Awake"),
                Reflect.DeclaredMethod(typeof(Placeable), "OnDestroy") })
                Plugin.Mass.Patch(method, typeof(MassHooks), "Dirty", null, null, "AfterMutation");
            Plugin.Mass.Patch(Reflect.DeclaredMethod(typeof(Placeable), "calculateMassRatio"), typeof(MassHooks), "BeforeRatio", "AfterRatio", null, null);
        }

        private static void Dirty() { unchecked { epoch++; } }
        private static Exception AfterMutation(Exception __exception) { Dirty(); return __exception; }

        private static bool BeforeRatio(Placeable __instance, ref float __result, out bool __state)
        {
            __state = false;
            if (!Plugin.Mass.Active || __instance.RadiusCalculatedMass) return true;
            try
            {
                Entry entry = Entries.GetOrCreateValue(__instance);
                if (entry.Ready && entry.Epoch == epoch && entry.Mass.Equals(__instance.Mass) && entry.Influence.Equals(__instance.massInfluenceRatio) && entry.Maximum.Equals(__instance.MaximumMassSpeedRatio))
                { __result = entry.Result; return false; }
                __state = true;
                return true;
            }
            catch (Exception e) { Plugin.Mass.Fail(e); return true; }
        }

        private static void AfterRatio(Placeable __instance, float __result, bool __state)
        {
            if (!Plugin.Mass.Active || !__state) return;
            try
            {
                Entry entry = Entries.GetOrCreateValue(__instance);
                entry.Epoch = epoch; entry.Mass = __instance.Mass; entry.Influence = __instance.massInfluenceRatio;
                entry.Maximum = __instance.MaximumMassSpeedRatio; entry.Result = __result; entry.Ready = true;
            }
            catch (Exception e) { Plugin.Mass.Fail(e); }
        }
    }
}
