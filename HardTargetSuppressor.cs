using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace ActionCamera;

/// <summary>
/// Suppresses FFXIV's hardcoded soft-target -> hard-target promotion when the player
/// uses an action with no existing hard target. Per FFXIV's own UI guide: if no hard
/// target exists when an action resolves on a soft target, the game converts the soft
/// target into a hard one. That's not gated by any ConfigOption — it's wired into the
/// action pipeline. Hooking TargetSystem.SetHardTarget and rejecting the specific
/// promotion call is the only reliable countermeasure.
/// </summary>
internal sealed unsafe class HardTargetSuppressor : IDisposable
{
    private delegate bool SetHardTargetDelegate(
        TargetSystem* thisPtr, GameObject* target, bool ignoreModes, bool a4, int a5);

    private readonly Hook<SetHardTargetDelegate>? hook;
    private readonly Configuration config;
    private readonly TargetSelector selector;
    private readonly Func<bool> isCameraActive;

    // One-shot bypass: when set, the very next SetHardTarget call passes through
    // to the original regardless of suppression state. Consumed inside the detour.
    // Callers should still wrap their SetHardTarget-triggering assignment in
    // try/finally + CancelAllow() so the flag can never leak past its intended
    // call (defence against any edge case where the setter short-circuits without
    // invoking the native function).
    private bool allowNext;

    public void AllowNext()   => allowNext = true;
    public void CancelAllow() => allowNext = false;

    public HardTargetSuppressor(Configuration config, TargetSelector selector, Func<bool> isCameraActive)
    {
        this.config = config;
        this.selector = selector;
        this.isCameraActive = isCameraActive;

        var addr = (nint)TargetSystem.MemberFunctionPointers.SetHardTarget;
        if (addr == 0)
        {
            Plugin.Log.Warning("HardTargetSuppressor: SetHardTarget address unresolved; suppression disabled.");
            return;
        }

        hook = Plugin.GameInterop.HookFromAddress<SetHardTargetDelegate>(addr, Detour);
        hook.Enable();
    }

    private bool Detour(TargetSystem* ts, GameObject* target, bool ign, bool a4, int a5)
    {
        if (allowNext)
        {
            allowNext = false;
            return hook!.Original(ts, target, ign, a4, a5);
        }

        // Only suppress the precise "promote soft to hard on action use" case:
        //   - feature toggled on
        //   - cone is currently active
        //   - no prior hard target (== promotion, not a re-target)
        //   - incoming target equals the cone's current pick
        // Manual clicks usually have a different pre-state; MMB/Tab cycle to a
        // different target won't match the cone pick. If the user MMB-cycles
        // onto the cone pick itself the first press is swallowed — acceptable.
        if (config.SuppressSoftToHardPromotion
            && isCameraActive()
            && ts->Target == null
            && target != null
            && (nint)target == selector.CachedBestAddress)
        {
            return false;
        }
        return hook!.Original(ts, target, ign, a4, a5);
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }
}
