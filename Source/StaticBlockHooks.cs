using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using GameEvent;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.3.0: during the PLAY phase, plain placed blocks that provably never move get a Static body instead of a
    // never-sleeping Kinematic one. Unity then skips them in every physics step. Contacts with the player
    // (Dynamic), projectiles and moving blocks (Kinematic) are unchanged; only Static-vs-Static pairs vanish, and
    // eligible blocks carry no logic that senses other blocks. Every body returns to its recorded type before
    // anything can move or re-parent it, and a per-step transform watchdog reverts any body that moves anyway.
    // 2.4.1: the per-block structural audit (parent chain + component walk, ~28 ms for 2,800 blocks) is remembered
    // between play phases (Physics/StaticBlocksAuditCache; the cheap state checks still run live, the audit is
    // repeated in full every 16th play start and any difference turns the cache off), and the body switches are
    // spread over a few frames (Physics/StaticBlocksSliced, 300 per frame). Switching back stays immediate.
    // 2.5.0: Physics/StaticMultipieceBlocks widens the rule to the exact types MultipieceBlock, MultipiecePart, Ice and
    // HoneyPiece (barbed wire, ice, glue: ~1,900 of the 2,400 bodies still kinematic on a full wire map). They carry no
    // motion of their own (Act is the empty base, no Update that moves anything; Ice/HoneyPiece only hand out friction
    // and jump modifiers), their parts hang under the block they attached to, and the same ancestor/component audit
    // applies. Their indicator animations (Animator, AttachmentIndicator, CanAttachIndicator) and glue strands
    // (HoneySticker) are accepted only on collider-free child transforms, where they cannot move a fixture.
    internal static class StaticBlockHooks
    {
        internal static readonly Feature Static = new Feature("Physics/StaticBlocks");
        internal static readonly Feature AuditCache = new Feature("Physics/StaticBlocksAuditCache");
        internal static readonly Feature Sliced = new Feature("Physics/StaticBlocksSliced");
        internal static readonly Feature Wide = new Feature("Physics/StaticMultipieceBlocks");
        // 2.7.0: Physics/StaticActiveBlocks. Active blocks whose own script never moves their root body (reviewed in the
        // game code 2026-10-26: no MovePosition/MoveRotation/velocity/isKinematic/bodyType write on the root; the only
        // ActiveBlocks that move their root by code are CrumblingBlock and UnLockBox, movers carry base-motion modifiers
        // and are rejected by IsMobileBlock). Their animations move child colliders, which are fixtures of the root body
        // and are re-synced by Unity exactly as under a Kinematic body; contacts with the player (Dynamic) and with
        // projectiles are unchanged in Box2D for a non-moving body of either type. The component audit is skipped for
        // these roots (their scripts, animators and sound emitters are the reason they were rejected before); the
        // ancestor rule, the live state checks, every revert path and the watchdog apply unchanged. Blocks attached to
        // such a root (wire on a glove block) now pass the ancestor rule too. Exact types only.
        internal static readonly Feature Roots = new Feature("Physics/StaticActiveBlocks");
        // 2.7.3 (owner-found bug): Box2D never creates a contact between two Static bodies, but this game's Unity build does
        // create trigger contacts between two Kinematic bodies (measured live: K-K, K-S, S-K yes, S-S no; two solid
        // fixtures of non-dynamic bodies never touch). A boxing glove's fist (a trigger fixture of the glove's own body,
        // moved by its animation) that enters another glove's projectile trigger punches that glove in vanilla; with both
        // roots Static nothing happened.
        // 2.7.5: the rule actually needed is "every contact pair a script reacts to has a non-static partner", not
        // "colliders never move". Proof per type from the game code and prefab data (play-phase fixtures, layer matrix,
        // every OnTrigger/OnCollision handler on both sides): the reacting partners of springs, pressure spikes, beehives,
        // lighters, axes, saws, flipping blocks and punching plants are only players (Dynamic) and projectiles (non-static);
        // the axe arm, saw blade, flipping-block spike and bee swarm have their own Kinematic bodies; every other fixture
        // stays inside the block's own cell, where it cannot reach a teleporter's blocker trigger. Kept out: gloves (their
        // fist), auto doors (a one-way door's projectile trigger reacts to static layer-22 colliders such as Doomsday
        // meteors) and hydrants (geyser and cap reach a teleporter's blocker trigger). Hockey shooters and timed auto doors
        // also stay out by decision (negligible count; the hockey proof rests on an unobserved inference, and a per-field
        // door rule would break "exact types only").
        // 2.7.6 (friend-found bug, party mode): a rotating block stopped turning ("Cannot use 'MoveRotation' on a static
        // body") and wire attached to it must turn with it. Measured live: a Static body hanging under a body that moves
        // does not follow it (Unity keeps its world pose and rewrites its local pose), and the watchdog only compared the
        // world pose, so it never saw this. Three changes: (1) a live rule, run every play start and never cached:
        // nothing that a code-moved block (rotators, moving platforms, crumbling blocks, unlock boxes, Ferris wheels, any
        // block with base motion, rotation or treadmill) carries, hangs under, is glued to, is attached by, or shares a
        // game object with is converted, in either direction of the attachment; (2) the watchdog also reverts a body whose
        // local pose changed (its parent moved) and puts the local pose back; (3) a prefix on Rigidbody2D.MoveRotation /
        // MovePosition gives a body we converted its recorded type back before the call runs, and keeps that body out of
        // later play phases of the same scene (Movers, keyed by the body instance).
        private static readonly HashSet<Type> CodeMovers = new HashSet<Type> {
            typeof(RotateBlock), typeof(MovingPlatform), typeof(HorizontalMovingPlatform), typeof(CrumblingBlock), typeof(UnLockBox), typeof(FerrisWheel) };
        private static readonly HashSet<Type> RootTypes = new HashSet<Type> {
            typeof(ProjectileLauncher), typeof(AnimalCannon), typeof(Teleporter), typeof(Blackhole), typeof(UpBlower), typeof(GoalBlock),
            typeof(Spring), typeof(PressureTriggerSpikes), typeof(Beehive), typeof(Lighter), typeof(SwingingAxe), typeof(LinearSaw),
            typeof(FlippingBlock), typeof(PunchingPlant) };
        private static int rootBodies;
        private static readonly HashSet<string> WideBlocks = new HashSet<string> { "MultipieceBlock", "MultipiecePart", "Ice", "HoneyPiece" };
        private static readonly HashSet<string> WideVisual = new HashSet<string> { "Animator", "AttachmentIndicator", "CanAttachIndicator", "HoneySticker" };
        internal static MonoBehaviour Host;
        private const int SliceSize = 300;
        private const int FullAuditEvery = 16;
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
        private static int generation, watchdogReverts, flagsCleared, parentReverts, moveReverts, moverLinked, moveWarnings, guards;
        private static readonly HashSet<Placeable> Linked = new HashSet<Placeable>(new ByReference());
        private static readonly HashSet<Rigidbody2D> Movers = new HashSet<Rigidbody2D>(new BodyReference());
        private static readonly List<Placeable> Found = new List<Placeable>();

        private struct Converted
        {
            internal Rigidbody2D Body;
            internal Transform Transform;
            internal RigidbodyType2D Original;
            internal bool Parented;
            // 2.5.0: pose at conversion. Unity flags a transform (and its children) as changed when a parent's body
            // switches type in a later slice, although nothing moved; the watchdog now reverts only a real move.
            internal Vector3 Position;
            internal Quaternion Rotation;
            // 2.7.6: local pose at conversion. A Static child keeps its world pose when its parent moves, so only the
            // local pose shows that the parent moved.
            internal Vector3 LocalPosition;
            internal Quaternion LocalRotation;
        }

        private sealed class BodyReference : IEqualityComparer<Rigidbody2D>
        {
            public bool Equals(Rigidbody2D a, Rigidbody2D b) { return ReferenceEquals(a, b); }
            public int GetHashCode(Rigidbody2D value) { return RuntimeHelpers.GetHashCode(value); }
        }

        // Cached structural audit of one block: verdict plus the parent it was audited under.
        private struct Verdict
        {
            internal bool Ok;
            internal Transform Parent;
        }

        private sealed class ByReference : IEqualityComparer<Placeable>
        {
            public bool Equals(Placeable a, Placeable b) { return ReferenceEquals(a, b); }
            public int GetHashCode(Placeable value) { return RuntimeHelpers.GetHashCode(value); }
        }
        private static Dictionary<Placeable, Verdict> audits = new Dictionary<Placeable, Verdict>(new ByReference());
        private static Dictionary<Placeable, Verdict> previous;   // only during a full re-audit
        private static bool fullAudit;
        private static int conversions, cacheMismatches;
        private static long cacheHits, cacheAudits, slices;

        private sealed class Listener : IGameEventListener
        {
            public void handleEvent(GameEvent.GameEvent e)
            {
                if (!Static.Active) return;
                try
                {
                    StartPhaseEvent start = e as StartPhaseEvent;
                    if (start != null && start.Phase == GameControl.GamePhase.PLAY) return;
                    if (e is QuicksaverLevelFinishedLoading) audits.Clear();
                    // Any other phase start, any phase end, a free-play player switching modes, or a quicksave load.
                    RevertAll(false);
                }
                catch (Exception x) { Static.Fail(x); }
            }
        }

        internal static void Install()
        {
            if (Host == null) throw new InvalidOperationException("No coroutine host");
            Static.Restore = delegate { RevertAll(false); Subscribe(false); audits.Clear(); Movers.Clear(); Linked.Clear(); };
            Static.Patch(Reflect.DeclaredMethod(typeof(GameControl), "AfterAFixedUpdate"), typeof(StaticBlockHooks), null, "AfterAFixedUpdate", null, null);
            // Anything that can move, re-parent or pick up a block reverts every converted body first.
            Static.Patch(Reflect.DeclaredMethod(typeof(PiecePlacementCursor), "SetPiece"), typeof(StaticBlockHooks), "BeforePickup", null, null, null);
            Static.Patch(Reflect.DeclaredMethod(typeof(Placeable), "PickUp"), typeof(StaticBlockHooks), "BeforePickup", null, null, null);
            Static.Patch(Reflect.DeclaredMethod(typeof(Placeable), "ResetTransform"), typeof(StaticBlockHooks), "BeforePickup", null, null, null);
            Static.Patch(Reflect.DeclaredMethod(typeof(Placeable), "ApplyTransformFromMessage"), typeof(StaticBlockHooks), "BeforePickup", null, null, null);
            Static.Patch(Reflect.DeclaredMethod(typeof(QuickSaver), "RestoreSaveables"), typeof(StaticBlockHooks), "BeforePickup", null, null, null);
            Static.Patch(Reflect.DeclaredMethod(typeof(Placeable), "AttachPiece"), typeof(StaticBlockHooks), "BeforeRelink", null, null, null);
            Static.Patch(Reflect.DeclaredMethod(typeof(Placeable), "DetachPiece"), typeof(StaticBlockHooks), "BeforeRelink", null, null, null);
            InstallMoveGuards();
            Subscribe(true);
            Host.StartCoroutine(Watchdog());
        }

        // 2.7.6: one prefix per public MoveRotation / MovePosition overload, each installed on its own. The overloads are
        // small managed wrappers around internal calls in this Unity build; HarmonyX marks a patched method NoInlining, so
        // callers compiled after this point (block Act methods are compiled when a level first runs them) reach the prefix.
        // A failed overload only costs this safety net; the live rule in Choose does not depend on it.
        private static void InstallMoveGuards()
        {
            guards = 0;
            MethodInfo[] methods = typeof(Rigidbody2D).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != "MoveRotation" && m.Name != "MovePosition") continue;
                try
                {
                    Static.Patch(m, typeof(StaticBlockHooks), "BeforeMove", null, null, null);
                    guards++;
                }
                catch (Exception e)
                {
                    Feature.Log.LogWarning(Static.Name + ": no guard on Rigidbody2D." + m.Name + "(" + m.GetParameters()[0].ParameterType.Name + ") (" + e.GetType().Name + ": " + e.Message + "); the static-body rule still applies.");
                }
            }
            if (guards == 0) Feature.Log.LogWarning(Static.Name + ": no MoveRotation/MovePosition guard installed.");
        }

        // Runs before every MoveRotation/MovePosition in the game (players, cursors, platforms, rotators). The common case
        // returns after one list-count or one body-type read.
        private static void BeforeMove(Rigidbody2D __instance)
        {
            if (Bodies.Count == 0 || !Static.Active) return;
            try
            {
                if (__instance == null || __instance.bodyType != RigidbodyType2D.Static) return;
                for (int i = 0; i < Bodies.Count; i++)
                {
                    if (!ReferenceEquals(Bodies[i].Body, __instance)) continue;
                    __instance.bodyType = Bodies[i].Original;
                    Bodies.RemoveAt(i);
                    moveReverts++;
                    if (Movers.Add(__instance) && moveWarnings++ < 10)
                        Feature.Log.LogWarning(Static.Name + ": " + __instance.name + " is moved by code (MoveRotation/MovePosition); it stays out of static bodies in this scene.");
                    return;
                }
            }
            catch (Exception e) { Static.Fail(e); }
        }

        // 2.4.1 options: both ride on Physics/StaticBlocks and patch nothing themselves.
        internal static void InstallAuditCache()
        {
            if (!Static.Active) throw new NotSupportedException("Physics/StaticBlocks is off");
            AuditCache.Restore = delegate { audits.Clear(); previous = null; fullAudit = false; };
        }

        internal static void InstallSliced()
        {
            if (!Static.Active) throw new NotSupportedException("Physics/StaticBlocks is off");
        }

        // 2.5.0: rides on Physics/StaticBlocks and patches nothing itself. If it switches itself off, every body
        // converted under the wider rule goes back first and the cached verdicts (computed under that rule) are dropped.
        internal static void InstallWide()
        {
            if (!Static.Active) throw new NotSupportedException("Physics/StaticBlocks is off");
            Wide.Restore = delegate { RevertAll(false); audits.Clear(); previous = null; fullAudit = false; };
        }

        // 2.7.0: rides on Physics/StaticBlocks and patches nothing itself; same revert on failure as the wider rule.
        internal static void InstallRoots()
        {
            if (!Static.Active) throw new NotSupportedException("Physics/StaticBlocks is off");
            Roots.Restore = delegate { RevertAll(false); audits.Clear(); previous = null; fullAudit = false; };
        }

        internal static void SceneUnloaded() { audits.Clear(); previous = null; Movers.Clear(); Linked.Clear(); }

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
        // The parent chain of the blocks involved changes, so cached audits are dropped.
        private static void BeforeRelink()
        {
            if (!Static.Active) return;
            try
            {
                audits.Clear();
                if (Bodies.Count != 0) RevertAll(true);
            }
            catch (Exception e) { Static.Fail(e); }
        }

        private static bool Stale(GameControl control, int ticket)
        {
            return !Static.Active || ticket != generation || control == null || control.Phase != GameControl.GamePhase.PLAY;
        }

        private static IEnumerator ConvertLater(GameControl control, int ticket)
        {
            yield return FixedWait;
            yield return FixedWait;
            if (Stale(control, ticket)) yield break;
            List<Rigidbody2D> chosen = null;
            try { chosen = Choose(); }
            catch (Exception e) { Static.Fail(e); }
            if (chosen == null) yield break;
            int done = 0;
            while (done < chosen.Count)
            {
                if (Stale(control, ticket)) yield break;
                int end = Sliced.Active ? Math.Min(chosen.Count, done + SliceSize) : chosen.Count;
                bool failed = false;
                try { ConvertRange(chosen, done, end); }
                catch (Exception e) { Static.Fail(e); failed = true; }
                if (failed) yield break;
                done = end;
                if (done < chosen.Count) { slices++; yield return null; }
            }
            Feature.Log.LogInfo(Static.Name + ": " + Bodies.Count + " block bodies static for this play phase (watchdog reverts so far: " + watchdogReverts
                + ", parent reverts: " + parentReverts + ", move reverts: " + moveReverts + ", mover-linked blocks: " + moverLinked + ").");
        }

        private static void RevertBodies()
        {
            for (int i = 0; i < Bodies.Count; i++)
                if (Bodies[i].Body != null && Bodies[i].Body.bodyType == RigidbodyType2D.Static) Bodies[i].Body.bodyType = Bodies[i].Original;
            Bodies.Clear();
        }

        private static void RevertAll(bool reconvert)
        {
            int ticket = ++generation;
            RevertBodies();
            if (reconvert)
            {
                GameControl control = UnityEngine.Object.FindObjectOfType<GameControl>();
                if (control != null && control.Phase == GameControl.GamePhase.PLAY) Host.StartCoroutine(ConvertLater(control, ticket));
            }
        }

        // The audit pass: every placed block's cheap state checks live, the structural audit from the cache when allowed.
        private static List<Rigidbody2D> Choose()
        {
            if (Bodies.Count != 0) RevertBodies();
            List<Placeable> all = Placeable.AllPlaceables;
            List<Rigidbody2D> chosen = new List<Rigidbody2D>();
            fullAudit = AuditCache.Active && (++conversions % FullAuditEvery) == 0;
            if (fullAudit) { previous = audits; audits = new Dictionary<Placeable, Verdict>(new ByReference()); }
            int mismatches = 0;
            rootBodies = 0;
            MarkMovers(all);
            for (int i = 0; i < all.Count; i++)
            {
                Rigidbody2D body = Eligible(all[i], ref mismatches);
                if (body != null) { chosen.Add(body); if (Root(all[i])) rootBodies++; }
            }
            if (fullAudit)
            {
                previous = null; fullAudit = false;
                if (mismatches != 0)
                {
                    cacheMismatches += mismatches;
                    AuditCache.Fail(new InvalidOperationException(mismatches + " cached block audits differed from a fresh audit"));
                }
            }
            return chosen;
        }

        private static void ConvertRange(List<Rigidbody2D> chosen, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                Rigidbody2D body = chosen[i];
                if (body == null || body.bodyType != RigidbodyType2D.Kinematic) continue;
                Converted c;
                c.Body = body; c.Transform = body.transform; c.Original = body.bodyType; c.Parented = c.Transform.parent != null;
                c.Body.bodyType = RigidbodyType2D.Static;
                c.Position = c.Transform.position; c.Rotation = c.Transform.rotation;
                c.LocalPosition = c.Transform.localPosition; c.LocalRotation = c.Transform.localRotation;
                c.Transform.hasChanged = false;
                Bodies.Add(c);
            }
        }

        private static bool Plain(Placeable p)
        {
            Type type = p.GetType();
            if (type == typeof(Placeable) || type == typeof(DecorativeItemBlock)) return true;
            return Wide.Active && (type == typeof(MultipieceBlock) || type == typeof(MultipiecePart) || type == typeof(Ice) || type == typeof(HoneyPiece));
        }

        private static bool Root(Placeable p)
        {
            return Roots.Active && RootTypes.Contains(p.GetType());
        }

        private static Rigidbody2D Eligible(Placeable p, ref int mismatches)
        {
            if (p == null || !(Plain(p) || Root(p)) || !p.Placed || p.PickedUp || p.MarkedForDestruction || p.Disabled || !p.gameObject.activeInHierarchy) return null;
            Rigidbody2D body = p.GetComponent<Rigidbody2D>();
            if (body == null || body.bodyType != RigidbodyType2D.Kinematic || !body.simulated) return null;
            // 2.7.6: the mover rule is checked live on every play start and never read from the audit cache.
            if (Linked.Contains(p) || (Movers.Count != 0 && Movers.Contains(body)) || !Unlinked(p)) return null;
            if (!AuditCache.Active) return Structure(p, body) ? body : null;
            Transform parent = p.transform.parent;
            Verdict cached;
            if (!fullAudit && audits.TryGetValue(p, out cached) && ReferenceEquals(cached.Parent, parent)) { cacheHits++; return cached.Ok ? body : null; }
            Verdict fresh;
            fresh.Ok = Structure(p, body); fresh.Parent = parent;
            cacheAudits++;
            if (fullAudit && previous != null && previous.TryGetValue(p, out cached) && ReferenceEquals(cached.Parent, parent) && cached.Ok != fresh.Ok) mismatches++;
            audits[p] = fresh;
            return fresh.Ok ? body : null;
        }

        // 2.7.6: a block that game code moves (its own script moves its body or transform, or it carries base motion,
        // rotation or treadmill modifiers).
        private static bool Mover(Placeable p)
        {
            return CodeMovers.Contains(p.GetType()) || p.IsMobileBlock;
        }

        // 2.7.6: live, every play start. Marks every block linked to a mover in either direction: on its game object,
        // anywhere under it, above it (transform parents and attachment parents), its attachment children, the blocks
        // that attach through it, its glue group, and the parts of a multi-piece mover (Ferris wheel).
        private static void MarkMovers(List<Placeable> all)
        {
            Linked.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                Placeable m = all[i];
                if (m == null || !Mover(m)) continue;
                MarkTree(m);
                for (Transform t = m.transform.parent; t != null; t = t.parent) { t.GetComponents(Found); MarkFound(); }
                Placeable up = m.ParentPiece;
                for (int n = 0; up != null && n < 256; n++) { Linked.Add(up); up = up.ParentPiece; }
                for (int k = 0; k < m.ChildPieces.Count; k++) if (m.ChildPieces[k] != null) MarkTree(m.ChildPieces[k]);
                foreach (Placeable by in m.AttachedBy) if (by != null) MarkTree(by);
                if (m.Group != null)
                    for (int k = 0; k < m.Group.links.Count; k++)
                    {
                        AttachmentLink link = m.Group.links[k];
                        if (link.Top != null && link.Top.Piece != null) MarkTree(link.Top.Piece);
                        if (link.Bottom != null && link.Bottom.Piece != null) MarkTree(link.Bottom.Piece);
                    }
                MultipieceBlock multi = m as MultipieceBlock;
                if (multi != null && multi.Parts != null)
                    for (int k = 0; k < multi.Parts.Length; k++) if (multi.Parts[k] != null) MarkTree(multi.Parts[k]);
            }
            moverLinked = Linked.Count;
        }

        private static void MarkTree(Placeable p)
        {
            p.GetComponentsInChildren(true, Found);
            MarkFound();
        }

        private static void MarkFound()
        {
            for (int i = 0; i < Found.Count; i++) if (Found[i] != null) Linked.Add(Found[i]);
            Found.Clear();
        }

        // 2.7.6: the same rule seen from the candidate, also live: no mover above it (transform or attachment parents), and
        // not in a glue group that a mover leads (the game gives a group a top parent only when a member is a mobile block).
        private static bool Unlinked(Placeable p)
        {
            for (Transform t = p.transform.parent; t != null; t = t.parent)
            {
                Placeable owner = t.GetComponent<Placeable>();
                if (owner != null && Mover(owner)) return false;
            }
            Placeable up = p.ParentPiece;
            for (int n = 0; up != null && n < 256; n++) { if (Mover(up)) return false; up = up.ParentPiece; }
            return p.Group == null || p.Group.TopParent == null;
        }

        // Structural audit (2.3.0 rule; 2.5.0 adds the wider type set): ancestors and the block's non-block descendants.
        private static bool Structure(Placeable p, Rigidbody2D body)
        {
            // Wider types only: a block that moves itself (base motion, rotation, treadmill) is never static.
            Type own = p.GetType();
            if (own != typeof(Placeable) && own != typeof(DecorativeItemBlock) && p.IsMobileBlock) return false;
            // Ancestors: only other plain blocks, non-moving active roots or bare transforms (nothing that could move this block).
            for (Transform t = p.transform.parent; t != null; t = t.parent)
            {
                Placeable owner = t.GetComponent<Placeable>();
                if (owner != null) { if (!(Plain(owner) || Root(owner)) || owner.IsMobileBlock) return false; continue; }
                t.GetComponents(Scratch);
                if (Scratch.Count != 1) return false;
            }
            // 2.7.0: a reviewed active root keeps its scripts, animators and emitters; only its body matters here.
            if (Root(p)) return true;
            // The block and its non-block descendants may only carry audited, motionless components.
            Stack.Clear(); Stack.Add(p.transform);
            while (Stack.Count != 0)
            {
                Transform t = Stack[Stack.Count - 1]; Stack.RemoveAt(Stack.Count - 1);
                t.GetComponents(Scratch);
                bool root = ReferenceEquals(t, p.transform), collider = false;
                for (int i = 0; i < Scratch.Count; i++) if (Scratch[i] is Collider2D) collider = true;
                for (int i = 0; i < Scratch.Count; i++)
                {
                    Component c = Scratch[i];
                    if (c == null) return false;
                    if (ReferenceEquals(c, body)) continue;
                    string name = c.GetType().Name;
                    if (!Allowed.Contains(name))
                    {
                        // 2.5.0: the wider block scripts themselves, and their visual-only helpers on collider-free children.
                        if (!Wide.Active) return false;
                        if (!WideBlocks.Contains(name) && (root || collider || !WideVisual.Contains(name))) return false;
                    }
                    CheckColliding check = c as CheckColliding;
                    if (check != null && check.dontTurnOffOnPlay) return false;
                }
                for (int i = 0; i < t.childCount; i++)
                {
                    Transform child = t.GetChild(i);
                    if (child.GetComponent<Placeable>() == null) Stack.Add(child);
                }
            }
            return true;
        }

        // Safety net: a converted block whose transform changes (anything the audit could not see) gets its
        // original body type back. Attached blocks (which move with a parent) are checked every step; root blocks
        // can only move through a direct transform write, and every audited writer is hooked, so they are
        // re-checked every 4th step to keep this net cheap. 2.5.0: a flagged transform whose pose is still exactly
        // the pose it had at conversion was not moved (Unity also flags children when a parent body changes type,
        // and 2.5.0 converts parents and attached pieces in slices); its flag is cleared and it stays static.
        // 2.7.6: unless its local pose changed: then its parent moved and Unity held the Static child in place, so it is
        // reverted and put back on the parent. Moves through MoveRotation / MovePosition never reach this check (the
        // call fails on a static body and nothing moves); BeforeMove covers those.
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
                            bool still = c.Transform.position == c.Position && c.Transform.rotation == c.Rotation;
                            if (still && c.Parented && (c.Transform.localPosition != c.LocalPosition || c.Transform.localRotation != c.LocalRotation))
                            {
                                // 2.7.6: the parent moved and Unity held this Static child at its world pose. Give the body its
                                // type back and put it back where it hangs on the parent, as a Kinematic child would be.
                                c.Body.bodyType = c.Original;
                                c.Transform.localPosition = c.LocalPosition;
                                c.Transform.localRotation = c.LocalRotation;
                                if (parentReverts++ == 0) Feature.Log.LogWarning(Static.Name + ": watchdog reverted a block whose parent moved (" + c.Body.name + ").");
                                continue;
                            }
                            if (still) { c.Transform.hasChanged = false; flagsCleared++; }
                            else
                            {
                                c.Body.bodyType = c.Original;
                                if (watchdogReverts++ == 0) Feature.Log.LogWarning(Static.Name + ": watchdog reverted a block that moved (" + c.Body.name + ").");
                                continue;
                            }
                        }
                        Bodies[write++] = c;
                    }
                    Bodies.RemoveRange(write, Bodies.Count - write);
                }
                catch (Exception e) { Static.Fail(e); }
            }
        }

        internal static string Status()
        {
            return "static=" + Bodies.Count + " watchdogReverts=" + watchdogReverts + " conversions=" + conversions + " cacheHits=" + cacheHits
                + " cacheAudits=" + cacheAudits + " cacheMismatches=" + cacheMismatches + " cached=" + audits.Count + " slices=" + slices + " wide=" + Wide.Active
                + " roots=" + Roots.Active + " rootBodies=" + rootBodies + " flagsCleared=" + flagsCleared + " parentReverts=" + parentReverts
                + " moveReverts=" + moveReverts + " movers=" + Movers.Count + " moverLinked=" + moverLinked + " moveGuards=" + guards;
        }
    }
}
