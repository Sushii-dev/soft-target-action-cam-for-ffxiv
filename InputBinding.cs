using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;

namespace ActionCamera;

/// <summary>
/// Centralised input polling. Replaces direct Plugin.KeyState[] reads so the
/// plugin can uniformly bind anything FFXIV exposes a virtual-key code for —
/// including mouse buttons (which FFXIV's keystate buffer does not include)
/// and the generic modifier keys (CONTROL/SHIFT/MENU).
///
/// All polling goes through Win32 GetAsyncKeyState. That works regardless of
/// which window has focus, so we explicitly gate on the FFXIV process's main
/// window being the foreground window — otherwise a key/click in another app
/// would fire bindings.
///
/// Two flavours:
///   IsDownRaw — focus-gated only. Used by the config key-picker so the user
///               can capture binds for keys/buttons that ImGui would otherwise
///               swallow while the config window has input.
///   IsDown    — focus-gated AND ImGui-capture-gated. Used by the runtime
///               keybind handlers so typing into a text field or hovering
///               the cursor over the config window doesn't trigger binds.
/// </summary>
internal static class InputBinding
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    // Lazily resolved on first call; MainWindowHandle can be IntPtr.Zero before
    // the game window is realised, which would never actually match the
    // foreground HWND, so callers correctly fall through to "not focused".
    private static IntPtr gameHwnd;
    private static IntPtr GameHwnd
    {
        get
        {
            if (gameHwnd == IntPtr.Zero)
                gameHwnd = Process.GetCurrentProcess().MainWindowHandle;
            return gameHwnd;
        }
    }

    public static bool IsGameFocused()
        => GameHwnd != IntPtr.Zero && GetForegroundWindow() == GameHwnd;

    // ── LMB activity tracking ────────────────────────────────────────────────
    //
    // FFXIV's "click-to-swap" pipeline (replace existing hard target via LMB)
    // doesn't call SetHardTarget while LBUTTON is still physically held — the
    // call appears to be deferred until LMB-up. By the time the SetHardTarget
    // hook fires, GetAsyncKeyState(LBUTTON) reports "up" and a point-in-time
    // check misses the click-source signal entirely. Tracking the last moment
    // LBUTTON was observed held lets the suppressor check a small post-release
    // window instead.
    //
    // Updated from two places:
    //   • Tick() — called once per framework update, polls the current state.
    //     Catches holds even if no SetHardTarget fires during the hold.
    //   • MarkLmbActive() — called from the SetHardTarget detour itself when
    //     it observes LBUTTON physically down. Defensive: catches the very
    //     first-frame case before Tick has had a chance to run.

    private static long lastLmbActiveUtcTicks;

    public static void MarkLmbActive() => lastLmbActiveUtcTicks = DateTime.UtcNow.Ticks;

    public static bool WasLmbDownWithin(int milliseconds)
    {
        var elapsedTicks = DateTime.UtcNow.Ticks - lastLmbActiveUtcTicks;
        return elapsedTicks >= 0
            && TimeSpan.FromTicks(elapsedTicks).TotalMilliseconds < milliseconds;
    }

    public static void Tick()
    {
        if (!IsGameFocused()) return;
        if ((GetAsyncKeyState((int)VirtualKey.LBUTTON) & 0x8000) != 0)
            MarkLmbActive();
    }

    public static bool IsMouseButton(VirtualKey k)
        => k is VirtualKey.LBUTTON or VirtualKey.RBUTTON or VirtualKey.MBUTTON
              or VirtualKey.XBUTTON1 or VirtualKey.XBUTTON2;

    /// <summary>
    /// Physical down-state of the key/button, gated only on the FFXIV window
    /// having foreground focus. Used by the config key-picker.
    /// </summary>
    public static bool IsDownRaw(VirtualKey key)
    {
        if (key == VirtualKey.NO_KEY) return false;
        if (!IsGameFocused()) return false;
        return (GetAsyncKeyState((int)key) & 0x8000) != 0;
    }

    /// <summary>
    /// Down-state for runtime keybind handlers. Adds an ImGui-capture gate
    /// over IsDownRaw — keys are suppressed while ImGui has keyboard focus
    /// (text input), and mouse buttons are suppressed while the cursor is
    /// over an ImGui window. Prevents the config UI from firing the binds
    /// the user is currently editing.
    /// </summary>
    public static bool IsDown(VirtualKey key)
    {
        if (!IsDownRaw(key)) return false;
        var io = ImGui.GetIO();
        if (IsMouseButton(key))
        {
            if (io.WantCaptureMouse) return false;
        }
        else
        {
            // WantTextInput is stricter than WantCaptureKeyboard — it only
            // fires when an ImGui text field actually has focus. Using
            // WantCaptureKeyboard would suppress modifier keys (CONTROL)
            // any time ImGui tracks them for shortcuts, which dropped the
            // activation key intermittently.
            if (io.WantTextInput) return false;
        }
        return true;
    }
}
