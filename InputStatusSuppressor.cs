using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace ActionCamera;

/// <summary>
/// Hooks <c>InputManager::GetInputStatus</c> and returns 0 for
/// <c>LeftMouse</c> / <c>RightMouse</c> input codes while the action
/// camera is active. The game's targeting layer reads click state
/// through this function (per SimpleTweaks' DisableClickTargeting
/// tweak) — returning 0 tells the layer "button not pressed", so the
/// click-target acquisition chain never runs and the "target
/// acquired" animation + SFX never queues.
///
/// Pairs with <see cref="MouseOverSuppressor"/> (GetMouseOverObject
/// → null) as a belt-and-suspenders kill-switch. v0.6.6 closed the
/// ray-pick result path; v0.6.9 hooked SetSoftTarget (which counter
/// data showed wasn't even called); v0.6.10 closes the
/// input-state-read path, the third leg of SimpleTweaks' approach.
///
/// Plugin's own LMB / RMB reads use Win32 GetAsyncKeyState directly
/// (see <c>InputBinding.cs</c>), bypassing InputManager entirely —
/// so this hook has zero effect on the plugin's bind firing.
///
/// Other plugins that read InputManager's click state will see "not
/// pressed" while action cam is on. Acceptable for the cam mode's
/// opinionated input contract.
/// </summary>
internal sealed unsafe class InputStatusSuppressor : IDisposable
{
    // Per SimpleTweaks' constants — these are values of InputManager's
    // internal InputCode enum, NOT VK_* virtual keys.
    private const int LeftMouseCode  = 11;
    private const int RightMouseCode = 4;

    private delegate byte GetInputStatusDelegate(InputManager* thisPtr, int inputCode);

    private readonly Hook<GetInputStatusDelegate>? hook;
    private readonly Func<bool> shouldSuppress;

    public long CallCount        { get; private set; }
    public long SuppressedCount  { get; private set; }
    public long PassThroughCount { get; private set; }

    public InputStatusSuppressor(Func<bool> shouldSuppress)
    {
        this.shouldSuppress = shouldSuppress;

        try
        {
            var addr = (nint)InputManager.MemberFunctionPointers.GetInputStatus;
            if (addr == 0)
            {
                Plugin.Log.Warning("InputStatusSuppressor: GetInputStatus address unresolved; suppression disabled.");
                return;
            }

            hook = Plugin.GameInterop.HookFromAddress<GetInputStatusDelegate>(addr, Detour);
            hook.Enable();
            Plugin.Log.Information($"InputStatusSuppressor: hook installed at 0x{addr:X}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[ActionCamera] Failed to hook GetInputStatus.");
        }
    }

    private byte Detour(InputManager* thisPtr, int inputCode)
    {
        CallCount++;
        // Only suppress reads for the two mouse-button codes. Every
        // other input id (keyboard, gamepad, scroll, etc.) passes
        // through so non-targeting code paths (e.g. movement keys)
        // keep working normally.
        if ((inputCode == LeftMouseCode || inputCode == RightMouseCode) && shouldSuppress())
        {
            SuppressedCount++;
            return 0;
        }
        PassThroughCount++;
        return hook!.Original(thisPtr, inputCode);
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }
}
