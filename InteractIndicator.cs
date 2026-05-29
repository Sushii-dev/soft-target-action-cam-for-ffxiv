using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;

namespace ActionCamera;

/// <summary>
/// Visual style for the interact indicator. Each value maps to a distinct
/// draw routine in <see cref="InteractIndicator"/>. The user picks one from
/// a dropdown in the config window.
///
/// Order matters for backward compatibility: existing saved configs
/// reference the enum by underlying int, so values 0..3 must keep their
/// original meaning. New entries append at the end.
/// </summary>
public enum InteractIndicatorStyle
{
    /// <summary>Soft semi-transparent ring on the ground at the target's
    /// feet. Closest match to FFXIV's own "interactable" affordance — reads
    /// as a native gameplay cue rather than a plugin overlay. Default.</summary>
    GroundRing = 0,

    /// <summary>Small filled dot floating just above the target's head.
    /// Maximally subtle — barely registers unless you look for it.</summary>
    HeadDot = 1,

    /// <summary>Downward-pointing chevron floating above the target's head.
    /// Reads as "this one" — slightly more directive than the dot.</summary>
    HeadChevron = 2,

    /// <summary>Four corner brackets framing the target's screen bounds
    /// (Elden Ring / Souls "locked-on" style). Short ticks, generous
    /// framing — the original "subtle" bracket size.</summary>
    ScreenBrackets = 3,

    /// <summary>(v0.5.25.0) Same bracket layout as <see cref="ScreenBrackets"/>
    /// but with longer corner ticks — easier to read at glance on busy
    /// terrain. Same generous framing as small.</summary>
    ScreenBracketsLarge = 4,

    /// <summary>(v0.5.25.0) Large ticks AND tighter framing — brackets pull
    /// inward toward the target so there's less negative space inside the
    /// frame. Strongest "this is locked on" read of the bracket variants.</summary>
    ScreenBracketsTight = 5,
}

/// <summary>
/// Draws a subtle marker over the candidate target for the interact key, so
/// the player can see at a glance what pressing the key will do.
///
/// <para>Drawn only when ALL of these are true:
/// <list type="bullet">
///   <item>The user enabled the indicator in config.</item>
///   <item>The local player has their weapon sheathed
///   (<see cref="StatusFlags.WeaponOut"/> not set).</item>
///   <item>The player is not in combat
///   (<see cref="ConditionFlag.InCombat"/> off).</item>
///   <item>The player is not in a duty
///   (<see cref="ConditionFlag.BoundByDuty"/> off — also covers trials /
///   raids / variant dungeons).</item>
/// </list>
/// Those gates intentionally err on the side of "stay out of the way" —
/// the indicator is for exploration / town / overworld use, not combat HUD.
/// </para>
///
/// Pulls the candidate from <see cref="InteractHandler.GetIndicatorCandidate"/>
/// so the same priority chain that drives the interact key drives what's
/// drawn — no risk of indicator pointing at one target while the key fires
/// against another.
/// </summary>
public sealed class InteractIndicator
{
    // Pure black with full alpha — the outline / halo color for every style.
    // User-chosen color drives the foreground stroke; we always pair it with
    // this fixed dark backing to keep the indicator readable against bright
    // skies / snow / pale walls etc. that bleed into the user color.
    private const uint OutlineColor = 0xFF000000u;

    private readonly Configuration config;
    private readonly Func<Dalamud.Game.ClientState.Objects.Types.IGameObject?> candidateFn;

    public InteractIndicator(Configuration config, Func<Dalamud.Game.ClientState.Objects.Types.IGameObject?> candidateFn)
    {
        this.config = config;
        this.candidateFn = candidateFn;
    }

    public void Draw()
    {
        if (!config.ShowInteractIndicator) return;
        if (!PassesGameStateGates()) return;

        var target = candidateFn();
        if (target == null) return;

        var dl  = ImGui.GetForegroundDrawList();
        var col = ImGui.ColorConvertFloat4ToU32(config.InteractIndicatorColor);

        switch (config.InteractIndicatorStyle)
        {
            case InteractIndicatorStyle.GroundRing:    DrawGroundRing(dl, target, col); break;
            case InteractIndicatorStyle.HeadDot:       DrawHeadDot(dl, target, col); break;
            case InteractIndicatorStyle.HeadChevron:   DrawHeadChevron(dl, target, col); break;

            // The three bracket variants share a draw routine — the sizing
            // knobs (tick length + frame-tightness factor) are the only
            // difference. See DrawScreenBrackets / BracketGeometry.
            case InteractIndicatorStyle.ScreenBrackets:
                DrawScreenBrackets(dl, target, col, tickLen: 8f,  halfWidthFactor: 0.35f, heightFactor: 1.00f); break;
            case InteractIndicatorStyle.ScreenBracketsLarge:
                DrawScreenBrackets(dl, target, col, tickLen: 16f, halfWidthFactor: 0.35f, heightFactor: 1.00f); break;
            case InteractIndicatorStyle.ScreenBracketsTight:
                DrawScreenBrackets(dl, target, col, tickLen: 16f, halfWidthFactor: 0.22f, heightFactor: 0.75f); break;
        }
    }

