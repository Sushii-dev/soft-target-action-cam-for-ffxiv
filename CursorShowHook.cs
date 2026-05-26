using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ActionCamera;

/// <summary>
/// Triple-layer cursor-visibility suppression while the user wants the action
/// cam active.
///
/// The game has multiple paths that can flip AtkCursor.IsVisible to true:
///
///   1. AtkCursor::Show() — the public method. Called by UI events (popup
///      open, addon focus, etc.). We hook + NOP it.
///
///   2. AtkCursor::SetVisible(bool) — a lower-level dispatcher. v0.5.14.0
///      observed flicker on scroll-zoom even with the Show hook installed,
///      meaning some path writes the field through SetVisible *without*
///      tail-calling Show. We hook this too and convert true→false when
///      suppression is active.
///
///   3. Direct field write — the game's input pipeline may stomp
///      AtkCursor.IsVisible = true on input events without going through any
///      function we can practically hook. As a render-time belt, the plugin
///      writes IsVisible = false from UiBuilder.Draw (see Plugin.cs) every
///      render frame. That handler doesn't live here — it's wired in Plugin
///      so the Draw subscription has the same lifetime as the rest of the
///      plugin.
///
/// Legitimate cursor-return paths (RMB release, menu / cutscene / popup
/// open, sticky-off deactivation, plugin unload) bypass the suppression
/// because the gate checks for each of them. If any signature scan fails
/// after a game patch, the per-tick re-Hide loop in
/// Plugin.ReconcileCursorSync remains as a final fallback.
/// </summary>
internal sealed unsafe class CursorShowHook : IDisposable
{
    // AtkCursor::Show — function prologue. Tests IsVisible (this+0x1A) and
    // early-outs if already visible; otherwise sets the flag and runs the
    // cursor-show plumbing.
    private const string ShowSignature = "48 83 EC 58 80 79 1A 00 75 6C";

    // SetVisible hook removed in v0.5.16.0. The sig `E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 8B FA`
    // turned out to be too generic — it matches many call sites in the
    // FFXIV binary and HookFromSignature locked onto a non-SetVisible
    // function. The wrong-function hook (with our (this, bool) delegate
    // misinterpreting the real arguments) broke scroll-zoom: after a
    // single scroll the cursor stayed visible, mouse motion turned into
    // super-fast camera input, and toggling the cam off left the cursor
    // un-warped with no camera response. Lesson: never ship a call-site
    // sig without verifying it's unique to the target function.

    private delegate void AtkCursorShowDelegate(AtkCursor* self);

    private readonly Hook<AtkCursorShowDelegate>? showHook;
    private readonly Func<bool> shouldSuppress;

    public bool IsShowHookInstalled => showHook is { IsEnabled: true };

    public CursorShowHook(Func<bool> shouldSuppress)
    {
        this.shouldSuppress = shouldSuppress;

        try
        {
            showHook = Plugin.GameInterop.HookFromSignature<AtkCursorShowDelegate>(
                ShowSignature, ShowDetour);
            showHook.Enable();
            Plugin.Log.Debug("[ActionCamera] AtkCursor.Show hook installed.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex,
                "[ActionCamera] Failed to hook AtkCursor.Show — falling back to " +
                "per-tick re-Hide. Cursor flicker may persist until the signature " +
                "is updated.");
        }
    }

    public void Dispose()
    {
        // Disable BEFORE the caller turns the cursor back on. Disposing the
        // hook removes the suppression so the final Show() call in
        // CameraController.RequestShowCursor() actually shows the cursor.
        showHook?.Disable();
        showHook?.Dispose();
    }

    private void ShowDetour(AtkCursor* self)
    {
        if (shouldSuppress())
        {
            // Drop the call. IsVisible stays false; no render-side flicker.
            return;
        }
        showHook!.Original(self);
    }
}
