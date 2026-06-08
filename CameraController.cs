using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace ActionCamera;

/// <summary>
/// Manages action-camera state: mouse capture, camera rotation writes, and
/// player character facing each game frame.
/// </summary>
public sealed unsafe class CameraController : IDisposable
{
    // ── Win32 interop ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    // Win32 ShowCursor intentionally dropped: cursor visibility now flows through
    // the game's own AtkCursor module so popups / cutscenes / inactivity-hide all
    // share state with us. Hiding via ShowCursor only worked for hardware-cursor
    // users and provided no signal back when something external re-showed it.

    // ── Camera control hook ──────────────────────────────────────────────────

    private enum CameraControlType { None, Keyboard, Gamepad, Mouse }
    private delegate CameraControlType GetCameraControlTypeDelegate();
    private readonly Hook<GetCameraControlTypeDelegate>? cameraControlHook;

    private CameraControlType CameraControlTypeDetour()
    {
        // Let the game drive camera rotation while RMB or LMB is held (LMB
        // can be bound to camera-rotate in FFXIV's mouse settings). When no
        // mouse-camera button is held but our cam is active, we own the
        // input — return None to suppress the game's native camera control.
        //
        // With BETA mouse-binds enabled, a held button that has a configured
        // bind is "ours" (fire-mode), not vanilla camera input — keep
        // suppressing the game's camera control in that case so the held
        // click doesn't pivot the camera or pause our rotation pipeline.
        if (isActive && !ShouldYieldToVanillaMouseCamera()) return CameraControlType.None;
        return cameraControlHook!.Original();
    }

    // ── State ────────────────────────────────────────────────────────────────

    private readonly Configuration config;
    private readonly TargetSelector targetSelector;

    private bool isActive;
    private bool cursorHidden;
    private POINT screenCenter;
    private POINT savedCursorPos;

    public bool IsActive => isActive;

    // ── Constructor / Dispose ────────────────────────────────────────────────

    public CameraController(Configuration config, TargetSelector targetSelector)
    {
        this.config = config;
        this.targetSelector = targetSelector;

        try
        {
            // Same call-site signature used by SimpleTweaks' DisableMouseCameraControl.
            // HookFromSignature resolves the relative CALL target internally, matching
            // what Dalamud's [Signature] attribute does.
            cameraControlHook = Plugin.GameInterop.HookFromSignature<GetCameraControlTypeDelegate>(
                "E8 ?? ?? ?? ?? 83 F8 01 74 5F", CameraControlTypeDetour);
            cameraControlHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[ActionCamera] Failed to hook GetCameraControlType.");
        }
    }

    public void Dispose()
    {
        if (isActive) Deactivate();
        // Make sure we don't leave the cursor hidden if the plugin is
        // disabled / hot-reloaded while userWantsActive was on.
        if (cursorHidden) RequestShowCursor();
        cameraControlHook?.Dispose();
    }

    // ── Activation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Activate the camera-control logic. Caller is responsible for the cursor
    /// being hidden — Activate() doesn't manipulate cursor visibility itself.
    /// That decoupling is what lets RMB-hold (which hides the cursor through
    /// the input layer) drive activation just like an explicit RequestHide().
    /// </summary>
    public void Activate()
    {
        if (isActive || !Plugin.ClientState.IsLoggedIn) return;

        RecalcScreenCenter();

        // Don't yank the cursor to centre if the game has it locked for RMB
        // camera control — that would fight the game's lock and produce
        // weird input. The rbHeld branch in Update() skips our cursor logic
        // entirely, so the centre warp isn't needed while RMB is active.
        if (!IsMouseCameraHeld())
        {
            SetCursorPos(screenCenter.X, screenCenter.Y);
        }

        isActive = true;
        Plugin.Log.Debug("[ActionCamera] Activated.");
    }

    public void Deactivate()
    {
        if (!isActive) return;
        isActive = false;

        targetSelector.ClearMouseOverHighlight();
        Plugin.Log.Debug("[ActionCamera] Deactivated.");
    }

