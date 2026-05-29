using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace ActionCamera;

/// <summary>
/// Hooks <c>TargetSystem::SetSoftTarget</c> and rejects every call while
/// the action camera is active. Pairs with <see cref="MouseOverSuppressor"/>:
/// the GetMouseOverObject hook (v0.6.6+) shut down ONE path the game uses
/// to acquire targets on click, but the click handler also reads the
/// cached <c>ts->MouseOverTarget</c> FIELD directly and calls SetSoftTarget
/// off that — and the plugin's TargetSelector writes that field every
/// frame as part of the cone-targeting feature, so the click handler
/// always finds an entity there even while GetMouseOverObject is returning
/// null. The setter call fires the "target acquired" animation + SFX
/// because it's the game-side setter (plugin's writes use direct field
/// access, which bypasses the animation hook).
///
/// Blocking SetSoftTarget at the function level closes that gap.
/// Plugin's direct field writes (TargetSelector.DirectSetSoftTarget,
/// v0.6.5) are unaffected because they don't go through this function.
/// Only game-side acquisition calls — click handler, vanilla auto-soft,
/// /softtarget macro — get rejected.
///
/// Side effect: while userWantsActive is on, no game-driven SoftTarget
/// change can land. Cone-driven SoftTarget (plugin owns it) is the only
/// source of truth in action-cam mode. Acceptable for the action-cam
/// design contract; user exits cam mode for vanilla targeting workflows.
/// </summary>
internal sealed unsafe class SoftTargetSuppressor : IDisposable
{
    private delegate bool SetSoftTargetDelegate(TargetSystem* thisPtr, GameObject* target);

    private readonly Hook<SetSoftTargetDelegate>? hook;
    private readonly Func<bool> shouldSuppress;

    // Surface to DebugOverlay so /actioncam debug shows whether the hook
    // is being invoked during clicks (and how often the suppression gate
    // matches).
    public long CallCount        { get; private set; }
    public long SuppressedCount  { get; private set; }
    public long PassThroughCount { get; private set; }

    public SoftTargetSuppressor(Func<bool> shouldSuppress)
    {
        this.shouldSuppress = shouldSuppress;

        try
        {
            var addr = (nint)TargetSystem.MemberFunctionPointers.SetSoftTarget;
            if (addr == 0)
            {
                Plugin.Log.Warning("SoftTargetSuppressor: SetSoftTarget address unresolved; suppression disabled.");
                return;
            }

            hook = Plugin.GameInterop.HookFromAddress<SetSoftTargetDelegate>(addr, Detour);
            hook.Enable();
            Plugin.Log.Information($"SoftTargetSuppressor: hook installed at 0x{addr:X}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[ActionCamera] Failed to hook SetSoftTarget.");
        }
    }

    private bool Detour(TargetSystem* ts, GameObject* target)
    {
        CallCount++;
        if (shouldSuppress())
        {
            SuppressedCount++;
            return false;
        }
        PassThroughCount++;
        return hook!.Original(ts, target);
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }
}
