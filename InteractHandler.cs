using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace ActionCamera;

/// <summary>
/// Outcome of a single interact-key press. Used by <see cref="Plugin"/> to
/// decide whether to play an audio cue (and which one) and by future
/// telemetry. Order matches the priority chain in <see cref="InteractHandler.TryInteract"/>.
/// </summary>
internal enum InteractResult
{
    /// <summary>An open dialog / prompt was advanced. The game plays its own
    /// click sound for these, so the plugin stays silent on this branch.</summary>
    AdvancedDialogue,

    /// <summary>An NPC, event object, or aetheryte was interacted with —
    /// either the current hard target or a cone-scan pick. Plays success sfx
    /// (first interaction with a fresh target gets no game-side audio
    /// feedback otherwise).</summary>
    InteractedWithTarget,

    /// <summary>A player character was examined via AgentInspect. Plays
    /// success sfx — the Examine window has its own open sound but starts
    /// silently for ~half a second while data loads.</summary>
    ExaminedPlayer,

    /// <summary>(v0.6.32) Initiated Ride Pillion on a mounted party member.
    /// Plays success sfx.</summary>
    RodePillion,

    /// <summary>Key pressed, nothing was advanceable / interactable in range.
    /// Plays fail sfx.</summary>
    NothingFound,
}

/// <summary>
/// Single-key "interact" handler covering four paths in priority order:
///
///   1. Dialogue advance — Talk / SelectString / SelectYesno / etc.
///   2. Hard-target interact — the player already has a hard target.
///   3. Cone scan for EventNpc / EventObj / Aetheryte (the "world interact"
///      kinds — quest givers, vendors, aetherytes, clickable scenery).
///   4. (v0.5.21.0) Cone scan for nearby player characters → open the
///      FFXIV Examine window via <c>AgentInspect::ExamineCharacter</c>.
///      Gated on <see cref="Configuration.InteractExaminePlayers"/> AND the
///      local player having no weapon drawn (sheathed-only feature —
///      examining strangers mid-fight makes no sense).
///
/// Detection / advance patterns adapted from ECommons' AddonMaster files.
/// FireCallback for list selections, simulated mouse click via
/// <see cref="AddonClick.Click"/> for typed-button dialogs, raw
/// MouseDown/Click/Up event triplet for Talk.
///
/// History note (0.5.13.0 → 0.5.20.0): the typed-button paths were originally
/// implemented with the same <c>FireCallback(1, [int 0])</c> shape as list
/// selections. That fires the addon's dismiss path on typed-button dialogs,
/// not the typed Accept button — so quest accepts closed without accepting.
/// 0.5.20.0 switched them to <see cref="AddonClick.Click"/> which replays
/// the button's bound AtkEvent through the addon's ReceiveEvent.
/// </summary>
internal sealed unsafe class InteractHandler
{
    private readonly Configuration config;
    private readonly Func<float> getCameraYaw;

    public InteractHandler(Configuration config, Func<float> getCameraYaw)
    {
        this.config = config;
        this.getCameraYaw = getCameraYaw;
    }

