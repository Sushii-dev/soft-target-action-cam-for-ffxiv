using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using System;

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

    // --- Vertical camera limits ---
    // These mirror the game's own limits and can be tightened by the user.
    public float MinVRotationOverride { get; set; } = -1.45f;   // ~-83°
    public float MaxVRotationOverride { get; set; } = 0.65f;    //  ~37°

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
