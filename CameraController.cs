using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
        // We want the game's native RMB camera rotation to keep working while
        // RMB is held — that's the gesture the user expects to "look around"
        // with. So even when our cam is active, only suppress the game's own
        // camera control when RMB is NOT held. While RMB is held, return the
        // original so the game rotates the camera; we do cone targeting and
        // character facing on top via the rbHeld branch in Update().
        if (isActive && !IsRmbHeld()) return CameraControlType.None;
        return cameraControlHook!.Original();
    }

    // ── State ────────────────────────────────────────────────────────────────

    private readonly Configuration config;
    private readonly TargetSelector targetSelector;

    private bool isActive;
    private bool cursorHidden;
    private POINT screenCenter;
    private POINT savedCursorPos;

    // Counter for the post-activation discard window. Reset to 0 on Activate
    // and on RMB-release, incremented each Update frame. While <= the discard
    // length, cursor deltas are dropped — they're the XWayland warp-staleness
    // spike, where SetCursorPos(center) updates XWayland's cache immediately
    // but the X server pointer doesn't resync until the next real motion
    // event. When the user is actively moving the cursor at activation, the
    // staleness compounds across multiple frames as motion events keep
    // resyncing the cache from the stale X server position. A single-frame
    // discard isn't enough; a fixed-frame window absorbs all the resync
    // spikes. ~83ms at 60fps — imperceptible. On Windows where the bug
    // doesn't exist, you lose ~83ms of input per activation, still
    // invisible.
    private int framesSinceActivate;
    private const int ActivateDiscardFrames = 5;

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
        if (!IsRmbHeld())
        {
            SetCursorPos(screenCenter.X, screenCenter.Y);
        }

        framesSinceActivate = 0;
        isActive = true;
        Plugin.Log.Debug("[ActionCamera] Activated.");
    }

    public void Deactivate()
    {
        if (!isActive) return;
        isActive = false;
        framesSinceActivate = 0;

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

    // ── Per-frame update ─────────────────────────────────────────────────────

    public void Update()
    {
        if (!isActive) return;
        if (!Plugin.ClientState.IsLoggedIn) { Deactivate(); return; }

        // RMB-hold: the game owns cursor lock and camera rotation. Skip our
        // cursor-warp + delta-based rotation entirely — they would fight the
        // game's lock and produce a constant non-zero delta from centre to
        // the click point, spinning the camera. Cone targeting and character
        // facing still run, using the game's HRotation which the game is
        // updating from RMB input.
        //
        // Reset the post-activation discard counter each frame while RMB is
        // held so when RMB releases the next few frames absorb the warp
        // staleness that would otherwise hit our reawakening cursor logic.
        if (IsRmbHeld())
        {
            var hrot = GetCameraHRotation();
            if (config.AutoTarget) targetSelector.Update(hrot);
            if (config.RotateCharacterWithCamera) SetPlayerFacing(hrot);
            framesSinceActivate = 0;
            return;
        }

        // Read & reset mouse position each frame.
        GetCursorPos(out var cur);
        var rawDx = cur.X - screenCenter.X;
        var rawDy = cur.Y - screenCenter.Y;
        var dx = rawDx * config.MouseSensitivityX;
        var dy = rawDy * config.MouseSensitivityY;
        SetCursorPos(screenCenter.X, screenCenter.Y);

        // Post-activation discard window. XWayland's SetCursorPos updates
        // its own cursor-position cache immediately, but the X server pointer
        // doesn't resync until the next real motion event. When the user is
        // actively moving the cursor at activation time, each motion event
        // in the first few frames resyncs the cache from the stale X server
        // position, surfacing big deltas. A single-frame discard catches the
        // first spike but misses the cascade; a small fixed window absorbs
        // them all.
        framesSinceActivate++;
        if (framesSinceActivate <= ActivateDiscardFrames)
        {
            if (config.AutoTarget)
                targetSelector.Update(GetCameraHRotation());
            return;
        }

        ApplyCameraRotation(dx, dy);

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

        if (config.RotateCharacterWithCamera)
            SetPlayerFacing(*hRot);
    }

    private float GetCameraHRotation()
    {
        const int OffHRot = 0x130;
        var cam = CameraManager.Instance()->CurrentCamera;
        if (cam == null) return 0f;
        return *(float*)((nint)cam + OffHRot);
    }

    // ── Character facing ─────────────────────────────────────────────────────

    private void SetPlayerFacing(float cameraYaw)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return;

        // BattleChara* → Character → GameObject → Rotation (float, radians).
        // The configurable offset converts camera-forward to character-forward;
        // default is π because the camera sits *behind* the player.
        var chara = (BattleChara*)localPlayer.Address;
        chara->Character.GameObject.Rotation = WrapAngle(cameraYaw + config.CharacterFacingOffset);
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
