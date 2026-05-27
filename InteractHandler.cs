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
                if (dalamudObj is { ObjectKind: DalamudObjectKind.Pc }
                    && config.InteractExaminePlayers
                    && !IsLocalPlayerWeaponDrawn())
                {
                    // Second press on a PC — examine. (First press set them
                    // as the hard target via the PC fallback below.)
                    AgentInspect.Instance()->ExamineCharacter(dalamudObj.EntityId);
                    return InteractResult.ExaminedPlayer;
                }

                // Non-PC hard target — call InteractWithObject as normal.
                if (dalamudObj == null || dalamudObj.ObjectKind != DalamudObjectKind.Pc)
                {
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

        ts->InteractWithObject((GameObject*)npc.Address);
        return true;
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
            || k == DalamudObjectKind.Aetheryte;
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
}
