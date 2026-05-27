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
    /// (Elden Ring / Souls "locked-on" style). Strongest signal of the
    /// four — useful when you want to be sure where the key will land.</summary>
    ScreenBrackets = 3,
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

        var dl = ImGui.GetForegroundDrawList();
        var col = ImGui.ColorConvertFloat4ToU32(config.InteractIndicatorColor);

        switch (config.InteractIndicatorStyle)
        {
            case InteractIndicatorStyle.GroundRing:    DrawGroundRing(dl, target, col); break;
            case InteractIndicatorStyle.HeadDot:       DrawHeadDot(dl, target, col); break;
            case InteractIndicatorStyle.HeadChevron:   DrawHeadChevron(dl, target, col); break;
            case InteractIndicatorStyle.ScreenBrackets: DrawScreenBrackets(dl, target, col); break;
        }
    }

    private static bool PassesGameStateGates()
    {
        // Sheathed: nothing else makes sense if the player is mid-fight.
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return false;
        if (lp.StatusFlags.HasFlag(StatusFlags.WeaponOut)) return false;

        // In combat at all (even out-of-aggro on a mob looking your way).
        if (Plugin.Condition[ConditionFlag.InCombat]) return false;

        // BoundByDuty covers dungeons, trials, raids, deep dungeons,
        // variant / criterion dungeons, alliance — all the dense HUD
        // scenarios where another overlay would be noise.
        if (Plugin.Condition[ConditionFlag.BoundByDuty]) return false;

        return true;
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

        // Outer line.
        DrawEllipse(dl, screen, radiusX, radiusY, color, 1.5f, segments: 36);
        // Inner faint ring at 60 % size for a subtle bullseye effect — keeps
        // the affordance readable on busy ground textures.
        var dim = (color & 0x00FFFFFFu) | ((uint)((color >> 24 & 0xFFu) * 0.55f) << 24);
        DrawEllipse(dl, screen, radiusX * 0.6f, radiusY * 0.6f, dim, 1f, segments: 28);
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
        // Hitbox radius is a decent proxy for "model height" — most NPCs
        // have hit radius ~0.5y and stand ~2y tall. 2 × HitboxRadius +
        // a small fixed bump approximates head height.
        var headWorld = HeadAnchor(t);
        if (!Plugin.GameGui.WorldToScreen(headWorld, out var screen)) return;

        // Small filled dot with a hairline outline for legibility against
        // bright skyboxes / dark interiors alike.
        dl.AddCircleFilled(screen, 4.5f, color);
        var outline = (color & 0x00FFFFFFu) | (0xFFu << 24);
        dl.AddCircle(screen, 4.5f, outline, 16, 1f);
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
        var top    = screen + new Vector2(0f, -h);
        var leftT  = screen + new Vector2(-w, -h * 2.2f);
        var rightT = screen + new Vector2( w, -h * 2.2f);

        dl.AddLine(leftT,  top, color, 2f);
        dl.AddLine(rightT, top, color, 2f);
    }

    // ── Style: screen brackets ──────────────────────────────────────────────

    private static void DrawScreenBrackets(ImDrawListPtr dl, Dalamud.Game.ClientState.Objects.Types.IGameObject t, uint color)
    {
        var headWorld = HeadAnchor(t);
        var feetWorld = t.Position;

        // Project both top and bottom to derive a screen-space bounding box.
        // Width is a fixed fraction of the projected height (FFXIV character
        // proportions don't vary that wildly) — saves us a third projection.
        if (!Plugin.GameGui.WorldToScreen(headWorld, out var topScreen)) return;
        if (!Plugin.GameGui.WorldToScreen(feetWorld, out var botScreen)) return;

        var height = MathF.Max(20f, MathF.Abs(botScreen.Y - topScreen.Y));
        var halfW  = height * 0.35f;
        var cx     = (topScreen.X + botScreen.X) * 0.5f;

        var tl = new Vector2(cx - halfW, topScreen.Y);
        var tr = new Vector2(cx + halfW, topScreen.Y);
        var bl = new Vector2(cx - halfW, botScreen.Y);
        var br = new Vector2(cx + halfW, botScreen.Y);

        // Short corner ticks — not a full rectangle. Souls-style.
        const float tick = 8f;

        // Top-left
        dl.AddLine(tl, tl + new Vector2(tick, 0f), color, 2f);
        dl.AddLine(tl, tl + new Vector2(0f, tick), color, 2f);
        // Top-right
        dl.AddLine(tr, tr + new Vector2(-tick, 0f), color, 2f);
        dl.AddLine(tr, tr + new Vector2(0f,  tick), color, 2f);
        // Bottom-left
        dl.AddLine(bl, bl + new Vector2(tick, 0f), color, 2f);
        dl.AddLine(bl, bl + new Vector2(0f, -tick), color, 2f);
        // Bottom-right
        dl.AddLine(br, br + new Vector2(-tick, 0f), color, 2f);
        dl.AddLine(br, br + new Vector2(0f, -tick), color, 2f);
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