    /// <summary>
    /// Run the priority chain once. Returns which branch fired (if any). The
    /// caller is responsible for translating the result into audio feedback
    /// — see <see cref="Plugin.HandleInteractKey"/>.
    /// </summary>
    public InteractResult TryInteract()
    {
        // Dialogue advance always wins. Order within matters — yes/no/ok and
        // list selections render on top of Talk and we want to act on the
        // topmost interactive layer.
        if (TryAdvanceDialogue()) return InteractResult.AdvancedDialogue;

        // Mounted → dismount. You can't interact with the world while
        // mounted anyway, so the interact key doubling as dismount is
        // natural (and symmetric with ride-pillion: pillion on, press
        // again to hop off). Runs after dialogue advance so an open
        // prompt still wins.
        if (IsLocalPlayerMounted())
        {
            SendGameCommand("/dismount");
            return InteractResult.InteractedWithTarget;
        }

        // Hard-target interact branch with PC awareness. We do this BEFORE the
        // general TryInteractWithTarget cone-scan path so that:
        //   • a hard-targeted PC routes to Examine (when enabled + sheathed),
        //     instead of falling into ts->InteractWithObject which is a no-op
        //     on player kinds and would silently consume the keypress.
        //   • a hard-targeted NPC / EventObj / Aetheryte still gets the normal
        //     InteractWithObject path below.
        var ts = TargetSystem.Instance();
        if (ts != null)
        {
            var hardTarget = ts->GetHardTarget();
            if (hardTarget != null)
            {
                var dalamudObj = Plugin.ObjectTable.CreateObjectReference((nint)hardTarget);

                // Ride pillion takes priority over examine for a mounted
                // party member — not weapon/examine gated (you pillion in
                // any state). Base-game limits pillion to party members.
                if (dalamudObj != null && IsPillionTarget(dalamudObj) && RidePillion(dalamudObj))
                    return InteractResult.RodePillion;

                if (dalamudObj is { ObjectKind: DalamudObjectKind.Pc }
                    && config.InteractExaminePlayers
                    && !IsLocalPlayerWeaponDrawn())
                {
                    // Second press on a PC — examine. (First press set them
                    // as the hard target via the PC fallback below.)
                    AgentInspect.Instance()->ExamineCharacter(dalamudObj.EntityId);
                    return InteractResult.ExaminedPlayer;
                }

                // Non-PC hard target — interact (kind-aware: nodes need
                // OpenObjectInteraction).
                if (dalamudObj == null || dalamudObj.ObjectKind != DalamudObjectKind.Pc)
                {
                    if (dalamudObj != null)
                        InteractWith(ts, dalamudObj);
                    else
                        ts->InteractWithObject(hardTarget);
                    return InteractResult.InteractedWithTarget;
                }

                // PC hard-targeted but examine is disabled / weapon drawn —
                // fall through to the cone scan paths. The keypress shouldn't
                // be silently lost.
            }
        }

        // No usable hard target — cone scan for NPC / EventObj / Aetheryte.
        if (TryInteractWithConeNpc()) return InteractResult.InteractedWithTarget;

        // Cone scan for a mounted party member → ride pillion. Runs before
        // the examine cone scan and independent of the examine/weapon gates
        // (pillion is always allowed).
        var pillionPc = FindCandidateInCone(IsPillionTarget);
        if (pillionPc != null && RidePillion(pillionPc))
            return InteractResult.RodePillion;

        // Last resort: PC cone scan. First press on a PC sets them as the
        // hard target so the next press can route into the Examine branch
        // above (matches the "click to target, click again to examine"
        // muscle memory from the vanilla game).
        if (config.InteractExaminePlayers && !IsLocalPlayerWeaponDrawn())
        {
            var pc = FindCandidateInCone(IsExaminablePlayer);
            if (pc != null)
            {
                // HardTargetSuppressor only blocks LMB-recency or combat
                // soft-target promotions — neither applies here, so the
                // straight assignment passes through cleanly. ITargetManager.Target
                // routes through SetHardTarget under the hood.
                Plugin.TargetManager.Target = pc;
                return InteractResult.InteractedWithTarget;
            }
        }

        return InteractResult.NothingFound;
    }

    /// <summary>
    /// Snapshot of "what the interact key would target right now". Used by
    /// the indicator overlay to draw a marker over the candidate. Returns
    /// null if nothing in cone or the player is currently in a state where
    /// the indicator wouldn't be useful (the indicator's own draw gates
    /// catch the gross cases; this method only returns the geometry pick).
    /// </summary>
    public IGameObject? GetIndicatorCandidate()
    {
        var npc = FindCandidateInCone(IsWorldInteractable);
        if (npc != null) return npc;

        // Mounted party member → the marker shows where pillion will fire.
        var pillion = FindCandidateInCone(IsPillionTarget);
        if (pillion != null) return pillion;

        if (config.InteractExaminePlayers && !IsLocalPlayerWeaponDrawn())
            return FindCandidateInCone(IsExaminablePlayer);

        return null;
    }

    // ── Dialogue advance ────────────────────────────────────────────────────

    private static bool TryAdvanceDialogue()
    {
        if (TryClickYesno())             return true;
        if (TrySelectFirst("SelectString"))         return true;
        if (TrySelectFirst("SelectIconString"))     return true;
        if (TrySelectFirst("CutSceneSelectString")) return true;
        if (TryClickOk())                return true;
        if (TryClickJournalAccept())     return true;
        if (TryClickJournalResult())     return true;
        if (TryClickMaterializeDialog()) return true;
        if (TryAdvanceTalk())            return true;
        return false;
    }

    private static AtkUnitBase* GetVisibleAddon(string name)
    {
        var wrapper = Plugin.GameGui.GetAddonByName(name, 1);
        if (wrapper.Address == IntPtr.Zero) return null;
        var addon = (AtkUnitBase*)wrapper.Address;
        return addon->IsVisible ? addon : null;
    }

    /// <summary>
    /// List-selection addons (SelectString / SelectIconString /
    /// CutSceneSelectString) advance via FireCallback(1, [int index], true).
    /// Index 0 = first option = equivalent of the user pressing Confirm.
    /// </summary>
    private static bool TrySelectFirst(string addonName)
    {
        var addon = GetVisibleAddon(addonName);
        if (addon == null) return false;

        var values = stackalloc AtkValue[1];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 0;
        addon->FireCallback(1, values, true);
        return true;
    }

