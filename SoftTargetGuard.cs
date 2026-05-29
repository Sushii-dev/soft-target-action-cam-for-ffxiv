using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace ActionCamera;

/// <summary>
/// Stops the soft-target reticle "acquire" pulse (the spinning 3-line
/// ring disappearing + reappearing with a sound) that fired whenever the
/// game cleared the SoftTarget field on Escape / cancel-target.
///
/// Mechanism (confirmed by decomp research + runtime testing):
/// the reticle is drawn by TargetSystem's TargetCircleManager (+0x20),
/// which POLLS SoftTarget (+0x88) every frame and plays the acquire
/// animation + SFX on a null→entity pointer transition. The plugin
/// writes SoftTarget directly every frame (cone targeting), so at rest
/// the value never changes and the reticle is silent. But
/// HandleTargetingKeybinds — the per-frame targeting-keybind dispatcher,
/// which handles Escape / "clear target" — zeroes +0x88 directly
/// (inlined, NOT via SetSoftTarget, which is why hooking SetSoftTarget
/// saw zero calls). The plugin's next-frame re-write then produces the
/// null→entity edge the reticle animates.
///
/// Fix: hook HandleTargetingKeybinds, snapshot SoftTarget before the
/// original runs, and if the original cleared it while the action camera
/// is active, restore the snapshot in the SAME call — before the reticle
/// manager's frame read. No null is ever observed, so no transition, no
/// pulse. The plugin's own cone writes are untouched (they run elsewhere
/// in the tick); this only undoes the game's clear.
///
/// This covers the Escape trigger deterministically. The LMB trigger
/// clears +0x88 from a different, inlined code path (the mouse click-
/// commit handler) and is handled separately.
/// </summary>
internal sealed unsafe class SoftTargetGuard : IDisposable
{
    private delegate void HandleTargetingKeybindsDelegate(TargetSystem* thisPtr);

    private readonly Hook<HandleTargetingKeybindsDelegate>? hook;
    private readonly Func<bool> shouldGuard;

    public long CallCount    { get; private set; }
    public long RepinCount   { get; private set; }

    public SoftTargetGuard(Func<bool> shouldGuard)
    {
        this.shouldGuard = shouldGuard;

        try
        {
            var addr = (nint)TargetSystem.MemberFunctionPointers.HandleTargetingKeybinds;
            if (addr == 0)
            {
                Plugin.Log.Warning("SoftTargetGuard: HandleTargetingKeybinds address unresolved; guard disabled.");
                return;
            }

            hook = Plugin.GameInterop.HookFromAddress<HandleTargetingKeybindsDelegate>(addr, Detour);
            hook.Enable();
            Plugin.Log.Information($"SoftTargetGuard: hook installed at 0x{addr:X}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[ActionCamera] Failed to hook HandleTargetingKeybinds.");
        }
    }

    private void Detour(TargetSystem* ts)
    {
        CallCount++;

        var softBefore = ts->SoftTarget;
        hook!.Original(ts);

        // Re-pin in the same call if the keybind handler cleared our soft
        // target while the cam is active. Restoring before the reticle
        // manager's frame read means the null→entity edge never exists.
        if (shouldGuard()
            && softBefore != null
            && ts->SoftTarget == null)
        {
            ts->SoftTarget = softBefore;
            RepinCount++;
        }
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }
}
