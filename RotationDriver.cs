using System;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace ActionCamera;

/// <summary>
/// Player Move Controller flight input — the struct the flying-mount pipeline
/// reads each frame to decide pitch/yaw/throttle. Defined here rather than
/// taken from FFXIVClientStructs (not exposed at the time of writing).
/// Layout taken from WesleyLuk90/ffxiv-vr's reverse engineering.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x18)]
internal unsafe struct PlayerMoveControllerFlyInput
{
    [FieldOffset(0x0)]  public float Forward;
    [FieldOffset(0x4)]  public float Left;
    [FieldOffset(0x8)]  public float Up;
    [FieldOffset(0xC)]  public float Turn;
    [FieldOffset(0x10)] public float u10;
    [FieldOffset(0x14)] public byte  DirMode;
    [FieldOffset(0x15)] public byte  HaveBackwardOrStrafe;
}

/// <summary>
/// Player movement controller (the "for mine" suffix is the original RE name —
/// the controller is per-local-player). Layout taken from Drahsid/HybridCamera.
/// We only need ActorStruct and a couple of flag fields; full layout omitted.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal unsafe struct MoveControllerSubMemberForMine
{
    [FieldOffset(0x20)] public IntPtr ActorStruct;
    [FieldOffset(0x3D)] public byte Rotated;
    [FieldOffset(0x3E)] public byte MovementLock;
    [FieldOffset(0x110)] public int WishdirChanged;
}

/// <summary>
/// Drives the player character's rotation toward the camera yaw while the
/// action cam is active, via three coordinated paths:
///
///   1. Per-frame Character.SetRotation() — typed setter that routes through
///      the game's RotationModified vtbl. Catches idle / stationary frames
///      that the pipeline hooks below never see, and is the only path that
///      runs if the hooks fail to resolve.
///
///   2. MovementDirectionUpdate hook — sets *align_with_camera = 1 inside
///      the game's own movement pipeline. While the player is moving on
///      foot, the game then writes rotation cooperatively to our value
///      instead of fighting us with movement-vector-driven rotation.
///
///   3. RMIFly hook — overrides PlayerMoveControllerFlyInput->Turn for the
///      flight controller. Flying mounts are otherwise impossible to steer
///      with field writes because the flight controller writes rotation
///      every tick from its own input state.
///
/// All three respect ShouldDrive() — gate on isActive, the user's
/// RotateCharacterWithCamera checkbox, and the blocklist of game states
/// (cutscenes, crafting, sitting, etc.) where rotation should NOT be driven.
/// </summary>
internal sealed unsafe class RotationDriver : IDisposable
{
    private delegate void MovementDirectionUpdateDelegate(
        MoveControllerSubMemberForMine* thisx,
        float* wishdir_h, float* wishdir_v, float* rotatedir,
        byte* align_with_camera, byte* autorun,
        byte dont_rotate_with_camera);

    private delegate void RMIFlyDelegate(IntPtr self, PlayerMoveControllerFlyInput* result);

    private readonly Hook<MovementDirectionUpdateDelegate>? movementHook;
    private readonly Hook<RMIFlyDelegate>? rmiFlyHook;

    private readonly Configuration config;
    private readonly Func<bool> isActive;
    private readonly Func<float> getCameraYaw;

    public RotationDriver(Configuration config, Func<bool> isActive, Func<float> getCameraYaw)
    {
        this.config = config;
        this.isActive = isActive;
        this.getCameraYaw = getCameraYaw;

        try
        {
            movementHook = Plugin.GameInterop.HookFromSignature<MovementDirectionUpdateDelegate>(
                "48 8b c4 4c 89 48 ?? 53 55 57 41 54 48 81 ec ?? 00 00 00",
                MovementDirectionUpdateDetour);
            movementHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[RotationDriver] MovementDirectionUpdate hook failed; falling back to per-frame SetRotation only.");
        }

        try
        {
            rmiFlyHook = Plugin.GameInterop.HookFromSignature<RMIFlyDelegate>(
                "E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8",
                RMIFlyDetour);
            rmiFlyHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[RotationDriver] RMIFly hook failed; flying mounts will not steer with the action cam.");
        }
    }

