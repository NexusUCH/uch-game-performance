using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.3.0: plain placed blocks (exact type Placeable) only have Placeable.FixedUpdate as a per-frame message, yet
    // Unity pays a native dispatch for each of them every physics step. After a block's Start has run, this disables
    // the component (Placeable has no OnEnable/OnDisable/Update; the game never reads Placeable.enabled; collision
    // messages, coroutines and OnDestroy are unaffected) and one early FixedUpdate runs the same logic for all of them:
    // unparented blocks only need the per-step CanPlace cache reset; attached blocks run the original method.
    // 2.7.0: CPU/CentralPartTick extends this to the exact types MultipiecePart, Ice, HoneyPiece (barbed wire, ice and
    // glue pieces: ~1,900 native FixedUpdate calls per step on a full wire level) and their invisible parent
    // MultipieceBlock. All four inherit ActiveBlock's empty Act (their act loop is a no-op that CPU/SkipEmptyBlockLoops
    // already drops) and declare no Unity message of their own except MultipieceBlock.Update, which the ticker now
    // calls once per frame for every registered parent (through the patched method, so CPU/MultipieceUpdateGate still
    // applies). Disabling the component changes nothing else: ActiveBlock.OnEnable only starts that empty loop, the
    // game never reads a block's enabled flag, coroutines, collision messages and OnDestroy keep working. Verified by
    // reflection at install: any extra message on these types turns the option off.
    // 2.11.0: CPU/CentralActiveBlockTick extends this to the exact active-block types in ActiveTypeList, whose Act may do
    // real work (doors, movers, saws, plants, hydrants, ...). Each listed type declares no FixedUpdate, Update,
    // LateUpdate, OnEnable, OnDisable or OnDestroy of its own anywhere below ActiveBlock (checked by reflection at install;
    // a type that fails is dropped and counted in activeSkipped). Their act loop is started by ActiveBlock.OnEnable before
    // Start and is not affected by enabled=false (a coroutine, or a CPU/CentralBlockLoops runner entry). Placeable.FixedUpdate
    // only clears the block's own CanPlace flag and snaps an attached block back to its attach position; none of the listed
    // types reads that flag or overrides FixedUpdate, so running it from the early ticker instead of at order 0 is invisible.
    // Guarantees (the OnEnable double-loop trap):
    //  - Installs only while CPU/CentralBlockLoops is active (its OnEnable prefix is the re-enable guard).
    //  - A listed block's component is only switched back on (a) while its object is inactive, which runs no OnEnable,
    //    or (b) through ActDispatchHooks.QuietEnable, which skips the OnEnable body for that one call. So switching the
    //    option off (failure of this option, CPU/CentralPlaceableTick or CPU/CentralBlockLoops, shutdown) leaves each
    //    block with exactly the loop it had: never a second one, never none.
    //  - Object deactivation: vanilla kills the loop and restarts it on reactivation through OnEnable. A ticked block found
    //    inactive (by this ticker before each step, or by the central loop runner when it drops the dead loop) gets its
    //    component back at once while inactive, so the reactivation OnEnable runs natively and restarts the loop exactly
    //    as in vanilla; the block then stays native. Not covered: an off-and-on toggle that completes between two checks
    //    (no game code toggles a placed block's object); the runner entry then keeps the loop running instead of restarting it.
    //  - CPU/CentralBlockLoops failing at runtime switches this option off at the next step (quiet re-enable).
    // Feature order: CPU/CentralBlockLoops is constructed first (LoopOwner below), so at shutdown, which stops features in
    // reverse order, this option restores while the OnEnable guard is still patched.
    internal static class PlaceableTickHooks
    {
        private static readonly Feature LoopOwner = ActDispatchHooks.Dispatch;
        internal static readonly Feature Tick = new Feature("CPU/CentralPlaceableTick");
        internal static readonly Feature Parts = new Feature("CPU/CentralPartTick");
        internal static readonly Feature Actives = new Feature("CPU/CentralActiveBlockTick");
        // Message-free ActiveBlock types (2.11.0 review). Not listed: Coin/CoinIdol (OnEnable/OnDisable keep Coin.AllCoins),
        // the types with their own FixedUpdate or Update, AnimalCannon and MetroTrainMove (own OnDestroy), MovingLevelBlock
        // (MultipiecePart.OnDestroy), the multipiece parents (MultipieceBlock.Update), Lighter (its Start never calls
        // Placeable.Start, so the registration postfix would never run).
        private static readonly Type[] ActiveTypeList = {
            typeof(ActiveBlock), typeof(AutoDoor), typeof(Boulder), typeof(CraneMove), typeof(ElevatorMove), typeof(FireHydrant),
            typeof(FlippingBlock), typeof(HorizontalMovingPlatform), typeof(JetpackDispenser), typeof(KeyItem), typeof(LinearSaw),
            typeof(LockItem), typeof(MetalPlantMover), typeof(MovingPlatform), typeof(PunchingBlock), typeof(PunchingPlant),
            typeof(RotateBlock), typeof(SphynxMove), typeof(Surfboard), typeof(TentaclesManager), typeof(Thwomp), typeof(ToxicWaste),
            typeof(UpBlower), typeof(VolcanoRockMove), typeof(WaveMove), typeof(WaveMoveV2) };
        private static readonly string[] ActiveMessages = { "FixedUpdate", "Update", "LateUpdate", "OnEnable", "OnDisable", "OnDestroy" };
        private static readonly HashSet<Type> ActiveTypes = new HashSet<Type>();
        private static readonly List<ActiveBlock> ActiveList = new List<ActiveBlock>();
        private static readonly Dictionary<ActiveBlock, int> ActiveIndex = new Dictionary<ActiveBlock, int>();
        private static string activeRejected = "";
        private static int activeSkipped;
        private static long activeTicks, handedBack, heldDisabled;
        private static readonly List<Placeable> Blocks = new List<Placeable>();
        private static readonly Dictionary<Placeable, int> Index = new Dictionary<Placeable, int>();
        private static readonly List<MultipieceBlock> Multi = new List<MultipieceBlock>();
        private static readonly Dictionary<MultipieceBlock, int> MultiIndex = new Dictionary<MultipieceBlock, int>();
        private static readonly string[] Messages = { "FixedUpdate", "Update", "LateUpdate", "OnEnable", "OnDisable" };
        private static Action<Placeable> fixedUpdate;
        private static Action<Placeable> resetCache;
        private static Action<MultipieceBlock> multiUpdate;
        private static GameObject host;
        private static long partTicks, multiUpdates;
        // 2.10.0 (CPU/MultipieceUpdateGate extension): whole-loop skip, see RunUpdate.
        private static readonly List<MultipieceBlock> Sleeping = new List<MultipieceBlock>();
        private static Func<long> readEpoch;
        private static bool passValid;
        private static long passEpoch, loopSkips, fullPasses;
        private static int registrations, passRegistrations;
        private static GameControl.GamePhase passPhase;
        // 2.12.0 (slim ticker loop): the per-step work of a registered block is (1) clearing CanPlaceCheckThisFixedUpdate and
        // (2) for a block with a ParentPiece, the attach-position repair branch. The flag is private to Placeable and only
        // set to true in Placeable.CanPlace (and only read there and in FixedUpdate), so a postfix on CanPlace records every
        // block whose flag can be true; each step clears exactly those (Dirty) instead of touching all ~3,900 blocks.
        // ParentPiece is only set non-null by Placeable.AttachPiece and Placeable.DetachChildrenForPickup (postfixed) or
        // before registration (checked at registration); those blocks form Parented, the only entries that can need the
        // native method. Every RescanSteps steps a full pass verifies both assumptions: a registered block with the flag
        // still set right after the flush, or with a ParentPiece that Parented does not hold, means a writer was missed;
        // the ticker then switches for the rest of the session to the full 2.11.0 loop (legacy) and counts it.
        private sealed class RefComparer : IEqualityComparer<Placeable>
        {
            public bool Equals(Placeable a, Placeable b) { return ReferenceEquals(a, b); }
            public int GetHashCode(Placeable p) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p); }
        }
        private static readonly RefComparer ByRef = new RefComparer();
        private static readonly HashSet<Placeable> Dirty = new HashSet<Placeable>(ByRef);
        private static readonly List<Placeable> Parented = new List<Placeable>();
        private static readonly Dictionary<Placeable, int> ParentedIndex = new Dictionary<Placeable, int>(ByRef);
        private const int RescanSteps = 30;
        private static Func<Placeable, bool> readFlag;
        private static int rescanIn, lastNative, lastCleared;
        private static bool legacy;
        private static long rescans, rescanFinds, missedFlags;

        internal static void Install()
        {
            MethodInfo original = Reflect.DeclaredMethod(typeof(Placeable), "FixedUpdate");
            Guards.Method(original);
            FieldInfo flag = Reflect.Field(typeof(Placeable), "CanPlaceCheckThisFixedUpdate");
            if (flag == null || flag.FieldType != typeof(bool) || flag.DeclaringType != typeof(Placeable)) throw new InvalidOperationException("Placeable.CanPlaceCheckThisFixedUpdate changed");
            DynamicMethod call = new DynamicMethod("PlaceableTickFixedUpdate", typeof(void), new Type[] { typeof(Placeable) }, typeof(PlaceableTickHooks), true);
            ILGenerator il = call.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, original); il.Emit(OpCodes.Ret);
            fixedUpdate = (Action<Placeable>)call.CreateDelegate(typeof(Action<Placeable>));
            DynamicMethod clear = new DynamicMethod("PlaceableTickResetCache", typeof(void), new Type[] { typeof(Placeable) }, typeof(PlaceableTickHooks), true);
            il = clear.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stfld, flag); il.Emit(OpCodes.Ret);
            resetCache = (Action<Placeable>)clear.CreateDelegate(typeof(Action<Placeable>));
            DynamicMethod read = new DynamicMethod("PlaceableTickReadCache", typeof(bool), new Type[] { typeof(Placeable) }, typeof(PlaceableTickHooks), true);
            il = read.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, flag); il.Emit(OpCodes.Ret);
            readFlag = (Func<Placeable, bool>)read.CreateDelegate(typeof(Func<Placeable, bool>));
            MethodInfo canPlace = Reflect.DeclaredMethod(typeof(Placeable), "CanPlace");
            if (canPlace == null || canPlace.ReturnType != typeof(bool) || canPlace.GetParameters().Length != 0) throw new InvalidOperationException("Placeable.CanPlace changed");
            MethodInfo attach = Reflect.DeclaredMethod(typeof(Placeable), "AttachPiece");
            if (attach == null || attach.GetParameters().Length != 1 || attach.GetParameters()[0].Name != "p") throw new InvalidOperationException("Placeable.AttachPiece changed");
            Tick.Restore = Restore;
            Tick.Patch(canPlace, typeof(PlaceableTickHooks), null, "AfterCanPlace", null, null);
            Tick.Patch(attach, typeof(PlaceableTickHooks), null, "AfterAttach", null, null);
            Tick.Patch(Reflect.DeclaredMethod(typeof(Placeable), "DetachChildrenForPickup"), typeof(PlaceableTickHooks), null, "AfterPickupDetach", null, null);
            host = new GameObject("UCHGamePerformance.PlaceableTick");
            host.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<PlaceableTicker>();
            Tick.Patch(Reflect.DeclaredMethod(typeof(Placeable), "Start"), typeof(PlaceableTickHooks), null, "AfterStart", null, null);
            Tick.Patch(Reflect.DeclaredMethod(typeof(Placeable), "OnDestroy"), typeof(PlaceableTickHooks), null, "AfterDestroy", null, null);
        }

        // 2.7.0: rides on CPU/CentralPlaceableTick. Every assumption about the four part types is checked here.
        internal static void InstallParts()
        {
            if (!Tick.Active) throw new NotSupportedException("CPU/CentralPlaceableTick is off");
            MethodInfo baseAct = Reflect.DeclaredMethod(typeof(ActiveBlock), "Act");
            Guards.Method(baseAct);
            foreach (Type type in new Type[] { typeof(MultipiecePart), typeof(Ice), typeof(HoneyPiece), typeof(MultipieceBlock) })
            {
                if (!BlockLoopPolicy.InheritsEmptyAct(type, typeof(ActiveBlock), baseAct)) throw new InvalidOperationException(type.Name + " overrides Act");
                for (Type t = type; t != typeof(ActiveBlock); t = t.BaseType)
                    foreach (string message in Messages)
                    {
                        MethodInfo m = t.GetMethod(message, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        if (m != null && !(t == typeof(MultipieceBlock) && message == "Update")) throw new InvalidOperationException(t.Name + " declares " + message);
                    }
            }
            foreach (string message in Messages)
                if (message != "OnEnable" && typeof(ActiveBlock).GetMethod(message, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) != null)
                    throw new InvalidOperationException("ActiveBlock declares " + message);
            MethodInfo update = Reflect.DeclaredMethod(typeof(MultipieceBlock), "Update");
            if (update == null || update.ReturnType != typeof(void) || update.GetParameters().Length != 0) throw new InvalidOperationException("MultipieceBlock.Update changed");
            DynamicMethod call = new DynamicMethod("MultipieceTickUpdate", typeof(void), new Type[] { typeof(MultipieceBlock) }, typeof(PlaceableTickHooks), true);
            ILGenerator il = call.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, update); il.Emit(OpCodes.Ret);
            multiUpdate = (Action<MultipieceBlock>)call.CreateDelegate(typeof(Action<MultipieceBlock>));
            // 2.10.0: read CpuHooks' multipiece epoch (CpuHooks itself stays byte-identical); without it the loop never skips.
            FieldInfo epoch = Reflect.Field(typeof(CpuHooks), "multiEpoch");
            if (epoch != null && epoch.IsStatic && epoch.FieldType == typeof(long))
            {
                DynamicMethod read = new DynamicMethod("MultipieceEpoch", typeof(long), Type.EmptyTypes, typeof(PlaceableTickHooks), true);
                il = read.GetILGenerator(); il.Emit(OpCodes.Ldsfld, epoch); il.Emit(OpCodes.Ret);
                readEpoch = (Func<long>)read.CreateDelegate(typeof(Func<long>));
            }
            Parts.Restore = RestoreParts;
            host.AddComponent<MultipieceTicker>();
        }

        // 2.11.0: rides on CPU/CentralPlaceableTick and needs CPU/CentralBlockLoops (the re-enable guard).
        internal static void InstallActive()
        {
            if (!Tick.Active) throw new NotSupportedException("CPU/CentralPlaceableTick is off");
            if (!LoopOwner.Active || !ActDispatchHooks.GuardPatched) throw new NotSupportedException("CPU/CentralBlockLoops is off");
            Actives.Restore = RestoreActive;
            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            // The two base classes: Placeable has only FixedUpdate and OnDestroy, ActiveBlock only OnEnable and OnDestroy.
            foreach (string message in ActiveMessages)
            {
                if (message != "FixedUpdate" && message != "OnDestroy" && typeof(Placeable).GetMethod(message, declared, null, Type.EmptyTypes, null) != null)
                    throw new InvalidOperationException("Placeable declares " + message);
                if (message != "OnEnable" && message != "OnDestroy" && typeof(ActiveBlock).GetMethod(message, declared, null, Type.EmptyTypes, null) != null)
                    throw new InvalidOperationException("ActiveBlock declares " + message);
            }
            ActiveTypes.Clear();
            List<string> rejected = new List<string>();
            foreach (Type type in ActiveTypeList)
            {
                string reason = null;
                if (!typeof(ActiveBlock).IsAssignableFrom(type) || PartType(type)) reason = "not a plain active block";
                for (Type t = type; reason == null && t != typeof(ActiveBlock); t = t.BaseType)
                    foreach (string message in ActiveMessages)
                        if (t.GetMethod(message, declared, null, Type.EmptyTypes, null) != null) { reason = t.Name + "." + message; break; }
                if (reason == null) ActiveTypes.Add(type);
                else rejected.Add(type.Name + "(" + reason + ")");
            }
            activeSkipped = rejected.Count;
            activeRejected = string.Join(",", rejected.ToArray());
            if (rejected.Count != 0) Feature.Log.LogWarning(Actives.Name + ": left native: " + activeRejected);
            if (ActiveTypes.Count == 0) throw new InvalidOperationException("no eligible active block type");
        }

        private static bool PartType(Type type)
        {
            return type == typeof(MultipiecePart) || type == typeof(Ice) || type == typeof(HoneyPiece) || type == typeof(MultipieceBlock);
        }

        // Unity has already called Start for this instance; disabling now cannot postpone it.
        private static void AfterStart(Placeable __instance)
        {
            if (!Tick.Active) return;
            Type type = null;
            try
            {
                type = __instance.GetType();
                if (!__instance.enabled || Index.ContainsKey(__instance)) return;
                if (Actives.Active && ActiveTypes.Contains(type))
                {
                    ActiveBlock active = (ActiveBlock)__instance;
                    if (ActiveIndex.ContainsKey(active)) return;
                    ActiveIndex.Add(active, ActiveList.Count);
                    ActiveList.Add(active);
                    __instance.enabled = false;   // OnEnable already ran; its act loop keeps running (see the class comment)
                    return;
                }
                if (type != typeof(Placeable) && !(Parts.Active && PartType(type))) return;
                Index.Add(__instance, Blocks.Count);
                Blocks.Add(__instance);
                MultipieceBlock multi = __instance as MultipieceBlock;
                if (multi != null) { MultiIndex.Add(multi, Multi.Count); Multi.Add(multi); unchecked { registrations++; } }
                Dirty.Add(__instance);   // CanPlace may have run before Start; the first step clears it as native would
                if (!ReferenceEquals(__instance.ParentPiece, null)) AddParented(__instance);
                __instance.enabled = false;
            }
            // 2.7.1: a failure on a part type switches off only CPU/CentralPartTick.
            catch (Exception e)
            {
                if (type != null && ActiveTypes.Contains(type)) Actives.Fail(e);
                else if (type != null && PartType(type)) Parts.Fail(e);
                else Tick.Fail(e);
            }
        }

        private static void AfterDestroy(Placeable __instance)
        {
            try
            {
                int at;
                if (Index.TryGetValue(__instance, out at))
                {
                    // Order among Placeable FixedUpdates is unspecified natively and each touches only its own state.
                    int last = Blocks.Count - 1;
                    Placeable moved = Blocks[last];
                    Blocks[at] = moved; Index[moved] = at;
                    Blocks.RemoveAt(last); Index.Remove(__instance);
                    RemoveParented(__instance);
                    Dirty.Remove(__instance);
                }
                MultipieceBlock multi = __instance as MultipieceBlock;
                if (multi != null && MultiIndex.TryGetValue(multi, out at))
                {
                    int last = Multi.Count - 1;
                    MultipieceBlock moved = Multi[last];
                    Multi[at] = moved; MultiIndex[moved] = at;
                    Multi.RemoveAt(last); MultiIndex.Remove(multi);
                }
                ActiveBlock active = __instance as ActiveBlock;
                if (active != null && ActiveIndex.Count != 0) RemoveActive(active);
            }
            catch (Exception e) { Tick.Fail(e); }   // 2.7.1: never let an exception escape Placeable.OnDestroy
        }

        private static bool RemoveActive(ActiveBlock block)
        {
            int at;
            if (!ActiveIndex.TryGetValue(block, out at)) return false;
            int last = ActiveList.Count - 1;
            ActiveBlock moved = ActiveList[last];
            ActiveList[at] = moved; ActiveIndex[moved] = at;
            ActiveList.RemoveAt(last); ActiveIndex.Remove(block);
            return true;
        }

        // 2.11.0: called by the central loop runner when it drops a loop because the block's object is inactive.
        internal static void OwnerInactive(ActiveBlock block)
        {
            try
            {
                if (ActiveIndex.Count == 0 || !ActiveIndex.ContainsKey(block)) return;
                HandBack(block);
            }
            catch (Exception e) { Actives.Fail(e); }
        }

        // Only while the object is inactive: enabled=true then runs no OnEnable; the reactivation runs it natively.
        private static void HandBack(ActiveBlock block)
        {
            RemoveActive(block);
            block.enabled = true;
            handedBack++;
        }

        private static void RunActive()
        {
            if (!Actives.Active) return;
            try
            {
                if (!LoopOwner.Active) throw new InvalidOperationException("CPU/CentralBlockLoops stopped");
                for (int i = 0; i < ActiveList.Count; i++)
                {
                    ActiveBlock b = ActiveList[i];
                    // Native FixedUpdate is not sent to inactive objects; such a block goes back to native at once.
                    if (!b.gameObject.activeInHierarchy) { HandBack(b); i--; continue; }
                    if (ReferenceEquals(b.ParentPiece, null)) resetCache(b);
                    else fixedUpdate(b);
                }
                activeTicks++;
            }
            catch (Exception e) { Actives.Fail(e); }
        }

        // 2.12.0: Placeable.CanPlace postfix. The flag is set only inside CanPlace; filtering happens at the flush.
        private static void AfterCanPlace(Placeable __instance)
        {
            if (!Tick.Active) return;
            try { if (!ReferenceEquals(__instance, null)) Dirty.Add(__instance); }
            catch (Exception e) { Tick.Fail(e); }
        }

        // 2.12.0: Placeable.AttachPiece(p) postfix (sets p.ParentPiece).
        private static void AfterAttach(Placeable p)
        {
            if (!Tick.Active) return;
            try { Track(p); }
            catch (Exception e) { Tick.Fail(e); }
        }

        // 2.12.0: Placeable.DetachChildrenForPickup postfix (sets ParentPiece = this on re-collected children).
        private static void AfterPickupDetach(Placeable __instance)
        {
            if (!Tick.Active) return;
            try
            {
                List<Placeable> children = __instance.ChildPieces;
                if (children == null) return;
                for (int i = 0; i < children.Count; i++) Track(children[i]);
            }
            catch (Exception e) { Tick.Fail(e); }
        }

        private static void Track(Placeable p)
        {
            if (ReferenceEquals(p, null) || ReferenceEquals(p.ParentPiece, null) || !Index.ContainsKey(p)) return;
            AddParented(p);
        }

        private static bool AddParented(Placeable p)
        {
            if (ParentedIndex.ContainsKey(p)) return false;
            ParentedIndex.Add(p, Parented.Count);
            Parented.Add(p);
            return true;
        }

        private static void RemoveParented(Placeable p)
        {
            int at;
            if (!ParentedIndex.TryGetValue(p, out at)) return;
            RemoveParentedAt(at);
        }

        private static void RemoveParentedAt(int at)
        {
            Placeable gone = Parented[at];
            int last = Parented.Count - 1;
            Placeable moved = Parented[last];
            Parented[at] = moved; ParentedIndex[moved] = at;
            Parented.RemoveAt(last); ParentedIndex.Remove(gone);
        }

        internal static void Run()
        {
            if (!Tick.Active) return;
            try
            {
                if (legacy) RunLegacy();
                else RunSlim();
                partTicks++;
            }
            catch (Exception e) { Tick.Fail(e); }
            RunActive();
        }

        // The 2.11.0 loop: every registered block every step.
        private static void RunLegacy()
        {
            int native = 0;
            for (int i = 0; i < Blocks.Count; i++)
            {
                Placeable p = Blocks[i];
                // Only attached blocks can take the relative-position repair branch, and that branch must not
                // run for inactive objects (native FixedUpdate would not be called for them).
                if (ReferenceEquals(p.ParentPiece, null)) resetCache(p);
                else if (p.gameObject.activeInHierarchy) { fixedUpdate(p); native++; }
            }
            Dirty.Clear();
            lastNative = native; lastCleared = Blocks.Count - native;
        }

        private static void RunSlim()
        {
            // (1) Clear the flag of every registered block on which CanPlace ran since the last step (an inactive one
            // included: the unparented path has always done this since 2.3.0; the flag only caches CanPlace for one step).
            int cleared = 0;
            if (Dirty.Count != 0)
            {
                foreach (Placeable p in Dirty)
                    if (Index.ContainsKey(p)) { resetCache(p); cleared++; }
                Dirty.Clear();
            }
            // (2) Parented blocks: mirror the managed part of Placeable.FixedUpdate's repair condition
            // (!disabled && KeepRelativeAttachPosition && (bool)ParentPiece && !ParentPiece.MarkedForDestruction); when it is
            // false the native method would only clear the flag, done above. Otherwise the vanilla path: active object ->
            // the original FixedUpdate (transform.parent, localPosition, the snap).
            int native = 0;
            for (int i = 0; i < Parented.Count; i++)
            {
                Placeable p = Parented[i];
                Placeable parent = p.ParentPiece;
                if (ReferenceEquals(parent, null) || p == null) { RemoveParentedAt(i); i--; continue; }
                if (p.Disabled || !p.KeepRelativeAttachPosition || !parent || parent.MarkedForDestruction) continue;
                if (p.gameObject.activeInHierarchy) { fixedUpdate(p); native++; }
            }
            lastNative = native; lastCleared = cleared;
            // (3) Self-check of both assumptions (see the field comment).
            if (--rescanIn > 0) return;
            rescanIn = RescanSteps;
            rescans++;
            bool missed = false;
            for (int i = 0; i < Blocks.Count; i++)
            {
                Placeable p = Blocks[i];
                if (readFlag(p)) { resetCache(p); missedFlags++; missed = true; }
                if (!ReferenceEquals(p.ParentPiece, null) && AddParented(p))
                {
                    rescanFinds++; missed = true;
                    if (p.gameObject.activeInHierarchy) fixedUpdate(p);
                }
            }
            if (missed)
            {
                legacy = true;
                Feature.Log.LogWarning(Tick.Name + ": slim loop missed a writer (missedFlags=" + missedFlags + " rescanFinds=" + rescanFinds + "); full loop for the rest of the session.");
            }
        }

        // MultipieceBlock.Update for every registered parent block (native Update is only sent to active objects).
        // 2.10.0: with CPU/MultipieceUpdateGate active, every one of these calls is skipped by the gate's prefix unless
        // the multipiece epoch moved (a part or block marked for destruction, RemovePart, a part's OnDestroy), the block
        // is new (first call), or its Parts array, Separable flag or HoveredCursors list reference changed. Parts is only
        // reassigned by RemovePart (epoch), Separable is never written by game code, and the HoveredCursors field is only
        // assigned inside MultipieceBlock.Update itself (cursors only add/remove list entries, which the body never reads
        // beyond copying the list reference). So when the epoch is unchanged since the last full pass, no block was
        // registered since then and no block that was inactive in that pass has become active, every per-block call would
        // be skipped and the whole loop is skipped instead. The build phase (PLACE) and any phase change always run the
        // full loop; without a game controller nothing is skipped. The epoch is read before the loop, so an epoch tick
        // during a pass (a block destroying itself) forces another full pass next frame, as the per-block gate would.
        internal static void RunUpdate()
        {
            if (!Parts.Active) return;
            try
            {
                long epoch = 0;
                GameControl.GamePhase phase = GameControl.GamePhase.NONE;
                bool skippable = readEpoch != null && Plugin.Multipiece.Active && CurrentPhase(out phase);
                if (skippable)
                {
                    epoch = readEpoch();
                    if (passValid && epoch == passEpoch && registrations == passRegistrations && phase == passPhase &&
                        phase != GameControl.GamePhase.PLACE && !SleeperAwake()) { loopSkips++; return; }
                }
                int registered = registrations;
                Sleeping.Clear();
                for (int i = 0; i < Multi.Count; i++)
                {
                    MultipieceBlock m = Multi[i];
                    if (m == null) continue;
                    if (m.gameObject.activeInHierarchy) { multiUpdate(m); multiUpdates++; }
                    else Sleeping.Add(m);
                }
                fullPasses++;
                passValid = skippable; passEpoch = epoch; passRegistrations = registered; passPhase = phase;
            }
            catch (Exception e) { passValid = false; Parts.Fail(e); }
        }

        private static bool CurrentPhase(out GameControl.GamePhase phase)
        {
            phase = GameControl.GamePhase.NONE;
            LobbyManager lobby = LobbyManager.instance;
            if (lobby == null) return false;
            GameControl control = lobby.CurrentGameController;
            if (control == null) return false;
            phase = control.Phase;
            return true;
        }

        private static bool SleeperAwake()
        {
            for (int i = 0; i < Sleeping.Count; i++)
            {
                MultipieceBlock m = Sleeping[i];
                if (m != null && m.gameObject.activeInHierarchy) return true;
            }
            return false;
        }

        // 2.11.0: every listed block goes back to native ticking with exactly the act loop it has (class comment).
        private static void RestoreActive()
        {
            List<ActiveBlock> all = new List<ActiveBlock>(ActiveList);
            ActiveList.Clear(); ActiveIndex.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                ActiveBlock b = all[i];
                if (b == null) continue;
                try
                {
                    if (!b.gameObject.activeInHierarchy) b.enabled = true;
                    else if (ActDispatchHooks.GuardPatched) ActDispatchHooks.QuietEnable(b);
                    else heldDisabled++;   // never re-enable without the guard: a second loop would start
                }
                catch (Exception e) { Feature.Log.LogWarning(Actives.Name + " restore: " + e.Message); }
            }
            if (heldDisabled != 0) Feature.Log.LogWarning(Actives.Name + ": " + heldDisabled + " blocks left disabled (no re-enable guard).");
        }

        private static void Restore()
        {
            if (Actives.Active) Actives.Fail(new InvalidOperationException("CPU/CentralPlaceableTick stopped"));
            else RestoreActive();
            for (int i = 0; i < Blocks.Count; i++) if (Blocks[i] != null) Blocks[i].enabled = true;
            Blocks.Clear(); Index.Clear(); Multi.Clear(); MultiIndex.Clear(); Sleeping.Clear(); passValid = false;
            Dirty.Clear(); Parented.Clear(); ParentedIndex.Clear();
            if (host != null) host.SetActive(false);
        }

        // Only the part types go back to native ticking; plain blocks stay with CPU/CentralPlaceableTick.
        private static void RestoreParts()
        {
            int write = 0;
            for (int i = 0; i < Blocks.Count; i++)
            {
                Placeable p = Blocks[i];
                if (ReferenceEquals(p, null)) continue;
                if (p == null) { Index.Remove(p); continue; }   // 2.7.1: destroyed instance leaves no stale index entry
                if (p.GetType() != typeof(Placeable)) { p.enabled = true; Index.Remove(p); continue; }
                Index[p] = write; Blocks[write++] = p;
            }
            Blocks.RemoveRange(write, Blocks.Count - write);
            Multi.Clear(); MultiIndex.Clear(); Sleeping.Clear(); passValid = false;
            // 2.12.0: the part types are native again (their own FixedUpdate clears their flag); rebuild Parented.
            Parented.Clear(); ParentedIndex.Clear();
            for (int i = 0; i < Blocks.Count; i++) if (!ReferenceEquals(Blocks[i].ParentPiece, null)) AddParented(Blocks[i]);
        }

        internal static string Status() { return "ticked=" + Blocks.Count + " flagOnly=" + (Blocks.Count - lastNative) + " native=" + lastNative + " parented=" + Parented.Count + " cleared=" + lastCleared + " dirty=" + Dirty.Count + " rescans=" + rescans + " rescanFinds=" + rescanFinds + " missedFlags=" + missedFlags + " legacy=" + legacy + " multi=" + Multi.Count + " partTicks=" + partTicks + " multiUpdates=" + multiUpdates + " fullPasses=" + fullPasses + " loopSkips=" + loopSkips + " sleeping=" + Sleeping.Count + " " + ActiveStatus(); }

        internal static string ActiveStatus() { return "activeTicked=" + ActiveList.Count + " activeSkipped=" + activeSkipped + " listTypes=" + ActiveTypes.Count + " activeTicks=" + activeTicks + " handedBack=" + handedBack + " heldDisabled=" + heldDisabled + (activeRejected.Length != 0 ? " rejected=" + activeRejected : ""); }

        // 2.11.0 lab helper: how many live central loop entries the ticked blocks have (each should have exactly one once
        // its first step has passed; 0 only during that first step or for NEVER-mode / empty-Act blocks).
        internal static string ActiveLoopCensus()
        {
            int zero = 0, one = 0, more = 0;
            for (int i = 0; i < ActiveList.Count; i++)
            {
                int n = ActDispatchHooks.LiveEntries(ActiveList[i]);
                if (n == 0) zero++; else if (n == 1) one++; else more++;
            }
            return "blocks=" + ActiveList.Count + " loops0=" + zero + " loops1=" + one + " loops2plus=" + more;
        }
    }

    [DefaultExecutionOrder(-1000)]
    internal sealed class PlaceableTicker : MonoBehaviour
    {
        private void FixedUpdate() { PlaceableTickHooks.Run(); }
    }

    // Late in the frame, after the cursors have updated their hover state for this frame.
    [DefaultExecutionOrder(1000)]
    internal sealed class MultipieceTicker : MonoBehaviour
    {
        private void Update() { PlaceableTickHooks.RunUpdate(); }
    }
}
