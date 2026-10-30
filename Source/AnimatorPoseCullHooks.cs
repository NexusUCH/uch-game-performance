using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.6.0: after Graphics/CullInvisibleAnimators (2.5.0) the animators left on a full level are the ones that drive
    // gameplay (Eternity, 8VKK-7G4K: ~300 enabled, 5-13 of them on screen). Unity still evaluates every one of them every
    // frame and writes their poses into transforms the physics step then has to re-sync. Unity's culling mode
    // "CullUpdateTransforms" keeps the state machine, the parameters and the animation events running while no renderer
    // of the animator is visible and only skips the pose/property writes. Verified live (lab profile, 2026-10-26): an
    // off-screen boxing glove culled this way finished its punch in 200 frames with EnableHazard/DisableHazard/
    // OnPunchAnimFinished firing; an off-screen spike's state time advanced while its hazard collider stayed unwritten.
    //
    // Two reviewed groups, each its own option:
    //  * Graphics/CullOffscreenAnimatorPoses: controllers whose clips never write a collider pose or state (measured by
    //    sampling every Collider2D below the animator while the clips played): arrow shooters (the arrow spawns from a
    //    fixed offset of the block root, the clip only draws the bow; ShootProjectile stays an event), animal cannons,
    //    teleporters, beehives, blowers, fans, crumble blocks (collider switches and the fall are script code), treadmills
    //    (the belt parts are positioned by script), punching plants. Off screen, only pixels nobody sees are skipped.
    //  * Graphics/CullOffscreenMoverAnimators: controllers whose clips do move or switch colliders (boxing glove arm, saw
    //    blade, spikes, flipping block, swinging axe, lighter flame, both door types). Off screen the collider keeps the
    //    pose it had when it left the view and snaps to the live pose the moment the block is back in view. 2.6.2: a
    //    mover is also kept live while any player or any live projectile is within its collider reach (MoverReach,
    //    View.NearInterest), so an off-screen arrow, puck or cannon animal meets the same collider pose as in vanilla;
    //    only movers that nothing can touch are frozen. Owner's decision (2026-10-26).
    //
    // Unity decides "visible" from the animator's renderers, which also fails for a block in view whose sprites are
    // switched off or blank, so this hook does not rely on it alone: a view loop keeps every tracked animator inside the
    // camera rectangle (plus a 12-unit margin) in its original mode and culls only animators outside it. The gameplay
    // camera is the game's own ZoomCamera.CurrentZoomCamera (UCH's Camera.main is the UI camera); it follows the local
    // players ("local cam") or all players ("all cam"), and the rectangle is read after the camera's own FixedUpdate in
    // the same frame, so a switch between the two modes un-culls in the frame the view changes (2.6.2). If the camera
    // is missing or no living player is inside its rectangle, nothing is culled that frame.
    internal static class AnimatorPoseCullHooks
    {
        internal static readonly Feature Poses = new Feature("Graphics/CullOffscreenAnimatorPoses");
        internal static readonly Feature Movers = new Feature("Graphics/CullOffscreenMoverAnimators");
        internal static MonoBehaviour Host;
        internal const float Margin = 12f;
        private const int PerFrame = 400;
        // Reviewed controllers (Eternity census + game code, 2026-10-26). Names are the runtime controller names.
        private static readonly HashSet<string> EventOnly = new HashSet<string> {
            "ArrowShooter", "AnimalCannonController", "teleporter_W83_143_0", "BeehiveController", "UpBlowerAnimator",
            "spinningFan", "CrumblingBlockAnimator", "TreadMill.000", "PunchingPlant_Controller" };
        private static readonly HashSet<string> MoverList = new HashSet<string> {
            "BoxingGlove-Punch", "LinearSaw", "PressureTriggerAnimator", "FlippingBlock", "SwingingAxe", "Lighter",
            "autoDoor1x4", "Oneway Door-OnewayDoor1x4.000" };
        // 2.6.2: how far any enabled collider below a mover animator reaches from the animator's transform, measured
        // live on Eternity (view-check.cs census: glove 7.7, axe 6.7, saw 3.5, one-way door 3.2, auto door 2.5, lighter
        // 2.3, spikes 2.0, flipping block 1.1) plus 2 units of slack; a projectile within this reach (plus its own
        // travel radius) keeps the mover live. Controllers not listed here fall back to the 12-unit margin.
        private static readonly Dictionary<string, float> MoverReach = new Dictionary<string, float> {
            { "BoxingGlove-Punch", 10f }, { "SwingingAxe", 9f }, { "LinearSaw", 6f }, { "Oneway Door-OnewayDoor1x4.000", 6f },
            { "autoDoor1x4", 5f }, { "Lighter", 5f }, { "PressureTriggerAnimator", 4f }, { "FlippingBlock", 4f } };
        // 2.7.4 (owner-found bug): a mover whose moving collider can set off ANOTHER BLOCK must play its whole animation
        // wherever it is, not only while a player or projectile is near. Layer facts measured live 2026-10-26: the block
        // receivers that react to any collider (glove projectile triggers, cannon projectile detector, crumble and door
        // projectile triggers, glove blockers) sit on layer 29 ProjectileTrigger, which collides only with layer 22
        // Particles (projectiles) and the unnamed layers 3/6/7. Among mover colliders only the boxing glove's fist
        // "ProjectileBox" is on 22; saw blades, spikes, doors, axes and lighters are on 9/18, which never reach 29. So a
        // glove punching off screen (hit by a far arrow, or by another glove) must move its fist, or the chain of gloves,
        // doors and crumble blocks it would have set off in vanilla stops. Such a "chain mover" is checked every frame,
        // not per slice: while its animator is not in the idle state it recorded when it was adopted, it stays live.
        private static readonly HashSet<string> ChainMovers = new HashSet<string> { "BoxingGlove-Punch" };

        // 2.8.0: Graphics/IdleAnimatorGate. Lab (Eternity, 2026-10-30): the ~227 culled gameplay animators still evaluate
        // their state machines every frame (0.3 ms/frame + 0.37 ms per physics step). Most of them sit in an idle state
        // whose clip has no animation event until a game script writes a parameter. While such an animator is culled by the
        // view decision above (off screen, nothing near a mover, chain glove not punching), is in its idle state, not in a
        // transition and has no trigger pending and every bool at its default, the component is switched off
        // (Animator.enabled = false, with keepAnimatorStateOnDisable = true for that time so the state machine state and the
        // parameters are kept). A prefix on every managed Animator parameter/playback wrapper switches it back on BEFORE the
        // game's write runs, so the write lands on a running animator exactly as in vanilla; from then on the normal cull
        // path takes over until the animator is idle and culled again at its next slice visit. Game code review
        // (PunchingBlock, CrumblingBlock, PressureTriggerSpikes, PunchingPlant, Beehive): every animator interaction is
        // SetTrigger / ResetTrigger / SetBool (patched) or a speed write (a stored property, harmless while off); nothing
        // reads enabled, state info, transitions or parameters of these animators, and no other game code touches their
        // components. Controller name -> idle clip (all clips verified event-free in the lab's clip table).
        // 2.8.1: AnimalCannonController removed from the list. Its controller has 3 layers, so Prepare never gated it and
        // it only logged a warning per instance (lab 2026-10-30); five block types remain.
        // 2.9.0: multi-layer controllers and per-controller expected bools. A gate entry lists one idle clip per layer and
        // optionally the bool values the idle state is reached with. Single-layer entries without expected bools take
        // exactly the 2.8.1 path (IsIdle below; their expected bools are the controller defaults as before).
        //  * AnimalCannonController (3 layers; lab-276: 25 on Eternity, all culled). Layer idle clips LaunchAnimalIdle
        //    (0.083 s loop), AddAnimalToCannonIdle (0 s), Empty (1 s), none with an event; the only event of the controller,
        //    SmookPoofSpawn@0.25, sits in LaunchAnimal. Game code (AnimalCannon): the animator is written only through
        //    SetTrigger("Add"/"Launch"/"ShootProjectile") (patched) and speed (stored property); the launch itself is timed
        //    by the script (ProcessChrsInCannon, fixedDeltaTime), not by the animation. Nothing reads its state, enabled or
        //    parameters.
        //  * teleporter_W83_143_0 (14 on Eternity, 11 culled). Idle clip onWait (0.5 s loop, no event), reached with On ==
        //    true (default false), so the entry expects On = true. Game code (Teleporter): SetBool("On"), SetTrigger
        //    Send/Receiving/Receive, ResetTrigger x3 (setReadyTrue, an event of turnOn, which has finished before onWait),
        //    speed writes; nothing reads the animator. A teleporter is also written by its PAIRED teleporter's script
        //    (Destination.portalAnimator.SetBool/SetTrigger("Receiving"/"Receive")): the wake prefix keys on the Animator
        //    instance that is written, so the receiving end wakes whoever's script makes the call. The ready flag and the
        //    teleport timing are script fields, not animator state. Character.Freeze(freezeAnimator) acts on the
        //    character's animator, not on the portal's. Both controllers stay in EventOnly (camera rectangle only).
        internal static readonly Feature Gate = new Feature("Graphics/IdleAnimatorGate");
        private sealed class GateSpec
        {
            internal readonly string[] IdleNames;     // idle clip per layer
            internal readonly string[] BoolNames;     // bools whose idle value differs from the controller default
            internal readonly bool[] BoolValues;
            internal GateSpec(string[] idleNames, string[] boolNames, bool[] boolValues) { IdleNames = idleNames; BoolNames = boolNames; BoolValues = boolValues; }
        }
        private static GateSpec Spec(string idle) { return new GateSpec(new string[] { idle }, new string[0], new bool[0]); }
        private static readonly Dictionary<string, GateSpec> GateIdle = new Dictionary<string, GateSpec> {
            { "BoxingGlove-Punch", Spec("BoxingGlove-Idle") }, { "CrumblingBlockAnimator", Spec("Hold") }, { "PressureTriggerAnimator", Spec("PressureTriggerHidden") },
            { "PunchingPlant_Controller", Spec("PunchingPlant_Idle") }, { "BeehiveController", Spec("BeehiveHold") },
            { "AnimalCannonController", new GateSpec(new string[] { "LaunchAnimalIdle", "AddAnimalToCannonIdle", "Empty" }, new string[0], new bool[0]) },
            { "teleporter_W83_143_0", new GateSpec(new string[] { "onWait" }, new string[] { "On" }, new bool[] { true }) } };
        // 2.9.0: a multi-layer controller whose idle hashes could not be learned after this many visits (summed over its
        // instances) is never gated; its visits count in skippedNoIdle.
        private const int MaxLearnVisits = 600;
        // Patched managed wrappers (Unity 2021.3.45 UnityEngine.AnimationModule). Internal-call overloads have no IL body
        // and cannot be pinned; every public managed overload funnels into them, and no game code calls them directly on a
        // gated controller.
        private static readonly string[] GateEntryPoints = {
            "SetTrigger", "ResetTrigger", "SetBool", "SetInteger", "SetFloat", "Play", "PlayInFixedTime", "CrossFade", "CrossFadeInFixedTime", "Rebind" };

        private sealed class GateInfo
        {
            internal string IdleName;
            internal int IdleHash;           // learned state hash of the idle state (0 = not yet seen)
            internal bool Prepared, Supported;
            internal int[] Triggers, Bools;
            internal bool[] BoolDefaults;    // 2.9.0: the expected value per bool (controller default unless the spec overrides it)
            // 2.9.0 multi-layer controllers (Layers > 1): one learned idle state hash per layer.
            internal GateSpec Spec;
            internal int Layers;
            internal int[] IdleHashes;
            internal bool Learned, Abandoned;
            internal int LearnVisits;
        }

        private static readonly Dictionary<string, GateInfo> GateInfos = new Dictionary<string, GateInfo>();
        private static readonly Dictionary<int, Entry> GatedMap = new Dictionary<int, Entry>();
        private static readonly List<AnimatorClipInfo> ClipScratch = new List<AnimatorClipInfo>();
        private static int gatedNow, gatePatched;
        private static long gates, wakes, foreignWakes, skippedNoIdle, skippedLayers;
        private static bool gateLogPending;
        private static float gateLogAt;

        private sealed class Entry
        {
            internal Animator Animator;
            internal Transform Transform;
            internal AnimatorCullingMode Original;
            internal float Reach;
            internal bool Mover, Culled, Chain;
            internal int IdleHash;
            // 2.8.0 idle gate
            internal GateInfo GateInfo;
            internal int Id;
            internal bool Gated, KeepOriginal;
        }

        private static readonly List<Entry> Tracked = new List<Entry>();
        private static readonly List<Entry> Chains = new List<Entry>();
        private static long chainWakes;
        private static readonly List<Animator> Scratch = new List<Animator>();
        private static int cursor, culledNow;
        private static long seen, adopted, culls, unculls;
        private static bool loopStarted;

        internal static void Install()
        {
            if (Host == null) throw new InvalidOperationException("No coroutine host");
            Poses.Restore = delegate { Restore(false); };
            Poses.Patch(Reflect.DeclaredMethod(typeof(Placeable), "Awake"), typeof(AnimatorPoseCullHooks), null, "AfterPlaceableAwake", null, null);
            if (!loopStarted) { loopStarted = true; Host.StartCoroutine(ViewLoop()); }
        }

        // Rides on Graphics/CullOffscreenAnimatorPoses and patches nothing itself.
        internal static void InstallMovers()
        {
            if (!Poses.Active) throw new NotSupportedException("Graphics/CullOffscreenAnimatorPoses is off");
            Movers.Restore = delegate { Restore(true); };
        }

        // Every block's animators are classified once when the block object awakes (prefab instance or level load).
        private static void AfterPlaceableAwake(Placeable __instance)
        {
            if (!Poses.Active) return;
            try
            {
                __instance.GetComponentsInChildren(true, Scratch);
                for (int i = 0; i < Scratch.Count; i++)
                {
                    Animator animator = Scratch[i];
                    seen++;
                    if (animator == null || animator.cullingMode != AnimatorCullingMode.AlwaysAnimate) continue;
                    RuntimeAnimatorController controller = animator.runtimeAnimatorController;
                    if (controller == null) continue;
                    bool mover;
                    if (EventOnly.Contains(controller.name)) mover = false;
                    else if (Movers.Active && MoverList.Contains(controller.name)) mover = true;
                    else continue;
                    // Unity never reports an animator without renderers as visible; such an animator would stay culled.
                    if (animator.GetComponentInChildren<Renderer>(true) == null) continue;
                    Entry entry = new Entry();
                    entry.Animator = animator;
                    entry.Transform = animator.transform;
                    entry.Original = animator.cullingMode;
                    entry.Mover = mover;
                    if (!mover || !MoverReach.TryGetValue(controller.name, out entry.Reach)) entry.Reach = Margin;
                    if (mover && ChainMovers.Contains(controller.name)) { entry.Chain = true; Chains.Add(entry); }
                    if (Gate.Active) entry.GateInfo = GateInfoFor(controller.name);
                    entry.Id = animator.GetInstanceID();
                    Tracked.Add(entry);
                    if (entry.GateInfo != null) { gateLogPending = true; gateLogAt = Time.realtimeSinceStartup + 5f; }
                    adopted++;
                }
                Scratch.Clear();
            }
            catch (Exception e) { Poses.Fail(e); }
        }

        // Resumes after every Update, before Unity evaluates the animators for the frame.
        private static IEnumerator ViewLoop()
        {
            while (true)
            {
                yield return null;
                if (!Poses.Active || Tracked.Count == 0) continue;
                try { Step(); }
                catch (Exception e) { Poses.Fail(e); }
            }
        }

        // 2.7.4: a chain mover is "active" while its animator is not in the idle state it was adopted in (or in a
        // transition). The idle hash is taken from the first initialized look at the animator: blocks awake idle.
        private static bool ChainActive(Entry entry)
        {
            // 2.8.0: a gated glove is switched off in its idle state; any write to it switches it on first (prefix).
            if (entry.Gated) return false;
            Animator animator = entry.Animator;
            if (!animator.isInitialized) return false;
            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
            if (entry.IdleHash == 0) { entry.IdleHash = state.shortNameHash; return false; }
            return state.shortNameHash != entry.IdleHash || animator.IsInTransition(0);
        }

        private static void Step()
        {
            View.Slices = (Tracked.Count + PerFrame - 1) / PerFrame;
            bool valid = View.Refresh();
            // 2.7.4: every frame, a culled chain mover that started its animation goes live at once (before Unity
            // evaluates the animators for this frame), whatever the view and whoever is near.
            for (int i = Chains.Count - 1; i >= 0; i--)
            {
                Entry chain = Chains[i];
                if (chain.Animator == null) { Chains[i] = Chains[Chains.Count - 1]; Chains.RemoveAt(Chains.Count - 1); continue; }
                if (chain.Culled && ChainActive(chain))
                {
                    chain.Animator.cullingMode = chain.Original;
                    chain.Culled = false;
                    unculls++; culledNow--; chainWakes++;
                }
            }
            // With a valid view, a slice of the list per frame; without one, every animator goes back to its own mode now.
            int budget = valid ? Math.Min(PerFrame, Tracked.Count) : Tracked.Count;
            for (int k = 0; k < budget && Tracked.Count != 0; k++)
            {
                if (cursor >= Tracked.Count) cursor = 0;
                Entry entry = Tracked[cursor];
                if (entry.Animator == null)
                {
                    // Destroyed block: drop the entry without a scene scan.
                    int last = Tracked.Count - 1;
                    Tracked[cursor] = Tracked[last];
                    Tracked.RemoveAt(last);
                    if (entry.Culled) culledNow--;
                    if (entry.Gated) { GatedMap.Remove(entry.Id); entry.Gated = false; gatedNow--; }
                    continue;
                }
                // Event-only clips write no collider pose: the camera rectangle alone decides. A mover's collider can be
                // met by a player or a projectile, so it also stays live while one is within its reach (2.6.2); a chain
                // mover also while its animation is running (2.7.4).
                Vector3 position = entry.Transform.position;
                bool cull = valid && !View.Contains(position, Margin) && !(entry.Mover && View.NearInterest(position, entry.Reach)) && !(entry.Chain && ChainActive(entry));
                if (cull != entry.Culled)
                {
                    entry.Animator.cullingMode = cull ? AnimatorCullingMode.CullUpdateTransforms : entry.Original;
                    entry.Culled = cull;
                    if (cull) { culls++; culledNow++; } else { unculls++; culledNow--; }
                }
                if (entry.GateInfo != null)
                {
                    try { GateVisit(entry, cull); }
                    catch (Exception e) { Gate.Fail(e); }
                }
                cursor++;
            }
            if (gateLogPending && Gate.Active && Time.realtimeSinceStartup >= gateLogAt)
            {
                gateLogPending = false;
                int candidates = 0;
                for (int i = 0; i < Tracked.Count; i++) if (Tracked[i].GateInfo != null) candidates++;
                Feature.Log.LogInfo(Gate.Name + ": " + gatedNow + " of " + candidates + " idle-gate animators switched off (view valid=" + valid + ").");
            }
        }

        // 2.8.0: one gate decision per slice visit, after the cull decision for the same entry. A gated animator is
        // switched back on as soon as the view no longer culls it (in view, a player or projectile near a mover, or no
        // valid view at all: then every entry is visited in this frame and everything goes back on, as for the poses).
        private static void GateVisit(Entry entry, bool cull)
        {
            Animator animator = entry.Animator;
            if (entry.Gated)
            {
                if (!cull) { Ungate(entry); return; }
                // Switched on by something other than the prefixes (no game code does this for the gated controllers):
                // it is simply no longer ours.
                if (animator.enabled) { Ungate(entry); foreignWakes++; }
                return;
            }
            if (!cull || !Gate.Active || !animator.isActiveAndEnabled || !animator.isInitialized) return;
            GateInfo info = entry.GateInfo;
            if (!info.Prepared) Prepare(info, animator);
            if (!info.Supported) return;
            if (info.Layers == 1 ? !IsIdle(info, animator) : !IsIdleLayers(info, animator)) { skippedNoIdle++; return; }
            entry.KeepOriginal = animator.keepAnimatorStateOnDisable;
            animator.keepAnimatorStateOnDisable = true;
            animator.enabled = false;
            entry.Gated = true;
            GatedMap[entry.Id] = entry;
            gatedNow++; gates++;
        }

        // Parameter hashes per controller, read once from the first initialized animator. Only controllers with exactly
        // the listed number of layers and only trigger and bool parameters are gated; anything else stays ungated.
        // 2.9.0: the expected value of a bool is the controller default unless the gate entry names it (teleporter On).
        private static void Prepare(GateInfo info, Animator animator)
        {
            info.Prepared = true;
            info.Supported = false;
            if (animator.layerCount != info.Layers) { skippedLayers++; Feature.Log.LogWarning(Gate.Name + ": " + info.IdleName + " controller has " + animator.layerCount + " layers (expected " + info.Layers + "); not gated."); return; }
            AnimatorControllerParameter[] parameters = animator.parameters;
            List<int> triggers = new List<int>(), bools = new List<int>();
            List<bool> defaults = new List<bool>();
            int overridden = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                AnimatorControllerParameter p = parameters[i];
                if (p.type == AnimatorControllerParameterType.Trigger) triggers.Add(p.nameHash);
                else if (p.type == AnimatorControllerParameterType.Bool)
                {
                    bool expected = p.defaultBool;
                    for (int k = 0; k < info.Spec.BoolNames.Length; k++)
                        if (p.name == info.Spec.BoolNames[k]) { expected = info.Spec.BoolValues[k]; overridden++; }
                    bools.Add(p.nameHash); defaults.Add(expected);
                }
                else { skippedLayers++; Feature.Log.LogWarning(Gate.Name + ": " + info.IdleName + " controller has a " + p.type + " parameter; not gated."); return; }
            }
            if (overridden != info.Spec.BoolNames.Length) { skippedLayers++; Feature.Log.LogWarning(Gate.Name + ": " + info.IdleName + " controller lacks an expected bool parameter; not gated."); return; }
            info.Triggers = triggers.ToArray();
            info.Bools = bools.ToArray();
            info.BoolDefaults = defaults.ToArray();
            if (info.Layers > 1) info.IdleHashes = new int[info.Layers];
            info.Supported = true;
        }

        // 2.9.0: idle for a multi-layer controller = on EVERY layer no transition and the learned idle state, plus no
        // trigger pending and every bool at its expected value. The cheap parameter checks run first, so the learning
        // below only ever looks at an animator with no trigger pending.
        private static bool IsIdleLayers(GateInfo info, Animator animator)
        {
            if (info.Abandoned) return false;
            int layers = info.Layers;
            for (int l = 0; l < layers; l++) if (animator.IsInTransition(l)) return false;
            for (int i = 0; i < info.Triggers.Length; i++) if (animator.GetBool(info.Triggers[i])) return false;
            for (int i = 0; i < info.Bools.Length; i++) if (animator.GetBool(info.Bools[i]) != info.BoolDefaults[i]) return false;
            if (!info.Learned && !LearnLayers(info, animator)) return false;
            int[] hashes = info.IdleHashes;
            for (int l = 0; l < layers; l++) if (animator.GetCurrentAnimatorStateInfo(l).shortNameHash != hashes[l]) return false;
            return true;
        }

        // Learns every layer's idle state hash at once, from an animator with no transition and no trigger pending:
        // a layer whose only current clip is its listed idle clip gives its current state hash. A layer that reports no
        // clip at all (an empty state) takes the state hash it is in now, but only when every other layer is confirmed
        // idle by its clip in the same look; that is logged once. Any layer playing another clip fails the look. After
        // MaxLearnVisits failed looks the controller is abandoned (never gated).
        private static bool LearnLayers(GateInfo info, Animator animator)
        {
            int layers = info.Layers;
            int[] hashes = new int[layers];
            bool[] bySight = new bool[layers];
            bool ok = true;
            for (int l = 0; l < layers && ok; l++)
            {
                ClipScratch.Clear();
                animator.GetCurrentAnimatorClipInfo(l, ClipScratch);
                int count = ClipScratch.Count;
                if (count == 0) bySight[l] = true;
                else ok = count == 1 && ClipScratch[0].clip != null && ClipScratch[0].clip.name == info.Spec.IdleNames[l];
                ClipScratch.Clear();
                if (ok) hashes[l] = animator.GetCurrentAnimatorStateInfo(l).shortNameHash;
            }
            int confirmed = 0;
            for (int l = 0; l < layers; l++) if (!bySight[l]) confirmed++;
            if (ok && confirmed == 0) ok = false;   // no layer confirmed idle by its clip: nothing to anchor the look on
            if (!ok)
            {
                info.LearnVisits++;
                if (info.LearnVisits >= MaxLearnVisits)
                {
                    info.Abandoned = true;
                    Feature.Log.LogWarning(Gate.Name + ": " + info.IdleName + " controller: idle states not established after " + MaxLearnVisits + " looks; not gated.");
                }
                return false;
            }
            for (int l = 0; l < layers; l++)
            {
                info.IdleHashes[l] = hashes[l];
                if (bySight[l]) Feature.Log.LogInfo(Gate.Name + ": " + info.IdleName + " controller layer " + l + " reports no clip in its idle state; idle = state " + hashes[l] + " seen with the other layers idle.");
            }
            info.Learned = true;
            return true;
        }

        // Idle = the controller's idle state (learned once from the state whose only clip is the idle clip), not in a
        // transition, no trigger pending, every bool at its default value (so the kept state equals what a fresh
        // animator would start with).
        private static bool IsIdle(GateInfo info, Animator animator)
        {
            if (animator.IsInTransition(0)) return false;
            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
            if (info.IdleHash == 0)
            {
                ClipScratch.Clear();
                animator.GetCurrentAnimatorClipInfo(0, ClipScratch);
                bool idle = ClipScratch.Count == 1 && ClipScratch[0].clip != null && ClipScratch[0].clip.name == info.IdleName;
                ClipScratch.Clear();
                if (!idle) return false;
                info.IdleHash = state.shortNameHash;
            }
            if (state.shortNameHash != info.IdleHash) return false;
            for (int i = 0; i < info.Triggers.Length; i++) if (animator.GetBool(info.Triggers[i])) return false;
            for (int i = 0; i < info.Bools.Length; i++) if (animator.GetBool(info.Bools[i]) != info.BoolDefaults[i]) return false;
            return true;
        }

        private static void Ungate(Entry entry)
        {
            GatedMap.Remove(entry.Id);
            entry.Gated = false;
            gatedNow--;
            Animator animator = entry.Animator;
            if (animator == null) return;
            animator.enabled = true;
            animator.keepAnimatorStateOnDisable = entry.KeepOriginal;
        }

        private static GateInfo GateInfoFor(string controller)
        {
            GateSpec spec;
            if (!GateIdle.TryGetValue(controller, out spec)) return null;
            GateInfo info;
            if (!GateInfos.TryGetValue(controller, out info))
            {
                info = new GateInfo(); info.IdleName = spec.IdleNames[0]; info.Spec = spec; info.Layers = spec.IdleNames.Length;
                GateInfos[controller] = info;
            }
            return info;
        }

        // Rides on Graphics/CullOffscreenAnimatorPoses (its view loop and tracked list).
        internal static void InstallGate()
        {
            if (!Poses.Active) throw new NotSupportedException("Graphics/CullOffscreenAnimatorPoses is off");
            Gate.Restore = RestoreGate;
            gatePatched = 0;
            MethodInfo[] methods = typeof(Animator).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (Array.IndexOf(GateEntryPoints, m.Name) < 0) continue;
                if ((m.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0) continue;
                // Every managed wrapper must be covered, or a gated animator could miss a write: any failure aborts the option.
                Gate.Patch(m, typeof(AnimatorPoseCullHooks), "BeforeAnimatorWrite", null, null, null);
                gatePatched++;
            }
            if (gatePatched == 0) throw new InvalidOperationException("no Animator entry point patched");
            Feature.Log.LogInfo(Gate.Name + ": animatorGuards=" + gatePatched + " managed Animator entry points patched.");
        }

        // Runs before every patched Animator call in the game (players, UI, blocks). The common case returns after one
        // int compare, or one dictionary lookup by instance id while animators are gated.
        private static void BeforeAnimatorWrite(Animator __instance)
        {
            if (gatedNow == 0) return;
            try
            {
                Entry entry;
                if ((object)__instance == null || !GatedMap.TryGetValue(__instance.GetInstanceID(), out entry)) return;
                wakes++;
                Ungate(entry);
            }
            catch (Exception e) { Gate.Fail(e); }
        }

        private static void RestoreGate()
        {
            for (int i = 0; i < Tracked.Count; i++) if (Tracked[i].Gated) Ungate(Tracked[i]);
            GatedMap.Clear();
            gatedNow = 0;
        }

        private static void Restore(bool moversOnly)
        {
            int write = 0;
            for (int i = 0; i < Tracked.Count; i++)
            {
                Entry entry = Tracked[i];
                if (moversOnly && !entry.Mover) { Tracked[write++] = entry; continue; }
                // 2.8.0: an entry that leaves the list is switched back on first.
                if (entry.Gated) Ungate(entry);
                if (entry.Animator != null && entry.Culled) entry.Animator.cullingMode = entry.Original;
                if (entry.Culled) culledNow--;
                entry.Culled = false;
            }
            Tracked.RemoveRange(write, Tracked.Count - write);
            Chains.Clear();
            for (int i = 0; i < Tracked.Count; i++) if (Tracked[i].Chain) Chains.Add(Tracked[i]);
            cursor = 0;
        }

        internal static string Status()
        {
            int movers = 0;
            for (int i = 0; i < Tracked.Count; i++) if (Tracked[i].Mover) movers++;
            return "tracked=" + Tracked.Count + " movers=" + movers + " chains=" + Chains.Count + " chainWakes=" + chainWakes + " culledNow=" + culledNow + " adopted=" + adopted + " seen=" + seen +
                " culls=" + culls + " unculls=" + unculls + " view[" + View.Status() + "]";
        }

        internal static string GateStatus()
        {
            return "gated=" + gatedNow + " gates=" + gates + " wakes=" + wakes + " patched=" + gatePatched + " skippedNoIdle=" + skippedNoIdle +
                " skippedLayers=" + skippedLayers + " foreignWakes=" + foreignWakes + " active=" + Gate.Active;
        }
    }

    // The gameplay camera's world rectangle at the block plane (z = 0), computed at most once per frame and shared by
    // the animator view loop and the far-ferris-wheel option. The camera is the game's ZoomCamera.CurrentZoomCamera
    // (set by the level's ZoomCamera at Start; UCH's Camera.main is the UI camera). It qualifies while at least one
    // living, active player stands inside its rectangle (with a small allowance for camera lag): with "local cam" the
    // local players, with "all cam" everyone. Remote players outside the rectangle are no longer a reason to stop
    // culling (2.6.2); they count as interest points instead.
    //
    // Interest points (2.6.2, sharpened 2.7.0): every active player and every live projectile (Projectile.AllProjectiles:
    // arrows, pucks, cannon animals, all with a trigger collider and a body) is stored in a 12-unit cell grid (per-cell
    // slot chains in flat arrays, no allocation after warm-up). A player is a point with radius 4. A projectile is the
    // segment from its position to where its current velocity carries it within the horizon (the physics steps of one
    // frame per view-loop slice, plus one), with radius 1: a 30 u/s arrow covers a 2-unit segment instead of the 2.6.2
    // circle of radius 3, about four times less area, and an arrow flying away from a block never wakes it.
    // NearInterest(p, reach) is an exact point-to-segment test over the surrounding cells: true when some segment lies
    // within reach + its radius of p. Rebuilt whenever the frame or the physics step changes (Time.fixedTime), so a
    // ferris wheel checked every step sees positions at most one step old. Eternity census (2026-10-26): 14-40
    // projectiles in flight at 30 units/s, 60 Hz physics, at most 3 steps per frame; a 2.6.1 build that marked whole
    // cells conservatively kept 75 of 145 movers live and lost 13 FPS.
    //
    // 2.7.1: the segment assumes straight flight, so its radius grows by the distance an in-flight acceleration can bend
    // the path within the horizon (0.5 * a * horizon^2). Game code review (Projectile, AttractorRepulsor, BlowerPusher,
    // AnimalCannon): projectiles are trigger colliders and never bounce; what bends them is gravity (cannon animals, the
    // body's own gravityScale), a black hole's AttractorRepulsor (pushAccel 300 u/s^2 within its radius 5) and the
    // projectile blower (at most 5 u/s^2). Teleporter and screen-wrap jumps are outside any radius, as they were for the
    // 2.6.2 circle. Hockey pucks are not projectiles (the shooter is a raycast).
    internal static class View
    {
        private const float PlayerAllowance = 4f;
        private const float PlayerRadius = 4f;
        private const float Cell = 12f;
        private static int frame = -1;
        private static float fixedStamp = -1f;
        private static bool valid;
        private static float minX, maxX, minY, maxY;
        private static readonly Dictionary<long, int> Heads = new Dictionary<long, int>();   // cell -> first slot
        private static float[] ax = new float[256], ay = new float[256], bx = new float[256], by = new float[256], pr = new float[256];
        private static int[] slotPoint = new int[1024], slotNext = new int[1024];
        private static int count, slots, lastPoints, slices = 1;
        private static float maxRadius, lastHorizon;
        private static long rejects, refreshes, nearHits, nearMisses;
        // 2.7.1: in-flight acceleration allowance (see the class comment). Black holes are found by scanning
        // Placeable.AllPlaceables for Blackhole blocks whenever the placeable count changes or every 600 frames; they
        // never move during play.
        private const float BlowerAccel = 5f;
        private static readonly List<AttractorRepulsor> Attractors = new List<AttractorRepulsor>();
        private static int attractorCount = -1, attractorFrame = -100000;
        private static float maxAllowance;

        // Set by the animator view loop: how many frames pass between two checks of the same animator.
        internal static int Slices { set { slices = value < 1 ? 1 : value; } }

        internal static bool Refresh()
        {
            int now = Time.frameCount;
            float fixedNow = Time.fixedTime;
            if (now == frame && fixedNow == fixedStamp) return valid;
            frame = now;
            fixedStamp = fixedNow;
            refreshes++;
            valid = Rect(ZoomCamera.CurrentZoomCamera) && PlayerInside();
            if (!valid) rejects++;
            else MarkInterest();
            return valid;
        }

        private static void MarkInterest()
        {
            Heads.Clear();
            count = 0; slots = 0;
            maxRadius = 0f;
            float fixedDelta = Time.fixedDeltaTime;
            int stepsPerFrame = fixedDelta > 0f ? (int)Math.Ceiling(Time.maximumDeltaTime / fixedDelta) : 1;
            if (stepsPerFrame < 1) stepsPerFrame = 1;
            float horizon = (1 + stepsPerFrame * slices) * fixedDelta;
            lastHorizon = horizon;
            List<Character> characters = Character.AllCharacters;
            if (characters != null)
                for (int i = 0; i < characters.Count; i++)
                {
                    Character character = characters[i];
                    if (character == null || !character.gameObject.activeInHierarchy) continue;
                    Vector3 p = character.transform.position;
                    Mark(p.x, p.y, p.x, p.y, PlayerRadius);
                }
            List<Projectile> projectiles = Projectile.AllProjectiles;
            if (projectiles != null && projectiles.Count != 0)
            {
                ScanAttractors();
                float halfHorizon2 = 0.5f * horizon * horizon;
                float gravity = Physics2D.gravity.magnitude;
                maxAllowance = 0f;
                for (int i = 0; i < projectiles.Count; i++)
                {
                    Projectile projectile = projectiles[i];
                    if (projectile == null) continue;
                    Vector3 p = projectile.transform.position;
                    Rigidbody2D body = projectile.GetComponent<Rigidbody2D>();
                    // No body (never seen): a generous circle instead of a segment.
                    if (body == null) { Mark(p.x, p.y, p.x, p.y, 1f + 60f * horizon); continue; }
                    Vector2 v = body.velocity;
                    // 2.7.1: everything that can bend this projectile's path before the next check.
                    float accel = BlowerAccel + gravity * Math.Abs(body.gravityScale);
                    float travel = v.magnitude * horizon;
                    for (int k = 0; k < Attractors.Count; k++)
                    {
                        AttractorRepulsor attractor = Attractors[k];
                        if (attractor == null || !attractor.isActiveAndEnabled) continue;
                        Vector3 c = attractor.transform.position;
                        float ddx = c.x - p.x, ddy = c.y - p.y, within = attractor.radius + travel + 1f;
                        if (ddx * ddx + ddy * ddy <= within * within) accel += Math.Abs(attractor.pushAccel);
                    }
                    float allowance = accel * halfHorizon2;
                    if (allowance > maxAllowance) maxAllowance = allowance;
                    Mark(p.x, p.y, p.x + v.x * horizon, p.y + v.y * horizon, 1f + allowance);
                }
            }
            lastPoints = count;
        }

        // 2.7.1: the black holes of the current level (AttractorRepulsor sits on a child of the Blackhole block).
        private static void ScanAttractors()
        {
            List<Placeable> all = Placeable.AllPlaceables;
            int n = all == null ? 0 : all.Count;
            int now = Time.frameCount;
            if (n == attractorCount && now - attractorFrame < 600) return;
            attractorCount = n; attractorFrame = now;
            Attractors.Clear();
            for (int i = 0; i < n; i++)
            {
                Placeable placeable = all[i];
                if (placeable == null || !(placeable is Blackhole)) continue;
                AttractorRepulsor[] found = placeable.GetComponentsInChildren<AttractorRepulsor>(true);
                for (int k = 0; k < found.Length; k++) Attractors.Add(found[k]);
            }
        }

        // A segment (ax,ay)-(bx,by) with radius r, filed in every cell its bounding box touches.
        private static void Mark(float x1, float y1, float x2, float y2, float radius)
        {
            // 2.7.1: an absurd velocity (NaN/infinite end point) files the projectile as a point at its position.
            if (float.IsNaN(x2) || float.IsInfinity(x2) || float.IsNaN(y2) || float.IsInfinity(y2)) { x2 = x1; y2 = y1; }
            if (count == ax.Length)
            {
                int size = ax.Length * 2;
                Array.Resize(ref ax, size); Array.Resize(ref ay, size); Array.Resize(ref bx, size); Array.Resize(ref by, size); Array.Resize(ref pr, size);
            }
            int index = count++;
            ax[index] = x1; ay[index] = y1; bx[index] = x2; by[index] = y2; pr[index] = radius;
            if (radius > maxRadius) maxRadius = radius;
            int sx = Mathf.FloorToInt(x1 / Cell), sy = Mathf.FloorToInt(y1 / Cell);
            int cx0 = Mathf.FloorToInt(Math.Min(x1, x2) / Cell), cx1 = Mathf.FloorToInt(Math.Max(x1, x2) / Cell);
            int cy0 = Mathf.FloorToInt(Math.Min(y1, y2) / Cell), cy1 = Mathf.FloorToInt(Math.Max(y1, y2) / Cell);
            // Never loop far (2.7.1: clamp around the current position's cell, whichever way the segment points).
            if (cx0 < sx - 8) cx0 = sx - 8; if (cx1 > sx + 8) cx1 = sx + 8;
            if (cy0 < sy - 8) cy0 = sy - 8; if (cy1 > sy + 8) cy1 = sy + 8;
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    if (slots == slotPoint.Length) { Array.Resize(ref slotPoint, slots * 2); Array.Resize(ref slotNext, slots * 2); }
                    long key = Key(cx, cy);
                    int head;
                    slotNext[slots] = Heads.TryGetValue(key, out head) ? head : -1;
                    slotPoint[slots] = index;
                    Heads[key] = slots;
                    slots++;
                }
        }

        private static long Key(int cx, int cy) { return ((long)cx << 32) | (uint)cy; }

        // True when a player or a projectile (its travel segment) lies within reach + its own radius of p.
        internal static bool NearInterest(Vector3 p, float reach)
        {
            if (count != 0)
            {
                int n = (int)Math.Ceiling((reach + maxRadius) / Cell);
                int cx = Mathf.FloorToInt(p.x / Cell), cy = Mathf.FloorToInt(p.y / Cell);
                for (int dx = -n; dx <= n; dx++)
                    for (int dy = -n; dy <= n; dy++)
                    {
                        int s;
                        if (!Heads.TryGetValue(Key(cx + dx, cy + dy), out s)) continue;
                        for (; s >= 0; s = slotNext[s])
                        {
                            int i = slotPoint[s];
                            float r = reach + pr[i];
                            if (SegmentDistanceSquared(ax[i], ay[i], bx[i], by[i], p.x, p.y) <= r * r) { nearHits++; return true; }
                        }
                    }
            }
            nearMisses++;
            return false;
        }

        private static float SegmentDistanceSquared(float x1, float y1, float x2, float y2, float px, float py)
        {
            float dx = x2 - x1, dy = y2 - y1;
            float len = dx * dx + dy * dy;
            float t = len > 1e-8f ? ((px - x1) * dx + (py - y1) * dy) / len : 0f;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float ex = x1 + t * dx - px, ey = y1 + t * dy - py;
            return ex * ex + ey * ey;
        }

        private static bool Rect(Camera c)
        {
            if (c == null || !c.enabled || c.targetTexture != null) return false;
            // Distance from the camera to the block plane; UCH's gameplay camera is a perspective camera looking along +z.
            float depth = Mathf.Abs(c.transform.position.z);
            if (depth < 0.5f) depth = 10f;
            Vector3 a = c.ViewportToWorldPoint(new Vector3(0f, 0f, depth));
            Vector3 b = c.ViewportToWorldPoint(new Vector3(1f, 1f, depth));
            minX = Mathf.Min(a.x, b.x); maxX = Mathf.Max(a.x, b.x);
            minY = Mathf.Min(a.y, b.y); maxY = Mathf.Max(a.y, b.y);
            float width = maxX - minX, height = maxY - minY;
            return width > 1f && height > 1f && width < 3000f && height < 3000f;   // NaN fails these too
        }

        private static bool PlayerInside()
        {
            List<Character> all = Character.AllCharacters;
            if (all == null) return false;
            for (int i = 0; i < all.Count; i++)
            {
                Character character = all[i];
                if (character == null || character.Dead || !character.gameObject.activeInHierarchy) continue;
                if (Contains(character.transform.position, PlayerAllowance)) return true;
            }
            return false;
        }

        internal static bool Contains(Vector3 p, float margin)
        {
            return p.x >= minX - margin && p.x <= maxX + margin && p.y >= minY - margin && p.y <= maxY + margin;
        }

        // 2.10.0: true when the box (x0,y0)-(x1,y1) touches the view rectangle grown by margin.
        internal static bool Overlaps(float x0, float y0, float x1, float y1, float margin)
        {
            return x1 >= minX - margin && x0 <= maxX + margin && y1 >= minY - margin && y0 <= maxY + margin;
        }

        internal static string Status()
        {
            Camera camera = ZoomCamera.CurrentZoomCamera;
            return "camera=" + (camera == null ? "none" : camera.name) + " valid=" + valid + " rect=" + minX.ToString("0.0") + "," + minY.ToString("0.0") +
                ".." + maxX.ToString("0.0") + "," + maxY.ToString("0.0") + " refreshes=" + refreshes + " rejects=" + rejects +
                " points=" + lastPoints + " cells=" + Heads.Count + " slots=" + slots + " maxRadius=" + maxRadius.ToString("0.0") + " horizon=" + lastHorizon.ToString("0.000") +
                " slices=" + slices + " attractors=" + Attractors.Count + " maxAllowance=" + maxAllowance.ToString("0.00") + " nearHits=" + nearHits + " nearMisses=" + nearMisses;
        }
    }
}
