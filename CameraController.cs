using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

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
    [DllImport("user32.dll")] private static extern int  ShowCursor(bool show);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);

    // ── Camera control hook ──────────────────────────────────────────────────

    private enum CameraControlType { None, Keyboard, Gamepad, Mouse }
    private delegate CameraControlType GetCameraControlTypeDelegate();
    private readonly Hook<GetCameraControlTypeDelegate>? cameraControlHook;

    private CameraControlType CameraControlTypeDetour()
    {
        var original = cameraControlHook!.Original();
        if (isActive && original == CameraControlType.Mouse)
            return CameraControlType.None;
        return original;
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
            // Signature from SimpleTweaks' DisableMouseCameraControl — finds the call site,
            // so we resolve the relative offset to get the actual function address.
            var callSite = Plugin.SigScanner.ScanText("E8 ?? ?? ?? ?? 83 F8 01 74 5F");
            var funcAddr = callSite + 5 + *(int*)(callSite + 1);
            cameraControlHook = Plugin.GameInterop.HookFromAddress<GetCameraControlTypeDelegate>(
                funcAddr, CameraControlTypeDetour);
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

        if (!cursorHidden)
        {
            ShowCursor(false);
            cursorHidden = true;
        }

        isActive = true;
        Plugin.Log.Debug("[ActionCamera] Activated.");
    }

    public void Deactivate()
    {
        if (!isActive) return;
        isActive = false;

        if (cursorHidden)
        {
            ShowCursor(true);
            cursorHidden = false;
        }

        SetCursorPos(savedCursorPos.X, savedCursorPos.Y);
        Plugin.Log.Debug("[ActionCamera] Deactivated.");
    }

    // ── Per-frame update ─────────────────────────────────────────────────────

    public void Update()
    {
        if (!isActive) return;
        if (!Plugin.ClientState.IsLoggedIn) { Deactivate(); return; }

        // Read & reset mouse position each frame.
        GetCursorPos(out var cur);
        var dx = (cur.X - screenCenter.X) * config.MouseSensitivityX;
        var dy = (cur.Y - screenCenter.Y) * config.MouseSensitivityY;
        SetCursorPos(screenCenter.X, screenCenter.Y);

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