    private static bool TryClickYesno()
    {
        var addr = GetVisibleAddon("SelectYesno");
        if (addr == null) return false;
        var typed = (AddonSelectYesno*)addr;
        return AddonClick.Click(typed->YesButton, (AtkUnitBase*)typed);
    }

    private static bool TryClickOk()
    {
        var addr = GetVisibleAddon("SelectOk");
        if (addr == null) return false;
        var typed = (AddonSelectOk*)addr;
        return AddonClick.Click(typed->OkButton, (AtkUnitBase*)typed);
    }

    private static bool TryClickJournalAccept()
    {
        var addr = GetVisibleAddon("JournalAccept");
        if (addr == null) return false;
        // No typed CS struct. ECommons resolves Accept via component id 44.
        var btn = addr->GetComponentButtonById(44);
        return AddonClick.Click(btn, addr);
    }

    private static bool TryClickJournalResult()
    {
        var addr = GetVisibleAddon("JournalResult");
        if (addr == null) return false;
        var typed = (AddonJournalResult*)addr;
        return AddonClick.Click(typed->CompleteButton, (AtkUnitBase*)typed);
    }

    private static bool TryClickMaterializeDialog()
    {
        var addr = GetVisibleAddon("MaterializeDialog");
        if (addr == null) return false;
        var typed = (AddonMaterializeDialog*)addr;
        return AddonClick.Click(typed->YesButton, (AtkUnitBase*)typed);
    }

    /// <summary>
    /// Talk (NPC speech bubble) advances on a click of the whole panel, not
    /// a button. Synthesise a MouseDown/Click/Up triplet with state flag 132
    /// — the magic value YesAlready and ClickLib both use; empirically correct.
    /// </summary>
    private static bool TryAdvanceTalk()
    {
        var addr = GetVisibleAddon("Talk");
        if (addr == null) return false;

        var evt = stackalloc AtkEvent[1];
        evt[0].Listener = (AtkEventListener*)addr;
        evt[0].Target   = &AtkStage.Instance()->AtkEventTarget;
        evt[0].State.StateFlags = (AtkEventStateFlags)132;
        var data = stackalloc AtkEventData[1];

        addr->ReceiveEvent(AtkEventType.MouseDown,  0, evt, data);
        addr->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
        addr->ReceiveEvent(AtkEventType.MouseUp,    0, evt, data);
        return true;
    }

    // ── Interact with target ────────────────────────────────────────────────

    /// <summary>
    /// Cone-only branch: pick the nearest world-interactable (EventNpc /
    /// EventObj / Aetheryte) and call <c>InteractWithObject</c>. Hard-target
    /// handling lives in <see cref="TryInteract"/> directly because it needs
    /// to special-case player-kind targets for the two-step Examine flow.
    /// </summary>
    private bool TryInteractWithConeNpc()
    {
        var ts = TargetSystem.Instance();
        if (ts == null) return false;

        var npc = FindCandidateInCone(IsWorldInteractable);
        if (npc == null) return false;

        InteractWith(ts, npc);
        return true;
    }

    /// <summary>
    /// Fire the correct interact call for the object's kind. Gathering
    /// nodes (GatheringPoint) open via <c>OpenObjectInteraction</c> — the
    /// game's event-handler route that actually starts gathering;
    /// <c>InteractWithObject</c> only targets a node, it doesn't open it
    /// (the v0.6.27 gathering bug). Everything else (NPC / EventObj /
    /// Aetheryte / Treasure) uses InteractWithObject as before.
    ///
    /// Neither call auto-paths, so a node only opens when the player is
    /// already within ~3.5y horizontal / ~3y vertical of it — walk up
    /// first. The cone can surface the node (indicator) from farther out.
    /// </summary>
    private static void InteractWith(TargetSystem* ts, IGameObject obj)
    {
        var go = (GameObject*)obj.Address;
        if (obj.ObjectKind == DalamudObjectKind.GatheringPoint)
            ts->OpenObjectInteraction(go);
        else
            ts->InteractWithObject(go);
    }

    /// <summary>
    /// Generic cone scan. Caller supplies the eligibility predicate (world
    /// interactable vs examinable player) — everything else (distance, angle,
    /// scoring) is shared.
    /// </summary>
    private IGameObject? FindCandidateInCone(Func<IGameObject, bool> eligible)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return null;

        var playerPos = localPlayer.Position;
        var camYaw    = getCameraYaw();
        var camForward = new Vector3(-MathF.Sin(camYaw), 0f, -MathF.Cos(camYaw));

        // Interact uses its own FOV + range, separate from the combat auto-
        // target system. Combat wants a tight forward cone matching the
        // player's swing arc; interact wants a wider sweep so peripheral
        // NPCs / aetherytes still resolve. See Configuration.InteractFovDegrees.
        var maxAngle  = config.InteractFovDegrees * (MathF.PI / 180f);
        var maxDistSq = config.InteractMaxDistance * config.InteractMaxDistance;

