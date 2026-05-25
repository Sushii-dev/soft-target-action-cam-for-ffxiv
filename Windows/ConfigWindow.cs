using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;

namespace ActionCamera.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Configuration Config => plugin.Configuration;

    // Temporary buffer for the key-picker popup.
    private bool listeningForKey;
    private bool listeningForClearKey;
    private bool listeningForHardKey;

    // Shared arming flag: while a picker is listening, we wait for all inputs
    // to be released before capturing the next press. Stops the LMB click
    // that opened the picker (and any held modifier) from being captured as
    // the binding. Only one picker can be listening at a time.
    private bool pickerArmed;

    // Cached once: we scan this on every picker frame, including a "is any
    // input held" probe for arming.
    private static readonly VirtualKey[] AllVirtualKeys = (VirtualKey[])Enum.GetValues(typeof(VirtualKey));

    public ConfigWindow(Plugin plugin)
        : base("Action Camera Settings###ActionCameraConfig",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawActivationSection();
        ImGui.Separator();
        DrawMouseSection();
        ImGui.Separator();
        DrawCharacterSection();
        ImGui.Separator();
        DrawTargetingSection();
        ImGui.Separator();
        DrawReticleSection();
        ImGui.Separator();
        DrawCameraLimitsSection();
    }

    // ── Activation ───────────────────────────────────────────────────────────

    private void DrawActivationSection()
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Activation");
        ImGui.Spacing();

        // Key picker
        ImGui.Text("Activation key:");
        ImGui.SameLine();
        DrawKeyPicker("actkey", Config.ActivationKey,
            k => Config.ActivationKey = k, ref listeningForKey,
            noneLabel: "(none – use /actioncam)");

        ImGui.Spacing();

        var holdMode = Config.HoldToActivate;
        if (ImGui.Checkbox("Hold to activate (release to deactivate)", ref holdMode))
        {
            Config.HoldToActivate = holdMode;
            Config.Save();
        }

        ImGui.TextDisabled("  Unchecked = key toggles action cam on/off.");
    }

    // ── Mouse ────────────────────────────────────────────────────────────────

    private void DrawMouseSection()
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Mouse");
        ImGui.Spacing();

        var sx = Config.MouseSensitivityX;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Horizontal sensitivity", ref sx, 0.0005f, 0.02f, "%.4f"))
        {
            Config.MouseSensitivityX = sx;
            Config.Save();
        }

        var sy = Config.MouseSensitivityY;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Vertical sensitivity", ref sy, 0.0005f, 0.02f, "%.4f"))
        {
            Config.MouseSensitivityY = sy;
            Config.Save();
        }

        var inv = Config.InvertY;
        if (ImGui.Checkbox("Invert vertical axis", ref inv))
        {
            Config.InvertY = inv;
            Config.Save();
        }
    }

    // ── Character ────────────────────────────────────────────────────────────

    private void DrawCharacterSection()
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Character Facing");
        ImGui.Spacing();

        var rotate = Config.RotateCharacterWithCamera;
        if (ImGui.Checkbox("Auto-rotate character with camera", ref rotate))
        {
            Config.RotateCharacterWithCamera = rotate;
            Config.Save();
        }

        ImGui.BeginDisabled(!Config.RotateCharacterWithCamera);

        var offsetDeg = Config.CharacterFacingOffset * (180f / MathF.PI);
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Facing offset (°)", ref offsetDeg, -180f, 180f, "%.1f°"))
        {
            Config.CharacterFacingOffset = offsetDeg * (MathF.PI / 180f);
            Config.Save();
        }
        ImGui.TextDisabled("  Default 180°: character faces the same direction as the camera.");

        ImGui.EndDisabled();
    }

    // ── Targeting ────────────────────────────────────────────────────────────

    private void DrawTargetingSection()
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Auto-Targeting");
        ImGui.Spacing();

        var autoTarget = Config.AutoTarget;
        if (ImGui.Checkbox("Auto-target nearest enemy in camera direction", ref autoTarget))
        {
            Config.AutoTarget = autoTarget;
            Config.Save();
        }

        ImGui.BeginDisabled(!Config.AutoTarget);

        var fov = Config.AutoTargetFovDegrees;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("FOV cone (°)", ref fov, 5f, 90f, "%.0f°"))
        {
            Config.AutoTargetFovDegrees = fov;
            Config.Save();
        }
        ImGui.TextDisabled("  Half-angle of the forward cone used for candidate search.");

        var dist = Config.AutoTargetMaxDistance;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Max distance (y)", ref dist, 5f, 100f, "%.0fy"))
        {
            Config.AutoTargetMaxDistance = dist;
            Config.Save();
        }

        var aw = Config.AutoTargetAngleWeight;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Angle weight", ref aw, 0.1f, 10f, "%.1f"))
        {
            Config.AutoTargetAngleWeight = aw;
            Config.Save();
        }
        ImGui.TextDisabled("  Higher = prefer more-centred targets; lower = prefer closer.");

        ImGui.Spacing();
        ImGui.TextDisabled("Target slots written each frame:");

        var mo = Config.WriteMouseOverTarget;
        if (ImGui.Checkbox("MouseOver Target (yellow outline, ReAction \"Field Target\")", ref mo))
        {
            Config.WriteMouseOverTarget = mo;
            Config.Save();
        }

        var st = Config.WriteSoftTarget;
        if (ImGui.Checkbox("Soft Target (red ring, ReAction \"Soft Target\")", ref st))
        {
            Config.WriteSoftTarget = st;
            Config.Save();
        }

        var ht = Config.WriteHardTarget;
        if (ImGui.Checkbox("Hard Target (locks on — upstream behavior; overrides MMB cycle)", ref ht))
        {
            Config.WriteHardTarget = ht;
            Config.Save();
        }

        ImGui.Spacing();
        var aggro = Config.RequireAggro;
        if (ImGui.Checkbox("Only target enemies engaged with player / party", ref aggro))
        {
            Config.RequireAggro = aggro;
            Config.Save();
        }
        ImGui.TextDisabled("  Off (default): cone includes loitering / non-aggroed mobs.");
        ImGui.TextDisabled("  On: only mobs currently targeting you or a party member.");

        ImGui.Spacing();
        var suppress = Config.SuppressSoftToHardPromotion;
        if (ImGui.Checkbox("Suppress soft -> hard promotion on action use", ref suppress))
        {
            Config.SuppressSoftToHardPromotion = suppress;
            Config.Save();
        }
        ImGui.TextDisabled("  FFXIV promotes the soft target to hard when you use an action");
        ImGui.TextDisabled("  with no hard target. This hook rejects that specific promotion");
        ImGui.TextDisabled("  while the cone is active.");

        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Text("Hard target key:");
        ImGui.SameLine();
        DrawKeyPicker("hardkey", Config.HardTargetKey,
            k => Config.HardTargetKey = k, ref listeningForHardKey);
        ImGui.TextDisabled("  Edge-triggered: hard-targets the cone pick (only while camera is active).");

        var clearsOnPress = Config.HardTargetKeyClearsOnPress;
        if (ImGui.Checkbox("  Same key also clears the hard target when one exists", ref clearsOnPress))
        {
            Config.HardTargetKeyClearsOnPress = clearsOnPress;
            Config.Save();
        }
        ImGui.TextDisabled("  Turns the hard target key into a single toggle. The standalone");
        ImGui.TextDisabled("  clear-key below is disabled while this is on.");

        // Standalone clear-target key — greyed out when the toggle flag owns
        // clearing. The runtime handler also skips firing in that case, so a
        // previously-saved binding can't double-fire with the toggle.
        ImGui.BeginDisabled(Config.HardTargetKeyClearsOnPress);

        ImGui.Spacing();
        ImGui.Text("Clear hard target key:");
        ImGui.SameLine();
        DrawKeyPicker("clearkey", Config.ClearHardTargetKey,
            k => Config.ClearHardTargetKey = k, ref listeningForClearKey,
            noneLabel: "(none – use /actioncam cleartarget)");
        ImGui.TextDisabled("  Edge-triggered: clears the current hard target on key down.");

        ImGui.EndDisabled();
    }

    // ── Key picker helper ────────────────────────────────────────────────────

    /// <summary>
    /// Generic picker shared by every keybind row. Supports keyboard keys,
    /// modifier keys (CONTROL/SHIFT/MENU), and mouse buttons uniformly via
    /// InputBinding.IsDownRaw — i.e. anything Win32 GetAsyncKeyState reports.
    ///
    /// Capture flow:
    ///   1. User clicks the labelled button → enters listening mode with
    ///      pickerArmed = false.
    ///   2. Each frame we check if ANY input is currently held. As long as
    ///      something is (typically LMB from the click that opened us), we
    ///      stay disarmed.
    ///   3. Once everything is released, pickerArmed flips true and the
    ///      next press is captured.
    ///
    /// ESCAPE is reserved for cancel and never captured as a binding.
    /// </summary>
    private void DrawKeyPicker(
        string idSuffix,
        VirtualKey current,
        Action<VirtualKey> setter,
        ref bool listening,
        string noneLabel = "(none)")
    {
        var label = current == VirtualKey.NO_KEY ? noneLabel : current.ToString();

        if (listening)
        {
            ImGui.Button("Press any key…##" + idSuffix, new Vector2(160, 0));

            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                listening = false;
                pickerArmed = false;
                return;
            }

            if (!pickerArmed)
            {
                if (!AnyInputDown()) pickerArmed = true;
                return;
            }

            foreach (var k in AllVirtualKeys)
            {
                if (k == VirtualKey.NO_KEY) continue;
                if (k == VirtualKey.ESCAPE) continue;
                if (!InputBinding.IsDownRaw(k)) continue;

                setter(k);
                Config.Save();
                listening = false;
                pickerArmed = false;
                return;
            }
        }
        else
        {
            if (ImGui.Button(label + "##" + idSuffix, new Vector2(160, 0)))
            {
                listening = true;
                pickerArmed = false;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear##" + idSuffix))
            {
                setter(VirtualKey.NO_KEY);
                Config.Save();
            }
        }
    }

    private static bool AnyInputDown()
    {
        foreach (var k in AllVirtualKeys)
        {
            if (k == VirtualKey.NO_KEY) continue;
            if (InputBinding.IsDownRaw(k)) return true;
        }
        return false;
    }

    // ── Reticle ──────────────────────────────────────────────────────────────

    private void DrawReticleSection()
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Reticle");
        ImGui.Spacing();

        var show = Config.ShowReticle;
        if (ImGui.Checkbox("Show targeting reticle", ref show))
        {
            Config.ShowReticle = show;
            Config.Save();
        }
        ImGui.TextDisabled("  GW2-style crosshair at screen centre. A ring appears when an enemy is soft-targeted.");

        ImGui.BeginDisabled(!Config.ShowReticle);

        var color = Config.ReticleColor;
        if (ImGui.ColorEdit4("Reticle colour", ref color, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
        {
            Config.ReticleColor = color;
            Config.Save();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Reset##reticle"))
        {
            Config.ReticleColor = new System.Numerics.Vector4(1f, 1f, 1f, 0.8f);
            Config.Save();
        }

        ImGui.EndDisabled();
    }

    // ── Camera limits ────────────────────────────────────────────────────────

    private void DrawCameraLimitsSection()
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Vertical Camera Limits");
        ImGui.Spacing();

        var minDeg = Config.MinVRotationOverride * (180f / MathF.PI);
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Min pitch (°)", ref minDeg, -89f, 0f, "%.1f°"))
        {
            Config.MinVRotationOverride = minDeg * (MathF.PI / 180f);
            Config.Save();
        }

        var maxDeg = Config.MaxVRotationOverride * (180f / MathF.PI);
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Max pitch (°)", ref maxDeg, 0f, 89f, "%.1f°"))
        {
            Config.MaxVRotationOverride = maxDeg * (MathF.PI / 180f);
            Config.Save();
        }

        ImGui.Spacing();
        if (ImGui.Button("Reset to defaults"))
        {
            Config.MinVRotationOverride = -1.45f;
            Config.MaxVRotationOverride = 0.65f;
            Config.Save();
        }
    }
}
