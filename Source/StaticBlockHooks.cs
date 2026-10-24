using System;
using System.Collections;
using System.Collections.Generic;
using GameEvent;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.3.0: during the PLAY phase, plain placed blocks that provably never move get a Static body instead of a
    // never-sleeping Kinematic one. Unity then skips them in every physics step. Contacts with the player
    // (Dynamic), projectiles and moving blocks (Kinematic) are unchanged; only Static-vs-Static pairs vanish, and
    // eligible blocks carry no logic that senses other blocks. Every body returns to its recorded type before
    // anything can move or re-parent it, and a per-step transform watchdog reverts any body that moves anyway.
    internal static class StaticBlockHooks
    {
        internal static readonly Feature Static = new Feature("Physics/StaticBlocks");
        internal static MonoBehaviour Host;
        private static readonly WaitForFixedUpdate FixedWait = new WaitForFixedUpdate();
        private static readonly List<Converted> Bodies = new List<Converted>();
        private static readonly HashSet<string> Allowed = new HashSet<string> {
            "Transform", "RectTransform", "Placeable", "DecorativeItemBlock", "PlaceableMetadata", "CollisionTag", "CheckColliding",
            "ColliderModeControl", "BoxCollider2D", "CircleCollider2D", "PolygonCollider2D", "CapsuleCollider2D", "EdgeCollider2D",
            "SpriteRenderer", "CanvasGroupFader", "Canvas", "CanvasScaler", "CanvasGroup", "CanvasRenderer", "Image", "Text",
            "Localize", "LocalizationFontSizeSwitcher" };
        private static readonly List<Component> Scratch = new List<Component>();
        private static readonly List<Transform> Stack = new List<Transform>();
        private static Listener listener;
        private static int generation, watchdogReverts;

        private struct Converted
        {
            internal Rigidbody2D Body;
            internal Transform Transform;
            internal RigidbodyType2D Original;
            internal bool Parented;
        }

        private sealed class Listener : IGameEventListener
        {
            public void handleEvent(GameEvent.GameEvent e)
            {
                if (!Static.Active) return;
                try
                {
                    StartPhaseEvent start = e as StartPhaseEvent;
                    if (start != null && start.Phase == GameControl.GamePhase.PLAY) return;
                    // Any other phase start, any phase end, a free-play player switching modes, or a quicksave load.
                    RevertAll(false);
                }
                catch (Exception x) { Static.Fail(x); }
            }
        }

        internal static void Install()
        {
            if (Host == null) throw new InvalidOperationException("No coroutine host");
            Static.Restore = delegate { RevertAll(false); Subscribe(false); };
            Static.Patch(Reflect.DeclaredMethod(typeof(GameControl), "AfterAFixedUpdate"), typeof(StaticBlockHooks), null, "AfterAFixedUpdate", null, null);
            // Anything that can move, re-parent or pick up a block reverts every converted body first.
            Static.Patch(Reflect.DeclaredMethod(typeof(PiecePlacementCursor), "SetPiece"), typeof(StaticBlockHooks), "BeforePickup", null, null, null);
            Static.Patch(Reflect.DeclaredMethod(typeof(Placeable), "PickUp"), typeof(StaticBlockHooks), "BeforePickup", null, null, null);
            Static.Patch(Reflect.DeclaredMethod(typeof(Placeable), "ResetTransform"), typeof(StaticBlockHooks), "BeforePickup", null, null, null);
            Static.Patch(Reflect.DeclaredMethod(typeof(Placeable), "ApplyTransformFromMessage"), typeof(StaticBlockHooks), "BeforePickup", null, null, null);
            Static.Patch(Reflect.DeclaredMethod(typeof(QuickSaver), "RestoreSaveables"), typeof(StaticBlockHooks), "BeforePickup", null, null, null);
            Static.Patch(Reflect.DeclaredMethod(typeof(Placeable), "AttachPiece"), typeof(StaticBlockHooks), "BeforeRelink", null, null, null);
            Static.Patch(Reflect.DeclaredMethod(typeof(Placeable), "DetachPiece"), typeof(StaticBlockHooks), "BeforeRelink", null, null, null);
            Subscribe(true);
            Host.StartCoroutine(Watchdog());
        }

        private static void Subscribe(bool adding)
        {
            if (listener == null) listener = new Listener();
            GameEventManager.ChangeListener<StartPhaseEvent>(listener, adding);
            GameEventManager.ChangeListener<EndPhaseEvent>(listener, adding);
            GameEventManager.ChangeListener<FreePlayPlayerSwitchEvent>(listener, adding);
            GameEventManager.ChangeListener<QuicksaverLevelFinishedLoading>(listener, adding);
        }

        // Runs after glue attachment and the RunPhase collider switch; convert two fixed steps later.
        private static void AfterAFixedUpdate(GameControl __instance)
        {
            if (!Static.Active) return;
            try { if (__instance.Phase == GameControl.GamePhase.PLAY) Host.StartCoroutine(ConvertLater(__instance, ++generation)); }
            catch (Exception e) { Static.Fail(e); }
        }

        private static void BeforePickup()
        {
            if (!Static.Active) return;
            try { RevertAll(false); }
            catch (Exception e) { Static.Fail(e); }
        }

        // Attach/detach during play (glue links, network piece destruction): revert, then re-evaluate later.
        private static void BeforeRelink()
        {
            if (!Static.Active || Bodies.Count == 0) return;
            try { RevertAll(true); }
            catch (Exception e) { Static.Fail(e); }
        }

        private static IEnumerator ConvertLater(GameControl control, int ticket)
        {
            yield return FixedWait;
            yield return FixedWait;
            if (!Static.Active || ticket != generation || control == null || control.Phase != GameControl.GamePhase.PLAY) yield break;
            try { Convert(); }
            catch (Exception e) { Static.Fail(e); }
        }

        private static void RevertAll(bool reconvert)
        {
            int ticket = ++generation;
            for (int i = 0; i < Bodies.Count; i++)
                if (Bodies[i].Body != null && Bodies[i].Body.bodyType == RigidbodyType2D.Static) Bodies[i].Body.bodyType = Bodies[i].Original;
            Bodies.Clear();
            if (reconvert)
            {
                GameControl control = UnityEngine.Object.FindObjectOfType<GameControl>();
                if (control != null && control.Phase == GameControl.GamePhase.PLAY) Host.StartCoroutine(ConvertLater(control, ticket));
            }
        }

        private static void Convert()
        {
            if (Bodies.Count != 0) RevertAll(false);
            List<Placeable> all = Placeable.AllPlaceables;
            List<Rigidbody2D> chosen = new List<Rigidbody2D>();
            for (int i = 0; i < all.Count; i++)
            {
                Rigidbody2D body = Eligible(all[i]);
                if (body != null) chosen.Add(body);
            }
            for (int i = 0; i < chosen.Count; i++)
            {
                Converted c;
                c.Body = chosen[i]; c.Transform = chosen[i].transform; c.Original = chosen[i].bodyType; c.Parented = c.Transform.parent != null;
                c.Body.bodyType = RigidbodyType2D.Static;
                c.Transform.hasChanged = false;
                Bodies.Add(c);
            }
            Feature.Log.LogInfo(Static.Name + ": " + Bodies.Count + " block bodies static for this play phase (watchdog reverts so far: " + watchdogReverts + ").");
        }

        private static bool Plain(Placeable p)
        {
            Type type = p.GetType();
            return type == typeof(Placeable) || type == typeof(DecorativeItemBlock);
        }

        private static Rigidbody2D Eligible(Placeable p)
        {
            if (p == null || !Plain(p) || !p.Placed || p.PickedUp || p.MarkedForDestruction || p.Disabled || !p.gameObject.activeInHierarchy) return null;
            Rigidbody2D body = p.GetComponent<Rigidbody2D>();
            if (body == null || body.bodyType != RigidbodyType2D.Kinematic || !body.simulated) return null;
            // Ancestors: only other plain blocks or bare transforms (nothing that could move this block).
            for (Transform t = p.transform.parent; t != null; t = t.parent)
            {
                Placeable owner = t.GetComponent<Placeable>();
                if (owner != null) { if (!Plain(owner) || owner.IsMobileBlock) return null; continue; }
                t.GetComponents(Scratch);
                if (Scratch.Count != 1) return null;
            }
            // The block and its non-block descendants may only carry audited, motionless components.
            Stack.Clear(); Stack.Add(p.transform);
            while (Stack.Count != 0)
            {
                Transform t = Stack[Stack.Count - 1]; Stack.RemoveAt(Stack.Count - 1);
                t.GetComponents(Scratch);
                for (int i = 0; i < Scratch.Count; i++)
                {
                    Component c = Scratch[i];
                    if (c == null) return null;
                    if (ReferenceEquals(c, body)) continue;
                    if (!Allowed.Contains(c.GetType().Name)) return null;
                    CheckColliding check = c as CheckColliding;
                    if (check != null && check.dontTurnOffOnPlay) return null;
                }
                for (int i = 0; i < t.childCount; i++)
                {
                    Transform child = t.GetChild(i);
                    if (child.GetComponent<Placeable>() == null) Stack.Add(child);
                }
            }
            return body;
        }

        // Safety net: a converted block whose transform changes (anything the audit could not see) gets its
        // original body type back. Attached blocks (which move with a parent) are checked every step; root blocks
        // can only move through a direct transform write, and every audited writer is hooked, so they are
        // re-checked every 4th step to keep this net cheap.
        private static IEnumerator Watchdog()
        {
            int step = 0;
            while (true)
            {
                yield return FixedWait;
                if (!Static.Active || Bodies.Count == 0) continue;
                try
                {
                    bool roots = (++step & 3) == 0;
                    int write = 0;
                    for (int i = 0; i < Bodies.Count; i++)
                    {
                        Converted c = Bodies[i];
                        if (c.Body == null) continue;
                        if ((roots || c.Parented) && c.Transform.hasChanged)
                        {
                            c.Body.bodyType = c.Original;
                            if (watchdogReverts++ == 0) Feature.Log.LogWarning(Static.Name + ": watchdog reverted a block that moved (" + c.Body.name + ").");
                            continue;
                        }
                        Bodies[write++] = c;
                    }
                    Bodies.RemoveRange(write, Bodies.Count - write);
                }
                catch (Exception e) { Static.Fail(e); }
            }
        }

        internal static string Status() { return "static=" + Bodies.Count + " watchdogReverts=" + watchdogReverts; }
    }
}