    public void Dispose()
    {
        movementHook?.Disable();
        movementHook?.Dispose();
        rmiFlyHook?.Disable();
        rmiFlyHook?.Dispose();
    }

    /// <summary>
    /// Per-frame typed-setter write. Call once per Framework.Update after
    /// camera yaw is up to date. Handles idle/stationary frames where the
    /// movement-pipeline hook never fires.
    /// </summary>
    public void Update()
    {
        if (!ShouldDrive()) return;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var target = WrapAngle(getCameraYaw() + config.CharacterFacingOffset);
        var go = (GameObject*)player.Address;
        go->SetRotation(target);
    }

    private bool ShouldDrive()
    {
        if (!isActive()) return false;
        if (!config.RotateCharacterWithCamera) return false;
        return !IsBlocklisted();
    }

    /// <summary>
    /// Game states where forcing rotation would be unwanted or actively
    /// hostile (cutscenes that lock the camera angle, crafting / gathering
    /// poses, sitting on furniture, riding pillion behind another player,
    /// loading screens, etc.). Mirrors the FFXIV-VR blocklist plus a couple
    /// of conditions specific to vanilla FFXIV.
    /// </summary>
    private static bool IsBlocklisted()
    {
        var c = Plugin.Condition;
        return c[ConditionFlag.Unconscious]
            || c[ConditionFlag.Crafting40]
            || c[ConditionFlag.Gathering42]
            || c[ConditionFlag.RidingPillion]
            || c[ConditionFlag.PlayingMiniGame]
            || c[ConditionFlag.PlayingLordOfVerminion]
            || c[ConditionFlag.TradeOpen]
            || c[ConditionFlag.Fishing]
            || c[ConditionFlag.MeldingMateria]
            || c[ConditionFlag.OccupiedInQuestEvent]
            || c[ConditionFlag.OccupiedSummoningBell]
            || c[ConditionFlag.OccupiedInCutSceneEvent]
            || c[ConditionFlag.WatchingCutscene]
            || c[ConditionFlag.WatchingCutscene78]
            || c[ConditionFlag.BetweenAreas]
            || c[ConditionFlag.BetweenAreas51]
            || c[ConditionFlag.RolePlaying]
            || c[ConditionFlag.Jumping61];
    }

    /// <summary>
    /// MovementDirectionUpdate is the game's per-frame movement-pipeline
    /// function. Default behaviour aligns rotation with the movement vector
    /// (Standard mode) or with the camera (Legacy mode) depending on the
    /// in-game "Movement Type" setting.
    ///
    /// We always want camera-driven rotation while our action cam is active.
    /// Setting *align_with_camera = 1 after the original runs forces the
    /// game's own pipeline to write rotation cooperatively to the camera
    /// direction — no field-write race.
    /// </summary>
    private void MovementDirectionUpdateDetour(
        MoveControllerSubMemberForMine* thisx,
        float* wishdir_h, float* wishdir_v, float* rotatedir,
        byte* align_with_camera, byte* autorun,
        byte dont_rotate_with_camera)
    {
        movementHook!.Original(thisx, wishdir_h, wishdir_v, rotatedir, align_with_camera, autorun, dont_rotate_with_camera);
        if (!ShouldDrive()) return;
        *align_with_camera = 1;
    }

    /// <summary>
    /// RMIFly is the flying-mount input read. We override the Turn field to
    /// the angular delta between the current rotation and our target
    /// (camera yaw + facing offset). The flight controller uses this value
    /// to rotate the mount during this tick; subsequent ticks converge to
    /// our target as the rotation closes the delta.
    /// </summary>
    private void RMIFlyDetour(IntPtr self, PlayerMoveControllerFlyInput* result)
    {
        rmiFlyHook!.Original(self, result);
        if (!ShouldDrive()) return;
        if (result == null) return;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var target = WrapAngle(getCameraYaw() + config.CharacterFacingOffset);
        var delta  = WrapAngle(target - player.Rotation);
        result->Turn = delta;
    }

    private static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }
}
