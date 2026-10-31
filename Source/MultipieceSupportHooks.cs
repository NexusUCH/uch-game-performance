using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.7.7: vanilla "floating wire pieces" fix. A piece placed by another player is placed on this machine with
    // force == true (PiecePlacementCursor.WaitForPlacement passes !hasAuthority), which skips MultipieceBlock.Place's
    // own "part has no support -> destroy it" branch; this machine then relies on the sender's later destroy message.
    // When that message does not take effect, the unsupported part floats until the next level rebuild.
    // For top-level forced placements only (not inside a level restore, not a child placed by its parent's Place),
    // the parts that fail the same CanPlace check the sender applies are destroyed one frame later, locally only
    // (no network message). force and the rest of Place stay vanilla.
    internal static class MultipieceSupportHooks
    {
        internal static readonly Feature Check = new Feature("Multipiece/RemotePartSupportCheck");
        internal static MonoBehaviour Host;
        private static int restoreDepth, placeDepth;
        private static int checkedCount, removedCount, skippedRestore, skippedChild;

        internal static void Install()
        {
            Check.Restore = Restore;
            Type[] place = new Type[] { typeof(int), typeof(bool), typeof(bool) };
            Check.Patch(Reflect.DeclaredMethod(typeof(QuickSaver), "RestoreSaveables"), typeof(RestoreScope), "Prefix", null, null, "Finalizer");
            Check.Patch(Reflect.DeclaredMethod(typeof(Placeable), "Place", place), typeof(PlaceScope), "Prefix", null, null, "Finalizer");
            Check.Patch(Reflect.DeclaredMethod(typeof(MultipieceBlock), "Place", place), typeof(MultipieceSupportHooks), "BeforePlace", "AfterPlace", null, null);
        }

        private static void Restore()
        {
            restoreDepth = 0; placeDepth = 0;
            checkedCount = 0; removedCount = 0; skippedRestore = 0; skippedChild = 0;
        }

        internal static string Status()
        {
            return Check.Name + " active=" + Check.Active + " checked=" + checkedCount + " removed=" + removedCount +
                " skippedRestore=" + skippedRestore + " skippedChild=" + skippedChild + " restoreDepth=" + restoreDepth + " placeDepth=" + placeDepth;
        }

        private static class RestoreScope
        {
            private static void Prefix(out bool __state)
            {
                __state = false;
                if (!Check.Active) return;
                try { restoreDepth++; __state = true; }
                catch (Exception e) { Check.Fail(e); }
            }

            private static void Finalizer(bool __state)
            {
                try { if (__state && restoreDepth > 0) restoreDepth--; }
                catch (Exception e) { Check.Fail(e); }
            }
        }

        private static class PlaceScope
        {
            private static void Prefix(out bool __state)
            {
                __state = false;
                if (!Check.Active) return;
                try { placeDepth++; __state = true; }
                catch (Exception e) { Check.Fail(e); }
            }

            private static void Finalizer(bool __state)
            {
                try { if (__state && placeDepth > 0) placeDepth--; }
                catch (Exception e) { Check.Fail(e); }
            }
        }

        // Runs before MultipieceBlock's own base.Place, so a top-level call sees placeDepth 0 and a child placed from a
        // parent's Placeable.Place sees 1. The verdict is read before any part's colliders switch to the placed phase.
        private static void BeforePlace(MultipieceBlock __instance, bool force, out List<MultipiecePart> __state)
        {
            __state = null;
            if (!force || !Check.Active) return;
            try
            {
                if (restoreDepth > 0) { skippedRestore++; return; }
                if (placeDepth > 0) { skippedChild++; return; }
                checkedCount++;
                MultipiecePart[] parts = __instance.Parts;
                if (parts == null) return;
                for (int i = 0; i < parts.Length; i++)
                {
                    MultipiecePart part = parts[i];
                    if (part == null || part.inDestructible || part.MarkedForDestruction || part.CanPlace()) continue;
                    if (__state == null) __state = new List<MultipiecePart>();
                    __state.Add(part);
                }
            }
            catch (Exception e) { __state = null; Check.Fail(e); }
        }

        private static void AfterPlace(MultipieceBlock __instance, int playerNumber, List<MultipiecePart> __state)
        {
            if (__state == null || !Check.Active) return;
            try { Host.StartCoroutine(RemoveNextFrame(__instance, playerNumber, __state)); }
            catch (Exception e) { Check.Fail(e); }
        }

        private static IEnumerator RemoveNextFrame(MultipieceBlock block, int playerNumber, List<MultipiecePart> parts)
        {
            yield return null;
            if (Check.Active) RemoveNow(block, playerNumber, parts);
        }

        // Same call vanilla's own branch ends in (DestroySelfIn -> DestroySelf(destroyChildren: true, useSmoke: true)),
        // except that no destroy message is sent: this machine is not the piece's owner.
        private static void RemoveNow(MultipieceBlock block, int playerNumber, List<MultipiecePart> parts)
        {
            try
            {
                int removed = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    MultipiecePart part = parts[i];
                    if (part == null || part.MarkedForDestruction) continue;
                    part.DestroySelf(true, true, false);
                    removed++;
                }
                if (removed == 0) return;
                removedCount += removed;
                Feature.Log.LogInfo(Check.Name + ": removed " + removed + " unsupported part(s) of " + (block != null ? block.name : "<destroyed>") + " placed by player " + playerNumber);
            }
            catch (Exception e) { Check.Fail(e); }
        }
    }
}
