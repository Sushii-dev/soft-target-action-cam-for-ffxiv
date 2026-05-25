using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
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
        if (isActive) return CameraControlType.None;
        return cameraControlHook!.Original();
    }

    // ── State ────────────────────────────────────────────────────────────────

    private readonly Configuration config;
    private readonly TargetSelector targetSelector;

    private bool isActive;
    private bool cursorHidden;
    private POINT screenCenter;
    private POINT savedCursorPos;

    // Set true when Activate() runs; cleared on the first Update frame where
    // the cursor delta is non-zero. That single sample is discarded — it's the
    // XWayland warp-staleness spike, where SetCursorPos(center) updates
    // XWayland's cache immediately but the X server pointer doesn't resync
    // until the next real motion event. The first motion event then surfaces
    // a delta the size of "wherever the cursor was left while disabled" → a
    // one-frame camera twitch proportional to that distance. Skipping that
    // sample costs ~16ms of input on first move; on Windows where the bug
    // doesn't exist, it's still a 1-frame skip but invisible.
    private bool firstMotionAfterActivate;

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
        cameraControlHook?.Dispose();
    }

    // ── Activation ───────────────────────────────────────────────────────────

    public void Activate()
    {
        if (isActive || !Plugin.ClientState.IsLoggedIn) return;

        // Remember current cursor so we can restore it on deactivation.
        GetCursorPos(out savedCursorPos);

        RecalcScreenCenter();
        SetCursorPos(screenCenter.X, screenCenter.Y);

        HideGameCursor();

        firstMotionAfterActivate = true;
        isActive = true;
        Plugin.Log.Debug("[ActionCamera] Activated.");
    }

    public void Deactivate()
    {
        if (!isActive) return;
        isActive = false;
        firstMotionAfterActivate = false;

        ShowGameCursor();

        SetCursorPos(savedCursorPos.X, savedCursorPos.Y);
        targetSelector.ClearMouseOverHighlight();
        Plugin.Log.Debug("[ActionCamera] Deactivated.");
    }

    private void HideGameCursor()
    {
        if (cursorHidden) return;
        var stage = AtkStage.Instance();
        if (stage == null) return;
        stage->AtkCursor.Hide();
        cursorHidden = true;
    }

    private void ShowGameCursor()
    {
        if (!cursorHidden) return;
        var stage = AtkStage.Instance();
        if (stage != null) stage->AtkCursor.Show();
        cursorHidden = false;
    }

    /// <summary>
    /// True iff FFXIV currently considers the cursor visible to the player.
    /// Read this from the game's AtkCursor (the same field SimpleTweaks,
    /// Dalamud, and FFXIV-VR treat as canonical). Reflects popups, cutscenes,
    /// inactivity-hide and our own Hide/Show calls uniformly — unlike the
    /// Win32 ShowCursor counter which only governs the OS hardware cursor.
    /// </summary>
    public static bool IsGameCursorVisible()
    {
        var stage = AtkStage.Instance();
        if (stage == null) return false;
        return stage->AtkCursor.IsVisible;
    }

    // ── Per-frame update ─────────────────────────────────────────────────────

    public void Update()
    {
        if (!isActive) return;
        if (!Plugin.ClientState.IsLoggedIn) { Deactivate(); return; }

        // Read & reset mouse position each frame.
        GetCursorPos(out var cur);
        var rawDx = cur.X - screenCenter.X;
        var rawDy = cur.Y - screenCenter.Y;
        var dx = rawDx * config.MouseSensitivityX;
        var dy = rawDy * config.MouseSensitivityY;
        SetCursorPos(screenCenter.X, screenCenter.Y);

        // Spike guard: on XWayland, SetCursorPos(center) updates XWayland's
        // cached cursor position but the X server pointer can lag — the cache
        // resyncs on the next real motion event, surfacing a delta the size
        // of (last-known X server pos − center). That's a one-frame twitch
        // proportional to how far the cursor was moved while the cam was
        // disabled. Discarding the first non-zero delta after Activate kills
        // the spike without affecting steady-state input.
        if (firstMotionAfterActivate)
        {
            if (rawDx != 0 || rawDy != 0)
                firstMotionAfterActivate = false;
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
