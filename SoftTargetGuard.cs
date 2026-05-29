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
/// Fix: hook HandleTargetingKeybinds and, after the original runs,
/// ENFORCE SoftTarget == the plugin's current cone pick whenever the cam
/// is active and the plugin owns the soft target. This call runs every
/// frame, after input processing, and before the reticle manager's frame
/// read (proven: re-pinning here kills the Escape pulse). Forcing the
/// field back to the cone pick here therefore undoes ANY same-frame
/// clear — Escape's keybind clear AND the left-click commit's inlined
/// clear — before the reticle can observe a null→entity edge. No edge,
/// no pulse.
///
/// Enforce (not just restore-if-was-set) so it catches clears that
/// happen in OTHER handlers earlier in the same frame — e.g. the LMB
/// mouse-commit, whose inlined +0x88 clear has no hookable symbol. As
/// long as that clear lands before this per-frame call, the enforce
/// repairs it in time.
///
/// The plugin's TargetSelector already writes the cone pick to +0x88
/// earlier in the tick; this is a second, later-in-frame assertion of
/// the same value, so it never fights the plugin's own intent.
/// </summary>
internal sealed unsafe class SoftTargetGuard : IDisposable
{
    private delegate void HandleTargetingKeybindsDelegate(TargetSystem* thisPtr);

    private readonly Hook<HandleTargetingKeybindsDelegate>? hook;
    private readonly Func<bool> shouldGuard;
    private readonly Func<nint> getConePickAddr;

    public long CallCount    { get; private set; }
    public long RepinCount   { get; private set; }

    public SoftTargetGuard(Func<bool> shouldGuard, Func<nint> getConePickAddr)
    {
        this.shouldGuard = shouldGuard;
        this.getConePickAddr = getConePickAddr;

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
        hook!.Original(ts);

        if (!shouldGuard()) return;

        // Enforce SoftTarget == cone pick, after the original keybind
        // processing and before the reticle's frame read. Repairs any
        // same-frame clear (Escape keybind OR the LMB mouse-commit's
        // inlined clear) so the reticle never sees a null→entity edge.
        var pick = (GameObject*)getConePickAddr();
        if (pick == null) return;
        if (ts->SoftTarget != pick)
        {
            ts->SoftTarget = pick;
            RepinCount++;
        }
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }
}
