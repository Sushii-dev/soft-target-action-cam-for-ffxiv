using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;

namespace ActionCamera;

/// <summary>
/// Drives the BETA mouse-bind → hotbar-fire pipeline. Polled once per
/// framework update from <see cref="Plugin.OnFrameworkUpdate"/>.
///
/// Hard gates (all must pass before any bind is considered):
///   • <c>Configuration.BetaMouseBindsEnabled</c> is on
///   • The action camera is active
///   • The game cursor is hidden (i.e. we're in cursor-locked mode)
///   • The FFXIV window has foreground focus
///   • No menu / cutscene / focused addon is up
///   • ImGui isn't capturing the mouse
///
/// All those together mean vanilla mouse semantics (UI click, world
/// raycast at a moving cursor, RMB drag rotating the camera) are
/// physically unreachable: the cursor is hidden and re-centred each
/// frame. That's why this controller can fire actions on raw click
/// events without ever swallowing native WM_*BUTTON* messages.
///
/// Per-bind:
///   • Rising-edge detect via a per-bind <c>prevDown</c> entry — holding
///     the button does not repeat. Matches the BDO press-per-attack
///     feel and avoids the GCD-toast spam class entirely.
///   • Modifier matching is strict and mutually exclusive — a bind
///     tagged <c>Shift</c> only fires when Shift is held AND Ctrl/Alt
///     are not. This is also the precedence mechanism: a plain
///     <c>LMB</c> bind and a <c>Shift+LMB</c> bind can never both
///     match the same physical state, so longest-match falls out
///     naturally without extra search code.
///
/// Edge state lives in this controller, never persisted. Plugin reload
/// or beta-toggle flip resets cleanly because the dict is recreated
/// with the controller.
/// </summary>
internal sealed class MouseBindController
{
    private readonly Configuration config;
    private readonly Func<bool> isCamActive;
    private readonly Func<bool> isCursorVisible;
    private readonly Func<bool> isMenuOpen;

    // Per-bind down-state from the previous frame, keyed by reference.
    // List<MouseBind> mutations (add / remove rows) don't touch existing
    // entries, so reference identity is stable as long as the bind object
    // itself isn't replaced. Stale entries from removed binds linger but
    // never match anything — negligible leak, cleared on plugin reload.
    private readonly Dictionary<MouseBind, bool> prevDown = new();

    public MouseBindController(
        Configuration config,
        Func<bool> isCamActive,
        Func<bool> isCursorVisible,
        Func<bool> isMenuOpen)
    {
        this.config         = config;
        this.isCamActive    = isCamActive;
        this.isCursorVisible = isCursorVisible;
        this.isMenuOpen     = isMenuOpen;
    }

    public void Update()
    {
        if (!config.BetaMouseBindsEnabled) return;
        if (!isCamActive())                return;
        if (isCursorVisible())             return;
        if (isMenuOpen())                  return;
        if (!InputBinding.IsGameFocused()) return;

        // Snapshot modifier state once per frame so all binds in this
        // tick see the same answer (avoids a tiny race where Shift
        // released mid-loop would let a later bind match the wrong
        // state). The cost is negligible compared to the loop body.
        var shiftDown = InputBinding.IsDownRaw(VirtualKey.SHIFT);
        var ctrlDown  = InputBinding.IsDownRaw(VirtualKey.CONTROL);
        var altDown   = InputBinding.IsDownRaw(VirtualKey.MENU);

        foreach (var bind in config.MouseBinds)
        {
            if (bind == null) continue;
            if (bind.Button == VirtualKey.NO_KEY) continue;

            // IsDown filters WantCaptureMouse already, which protects
            // clicks inside the plugin's config window from firing
            // bindings even if the user has the config open at the
            // moment they're in cursor-locked mode.
            var down = InputBinding.IsDown(bind.Button);

            prevDown.TryGetValue(bind, out var prev);
            prevDown[bind] = down;

            if (!down || prev) continue;
            if (!ModifierMatches(bind.Modifier, shiftDown, ctrlDown, altDown)) continue;

            // Snapshot SoftTarget before fire. UseAction internally
            // clears the game's SoftTarget pointer mid-frame ("consumed
            // the target" bookkeeping); without a same-frame restore,
            // TargetSelector's next-frame WriteSoftTarget produces a
            // null→entity pointer transition, and the game's UI subsystem
            // reads that as a fresh acquire — playing the ring re-attach
            // animation + sound on every successful bound fire. Restoring
            // the pointer here (after fire, before frame end) keeps the
            // renderer's view of SoftTarget stable across the frame so
            // the transition never reaches the UI layer.
            //
            // Only restore when the value the game left looks like a
            // mid-fire clear / change (null or a different entity than
            // what we held). If the user / another plugin set SoftTarget
            // to something else after our pre-snapshot, leave it alone.
            var preSoft = Plugin.TargetManager.SoftTarget;

            HotbarFirer.Fire(bind.HotbarId, bind.SlotId);

            if (preSoft != null)
            {
                var postSoft = Plugin.TargetManager.SoftTarget;
                if (postSoft?.GameObjectId != preSoft.GameObjectId)
                    TargetSelector.DirectSetSoftTarget(preSoft);
            }
        }
    }

    private static bool ModifierMatches(MouseBindModifier required, bool shift, bool ctrl, bool alt)
        => required switch
        {
            MouseBindModifier.None  => !shift && !ctrl && !alt,
            MouseBindModifier.Shift =>  shift && !ctrl && !alt,
            MouseBindModifier.Ctrl  =>  ctrl  && !shift && !alt,
            MouseBindModifier.Alt   =>  alt   && !shift && !ctrl,
            _ => false,
        };
}