    private static bool PassesGameStateGates()
    {
        // v0.6.27: the old gates hid the indicator while the weapon was
        // out / in combat / bound by duty. That defeated the main use the
        // user wanted — seeing dungeon/raid LOOT COFFERS (you're always
        // weapon-out + BoundByDuty next to those) — and was the prime
        // cause of the indicator "bugging out / disappearing": those
        // conditions flip constantly near combat, blinking the overlay.
        // The candidate-null check in Draw() already hides it when there's
        // nothing to point at, so unconditional show (when logged in) is
        // both what the user wants and more robust. The ShowInteractIndicator
        // master toggle still gates the whole feature.
        return Plugin.ObjectTable.LocalPlayer != null;
    }

    // ── Style: ground ring ──────────────────────────────────────────────────

    private static void DrawGroundRing(ImDrawListPtr dl, Dalamud.Game.ClientState.Objects.Types.IGameObject t, uint color)
    {
        var feet = t.Position; // GameObject.Position is at the feet anchor
        if (!Plugin.GameGui.WorldToScreen(feet, out var screen)) return;

        // Two faint concentric ovals to fake a perspective ring without
        // requiring a real 3D projection. Wider horizontally than vertically
        // sells the "lying flat on the ground" read.
        const float radiusX = 22f;
        const float radiusY = 8f;
        const float thickness = 1.5f;

        // Outline pass first — same shape, slightly thicker, in black. Acts
        // as a halo around the colored stroke so the ring reads against
        // both light and dark ground textures. Same pattern for every style.
        DrawEllipse(dl, screen, radiusX, radiusY, OutlineColor, thickness + 1.5f, segments: 36);
        DrawEllipse(dl, screen, radiusX, radiusY, color,        thickness,       segments: 36);

        // Inner faint ring at 60 % size for a subtle bullseye effect — keeps
        // the affordance readable on busy ground textures.
        var dim = (color & 0x00FFFFFFu) | ((uint)((color >> 24 & 0xFFu) * 0.55f) << 24);
        DrawEllipse(dl, screen, radiusX * 0.6f, radiusY * 0.6f, OutlineColor, 2.0f, segments: 28);
        DrawEllipse(dl, screen, radiusX * 0.6f, radiusY * 0.6f, dim,          1.0f, segments: 28);
    }

    private static void DrawEllipse(ImDrawListPtr dl, Vector2 center, float rx, float ry, uint color, float thickness, int segments)
    {
        var prev = center + new Vector2(rx, 0f);
        for (var i = 1; i <= segments; i++)
        {
            var a = (i / (float)segments) * MathF.Tau;
            var next = center + new Vector2(MathF.Cos(a) * rx, MathF.Sin(a) * ry);
            dl.AddLine(prev, next, color, thickness);
            prev = next;
        }
    }

    // ── Style: head dot ─────────────────────────────────────────────────────

    private static void DrawHeadDot(ImDrawListPtr dl, Dalamud.Game.ClientState.Objects.Types.IGameObject t, uint color)
    {
        var headWorld = HeadAnchor(t);
        if (!Plugin.GameGui.WorldToScreen(headWorld, out var screen)) return;

        // Black disk slightly larger than the colored disk — the black ring
        // around the dot is what makes it pop against bright skyboxes and
        // dark interiors equally. Previous implementation outlined with the
        // user color at full alpha, which disappeared against same-hue
        // backgrounds (eg gold dot on Limsa fountain rim).
        const float radius = 4.5f;
        dl.AddCircleFilled(screen, radius + 1.5f, OutlineColor);
        dl.AddCircleFilled(screen, radius,        color);
    }

    // ── Style: head chevron ─────────────────────────────────────────────────

    private static void DrawHeadChevron(ImDrawListPtr dl, Dalamud.Game.ClientState.Objects.Types.IGameObject t, uint color)
    {
        var headWorld = HeadAnchor(t);
        if (!Plugin.GameGui.WorldToScreen(headWorld, out var screen)) return;

        // Downward-pointing chevron — "this one". Sized small to stay
        // subtle but big enough to read at a glance.
        const float w = 8f;
        const float h = 6f;
        const float thickness = 2f;
        var top    = screen + new Vector2(0f, -h);
        var leftT  = screen + new Vector2(-w, -h * 2.2f);
        var rightT = screen + new Vector2( w, -h * 2.2f);

        // Halo pass — thicker black lines first, then colored on top.
        dl.AddLine(leftT,  top, OutlineColor, thickness + 1.5f);
        dl.AddLine(rightT, top, OutlineColor, thickness + 1.5f);
        dl.AddLine(leftT,  top, color,        thickness);
        dl.AddLine(rightT, top, color,        thickness);
    }

