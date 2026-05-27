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
    private bool listeningForInteractKey;

    // Shared arming flag: while a picker is listening, we wait for all inputs
    // to be released before capturing the next press. Stops the LMB click
    // that opened the picker (and any held modifier) from being captured as
    // the binding. Only one picker can be listening at a time.
    private bool pickerArmed;

    // Cached once: we scan this on every picker frame, including a "is any
    // input held" probe for arming.
    private static readonly VirtualKey[] AllVirtualKeys = (VirtualKey[])Enum.GetValues(typeof(VirtualKey));

    public ConfigWindow(Plugin plugin)
        : base("Action Camera Settings###ActionCameraConfig")
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
        DrawInteractFeedbackSection();
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

        ImGui.TextDisabled("  Press to toggle on. While on, the camera stays active until");
        ImGui.TextDisabled("  the cursor becomes visible (popup, menu, alt-tab, etc.) — then");
        ImGui.TextDisabled("  the camera turns off and stays off until you press this key again.");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Auto-resume exemptions");
        ImGui.TextDisabled("  By default any cursor-visible event sticks the cam off. Tick a box");
        ImGui.TextDisabled("  to keep the activation key's intent across that scenario — the cam");
        ImGui.TextDisabled("  will deactivate while the cursor is visible and auto-resume when");
        ImGui.TextDisabled("  the cursor hides again.");
        ImGui.Spacing();

        var arUI = Config.AutoResumeAfterUI;
        if (ImGui.Checkbox("After closing UI (inventory, map, chat, ...)", ref arUI))
        {
            Config.AutoResumeAfterUI = arUI;
            Config.Save();
        }

        var arCut = Config.AutoResumeAfterCutscene;
        if (ImGui.Checkbox("After cutscenes", ref arCut))
        {
            Config.AutoResumeAfterCutscene = arCut;
            Config.Save();
        }

        var arEvt = Config.AutoResumeAfterEvent;
        if (ImGui.Checkbox("After dialogues / scripted events", ref arEvt))
        {
            Config.AutoResumeAfterEvent = arEvt;
            Config.Save();
        }

        var arZone = Config.AutoResumeAfterZoneTransition;
        if (ImGui.Checkbox("After zone transitions / loading screens", ref arZone))
        {
            Config.AutoResumeAfterZoneTransition = arZone;
            Config.Save();
        }

        ImGui.TextDisabled("  Alt-tab / loss of focus is never exempted.");
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

        ImGui.Spacing();
        var suppressClick = Config.SuppressClickHardTargetInCam;
        if (ImGui.Checkbox("Suppress left-click hard targeting in camera mode", ref suppressClick))
        {
            Config.SuppressClickHardTargetInCam = suppressClick;
            Config.Save();
        }
        ImGui.TextDisabled("  Blocks LMB-click-to-target and LMB-click-empty-to-clear while");
        ImGui.TextDisabled("  the camera is active. Hard target can only change via the");
        ImGui.TextDisabled("  configured keybinds. Does not affect non-camera mode.");

        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Text("Hard target key:");
        ImGui.SameLine();
        DrawKeyPicker("hardkey", Config.HardTargetKey,
            k => Config.HardTargetKey = k, ref listeningForHardKey);
        ImGui.TextDisabled("  Edge-triggered: hard-targets the cone pick (only while camera is active).");

        ImGui.Spacing();
        ImGui.Text("Interact key:");
        ImGui.SameLine();
        DrawKeyPicker("interactkey", Config.InteractKey,
            k => Config.InteractKey = k, ref listeningForInteractKey);
        ImGui.TextDisabled("  Advances open dialogue (Talk / Select* / Yes-No / Ok / Journal / etc.)");
        ImGui.TextDisabled("  OR interacts with the hard target / nearest EventNpc-EventObj in the cone.");

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

    // ── Interact feedback (indicator + audio + player examine) ──────────────

    private static readonly string[] IndicatorStyleLabels =
    {
        "Ground ring",
        "Dot above head",
        "Chevron above head",
        "Screen brackets (small)",
        "Screen brackets (large)",
        "Screen brackets (tight)",
    };

    private void DrawInteractFeedbackSection()
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Interact Targeting");
        ImGui.TextDisabled("  Separate cone + range for the interact key, distinct from");
        ImGui.TextDisabled("  the combat auto-target settings above.");
        ImGui.Spacing();

        var ifov = Config.InteractFovDegrees;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Interact FOV cone (°)", ref ifov, 5f, 90f, "%.0f°"))
        {
            Config.InteractFovDegrees = ifov;
            Config.Save();
        }

        var idist = Config.InteractMaxDistance;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Interact max distance (y)", ref idist, 2f, 30f, "%.0fy"))
        {
            Config.InteractMaxDistance = idist;
            Config.Save();
        }
        ImGui.TextDisabled("  The game's own InteractWithObject silently rejects out-of-range");
        ImGui.TextDisabled("  targets — raising this just lets the indicator look further ahead.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "Interact Feedback");
        ImGui.TextDisabled("  Visual + audio cues for the interact key.");
        ImGui.Spacing();

        // ── Indicator ───────────────────────────────────────────────────────
        var showInd = Config.ShowInteractIndicator;
        if (ImGui.Checkbox("Show indicator over the candidate interact target", ref showInd))
        {
            Config.ShowInteractIndicator = showInd;
            Config.Save();
        }
        ImGui.TextDisabled("  Only shown when sheathed, out of combat, and not in a duty.");

        ImGui.BeginDisabled(!Config.ShowInteractIndicator);

        var styleIdx = (int)Config.InteractIndicatorStyle;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Style", ref styleIdx, IndicatorStyleLabels, IndicatorStyleLabels.Length))
        {
            Config.InteractIndicatorStyle = (InteractIndicatorStyle)styleIdx;
            Config.Save();
        }

        var color = Config.InteractIndicatorColor;
        if (ImGui.ColorEdit4("Indicator color", ref color,
                ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoInputs))
        {
            Config.InteractIndicatorColor = color;
            Config.Save();
        }

        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Audio ───────────────────────────────────────────────────────────
        ImGui.TextDisabled("Sounds use the game's own UI sfx — they respect the");
        ImGui.TextDisabled("System Sounds volume slider in FFXIV's settings.");
        ImGui.Spacing();

        DrawSfxRow("On first interaction (NPC / examine)",
            "successsfx",
            Config.PlayInteractSuccessSound,
            Config.InteractSuccessSoundId,
            (en, id) =>
            {
                Config.PlayInteractSuccessSound = en;
                Config.InteractSuccessSoundId   = id;
                Config.Save();
            });

        DrawSfxRow("On miss (no target found)",
            "failsfx",
            Config.PlayInteractFailSound,
            Config.InteractFailSoundId,
            (en, id) =>
            {
                Config.PlayInteractFailSound = en;
                Config.InteractFailSoundId   = id;
                Config.Save();
            });

        ImGui.TextDisabled("  Dialogue-advance presses stay silent — the game plays its own.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Player examine ──────────────────────────────────────────────────
        var examine = Config.InteractExaminePlayers;
        if (ImGui.Checkbox("Interact key opens Examine on nearby players", ref examine))
        {
            Config.InteractExaminePlayers = examine;
            Config.Save();
        }
        ImGui.TextDisabled("  Used only as a fallback — NPCs / event objects / aetherytes");
        ImGui.TextDisabled("  win the cone first. Blocked while your weapon is drawn.");
    }

    /// <summary>
    /// One row: enable checkbox + sound id input + "Test" button. The
    /// game's UI sfx live roughly in the 1..80 range; 37..52 are the
    /// configurable <c>&lt;se.1&gt;..&lt;se.16&gt;</c> chat alerts that
    /// players can already audition in System Configuration. Test button
    /// always plays regardless of the enable checkbox state.
    /// </summary>
    private static void DrawSfxRow(string label, string idSuffix, bool enabled, uint soundId, Action<bool, uint> apply)
    {
        var en = enabled;
        if (ImGui.Checkbox($"{label}##en{idSuffix}", ref en))
            apply(en, soundId);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        var idInt = (int)soundId;
        if (ImGui.InputInt($"id##{idSuffix}", ref idInt))
        {
            if (idInt < 1)  idInt = 1;
            if (idInt > 80) idInt = 80;
            apply(en, (uint)idInt);
        }

        ImGui.SameLine();
        if (ImGui.Button($"Test##{idSuffix}"))
            Sfx.PlayTest((uint)idInt);
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
