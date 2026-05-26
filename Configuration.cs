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

    // Deprecated: hold-to-activate was removed when cursor-sync became the
    // activation model. Field kept so existing configs round-trip cleanly;
    // the value is no longer read at runtime.
    public bool HoldToActivate { get; set; } = false;

    // --- Cursor sync / auto-resume ---
    //
    // Cursor visibility (the game's AtkCursor.IsVisible flag) is the source of
    // truth for whether the action camera should be active. When the cursor
    // becomes visible while the camera is active, the camera deactivates and
    // by default the user must press the activation key again to re-enter
    // (sticky-off, BDO-style).
    //
    // The flags below carve out per-scenario exceptions: while ANY of these is
    // ticked AND its matching game state is currently true, sticky-off is
    // demoted to "defer" — the camera deactivates temporarily but the
    // userWantsActive intent is preserved, so the camera re-enters
    // automatically as soon as the cursor hides again.
    //
    // Alt-tab / loss of foreground focus is intentionally never exempted.

    // Any in-game addon is focused (inventory, character sheet, map, chat
    // input, etc. — anything FocusedAddon != null).
    public bool AutoResumeAfterUI { get; set; } = false;

    // Cutscene / event cinematic playback (Dalamud ConditionFlag covers).
    public bool AutoResumeAfterCutscene { get; set; } = false;

    // Generic "occupied in event" / quest dialogue states.
    public bool AutoResumeAfterEvent { get; set; } = false;

    // Zone transition / loading screen (BetweenAreas).
    public bool AutoResumeAfterZoneTransition { get; set; } = false;

    // Key that clears the current hard target on press. Edge-triggered.
    public VirtualKey ClearHardTargetKey { get; set; } = VirtualKey.NO_KEY;

    // Key that interacts with the targeted object OR advances an open
    // dialogue / confirms an open prompt. Priority:
    //   1. If a confirm-style addon is open (Talk, SelectYesno, SelectString,
    //      etc.) advance it — same as FFXIV's "Confirm" (Numpad 0).
    //   2. Else if there's a hard target, call InteractWithObject on it.
    //   3. Else scan the camera cone for the nearest EventNpc/EventObj and
    //      interact with that.
    public VirtualKey InteractKey { get; set; } = VirtualKey.NO_KEY;

    // Key that hard-targets the cone's current pick. Edge-triggered.
    // Only fires while the action camera is active (otherwise the cone pick is
    // stale — TargetSelector.Update is only called while the camera is active).
    // Punches through SuppressSoftToHardPromotion via a one-shot bypass on the hook.
    public VirtualKey HardTargetKey { get; set; } = VirtualKey.NO_KEY;

    // When true, HardTargetKey doubles as a clear: pressing it while a hard
    // target exists clears it instead of re-targeting. The clear branch is
    // not gated on IsActive — clearing is meaningful outside camera mode too.
    // While this flag is on, the standalone ClearHardTargetKey handler is
    // skipped at runtime (regardless of what's bound to it) so we can't
    // double-fire and accidentally turn a clear into a target-swap.
    public bool HardTargetKeyClearsOnPress { get; set; } = false;

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

    // Hardcoded FFXIV behavior: if you have no hard target and use an action against
    // your soft target, the game promotes it to a hard target. The plugin can suppress
    // this by hooking TargetSystem.SetHardTarget and rejecting calls whose target
    // matches the cone's current pick while the camera is active.
    public bool SuppressSoftToHardPromotion { get; set; } = true;

    // When camera is active, suppress LMB-driven SetHardTarget calls. Stops the
    // game's click-to-target / click-empty-to-clear behaviour, so hard target
    // is only ever changed via the configured keybinds. Detected by checking
    // whether LBUTTON is physically held at the moment the hook fires —
    // FFXIV's click-targeting calls SetHardTarget on the click frame while
    // the button is still down. AllowNext bypass is checked first, so the
    // hard-target keybind continues to work even when bound to a mouse button
    // and even if LMB happens to be held simultaneously.
    public bool SuppressClickHardTargetInCam { get; set; } = true;

    // --- Reticle ---
    public bool ShowReticle { get; set; } = true;
    public Vector4 ReticleColor { get; set; } = new Vector4(1f, 1f, 1f, 0.8f);

    // --- Vertical camera limits ---
    // These mirror the game's own limits and can be tightened by the user.
    public float MinVRotationOverride { get; set; } = -1.45f;   // ~-83°
    public float MaxVRotationOverride { get; set; } = 0.65f;    //  ~37°

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
