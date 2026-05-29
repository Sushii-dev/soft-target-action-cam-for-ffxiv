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
        var emissive = config.IndicatorEmissive;
        var pulse = config.IndicatorPulse ? PulseFactor() : 1f;

        switch (config.InteractIndicatorStyle)
        {
            case InteractIndicatorStyle.GroundRing:    DrawGroundRing(dl, target, col, emissive, pulse); break;
            case InteractIndicatorStyle.HeadDot:       DrawHeadDot(dl, target, col, emissive, pulse); break;
            case InteractIndicatorStyle.HeadChevron:   DrawHeadChevron(dl, target, col, emissive, pulse); break;

            // The three bracket variants share a draw routine — the sizing
            // knobs (tick length + frame-tightness factor) are the only
            // difference. See DrawScreenBrackets / BracketGeometry.
            case InteractIndicatorStyle.ScreenBrackets:
                DrawScreenBrackets(dl, target, col, 8f,  0.35f, 1.00f, emissive, pulse); break;
            case InteractIndicatorStyle.ScreenBracketsLarge:
                DrawScreenBrackets(dl, target, col, 16f, 0.35f, 1.00f, emissive, pulse); break;
            case InteractIndicatorStyle.ScreenBracketsTight:
                DrawScreenBrackets(dl, target, col, 16f, 0.22f, 0.75f, emissive, pulse, centerBiasFactor: 0.12f); break;
        }
    }

    // ── Emissive rendering helpers (v0.6.29) ────────────────────────────────
    //
    // Every style renders through these so the look is consistent: a couple
    // of wide, low-alpha glow passes (the "emissive" bloom), then a thin dark
    // contrast contour, then a brightened core stroke on top. The pulse
    // factor scales the glow so the marker gently breathes like an MSQ quest
    // icon. ImGui U32 colors are packed R(low)..A(high) — see WithAlpha/
    // Brighten which respect that order.

    /// <summary>Breathing factor in ~[0.70, 1.0] at a calm cadence.</summary>
    private static float PulseFactor()
    {
        var t = (float)ImGui.GetTime();
        return 0.85f + 0.15f * MathF.Sin(t * 3.0f);
    }

    private static uint WithAlpha(uint col, float mul)
    {
        var a = (uint)Math.Clamp(((col >> 24) & 0xFFu) * mul, 0f, 255f);
        return (col & 0x00FFFFFFu) | (a << 24);
    }

    /// <summary>Lerp the RGB channels toward white by <paramref name="k"/>
    /// (alpha preserved) — gives the core stroke its emissive "hot" look.</summary>
    private static uint Brighten(uint col, float k)
    {
        uint a = (col >> 24) & 0xFFu;
        uint b = (col >> 16) & 0xFFu;
        uint g = (col >> 8)  & 0xFFu;
        uint r =  col        & 0xFFu;
        r = (uint)(r + (255 - r) * k);
        g = (uint)(g + (255 - g) * k);
        b = (uint)(b + (255 - b) * k);
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    /// <summary>
    /// Emissive line stroke with ROUNDED ends. AddLine draws a flat-capped
    /// rectangle, so a wide glow pass has blunt rectangular ends and, where
    /// two strokes meet (bracket corners), a gap. We round both by stamping
    /// filled discs at each endpoint at every layer's radius — that softens
    /// the bloom ends and fills the corner joint (both strokes stamp the
    /// shared corner). Three glow layers for a stronger emissive falloff,
    /// then a dark contrast contour, a brightened core, and a hot near-white
    /// inner line for the "glowing filament" look.
    /// </summary>
    private static void EmissiveStroke(ImDrawListPtr dl, Vector2 p1, Vector2 p2, uint color, float thickness, bool emissive, float pulse)
    {
        if (emissive)
        {
            GlowSeg(dl, p1, p2, WithAlpha(color, 0.10f * pulse), thickness + 8f);
            GlowSeg(dl, p1, p2, WithAlpha(color, 0.18f * pulse), thickness + 5f);
            GlowSeg(dl, p1, p2, WithAlpha(color, 0.30f * pulse), thickness + 2.5f);
        }

        // Dark contrast contour (rounded), then bright core (rounded), then
        // a hot inner filament.
        GlowSeg(dl, p1, p2, 0xB0000000u, thickness + 1.4f);

        var core = Brighten(color, 0.45f);
        GlowSeg(dl, p1, p2, core, thickness);

        var hot = Brighten(color, 0.85f);
        dl.AddLine(p1, p2, hot, MathF.Max(1f, thickness - 1.4f));
    }

    /// <summary>One rounded-cap segment: the line plus a filled disc at each
    /// endpoint sized to the stroke half-width, so ends are round and
    /// adjoining segments merge with no gap.</summary>
    private static void GlowSeg(ImDrawListPtr dl, Vector2 p1, Vector2 p2, uint col, float thickness)
    {
        dl.AddLine(p1, p2, col, thickness);
        var r = thickness * 0.5f;
        dl.AddCircleFilled(p1, r, col, 16);
        dl.AddCircleFilled(p2, r, col, 16);
    }

    private static void EmissiveEllipse(ImDrawListPtr dl, Vector2 c, float rx, float ry, uint color, float thickness, int seg, bool emissive, float pulse)
    {
        if (emissive)
        {
            DrawEllipse(dl, c, rx, ry, WithAlpha(color, 0.10f * pulse), thickness + 8f,   seg);
            DrawEllipse(dl, c, rx, ry, WithAlpha(color, 0.18f * pulse), thickness + 5f,   seg);
            DrawEllipse(dl, c, rx, ry, WithAlpha(color, 0.30f * pulse), thickness + 2.5f, seg);
        }
        DrawEllipse(dl, c, rx, ry, 0xB0000000u,              thickness + 1.4f, seg);
        DrawEllipse(dl, c, rx, ry, Brighten(color, 0.45f),   thickness,        seg);
        DrawEllipse(dl, c, rx, ry, Brighten(color, 0.85f),   MathF.Max(1f, thickness - 1.4f), seg);
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

    private static void DrawGroundRing(ImDrawListPtr dl, Dalamud.Game.ClientState.Objects.Types.IGameObject t, uint color, bool emissive, float pulse)
    {
        var feet = t.Position; // GameObject.Position is at the feet anchor
        if (!Plugin.GameGui.WorldToScreen(feet, out var screen)) return;

        // Perspective-faked ground oval (wider than tall). Emissive glow +
        // bright core via the shared helper, plus a fainter inner ring.
        const float radiusX = 22f;
        const float radiusY = 8f;
        const float thickness = 2f;

        EmissiveEllipse(dl, screen, radiusX, radiusY, color, thickness, 40, emissive, pulse);
        EmissiveEllipse(dl, screen, radiusX * 0.6f, radiusY * 0.6f, WithAlpha(color, 0.6f), 1.2f, 28, emissive, pulse);
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

    private static void DrawHeadDot(ImDrawListPtr dl, Dalamud.Game.ClientState.Objects.Types.IGameObject t, uint color, bool emissive, float pulse)
    {
        var headWorld = HeadAnchor(t);
        if (!Plugin.GameGui.WorldToScreen(headWorld, out var screen)) return;

        const float radius = 5f;
        if (emissive)
        {
            dl.AddCircleFilled(screen, radius + 10f, WithAlpha(color, 0.10f * pulse));
            dl.AddCircleFilled(screen, radius + 6f,  WithAlpha(color, 0.18f * pulse));
            dl.AddCircleFilled(screen, radius + 3f,  WithAlpha(color, 0.30f * pulse));
        }
        dl.AddCircleFilled(screen, radius + 1.4f, 0xB0000000u);           // dark contrast ring
        dl.AddCircleFilled(screen, radius,        Brighten(color, 0.45f)); // bright core
        dl.AddCircleFilled(screen, radius - 1.6f, Brighten(color, 0.85f)); // hot center
    }

    // ── Style: head chevron ─────────────────────────────────────────────────

    private static void DrawHeadChevron(ImDrawListPtr dl, Dalamud.Game.ClientState.Objects.Types.IGameObject t, uint color, bool emissive, float pulse)
    {
        var headWorld = HeadAnchor(t);
        if (!Plugin.GameGui.WorldToScreen(headWorld, out var screen)) return;

        const float w = 9f;
        const float h = 7f;
        const float thickness = 2.5f;
        var top    = screen + new Vector2(0f, -h);
        var leftT  = screen + new Vector2(-w, -h * 2.2f);
        var rightT = screen + new Vector2( w, -h * 2.2f);

        EmissiveStroke(dl, leftT,  top, color, thickness, emissive, pulse);
        EmissiveStroke(dl, rightT, top, color, thickness, emissive, pulse);
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
        float heightFactor,
        bool emissive,
        float pulse,
        float centerBiasFactor = 0f)
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
        // the smaller frame stays centred on the target. The head anchor
        // sits ~2.2y above the feet, so the raw midline lands at upper-
        // torso/chest — a shrunk frame there reads as "too high". A
        // positive centerBiasFactor nudges the frame down (screen Y grows
        // downward) toward true body-centre for the tight variant.
        var centerY = (topScreen.Y + botScreen.Y) * 0.5f + rawHeight * centerBiasFactor;
        var height  = rawHeight * heightFactor;
        var topY    = centerY - height * 0.5f;
        var botY    = centerY + height * 0.5f;

        var halfW = height * halfWidthFactor;
        var cx    = (topScreen.X + botScreen.X) * 0.5f;

        var tl = new Vector2(cx - halfW, topY);
        var tr = new Vector2(cx + halfW, topY);
        var bl = new Vector2(cx - halfW, botY);
        var br = new Vector2(cx + halfW, botY);

        const float thickness = 2.5f;

        DrawCornerTick(dl, tl, +tickLen, +tickLen, color, thickness, emissive, pulse);
        DrawCornerTick(dl, tr, -tickLen, +tickLen, color, thickness, emissive, pulse);
        DrawCornerTick(dl, bl, +tickLen, -tickLen, color, thickness, emissive, pulse);
        DrawCornerTick(dl, br, -tickLen, -tickLen, color, thickness, emissive, pulse);
    }

    /// <summary>
    /// Draws one L-shaped corner tick rooted at <paramref name="corner"/>,
    /// extending <paramref name="dx"/> along the X axis and
    /// <paramref name="dy"/> along Y. Signed deltas pick the corner
    /// direction (positive = inward and down for top-left, etc.). Draws
    /// the black halo first, then the user-colored stroke on top.
    /// </summary>
    private static void DrawCornerTick(ImDrawListPtr dl, Vector2 corner, float dx, float dy, uint color, float thickness, bool emissive, float pulse)
    {
        EmissiveStroke(dl, corner, corner + new Vector2(dx, 0f), color, thickness, emissive, pulse);
        EmissiveStroke(dl, corner, corner + new Vector2(0f, dy), color, thickness, emissive, pulse);
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
