using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace ActionCamera;

/// <summary>
/// Single entry point for "fire whatever is in hotbar X slot Y". Wraps
/// <see cref="RaptureHotbarModule.ExecuteSlotById"/> — the same path the
/// game itself drives when the user presses a slot's bound keyboard
/// hotkey, so Macro / Item / GeneralAction / PetAction / etc. all
/// dispatch correctly and any plugin that hooks the slot path
/// (ReAction, MOAction, Redirect) layers on top automatically.
///
/// All gating (cam active, cursor hidden, focused, menu, ImGui) lives in
/// <see cref="MouseBindController"/>; this class is dumb and just makes
/// the call. The <see cref="Fire"/> method does spam-protection via
/// <c>ActionManager.GetActionStatus</c> for the Action / Item paths so
/// that clicking through a GCD doesn't fill the screen with "Unable to
/// use that action now" toasts. Empty slots short-circuit silently.
/// </summary>
internal static unsafe class HotbarFirer
{
    /// <summary>
    /// Hotbar id range 0..17 inclusive — 10 standard + 8 cross.
    /// </summary>
    public const uint MaxHotbarId = 17;

    /// <summary>
    /// Slot id range 0..15 inclusive — 16 slots per hotbar.
    /// </summary>
    public const uint MaxSlotId = 15;

    /// <summary>
    /// Try to fire the slot at <paramref name="hotbarId"/> / <paramref name="slotId"/>.
    /// Returns true if the game accepted the call (i.e. ExecuteSlotById was
    /// invoked); false if the bar/slot is out of range, empty, or the
    /// resolved action is on cooldown / unusable right now.
    /// </summary>
    public static bool Fire(uint hotbarId, uint slotId)
    {
        if (hotbarId > MaxHotbarId) return false;
        if (slotId   > MaxSlotId)   return false;

        var module = RaptureHotbarModule.Instance();
        if (module == null) return false;

        var slot = module->GetSlotById(hotbarId, slotId);
        if (slot == null) return false;

        // Empty slot — no command bound. Don't waste the fire / risk a
        // game-side complaint.
        if (slot->CommandType == RaptureHotbarModule.HotbarSlotType.Empty)
            return false;

        // Spam gate. For Action and CraftAction the game emits a toast
        // when called while on cooldown or otherwise unusable; for Item
        // it raises a chat error. Both feel noisy if rapid-fire clicks
        // happen during a GCD, so we short-circuit before the call.
        // GeneralAction / Macro / Mount / etc. don't have GetActionStatus
        // semantics, so we let them through unconditionally — the slot
        // dispatcher will handle its own "can't do that right now"
        // behaviour quietly.
        var am = ActionManager.Instance();
        if (am != null)
        {
            switch (slot->CommandType)
            {
                case RaptureHotbarModule.HotbarSlotType.Action:
                case RaptureHotbarModule.HotbarSlotType.CraftAction:
                    if (am->GetActionStatus(ActionType.Action, slot->CommandId) != 0)
                        return false;
                    break;
                case RaptureHotbarModule.HotbarSlotType.Item:
                    if (am->GetActionStatus(ActionType.Item, slot->CommandId) != 0)
                        return false;
                    break;
            }
        }

        module->ExecuteSlotById(hotbarId, slotId);
        return true;
    }

    /// <summary>
    /// Resolve a slot to a short human-readable label for the config UI —
    /// e.g. <c>"Action #9 (Fast Blade)"</c> or <c>"Macro #2"</c>. Returns
    /// false when the bar/slot is out of range. Out-params describe the
    /// resolved slot so the UI can hint that an empty slot will silently
    /// no-op when fired.
    /// </summary>
    public static bool TryGetSlotPreview(uint hotbarId, uint slotId, out string label, out bool isEmpty)
    {
        label = string.Empty;
        isEmpty = true;

        if (hotbarId > MaxHotbarId) return false;
        if (slotId   > MaxSlotId)   return false;

        var module = RaptureHotbarModule.Instance();
        if (module == null) return false;

        var slot = module->GetSlotById(hotbarId, slotId);
        if (slot == null) return false;

        if (slot->CommandType == RaptureHotbarModule.HotbarSlotType.Empty)
        {
            label = "(empty)";
            isEmpty = true;
            return true;
        }

        isEmpty = false;

        // Minimal preview for v0.6.0 — shows the resolved command type and
        // id without a Lumina lookup. Lets the user verify by id that they
        // picked the right slot; a friendlier name resolution (e.g. "Fast
        // Blade") could be wired up via the Action / Item / Macro sheets
        // in a follow-up.
        label = $"{slot->CommandType} #{slot->CommandId}";
        return true;
    }
}
