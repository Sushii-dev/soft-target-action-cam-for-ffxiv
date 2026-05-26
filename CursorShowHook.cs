using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ActionCamera;

/// <summary>
/// Root-cause fix for cursor flicker while cam mode is active.
///
/// The game's UI module re-asserts AtkCursor.IsVisible = true once per tick
/// whenever it thinks the user is hovering an interactable — which is exactly
/// what writing to TargetSystem.MouseOverTarget triggers (we do that so
/// ReAction's Field Target / Soft Target pronoun picks up the cone pick).
/// Calling AtkCursor.Hide() in our Framework.Update fights that re-assert,
/// but there's a single-render-frame window where IsVisible = true is live
/// and the cursor sprite renders. Linux/XWayland compositors mostly drop
/// these sub-frame flashes; Windows shows every one of them, hence the
/// platform-divergent flicker reports.
///
/// Solution: hook AtkCursor.Show() itself and NOP it while the user wants
/// the cam active and no legitimate consumer (RMB-hold gesture, open menu /
/// popup / cutscene) needs the cursor. With the hook installed, the game's
/// re-assert call lands here, gets suppressed, and IsVisible never flips to
/// true in the first place — so there's nothing to flicker.
///
/// Legitimate paths (menu opens, cutscene starts, plugin deactivates,
/// RMB releases) all bypass the suppression because the gate checks for
/// each of them. If the signature scan fails (post-patch, etc.) the per-
/// tick re-Hide loop in Plugin.ReconcileCursorSync remains as a safety net.
/// </summary>
internal sealed unsafe class CursorShowHook : IDisposable
{
    // AtkCursor::Show — function prologue. Tests IsVisible (this+0x1A) and
    // early-outs if already visible; otherwise sets the flag and runs the
    // cursor-show plumbing. SetVisible(true) tail-calls this, so hooking
    // here catches every internal "show the cursor" path.
    private const string ShowSignature = "48 83 EC 58 80 79 1A 00 75 6C";

    private delegate void AtkCursorShowDelegate(AtkCursor* self);

    private readonly Hook<AtkCursorShowDelegate>? hook;
    private readonly Func<bool> shouldSuppress;

    public bool IsInstalled => hook is { IsEnabled: true };

    public CursorShowHook(Func<bool> shouldSuppress)
    {
        this.shouldSuppress = shouldSuppress;

        try
        {
            hook = Plugin.GameInterop.HookFromSignature<AtkCursorShowDelegate>(
                ShowSignature, ShowDetour);
            hook.Enable();
            Plugin.Log.Debug("[ActionCamera] AtkCursor.Show hook installed.");
        }
        catch (Exception ex)
        {
            // Sig miss — the per-tick re-Hide loop will still keep the cam
            // working, just with the original flicker on Windows. Log loudly
            // so we know to chase the new sig after a patch.
            Plugin.Log.Error(ex,
                "[ActionCamera] Failed to hook AtkCursor.Show — falling back to " +
                "per-tick re-Hide. Cursor flicker may return on Windows until the " +
                "signature is updated.");
        }
    }

    public void Dispose()
    {
        // Disable BEFORE the caller turns the cursor back on. Disposing the
        // hook removes the suppression so the final Show() call in
        // CameraController.RequestShowCursor() actually shows the cursor.
        hook?.Disable();
        hook?.Dispose();
    }

    private void ShowDetour(AtkCursor* self)
    {
        if (shouldSuppress())
        {
            // Drop the call. IsVisible stays false; no render-side flicker.
            return;
        }
        hook!.Original(self);
    }
}
