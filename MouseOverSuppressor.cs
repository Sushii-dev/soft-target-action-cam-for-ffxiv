using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace ActionCamera;

/// <summary>
/// Hooks <c>TargetSystem::GetMouseOverObject</c> — the game's ray-pick
/// "what world entity is under the cursor right now" function — and
/// returns null while the action camera is active. With cursor hidden
/// + center-locked, every LMB / RMB click ray-hits whatever entity is
/// under the crosshair (typically the soft-targeted enemy). The
/// game's input-pipeline release-handler then runs the click-target
/// acquisition chain off that ray-pick result: plays the "target
/// acquired" sound, animates the soft-target reticle re-attach,
/// calls SetSoftTarget / SetHardTarget / InteractWithObject. Hooking
/// SetHardTarget alone (the previous v0.6.x approach) only caught
/// the tail of that chain — the sound + animation queued upstream
/// from the ray-pick had already fired.
///
/// Returning null from GetMouseOverObject is the single upstream
/// choke point: the click-target chain finds nothing under cursor,
/// so it short-circuits before any acquisition work runs. Same
/// pattern as SimpleTweaks' DisableClickTargeting tweak.
///
/// Predicate keeps the suppression scoped to action-camera mode:
/// outside that state, the cursor is visible and the user has a
/// legitimate need for vanilla hover + click targeting.
/// </summary>
internal sealed unsafe class MouseOverSuppressor : IDisposable
{
    // GameObjectArray + Camera are opaque pointers from this code's
    // perspective — we pass them through to Original unchanged.
    private delegate GameObject* GetMouseOverObjectDelegate(
        TargetSystem* ts, int x, int y, nint objectsArray, nint camera);

    private readonly Hook<GetMouseOverObjectDelegate>? hook;
    private readonly Func<bool> shouldSuppress;

    // Debug counters surfaced via /actioncam debug. Help diagnose "the hook
    // installed but does it actually fire on click?" without spamming the
    // log every per-frame hover call.
    public long CallCount        { get; private set; }
    public long SuppressedCount  { get; private set; }
    public long PassThroughCount { get; private set; }

    public MouseOverSuppressor(Func<bool> shouldSuppress)
    {
        this.shouldSuppress = shouldSuppress;

        try
        {
            // Prefer FFXIVClientStructs' resolved MemberFunctionPointer when
            // available — same path HardTargetSuppressor uses. The struct
            // exposes GetMouseOverObject as a typed member, so the address
            // is sig-scanned once at CS init time and we just reference it.
            var addr = (nint)TargetSystem.MemberFunctionPointers.GetMouseOverObject;
            if (addr == 0)
            {
                Plugin.Log.Warning("MouseOverSuppressor: GetMouseOverObject address unresolved; suppression disabled.");
                return;
            }

            hook = Plugin.GameInterop.HookFromAddress<GetMouseOverObjectDelegate>(addr, Detour);
            hook.Enable();
            Plugin.Log.Information($"MouseOverSuppressor: hook installed at 0x{addr:X}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[ActionCamera] Failed to hook GetMouseOverObject.");
        }
    }

    private GameObject* Detour(TargetSystem* ts, int x, int y, nint objectsArray, nint camera)
    {
        CallCount++;
        if (shouldSuppress())
        {
            SuppressedCount++;
            return null;
        }
        PassThroughCount++;
        return hook!.Original(ts, x, y, objectsArray, camera);
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }
}
