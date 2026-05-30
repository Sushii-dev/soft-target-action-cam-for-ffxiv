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
/// the call.
///
/// Action / CraftAction fire through <c>ActionManager.UseAction</c> in
/// native mode, which reproduces real-keypress semantics including the
/// game's input QUEUE — a press during the GCD / animation-lock tail is
/// buffered and auto-fires when the GCD clears, so binds feel as
/// responsive as native hotbar keys (v0.6.24). The pre-fire
/// <c>GetActionStatus</c> check runs with cooldown checks disabled, so it
/// blocks only genuinely-unusable presses (no target / range / job /
/// resources) — keeping "Unable to use that action now" toasts rare
/// without defeating the queue. Empty slots short-circuit silently.
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
    /// Fire the slot at <paramref name="hotbarId"/> / <paramref name="slotId"/>
    /// exactly like a native hotbar keypress, via
    /// <see cref="RaptureHotbarModule.ExecuteSlotById"/>. Returns false only
    /// when the bar/slot is out of range or empty.
    ///
    /// ExecuteSlotById is the literal keyboard-hotkey path, so every slot
    /// kind behaves identically to pressing the key: instants fire, cast-
    /// time abilities INITIATE their cast (the v0.6.37 bug — the old
    /// UseAction-explicit-target route failed to start casts), the native
    /// input QUEUE buffers a press in the GCD/anim-lock tail, hold-to-
    /// repeat re-fires each frame with the game's own rate-limiting (no
    /// toast spam), and the action targets the soft/hard target per the
    /// game's normal rules. Macro / Item / GeneralAction / PetAction /
    /// area-targeted ground reticle all dispatch correctly, and ReAction /
    /// MOAction / Redirect layer on top.
    ///
    /// The soft→hard promotion that this path triggers (and its reticle
    /// pulse + acquire sound — the reason v0.6.2 detoured to UseAction) is
    /// now neutralised fire-path-agnostically by SoftTargetGuard (+0x88
    /// re-pin in HandleTargetingKeybinds), MouseOverSuppressor,
    /// SoundSuppressor, and HardTargetSuppressor — so the detour is no
    /// longer needed, and dropping it is what lets casts initiate.
    /// </summary>
    public static bool Fire(uint hotbarId, uint slotId)
    {
        if (hotbarId > MaxHotbarId) return false;
        if (slotId   > MaxSlotId)   return false;

        var module = RaptureHotbarModule.Instance();
        if (module == null) return false;

        var slot = module->GetSlotById(hotbarId, slotId);
        if (slot == null) return false;

        // Empty slot — nothing bound.
        if (slot->CommandType == RaptureHotbarModule.HotbarSlotType.Empty)
            return false;

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
