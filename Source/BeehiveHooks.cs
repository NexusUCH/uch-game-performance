using System;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.4.2: Beehive.Start switches off the hive's bee objects (the swarm, the indicator bees and every followBees
    // child, about 60 GameObjects per hive). In the level-start frame every component created in that frame still
    // has its deferred Start queued in Unity's delayed-call list, and switching off an object with a pending Start
    // cancels it with a linear scan of that list (about 100k entries on a 10,000-block level). Measured on the mega map:
    // 31 ms per hive, 16.1 s for 499 hives, the whole previously unexplained part of the 21 s level-start freeze.
    // The same objects are switched off already when the hive awakes (postfix on Placeable.Awake), i.e. before their
    // components are ever enabled, so nothing is queued for them and Start's own SetActive(false) calls find them off.
    // Everything else is vanilla: Beehive.Start and Reset run unchanged, Reset switches the indicator bees back on,
    // and releasing the swarm (SetFollowedCharacter -> bees active -> Release) is untouched. Prototype result on the
    // mega map: Beehive.Start 16,085 ms -> 95 ms, level-start frame 20.9 s -> 5.2 s, portal-to-ready 25 s -> 9.9 s.
    internal static class BeehiveHooks
    {
        internal static readonly Feature EarlyBees = new Feature("Loading/BeehiveEarlyBeeDisable");
        private static long hives, beeObjects;

        internal static void Install()
        {
            EarlyBees.Patch(Reflect.DeclaredMethod(typeof(Placeable), "Awake"), typeof(BeehiveHooks), null, "AfterPlaceableAwake", null, null);
        }

        private static void AfterPlaceableAwake(Placeable __instance)
        {
            if (!EarlyBees.Active) return;
            Beehive hive = __instance as Beehive;
            if (hive == null) return;
            try
            {
                if (hive.bees != null) hive.bees.gameObject.SetActive(false);
                if (hive.indicatorBees != null) hive.indicatorBees.gameObject.SetActive(false);
                Transform follow = hive.followBees;
                if (follow != null)
                {
                    int count = follow.childCount;
                    for (int i = 0; i < count; i++) { follow.GetChild(i).gameObject.SetActive(false); beeObjects++; }
                }
                hives++;
            }
            catch (Exception e) { EarlyBees.Fail(e); }
        }

        internal static string Status() { return "hives=" + hives + " beeObjects=" + beeObjects; }
    }
}