    /// <summary>
    /// Caller-driven cursor hide. Used by the activation keybind. Saves the
    /// current cursor position (once — repeated calls don't overwrite) so a
    /// later RequestShowCursor can restore it. Auto-resume hits this path
    /// too; the idempotent save means it doesn't clobber the pre-cam position
    /// with whatever the popup placed the cursor at.
    /// </summary>
    public void RequestHideCursor()
    {
        var stage = AtkStage.Instance();
        if (stage == null) return;
        if (!cursorHidden)
        {
            GetCursorPos(out savedCursorPos);
            cursorHidden = true;
        }
        stage->AtkCursor.Hide();
    }

    public void RequestShowCursor()
    {
        var stage = AtkStage.Instance();
        if (stage != null) stage->AtkCursor.Show();
        if (cursorHidden)
        {
            SetCursorPos(savedCursorPos.X, savedCursorPos.Y);
            cursorHidden = false;
        }
    }

    /// <summary>
    /// Warp the OS cursor to a point given in game-client (render) pixel
    /// coordinates — e.g. an addon's X/Y. Converts to screen space via the
    /// window's client rect. Used to drop the cursor onto the gathering UI
    /// when it auto-opens.
    /// </summary>
    public void WarpCursorToClient(int clientX, int clientY)
    {
        var hwnd = Process.GetCurrentProcess().MainWindowHandle;
        if (hwnd == IntPtr.Zero) return;
        var p = new POINT { X = clientX, Y = clientY };
        ClientToScreen(hwnd, ref p);
        SetCursorPos(p.X, p.Y);
    }

    /// <summary>
    /// True iff FFXIV's UI layer considers the cursor visible to the player.
    /// Reads AtkCursor.IsVisible — the same flag SimpleTweaks, Dalamud, and
    /// FFXIV-VR treat as canonical for popups, menus, cutscenes, and our own
    /// Hide/Show calls.
    ///
    /// We deliberately do NOT consult the input-layer Cursor.IsCursorVisible
    /// here. That flag flips on transient input-pipeline events (mouse-button
    /// hold, etc.) and combining it produced sticky-off churn. RMB-driven cam
    /// activation is handled explicitly via IsRmbHeld below.
    /// </summary>
    public static bool IsGameCursorVisible()
    {
        var stage = AtkStage.Instance();
        if (stage == null) return false;
        return stage->AtkCursor.IsVisible;
    }

    /// <summary>
    /// True while the right mouse button is physically held and the game has
    /// foreground focus. Drives the "let the game rotate the camera, we only
    /// run cone targeting + character facing on top" path.
    /// </summary>
    public static bool IsRmbHeld() => InputBinding.IsDownRaw(VirtualKey.RBUTTON);

    /// <summary>
    /// True while the left mouse button is physically held. Some users bind
    /// LMB to camera rotation in FFXIV's mouse settings; in that case LMB
    /// behaves the same as RMB and would fight our cursor lock if we don't
    /// step aside.
    /// </summary>
    public static bool IsLmbHeld() => InputBinding.IsDownRaw(VirtualKey.LBUTTON);

    /// <summary>
    /// True when any mouse button the game might use for camera-rotate is
    /// held. While true, our cursor-warp + delta-based rotation should be
    /// skipped to avoid fighting the game's own cursor lock.
    /// </summary>
    public static bool IsMouseCameraHeld() => IsRmbHeld() || IsLmbHeld();

    /// <summary>
    /// Like <see cref="IsMouseCameraHeld"/> but takes the BETA mouse-bind
    /// configuration into account: a held button that has a configured
    /// bind is owned by the fire pipeline, not vanilla camera input —
    /// returning false for that case keeps the plugin's cursor-warp +
    /// delta-based rotation running so the click doesn't introduce a
    /// camera hitch.
    ///
    /// When the beta toggle is off, behaviour is identical to the
    /// original <c>IsMouseCameraHeld</c> check — no regression for
    /// users who haven't opted in.
    /// </summary>
    private bool ShouldYieldToVanillaMouseCamera()
    {
        if (!config.BetaMouseBindsEnabled)
            return IsMouseCameraHeld();

        var rmbYields = IsRmbHeld() && !HasBindForButton(VirtualKey.RBUTTON);
        var lmbYields = IsLmbHeld() && !HasBindForButton(VirtualKey.LBUTTON);
        return rmbYields || lmbYields;
    }

    private bool HasBindForButton(VirtualKey button)
    {
        foreach (var bind in config.MouseBinds)
        {
            if (bind == null) continue;
            if (bind.Button == button) return true;
        }
        return false;
    }

