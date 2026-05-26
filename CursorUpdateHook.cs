using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ActionCamera;

/// <summary>
/// Hooks <c>AtkUnitManager::UpdateCursor</c> — the per-frame routine that
/// reads mouse-activity state and writes <c>Client::System::Input::Cursor.IsCursorVisible</c>
/// (the byte the software-cursor renderer reads to decide whether to draw
/// the sprite). This is the path the v0.5.14-0.5.17 attempts kept missing.
///
/// Background. FFXIV has two different cursor structs:
///   - <see cref="AtkCursor"/> (UI policy, <c>IsVisible</c> at +0x1A) — the
///     one we've been hiding via <see cref="AtkCursor.Hide()"/>.
///   - <c>Client::System::Input::Cursor</c> (render state, <c>IsCursorVisible</c>
///     at +0x0B) — what the SW-cursor draw actually checks.
///
/// <c>UpdateCursor</c> bridges them — it consumes mouse input + addon focus
/// each tick and writes the render-state byte. Scroll-wheel events tick
/// <c>CursorInputData.MouseWheel</c>, which triggers <c>UpdateCursor</c> to
/// re-assert visibility for the duration of one frame — which is exactly the
/// flicker our SW-cursor users see. Hooking <c>UpdateCursor</c> and skipping
/// the original call while cam mode is active stops the write at its source.
///
/// SimpleTweaks ships the same hook in production (<c>Utility/Common.cs</c>
/// <c>UpdateCursorDetour</c>) for its ForceMouseCursor / cursor-type-lock
/// features, which is good evidence the hook is patch-stable.
/// </summary>
internal sealed unsafe class CursorUpdateHook : IDisposable
{
    // AtkUnitManager::UpdateCursor — call-site signature anchored by the
    // surrounding three consecutive E8 CALLs and a movaps xmm1,xmm6
    // (0F 28 CE). HookFromSignature follows the relative CALL target.
    // Mirrors what SimpleTweaks uses; stable across patches in their build.
    private const string UpdateCursorSignature =
        "E8 ?? ?? ?? ?? 0F 28 CE 48 8B CB E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 0F 28 CE";

    private delegate void UpdateCursorDelegate(IntPtr atkUnitManager);

    private readonly Hook<UpdateCursorDelegate>? hook;
    private readonly Func<bool> shouldSuppress;

    public bool IsInstalled => hook is { IsEnabled: true };

    /// <summary>Diagnostic counter — incremented every time the detour suppresses a call.</summary>
    public long SuppressedCount { get; private set; }

    /// <summary>Diagnostic counter — incremented every time the detour passes the call through.</summary>
    public long PassThroughCount { get; private set; }

    public CursorUpdateHook(Func<bool> shouldSuppress)
    {
        this.shouldSuppress = shouldSuppress;

        try
        {
            hook = Plugin.GameInterop.HookFromSignature<UpdateCursorDelegate>(
                UpdateCursorSignature, UpdateCursorDetour);
            hook.Enable();
            Plugin.Log.Information(
                "[ActionCamera] AtkUnitManager.UpdateCursor hook installed " +
                "(targets the SW-cursor render-state byte that the Show hook " +
                "couldn't reach).");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex,
                "[ActionCamera] Failed to hook AtkUnitManager.UpdateCursor — " +
                "cursor flicker on SW-cursor users will continue. The Show " +
                "hook + per-tick re-Hide loop remain as a partial fallback.");
        }
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }

    private void UpdateCursorDetour(IntPtr self)
    {
        if (shouldSuppress())
        {
            // Skip the original. The per-frame cursor-state update doesn't
            // run, so the render-state byte stays at whatever the prior
            // tick set it to (false, while cam is active). The renderer
            // reads false → no sprite → no flicker.
            SuppressedCount++;
            return;
        }
        PassThroughCount++;
        hook!.Original(self);
    }
}
