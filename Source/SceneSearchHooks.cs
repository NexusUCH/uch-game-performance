using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UCHGamePerformance
{
    // 2.3.1: every Projectile.Awake (each launcher fills a pool of ~20 when a level starts) and every HockeyShooter.Start
    // runs FindObjectOfType<ScreenWrapping>(), a search through the whole scene. On a full level that is ~13 ms per call
    // and 480+ calls in one frame: a multi-second freeze after loading. ScreenWrapping is a fixed scene object that no
    // code enables, disables or creates, so within one frame every call returns the same object (or null). This
    // answers repeated calls in the same frame from one search; the cache also resets whenever a ScreenWrapping
    // awakes/enables/disables and on every scene load or unload.
    internal static class SceneSearchHooks
    {
        internal static readonly Feature Search = new Feature("Loading/ScreenWrapLookupCache");
        private static ScreenWrapping cached;
        private static bool cachedNull;
        private static int frame = -1, generation, cachedGeneration = -1;
        private static long searches, answered;

        internal static void Install()
        {
            Search.Restore = delegate { SceneManager.sceneLoaded -= Loaded; SceneManager.sceneUnloaded -= Unloaded; Invalidate(); };
            foreach (string name in new[] { "Awake", "OnEnable", "OnDisable" })
                Search.Patch(Reflect.DeclaredMethod(typeof(ScreenWrapping), name), typeof(SceneSearchHooks), null, "Changed", null, null);
            Search.Patch(Reflect.DeclaredMethod(typeof(Projectile), "Awake"), typeof(SceneSearchHooks), null, null, "UseCache", null);
            Search.Patch(Reflect.DeclaredMethod(typeof(HockeyShooter), "Start"), typeof(SceneSearchHooks), null, null, "UseCache", null);
            SceneManager.sceneLoaded += Loaded;
            SceneManager.sceneUnloaded += Unloaded;
        }

        private static void Loaded(Scene scene, LoadSceneMode mode) { Invalidate(); }
        private static void Unloaded(Scene scene) { Invalidate(); }
        private static void Changed() { Invalidate(); }
        private static void Invalidate() { unchecked { generation++; } cached = null; cachedNull = false; }

        internal static IEnumerable<CodeInstruction> UseCache(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int count = 0;
            foreach (CodeInstruction instruction in code)
            {
                MethodInfo method = instruction.operand as MethodInfo;
                if (instruction.opcode != OpCodes.Call || method == null || !method.IsGenericMethod || method.Name != "FindObjectOfType" ||
                    method.DeclaringType != typeof(UnityEngine.Object) || method.GetGenericArguments()[0] != typeof(ScreenWrapping) || method.GetParameters().Length != 0) continue;
                instruction.operand = Reflect.DeclaredMethod(typeof(SceneSearchHooks), "FindScreenWrapping");
                count++;
            }
            Guards.Count(count, 1, "ScreenWrapping scene search");
            return code;
        }

        // Same result as FindObjectOfType<ScreenWrapping>() for every call within one frame.
        private static ScreenWrapping FindScreenWrapping()
        {
            if (!Search.Active) return UnityEngine.Object.FindObjectOfType<ScreenWrapping>();
            try
            {
                int now = Time.frameCount;
                if (now == frame && cachedGeneration == generation)
                {
                    if (cachedNull) { answered++; return null; }
                    // A cached hit must still be a live, active object (FindObjectOfType skips inactive ones).
                    if (cached != null && cached.gameObject.activeInHierarchy) { answered++; return cached; }
                }
                ScreenWrapping found = UnityEngine.Object.FindObjectOfType<ScreenWrapping>();
                searches++;
                frame = now; cachedGeneration = generation; cached = found; cachedNull = found == null;
                return found;
            }
            catch (Exception e) { Search.Fail(e); return UnityEngine.Object.FindObjectOfType<ScreenWrapping>(); }
        }

        internal static string Status() { return "searches=" + searches + " answeredFromCache=" + answered; }
    }
}