        IGameObject? best = null;
        var bestScore = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.GameObjectId == localPlayer.GameObjectId) continue;
            if (!eligible(obj)) continue;

            var toTarget = obj.Position - playerPos;
            var distSq = toTarget.LengthSquared();
            if (distSq > maxDistSq || distSq < 0.01f) continue;

            var toTargetXZ = new Vector3(toTarget.X, 0f, toTarget.Z);
            var len = toTargetXZ.Length();
            if (len < 0.01f) continue;

            var dot   = Vector3.Dot(toTargetXZ / len, camForward);
            var angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
            if (angle > maxAngle) continue;

            // Same scoring shape as the combat cone — angle weighted heavier
            // than distance so the most-centred candidate wins.
            var score = angle * 2.0f + MathF.Sqrt(distSq) * 0.05f;
            if (score < bestScore)
            {
                bestScore = score;
                best = obj;
            }
        }

        return best;
    }

    // ── Eligibility predicates ──────────────────────────────────────────────

    private static bool IsWorldInteractable(IGameObject obj)
    {
        if (!obj.IsTargetable) return false;
        var k = obj.ObjectKind;
        return k == DalamudObjectKind.EventNpc
            || k == DalamudObjectKind.EventObj
            || k == DalamudObjectKind.Aetheryte
            // Treasure = dungeon/raid loot coffers. Opening them via
            // InteractWithObject is not blocked by weapon stance, and the
            // world-interact path here has no weapon-drawn gate (only the
            // PC-examine path does), so coffers open fine mid-duty with the
            // weapon out — which is exactly when you loot them.
            || k == DalamudObjectKind.Treasure
            // GatheringPoint = mining / botany / fishing nodes. The
            // interact key should start gathering just like clicking them.
            || k == DalamudObjectKind.GatheringPoint;
    }

    private static bool IsExaminablePlayer(IGameObject obj)
    {
        if (!obj.IsTargetable) return false;
        return obj.ObjectKind == DalamudObjectKind.Pc;
    }

    private static bool IsLocalPlayerWeaponDrawn()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        return lp != null && lp.StatusFlags.HasFlag(StatusFlags.WeaponOut);
    }

    // ── Ride pillion ────────────────────────────────────────────────────────

    /// <summary>
    /// A valid ride-pillion target: a targetable PARTY MEMBER who is
    /// currently mounted (base game limits pillion to party members). We
    /// don't pre-check seat capacity — /ridepillion fails harmlessly on a
    /// single-seat mount, and mounted allies you'd point at are normally on
    /// shared mounts. Self is excluded (not in the cone / not targetable as
    /// other).
    /// </summary>
    private static bool IsPillionTarget(IGameObject obj)
    {
        if (obj.ObjectKind != DalamudObjectKind.Pc) return false;
        if (!obj.IsTargetable) return false;
        if (!IsPartyMember(obj)) return false;
        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)obj.Address;
        return chara != null && chara->IsMounted();
    }

    private static bool IsLocalPlayerMounted()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return false;
        var c = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)lp.Address;
        return c != null && c->IsMounted();
    }

    private static bool IsPartyMember(IGameObject obj)
    {
        foreach (var m in Plugin.PartyList)
            if (m.GameObject != null && m.GameObject.GameObjectId == obj.GameObjectId)
                return true;
        return false;
    }

    /// <summary>
    /// Fire /ridepillion at the given party member. The numeric party-slot
    /// placeholder (&lt;1&gt;..&lt;8&gt;) does NOT expand through
    /// ProcessChatBoxEntry — it reaches the game as a literal name
    /// ("<1>" is not a valid target name). The reliable route is the
    /// &lt;t&gt; (current target) placeholder, so set the member as the
    /// hard target first, then /ridepillion &lt;t&gt;. Targeting the ally
    /// you're about to mount with is expected/harmless.
    /// </summary>
    private static bool RidePillion(IGameObject pc)
    {
        Plugin.TargetManager.Target = pc;
        SendGameCommand("/ridepillion <t>");
        return true;
    }

    /// <summary>
    /// Crash-safe game text-command send (UIModule.ProcessChatBoxEntry with
    /// FFXIVClientStructs-managed Utf8String alloc/Dtor, ECommons pattern).
    /// MUST run on the framework thread — the interact handler already does.
    /// </summary>
    private static void SendGameCommand(string command)
    {
        if (string.IsNullOrEmpty(command) || command[0] != '/') return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(command);
        if (bytes.Length == 0 || bytes.Length > 500) return;

        var ui = UIModule.Instance();
        if (ui == null) return;

        var mes = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromSequence(bytes);
        try { ui->ProcessChatBoxEntry(mes); }
        finally { mes->Dtor(true); }
    }
}