    // ── Style: screen brackets (all 3 sizes) ────────────────────────────────

    /// <summary>
    /// Draws Souls-style corner brackets framing the target. All three
    /// bracket variants funnel through this routine and differ only in
    /// the three tunable knobs:
    /// <list type="bullet">
    ///   <item><paramref name="tickLen"/> — length in pixels of each corner
    ///   tick. Larger = more visible, less subtle.</item>
    ///   <item><paramref name="halfWidthFactor"/> — multiplier on projected
    ///   height that determines bracket-frame width. Lower = tighter
    ///   framing (less negative space inside).</item>
    ///   <item><paramref name="heightFactor"/> — multiplier on the
    ///   projected target height. Lower = brackets hug closer to the
    ///   torso instead of the full silhouette. 1.0 = full head-to-feet.</item>
    /// </list>
    /// </summary>
    private static void DrawScreenBrackets(
        ImDrawListPtr dl,
        Dalamud.Game.ClientState.Objects.Types.IGameObject t,
        uint color,
        float tickLen,
        float halfWidthFactor,
        float heightFactor)
    {
        var headWorld = HeadAnchor(t);
        var feetWorld = t.Position;

        // Project both top and bottom to derive a screen-space bounding box.
        // Width is a fixed fraction of the projected height (FFXIV character
        // proportions don't vary that wildly) — saves a third projection.
        if (!Plugin.GameGui.WorldToScreen(headWorld, out var topScreen)) return;
        if (!Plugin.GameGui.WorldToScreen(feetWorld, out var botScreen)) return;

        var rawHeight = MathF.Max(20f, MathF.Abs(botScreen.Y - topScreen.Y));
        // heightFactor < 1 shrinks the frame around the midline — used by
        // the "tight" variant. Recompute top/bottom relative to centerY so
        // the smaller frame stays centred on the target.
        var centerY = (topScreen.Y + botScreen.Y) * 0.5f;
        var height  = rawHeight * heightFactor;
        var topY    = centerY - height * 0.5f;
        var botY    = centerY + height * 0.5f;

        var halfW = height * halfWidthFactor;
        var cx    = (topScreen.X + botScreen.X) * 0.5f;

        var tl = new Vector2(cx - halfW, topY);
        var tr = new Vector2(cx + halfW, topY);
        var bl = new Vector2(cx - halfW, botY);
        var br = new Vector2(cx + halfW, botY);

        const float thickness = 2f;

        DrawCornerTick(dl, tl, +tickLen, +tickLen, color, thickness);
        DrawCornerTick(dl, tr, -tickLen, +tickLen, color, thickness);
        DrawCornerTick(dl, bl, +tickLen, -tickLen, color, thickness);
        DrawCornerTick(dl, br, -tickLen, -tickLen, color, thickness);
    }

    /// <summary>
    /// Draws one L-shaped corner tick rooted at <paramref name="corner"/>,
    /// extending <paramref name="dx"/> along the X axis and
    /// <paramref name="dy"/> along Y. Signed deltas pick the corner
    /// direction (positive = inward and down for top-left, etc.). Draws
    /// the black halo first, then the user-colored stroke on top.
    /// </summary>
    private static void DrawCornerTick(ImDrawListPtr dl, Vector2 corner, float dx, float dy, uint color, float thickness)
    {
        var hx = new Vector2(dx, 0f);
        var vy = new Vector2(0f, dy);

        // Halo
        dl.AddLine(corner, corner + hx, OutlineColor, thickness + 1.5f);
        dl.AddLine(corner, corner + vy, OutlineColor, thickness + 1.5f);
        // Foreground
        dl.AddLine(corner, corner + hx, color, thickness);
        dl.AddLine(corner, corner + vy, color, thickness);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Approximate world position above the target's head. Uses the hitbox
    /// radius as a rough model-height proxy plus a fixed bump — accurate
    /// enough for an indicator that lives in screen space and won't be
    /// scrutinised against the geometry.
    /// </summary>
    private static Vector3 HeadAnchor(Dalamud.Game.ClientState.Objects.Types.IGameObject t)
    {
        var feet = t.Position;
        // 2.2y default + 0.7×hitbox radius. NPCs and players alike fall in
        // this range; large bosses end up with the marker over the upper
        // body which is fine for an indicator.
        var headY = feet.Y + 2.2f + t.HitboxRadius * 0.7f;
        return new Vector3(feet.X, headY, feet.Z);
    }
}
