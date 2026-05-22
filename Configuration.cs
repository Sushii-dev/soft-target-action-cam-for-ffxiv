using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using System;
using System.Numerics;

namespace ActionCamera;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // --- Activation ---

    // Key that activates action cam (toggle or hold).
    public VirtualKey ActivationKey { get; set; } = VirtualKey.NO_KEY;

    // If true, action cam is only active while the key is held down.
    // If false, the key toggles action cam on/off.
    public bool HoldToActivate { get; set; } = false;

    // --- Mouse sensitivity ---

    public float MouseSensitivityX { get; set; } = 0.003f;
    public float MouseSensitivityY { get; set; } = 0.003f;
    public bool InvertY { get; set; } = false;

    // --- Character facing ---

    // Rotate the player character to match the camera's yaw.
    public bool RotateCharacterWithCamera { get; set; } = true;

    // Radians added to camera HRotation when setting character facing.
    // Default π because the camera looks "toward" the character (behind-player view).
    // Adjust if your character faces the wrong way.
    public float CharacterFacingOffset { get; set; } = MathF.PI;

    // --- Auto-targeting ---

    public bool AutoTarget { get; set; } = true;

    // Half-angle of the targeting cone in front of the camera, in degrees.
    public float AutoTargetFovDegrees { get; set; } = 30f;

    // Maximum range (yalms) to consider auto-target candidates.
    public float AutoTargetMaxDistance { get; set; } = 30f;

    // Weight given to angle vs distance when scoring candidates.
    // Higher = prefer more centred targets; lower = prefer closer targets.
    public float AutoTargetAngleWeight { get; set; } = 2.0f;

    // Which target slot(s) the cone writes each frame.
    // MouseOverTarget = yellow outline; SoftTarget = red ring; Target = hard.
    public bool WriteMouseOverTarget { get; set; } = true;
    public bool WriteSoftTarget { get; set; } = true;
    public bool WriteHardTarget { get; set; } = false;

    // When true, only enemies currently targeting the player or party are considered
    // (upstream behavior — avoids accidentally engaging loitering mobs via hard target).
    // When false, all valid hostile NPCs in the cone are considered.
    public bool RequireAggro { get; set; } = false;

    // --- Reticle ---
    public bool ShowReticle { get; set; } = true;
    public Vector4 ReticleColor { get; set; } = new Vector4(1f, 1f, 1f, 0.8f);

    // --- Vertical camera limits ---
    // These mirror the game's own limits and can be tightened by the user.
    public float MinVRotationOverride { get; set; } = -1.45f;   // ~-83°
    public float MaxVRotationOverride { get; set; } = 0.65f;    //  ~37°

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
