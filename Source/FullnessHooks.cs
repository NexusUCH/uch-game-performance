using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UCHGamePerformance
{
    // QuickSaver.CalculateLevelFullness scans the whole scene with FindObjectsOfType<PlaceableMetadata>
    // (~7 ms on a full level). FreeplayFullMessage runs it on every 21st event, and every received
    // network message counts, so creative/free play hitches regularly online. The same sum comes from
    // the placeables registered in Placeable.Awake, which is exact while every PlaceableMetadata sits
    // on the GameObject of exactly one Placeable. That invariant is checked on every loaded metadata
    // (prefabs and scene) and the sum is compared with vanilla once per scene load and on every
    // 32nd call afterwards. Any mismatch turns the feature off for the session.
    internal static class FullnessHooks
    {
        internal static readonly Feature Fullness = new Feature("CPU/LevelFullnessCount");
        private const int RecheckInterval = 32;
        private static readonly List<Placeable> Placeables = new List<Placeable>();
        private static readonly HashSet<int> Known = new HashSet<int>();
        private static bool verified;
        private static int calls, nextCompact = 1024;

        internal static void Install()
        {
            Fullness.Patch(Reflect.DeclaredMethod(typeof(Placeable), "Awake"), typeof(FullnessHooks), null, "AfterPlaceableAwake", null, null);
            Fullness.Patch(Reflect.DeclaredMethod(typeof(QuickSaver), "CalculateLevelFullness"), typeof(FullnessHooks), "BeforeCount", "AfterCount", null, null);
            SceneManager.sceneLoaded += SceneLoaded;
            Fullness.Restore = delegate { SceneManager.sceneLoaded -= SceneLoaded; Placeables.Clear(); Known.Clear(); verified = false; };
        }

        private static void SceneLoaded(Scene scene, LoadSceneMode mode) { verified = false; }

        private static void AfterPlaceableAwake(Placeable __instance)
        {
            if (!Fullness.Active) return;
            try
            {
                if (!Known.Add(__instance.GetInstanceID())) return;
                Placeables.Add(__instance);
                if (Placeables.Count >= nextCompact) Compact();
            }
            catch (Exception e) { Fullness.Fail(e); }
        }

        private static bool BeforeCount(ref int __result, out bool __state)
        {
            __state = false;
            if (!Fullness.Active) return true;
            try
            {
                if (verified && ++calls % RecheckInterval != 0) { __result = Fast(); return false; }
                __state = true;
            }
            catch (Exception e) { Fullness.Fail(e); }
            return true;
        }

        // Runs only after a vanilla count (first call per scene load, then every 32nd call).
        private static void AfterCount(int __result, bool __state)
        {
            if (!__state || !Fullness.Active) return;
            try
            {
                if (!verified && !Invariant()) throw new InvalidOperationException("a PlaceableMetadata is not on exactly one registered Placeable");
                int fast = Fast();
                if (fast != __result) throw new InvalidOperationException("fast count " + fast + " differs from vanilla " + __result);
                verified = true;
            }
            catch (Exception e) { verified = false; Fullness.Fail(e); }
        }

        private static int Fast()
        {
            int sum = 0;
            for (int i = 0; i < Placeables.Count; i++)
            {
                Placeable placeable = Placeables[i];
                // Same filter as FindObjectsOfType (active scene objects) and the vanilla loop body.
                if (placeable == null || !placeable.gameObject.activeInHierarchy) continue;
                PlaceableMetadata metadata = placeable.GetComponent<PlaceableMetadata>();
                if (metadata == null || metadata.isLevelGeometry) continue;
                if (placeable.Placed && placeable.IsSaveable) sum += placeable.placementCost;
            }
            return sum;
        }

        private static bool Invariant()
        {
            foreach (PlaceableMetadata metadata in Resources.FindObjectsOfTypeAll<PlaceableMetadata>())
            {
                Placeable[] owners = metadata.GetComponents<Placeable>();
                if (owners.Length != 1 || metadata.GetComponents<PlaceableMetadata>().Length != 1) return false;
                if (metadata.placeableRef != null && metadata.placeableRef != owners[0]) return false;
                if (metadata.gameObject.scene.IsValid() && metadata.gameObject.activeInHierarchy && !Known.Contains(owners[0].GetInstanceID())) return false;
            }
            return true;
        }

        private static void Compact()
        {
            for (int i = Placeables.Count - 1; i >= 0; i--)
                if (Placeables[i] == null) { Known.Remove(Placeables[i].GetInstanceID()); Placeables.RemoveAt(i); }
            nextCompact = Math.Max(1024, Placeables.Count * 2);
        }
    }
}
