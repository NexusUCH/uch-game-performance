using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace UCHGamePerformance
{
    // Reuses projectile miss debris (Projectile.FixedUpdate "debris" Instantiate) instead of
    // Instantiate + Destroy per hit. Only prefabs whose whole lifecycle is known are pooled:
    // a ProjectileDebris root (optional Rigidbody2D) with SpriteRenderer/Animator/deleteSprite
    // children, where an animation event calls deleteSprite.DeleteSprite to destroy the root.
    // Everything else (player-hit debris, airplane debris with sounds, explosion modifier) stays vanilla.
    internal static class DebrisHooks
    {
        internal static readonly Feature Pooling = new Feature("Projectiles/DebrisPooling");
        private const int MaxFreePerPrefab = 64;

        private sealed class Part
        {
            public Part() { }
            internal Vector3 Position, Scale;
            internal Quaternion Rotation;
            internal bool Active;
            internal bool HasRenderer, Enabled, FlipX, FlipY;
            internal Sprite Sprite;
            internal Color Color;
            internal int Order;
        }

        private sealed class Pool
        {
            public Pool() { }
            internal bool Eligible;
            internal Part[] Parts;
            internal readonly Stack<Live> Free = new Stack<Live>();
        }

        private sealed class Live
        {
            public Live() { }
            internal int Id;
            internal GameObject Root;
            internal Pool Pool;
            internal Transform[] Parts;
            internal SpriteRenderer[] Renderers;
            internal Rigidbody2D Body;
            internal Transform Explosion;
            internal bool Free;
        }

        private static readonly Dictionary<int, Pool> Pools = new Dictionary<int, Pool>();
        private static readonly Dictionary<int, Live> Registry = new Dictionary<int, Live>();
        private static int nextSweep = 256;

        internal static void Install()
        {
            Pooling.Patch(Reflect.DeclaredMethod(typeof(Projectile), "FixedUpdate"), typeof(DebrisHooks), null, null, "SpawnSite", null);
            Pooling.Patch(Reflect.DeclaredMethod(typeof(deleteSprite), "DeleteSprite"), typeof(DebrisHooks), "BeforeDelete", null, null, null);
            Pooling.Restore = Clear;
        }

        internal static IEnumerable<CodeInstruction> SpawnSite(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            // Projectiles/ComponentCache transpiles the same method first; verify against vanilla after
            // reversing only its exact helper edits.
            List<CodeInstruction> code = new List<CodeInstruction>(input);
            Guards.Stream(Normalize(code, __originalMethod), __originalMethod);
            FieldInfo debris = Reflect.Field(typeof(Projectile), "debris");
            int count = 0;
            for (int i = 0; i < code.Count; i++)
            {
                MethodInfo method = code[i].operand as MethodInfo;
                if (method == null || method.Name != "Instantiate" || method.DeclaringType != typeof(UnityEngine.Object) || !method.IsGenericMethod) continue;
                // The prefab is the first argument: the nearest earlier ldfld of a Projectile GameObject field.
                FieldInfo source = null;
                for (int j = i - 1; j >= 0 && j >= i - 10 && source == null; j--)
                    if (code[j].opcode == OpCodes.Ldfld) { FieldInfo field = code[j].operand as FieldInfo; if (field != null && field.DeclaringType == typeof(Projectile) && field.FieldType == typeof(GameObject)) source = field; }
                if (source != debris) continue;
                code[i].opcode = OpCodes.Call;
                code[i].operand = Reflect.DeclaredMethod(typeof(DebrisHooks), "Spawn");
                count++;
            }
            Guards.Count(count, 1, "projectile debris spawn");
            return code;
        }

        private static List<CodeInstruction> Normalize(List<CodeInstruction> code, MethodBase original)
        {
            MethodInfo body = Reflect.DeclaredMethod(typeof(ProjectileHooks), "Body"), settings = Reflect.DeclaredMethod(typeof(ProjectileHooks), "Settings");
            List<CodeInstruction> shape = new List<CodeInstruction>();
            List<int> helpers = new List<int>();
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].opcode == OpCodes.Ldarg_0 && i + 1 < code.Count && (code[i + 1].operand as MethodInfo) == settings && code[i].blocks.Count == 0)
                {
                    CodeInstruction call = new CodeInstruction(code[i + 1]);
                    call.labels.InsertRange(0, code[i].labels);
                    helpers.Add(shape.Count); shape.Add(call); i++;
                    continue;
                }
                if ((code[i].operand as MethodInfo) == body) helpers.Add(shape.Count);
                shape.Add(new CodeInstruction(code[i]));
            }
            if (helpers.Count == 0) return shape;
            List<CodeInstruction> vanilla = Guards.ReadOriginal(original);
            if (vanilla.Count != shape.Count) throw new InvalidOperationException("Unexpected earlier FixedUpdate transpiler");
            foreach (int index in helpers)
            {
                MethodInfo expected = vanilla[index].operand as MethodInfo;
                bool isBody = (shape[index].operand as MethodInfo) == body;
                if (expected == null || (isBody ? !(expected.Name == "GetComponent" && expected.IsGenericMethod && expected.GetGenericArguments()[0] == typeof(Rigidbody2D)) : !Guards.Calls(vanilla[index], typeof(Modifiers), "GetInstance")))
                    throw new InvalidOperationException("Unexpected ComponentCache edit at " + index);
                shape[index].opcode = vanilla[index].opcode; shape[index].operand = expected;
            }
            return shape;
        }

        // Same signature and stack shape as Object.Instantiate<GameObject>(GameObject, Vector3, Quaternion).
        private static GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            Live reuse = null;
            Pool pool = null;
            if (Pooling.Active && prefab != null)
            {
                try
                {
                    // ProjectileDebris.Awake detaches the explosion particle when this modifier is on.
                    if (!Modifiers.GetInstance().ProjectilesExplode)
                    {
                        pool = PoolFor(prefab);
                        if (!pool.Eligible) pool = null;
                        else reuse = Take(pool);
                    }
                    if (reuse != null) { Reset(reuse, position, rotation); return reuse.Root; }
                }
                catch (Exception e)
                {
                    Pooling.Fail(e); pool = null;
                    if (reuse != null) { Forget(reuse); if (reuse.Root != null) UnityEngine.Object.Destroy(reuse.Root); }
                }
            }
            GameObject created = UnityEngine.Object.Instantiate(prefab, position, rotation);
            if (pool != null && Pooling.Active)
            {
                try { Register(created, pool); }
                catch (Exception e) { Pooling.Fail(e); }
            }
            return created;
        }

        private static Pool PoolFor(GameObject prefab)
        {
            Pool pool;
            int id = prefab.GetInstanceID();
            if (Pools.TryGetValue(id, out pool)) return pool;
            pool = new Pool();
            pool.Eligible = Inspect(prefab, pool);
            Pools.Add(id, pool);
            Feature.Log.LogInfo("Projectiles/DebrisPooling " + prefab.name + (pool.Eligible ? ": pooled" : ": vanilla (unrecognized lifecycle)"));
            return pool;
        }

        private static bool Inspect(GameObject prefab, Pool pool)
        {
            Transform root = prefab.transform;
            ProjectileDebris owner = prefab.GetComponent<ProjectileDebris>();
            if (owner == null || !prefab.activeSelf) return false;
            Transform explosion = owner.ExplosionParticle;
            if (explosion != null && (explosion.parent != root || explosion.gameObject.activeSelf)) return false;
            Transform[] parts = prefab.GetComponentsInChildren<Transform>(true);
            pool.Parts = new Part[parts.Length];
            bool returns = false;
            for (int i = 0; i < parts.Length; i++)
            {
                Transform t = parts[i];
                Part part = new Part();
                part.Position = t.localPosition; part.Rotation = t.localRotation; part.Scale = t.localScale; part.Active = t.gameObject.activeSelf;
                pool.Parts[i] = part;
                // The explosion subtree stays inactive and untouched while the modifier is off.
                if (explosion != null && t.IsChildOf(explosion)) continue;
                if (!t.gameObject.activeSelf) return false;
                foreach (Component c in t.GetComponents<Component>())
                {
                    if (c == null) return false;
                    if (c is Transform) continue;
                    SpriteRenderer renderer = c as SpriteRenderer;
                    if (renderer != null)
                    {
                        part.HasRenderer = true; part.Enabled = renderer.enabled; part.Sprite = renderer.sprite; part.Color = renderer.color;
                        part.FlipX = renderer.flipX; part.FlipY = renderer.flipY; part.Order = renderer.sortingOrder;
                        continue;
                    }
                    Animator animator = c as Animator;
                    if (animator != null) { if (animator.keepAnimatorStateOnDisable) return false; continue; }
                    if (c is deleteSprite) { if (t.parent != root) return false; returns = true; continue; }
                    if (t == root && (c is ProjectileDebris || c is Rigidbody2D)) continue;
                    return false;
                }
            }
            return returns;
        }

        private static Live Take(Pool pool)
        {
            while (pool.Free.Count > 0)
            {
                Live live = pool.Free.Pop();
                live.Free = false;
                if (live.Root != null && live.Root.transform.parent == null && (live.Explosion == null || live.Explosion.parent == live.Root.transform)) return live;
                Forget(live);
                if (live.Root != null) UnityEngine.Object.Destroy(live.Root);
            }
            return null;
        }

        private static void Register(GameObject created, Pool pool)
        {
            Transform[] parts = created.GetComponentsInChildren<Transform>(true);
            if (parts.Length != pool.Parts.Length) return;
            Live live = new Live();
            live.Id = created.GetInstanceID(); live.Root = created; live.Pool = pool; live.Parts = parts;
            live.Renderers = new SpriteRenderer[parts.Length];
            for (int i = 0; i < parts.Length; i++) if (pool.Parts[i].HasRenderer) live.Renderers[i] = parts[i].GetComponent<SpriteRenderer>();
            live.Body = created.GetComponent<Rigidbody2D>();
            ProjectileDebris owner = created.GetComponent<ProjectileDebris>();
            live.Explosion = owner == null ? null : owner.ExplosionParticle;
            if (Registry.Count >= nextSweep) Sweep();
            Registry[live.Id] = live;
        }

        // Explicit reset of everything a pooled instance's animators or rigidbody may have changed.
        private static void Reset(Live live, Vector3 position, Quaternion rotation)
        {
            Part[] snapshot = live.Pool.Parts;
            Transform root = live.Parts[0];
            root.SetPositionAndRotation(position, rotation);
            root.localScale = snapshot[0].Scale;
            for (int i = 1; i < snapshot.Length; i++)
            {
                Transform t = live.Parts[i];
                t.localPosition = snapshot[i].Position; t.localRotation = snapshot[i].Rotation; t.localScale = snapshot[i].Scale;
                if (t.gameObject.activeSelf != snapshot[i].Active) t.gameObject.SetActive(snapshot[i].Active);
            }
            for (int i = 0; i < snapshot.Length; i++)
            {
                SpriteRenderer renderer = live.Renderers[i];
                if (renderer == null) continue;
                renderer.sprite = snapshot[i].Sprite; renderer.color = snapshot[i].Color; renderer.flipX = snapshot[i].FlipX;
                renderer.flipY = snapshot[i].FlipY; renderer.sortingOrder = snapshot[i].Order; renderer.enabled = snapshot[i].Enabled;
            }
            live.Root.SetActive(true);
            if (live.Body != null) { live.Body.velocity = Vector2.zero; live.Body.angularVelocity = 0f; }
        }

        private static bool BeforeDelete(deleteSprite __instance)
        {
            if (!Pooling.Active) return true;
            try
            {
                Transform parent = __instance.transform.parent;
                Live live;
                if (parent == null || !Registry.TryGetValue(parent.gameObject.GetInstanceID(), out live) || live.Root != parent.gameObject) return true;
                // A second DeleteSprite on an already returned root: vanilla's repeated Destroy is a no-op.
                if (live.Free) return false;
                if (live.Pool.Free.Count >= MaxFreePerPrefab) { Forget(live); return true; }
                live.Root.SetActive(false);
                live.Free = true;
                live.Pool.Free.Push(live);
                return false;
            }
            catch (Exception e) { Pooling.Fail(e); return true; }
        }

        private static void Forget(Live live) { Registry.Remove(live.Id); }

        // Pooled instances belong to their scene and are destroyed with it; drop the dead references.
        private static void Sweep()
        {
            List<int> dead = new List<int>();
            foreach (KeyValuePair<int, Live> item in Registry) if (item.Value.Root == null) dead.Add(item.Key);
            foreach (int id in dead) Registry.Remove(id);
            foreach (Pool pool in Pools.Values)
            {
                if (pool.Free.Count == 0) continue;
                Live[] free = pool.Free.ToArray();
                pool.Free.Clear();
                for (int i = free.Length - 1; i >= 0; i--) if (free[i].Root != null) pool.Free.Push(free[i]);
            }
            nextSweep = Math.Max(256, Registry.Count * 2);
        }

        private static void Clear()
        {
            Registry.Clear();
            Pools.Clear();
        }
    }
}
