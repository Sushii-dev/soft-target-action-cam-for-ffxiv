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
    /// Sentinel "no target / let the game auto-resolve" id used by
    /// <see cref="ActionManager.UseAction"/>. Lower 32 bits = uint.MaxValue
    /// masked off — matches FFXIVClientStructs' default.
    /// </summary>
    private const ulong EmptyTargetId = 0xE000_0000UL;

    /// <summary>
    /// Try to fire the slot at <paramref name="hotbarId"/> / <paramref name="slotId"/>.
    /// Returns true if the game accepted the call (i.e. an action was
    /// invoked); false if the bar/slot is out of range, empty, or the
    /// resolved action is on cooldown / unusable right now.
    ///
    /// For <c>Action</c> / <c>CraftAction</c> slots with a resolvable
    /// target (hard target preferred, soft target fallback), this calls
    /// <see cref="ActionManager.UseAction"/> directly with the explicit
    /// target id. That bypasses the game's soft→hard promotion path —
    /// promotion was previously rejected by <c>HardTargetSuppressor</c>
    /// but the brief promotion still fired UI cues (re-highlight
    /// animation + click SFX) that the user perceived as the soft-target
    /// reticle "reattaching" on every bound fire (v0.6.0 playtest).
    /// Pre-resolving the target avoids the promotion path entirely.
    ///
    /// All other slot types — including Action with no resolvable target
    /// — go through <see cref="RaptureHotbarModule.ExecuteSlotById"/>, the
    /// same path the game drives from a keyboard hotkey, so Macro / Item
    /// / GeneralAction / PetAction / area-targeted ground reticle / etc.
    /// dispatch correctly and any plugin that hooks the slot path
    /// (ReAction, MOAction, Redirect) layers on top automatically.
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

        var am = ActionManager.Instance();
        if (am == null) return false;

        var t = slot->CommandType;
        if (t == RaptureHotbarModule.HotbarSlotType.Action ||
            t == RaptureHotbarModule.HotbarSlotType.CraftAction)
        {
            var targetId = ResolveExplicitTarget();
            if (targetId != EmptyTargetId)
            {
                // Queueable gate (v0.6.24): check status with cooldown
                // checks DISABLED. A rolling GCD / active cast then reads
                // as usable (0), so the press flows through to UseAction —
                // which, in native mode, auto-queues it to fire the instant
                // the GCD clears, exactly like a real keybind. Only truly-
                // unusable presses (no/invalid target, out of range, wrong
                // job, not enough resources) return non-zero and are
                // dropped, keeping "Unable to use that action now" toasts
                // rare. Edge-fire means at most one toast per press.
                //
                // Previously this gate used the default checkRecastActive:
                // true, which aborted during the GCD tail and prevented
                // queueing — the felt unresponsiveness vs native binds.
                if (am->GetActionStatus(ActionType.Action, slot->CommandId, targetId,
                        checkRecastActive: false, checkCastingActive: false) != 0)
                    return false;

                // Direct fire with explicit target, native mode (default).
                // Native mode = real-keypress semantics: fires now if ready,
                // auto-queues if within the GCD/anim-lock window. Explicit
                // target avoids slot re-resolution → no soft→hard promotion.
                return am->UseAction(ActionType.Action, slot->CommandId, targetId);
            }

            // No target resolvable — fall through to ExecuteSlotById so
            // area-targeted (ground reticle) actions get their normal
            // null-target dispatch. ExecuteSlotById is the keypress path
            // and native-queues too.
        }
        else if (t == RaptureHotbarModule.HotbarSlotType.Item)
        {
            // Same queueable gate for items (cooldown ignored). Items
            // largely don't participate in the GCD queue, but matching
            // native keypress behaviour via ExecuteSlotById below is
            // correct either way.
            if (am->GetActionStatus(ActionType.Item, slot->CommandId, EmptyTargetId,
                    checkRecastActive: false, checkCastingActive: false) != 0)
                return false;
        }

        module->ExecuteSlotById(hotbarId, slotId);
        return true;
    }

    /// <summary>
    /// Resolve the player's currently-intended target for an action fire.
    /// Mirrors FFXIV's own priority (hard target wins; soft target falls
    /// back). Returns <see cref="EmptyTargetId"/> when nothing is
    /// targeted — the caller treats that as "let the game decide" and
    /// falls back to <c>ExecuteSlotById</c>.
    /// </summary>
    private static ulong ResolveExplicitTarget()
    {
        var tm = Plugin.TargetManager;
        var t = tm.Target ?? tm.SoftTarget;
        return t?.GameObjectId ?? EmptyTargetId;
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
