using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ActionCamera;

/// <summary>
/// Single-key "interact" handler covering three paths:
///
///   1. Dialogue advance — Talk / SelectString / SelectYesno / etc. — same
///      as the user pressing the game's "Confirm" (Numpad 0). Highest
///      priority, so the key always advances an open prompt before doing
///      anything else.
///   2. Hard-target interact — if the player already has a hard target,
///      call TargetSystem.InteractWithObject on it.
///   3. Cone scan — when there's no hard target, scan the camera cone for
///      the nearest EventNpc / EventObj / Aetheryte and interact with that.
///      Mirrors how the user would naturally aim at an NPC in cam mode and
///      expect a single keypress to open the dialogue.
///
/// Detection / advance patterns adapted from ECommons' AddonMaster files
/// (the current canonical reference; YesAlready dropped ClickLib for these
/// in 2024). FireCallback for list selections, button click for yes/no/ok,
/// raw MouseDown/Click/Up event triplet for Talk.
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
    /// Try paths in order. Returns true once any path fires (so the caller
    /// can stop looking).
    /// </summary>
    public bool TryInteract()
    {
        // Dialogue advance always wins. Order within matters — yes/no/ok and
        // list selections render on top of Talk and we want to act on the
        // topmost interactive layer.
        if (TryAdvanceDialogue()) return true;

        return TryInteractWithTarget();
    }

    // ── Dialogue advance ────────────────────────────────────────────────────

    private static bool TryAdvanceDialogue()
    {
        if (TryClickYesno())          return true;
        if (TrySelectFirst("SelectString"))     return true;
        if (TrySelectFirst("SelectIconString")) return true;
        if (TrySelectFirst("CutSceneSelectString")) return true;
        if (TryClickOk())             return true;
        if (TryClickJournalAccept())  return true;
        if (TryClickJournalResult())  return true;
        if (TryClickMaterializeDialog()) return true;
        if (TryAdvanceTalk())         return true;
        return false;
    }

    private static AtkUnitBase* GetVisibleAddon(string name)
    {
        var addr = Plugin.GameGui.GetAddonByName(name, 1);
        if (addr == IntPtr.Zero) return null;
        var addon = (AtkUnitBase*)addr;
        return addon->IsVisible ? addon : null;
    }

    /// <summary>
    /// List-selection addons (SelectString / SelectIconString /
    /// CutSceneSelectString) all advance via FireCallback(1, [int index],
    /// updateState=true). Selecting index 0 = first option = the equivalent
    /// of the user pressing Confirm on the default-highlighted entry.
    /// </summary>
    private static bool TrySelectFirst(string addonName)
    {
        var addon = GetVisibleAddon(addonName);
        if (addon == null) return false;

        var values = stackalloc AtkValue[1];
        values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[0].Int = 0;
        addon->FireCallback(1, values, true);
        return true;
    }

    private static bool TryClickYesno()
    {
        var addr = GetVisibleAddon("SelectYesno");
        if (addr == null) return false;
        // SelectYesno advances via FireCallback with int 0 = Yes, 1 = No.
        // We always pick Yes — caller is responsible for warning the user
        // that destructive prompts default through. No typed-button click
        // path because FFXIVClientStructs doesn't expose ClickAddonButton.
        var values = stackalloc AtkValue[1];
        values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[0].Int = 0;
        addr->FireCallback(1, values, true);
        return true;
    }

    private static bool TryClickOk()
    {
        var addr = GetVisibleAddon("SelectOk");
        if (addr == null) return false;
        // SelectOk has no typed CS struct as of the time of writing; the
        // standard advance pattern is the same FireCallback shape as list
        // selections — index 0 = OK button.
        var values = stackalloc AtkValue[1];
        values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[0].Int = 0;
        addr->FireCallback(1, values, true);
        return true;
    }

    private static bool TryClickJournalAccept()
    {
        var addr = GetVisibleAddon("JournalAccept");
        if (addr == null) return false;
        // Accept = button index 44 in YesAlready's reference, but the simpler
        // FireCallback-with-1 path also works for accepting quest offers.
        var values = stackalloc AtkValue[1];
        values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[0].Int = 0;
        addr->FireCallback(1, values, true);
        return true;
    }

    private static bool TryClickJournalResult()
    {
        var addr = GetVisibleAddon("JournalResult");
        if (addr == null) return false;
        var values = stackalloc AtkValue[1];
        values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[0].Int = 0;
        addr->FireCallback(1, values, true);
        return true;
    }

    private static bool TryClickMaterializeDialog()
    {
        var addr = GetVisibleAddon("MaterializeDialog");
        if (addr == null) return false;
        var values = stackalloc AtkValue[1];
        values[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        values[0].Int = 0;
        addr->FireCallback(1, values, true);
        return true;
    }

    /// <summary>
    /// Talk (NPC speech bubble) advances on a click of the whole panel rather
    /// than a button. Synthesise a mouse-down/click/up triplet with state
    /// flag 132 — the magic value YesAlready and ClickLib both use; not
    /// formally documented but empirically correct.
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

    private bool TryInteractWithTarget()
    {
        var ts = TargetSystem.Instance();
        if (ts == null) return false;

        // Prefer the existing hard target — the player picked it on purpose.
        var target = ts->GetHardTarget();
        if (target == null)
        {
            // No hard target — find the nearest interactable in the cone.
            var npc = FindInteractableInCone();
            if (npc == null) return false;
            target = (GameObject*)npc.Address;
        }
        if (target == null) return false;

        ts->InteractWithObject(target);
        return true;
    }

    /// <summary>
    /// Cone scan for the nearest interactable game object — EventNpc (quest
    /// givers, vendors), EventObj (clickable scenery, aetherytes, levequest
    /// posts), and BattleNpc isn't included here because those aren't
    /// interact-targets (combat hooks them in different ways).
    /// </summary>
    private IGameObject? FindInteractableInCone()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return null;

        var playerPos = localPlayer.Position;
        var camYaw    = getCameraYaw();
        var camForward = new Vector3(-MathF.Sin(camYaw), 0f, -MathF.Cos(camYaw));

        var maxAngle  = config.AutoTargetFovDegrees * (MathF.PI / 180f);
        // Cap at game's typical interact range — InteractWithObject already
        // handles "too far" gracefully, but a tight cap avoids picking the
        // wrong NPC when several are visible.
        const float interactRange = 10f;
        var maxDistSq = interactRange * interactRange;

        IGameObject? best = null;
        var bestScore = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.GameObjectId == localPlayer.GameObjectId) continue;
            if (!IsInteractKind(obj.ObjectKind)) continue;
            if (!obj.IsTargetable) continue;

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
            // than distance so the most-centred interactable wins.
            var score = angle * 2.0f + MathF.Sqrt(distSq) * 0.05f;
            if (score < bestScore)
            {
                bestScore = score;
                best = obj;
            }
        }

        return best;
    }

    private static bool IsInteractKind(ObjectKind kind)
        => kind == ObjectKind.EventNpc
        || kind == ObjectKind.EventObj
        || kind == ObjectKind.Aetheryte;
}