    // ── Per-frame update ─────────────────────────────────────────────────────

    public void Update()
    {
        if (!isActive) return;
        if (!Plugin.ClientState.IsLoggedIn) { Deactivate(); return; }

        // Mouse-button-held (RMB or LMB) WITHOUT a configured BETA bind:
        // the game owns cursor lock and camera rotation. Skip our rotation
        // write so we don't double up on the game's own mouse-camera. Cone
        // targeting and character facing still run, using the game's
        // HRotation which the game is updating from the mouse input.
        //
        // When BETA mouse-binds is on and the held button has a bind,
        // ShouldYieldToVanillaMouseCamera() returns false — the click is
        // a hotbar fire, not a camera input, so the rotation pipeline keeps
        // running through the click and the user doesn't perceive a hitch.
        if (ShouldYieldToVanillaMouseCamera())
        {
            if (config.AutoTarget) targetSelector.Update(GetCameraHRotation());
            return;
        }

        // Mouselook input = the game's OWN per-frame relative delta
        // (Framework->CursorInputs.DeltaX/DeltaY), NOT a cursor-warp diff.
        // Under XWayland the absolute cursor (GetCursorPos) goes stale after
        // button events and the warp-back fakes motion, which drove the old
        // drift; the native relative delta arrives via the relative-pointer
        // path and is clean (verified: 0 at rest, only nonzero on real
        // motion, uncontaminated by our warp). We still warp the hidden
        // cursor back to centre purely so the OS pointer never hits a screen
        // edge and clamps the motion the game reports.
        var fw = GameFramework.Instance();
        var rawDx = fw != null ? fw->CursorInputs.DeltaX : 0;
        var rawDy = fw != null ? fw->CursorInputs.DeltaY : 0;

        SetCursorPos(screenCenter.X, screenCenter.Y);

        if (rawDx != 0 || rawDy != 0)
            ApplyCameraRotation(rawDx * config.MouseSensitivityX,
                                rawDy * config.MouseSensitivityY);

        if (config.AutoTarget)
            targetSelector.Update(GetCameraHRotation());
    }

    // ── Camera write ─────────────────────────────────────────────────────────

    private void ApplyCameraRotation(float dx, float dy)
    {
        // CameraManager.Instance()->CurrentCamera is the active GameCamera*.
        // GameCamera sits at offset 0 so a cast from the base Camera* is valid.
        //
        // Field offsets used here are stable across recent FFXIV patches:
        //   0x130  HRotation  (float, radians, camera yaw)
        //   0x134  VRotation  (float, radians, camera pitch)
        //
        // If the game has been patched and these offsets have moved, update
        // the constants below or use FFXIVClientStructs' typed access if the
        // fields have been added to the generated structs.
        const int OffHRot = 0x130;
        const int OffVRot = 0x134;

        var cam = CameraManager.Instance()->CurrentCamera;
        if (cam == null) return;

        var camBase = (nint)cam;
        var hRot = (float*)(camBase + OffHRot);
        var vRot = (float*)(camBase + OffVRot);

        *hRot -= dx;

        // Wrap yaw to [-π, π] so it never drifts beyond those bounds.
        *hRot = WrapAngle(*hRot);

        var newV = *vRot + (config.InvertY ? dy : -dy);
        *vRot = Math.Clamp(newV, config.MinVRotationOverride, config.MaxVRotationOverride);

        // Character/mount rotation is handled by RotationDriver, which cooperates
        // with the game's own movement and flight pipelines via hooks rather than
        // racing it via raw field writes. Keeping the work out of here means
        // there's a single source of truth for player rotation each frame.
    }

    /// <summary>
    /// Current camera yaw in radians. Public so RotationDriver can read it.
    /// </summary>
    public float GetCameraHRotation()
    {
        const int OffHRot = 0x130;
        var cam = CameraManager.Instance()->CurrentCamera;
        if (cam == null) return 0f;
        return *(float*)((nint)cam + OffHRot);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    private void RecalcScreenCenter()
    {
        var hwnd = Process.GetCurrentProcess().MainWindowHandle;
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out var r)) return;

        var p = new POINT
        {
            X = (r.Left + r.Right) / 2,
            Y = (r.Top + r.Bottom) / 2
        };
        ClientToScreen(hwnd, ref p);
        screenCenter = p;
    }
}
