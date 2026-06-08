using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ActionCamera;

/// <summary>
/// Makes the current FOCUS heal target obvious, two ways:
///   • A pulsing glowing world-space marker over the target (same styles +
///     emissive look as the interact indicator, via
///     <see cref="InteractIndicator.DrawMarker"/>) so you can find them in 3D.
///   • A glowing highlight over the target's row in the party list — the
///     focus-target analogue of the game's native hard-target row glow, in a
///     distinct colour so the two never read the same.
///
/// Both draw only while a focus target is set, every frame from
/// <c>UiBuilder.Draw</c>, recomputed from the live <c>FocusTarget</c> — so a
/// focus swap moves the markers next frame and they never leave stale paint.
///
/// Unlike the interact indicator, there are NO weapon/combat/duty gates: the
/// whole point is healing inside dungeons, weapon out and bound by duty.
///
/// Party-list mapping: <see cref="AgentHUD"/> holds the displayed party in row
/// order, each entry carrying the member's <c>EntityId</c> and display
/// <c>Index</c>. We match the focus by EntityId to get its row index, then
/// resolve that row's collision node in <see cref="AddonPartyList"/> (trying the
/// player-party array first, then the trust/duty-support array) for the screen
/// rect. No node mutation — we only read positions and draw on top.
/// </summary>
public sealed unsafe class FocusIndicator
{
    private readonly Configuration config;

    public FocusIndicator(Configuration config)
    {
        this.config = config;
    }

    public void Draw()
    {
        if (!config.ShowFocusIndicator) return;
        if (Plugin.ObjectTable.LocalPlayer == null) return;

        var focus = Plugin.TargetManager.FocusTarget;
        if (focus == null) return;

        var col = ImGui.ColorConvertFloat4ToU32(config.FocusIndicatorColor);
        var emissive = config.FocusIndicatorEmissive;
        var pulse = config.FocusIndicatorPulse ? InteractIndicator.PulseFactor() : 1f;

        var dl = ImGui.GetForegroundDrawList();

        if (config.FocusIndicatorWorld)
            DrawFocusIcon(dl, focus, col, emissive, pulse);

        if (config.FocusIndicatorPartyList)
            DrawPartyListHighlight(dl, focus, col, emissive, pulse);
    }

    // ── World icon (dedicated focus marker) ──────────────────────────────────
    //
    // A glowing diamond floating above the target's head — deliberately NOT one
    // of the interact indicator's shapes (ring / dot / chevron / brackets) so
    // the focus marker is its own distinct icon. Same emissive stack (wide glow
    // passes → dark contour → bright core → hot inner) + breathing pulse so it
    // reads as one family with the rest of Veiled's cues, just unmistakably
    // "the focus".

    private static void DrawFocusIcon(ImDrawListPtr dl, IGameObject t, uint col, bool emissive, float pulse)
    {
        var head = InteractIndicator.HeadAnchor(t);
        if (!Plugin.GameGui.WorldToScreen(head, out var screen)) return;

        // Float the diamond a little above the head anchor; breathe its size
        // gently with the pulse so it pops without being noisy.
        var c = screen + new Vector2(0f, -20f);
        var r = 10f * (0.92f + 0.08f * pulse);

        if (emissive)
        {
            Diamond(dl, c, r + 7f,   InteractIndicator.WithAlpha(col, 0.10f * pulse), true, 0f);
            Diamond(dl, c, r + 4f,   InteractIndicator.WithAlpha(col, 0.18f * pulse), true, 0f);
            Diamond(dl, c, r + 2f,   InteractIndicator.WithAlpha(col, 0.30f * pulse), true, 0f);
        }

        Diamond(dl, c, r + 1.4f, 0xB0000000u,                       false, 2.2f); // dark contour
        Diamond(dl, c, r,        InteractIndicator.Brighten(col, 0.45f), true, 0f);   // bright core
        Diamond(dl, c, r - 3.2f, InteractIndicator.Brighten(col, 0.85f), true, 0f);   // hot center
    }

    /// <summary>Axis-aligned diamond (rotated square) centred at
    /// <paramref name="c"/> with half-extent <paramref name="r"/>. Filled, or a
    /// stroked outline at <paramref name="thickness"/>.</summary>
    private static void Diamond(ImDrawListPtr dl, Vector2 c, float r, uint col, bool filled, float thickness)
    {
        var top    = c + new Vector2(0f, -r);
        var right   = c + new Vector2(r, 0f);
        var bottom = c + new Vector2(0f, r);
        var left   = c + new Vector2(-r, 0f);
        if (filled) dl.AddQuadFilled(top, right, bottom, left, col);
        else        dl.AddQuad(top, right, bottom, left, col, thickness);
    }

    // ── Party-list row highlight ─────────────────────────────────────────────

    private void DrawPartyListHighlight(ImDrawListPtr dl, IGameObject focus, uint col, bool emissive, float pulse)
    {
        var rowIndex = FindHudRowIndex(focus.EntityId);
        if (rowIndex < 0) return;

        var addonPtr = Plugin.GameGui.GetAddonByName("_PartyList", 1);
        if (addonPtr.Address == IntPtr.Zero) return;
        var addon = (AddonPartyList*)addonPtr.Address;
        if (!addon->AtkUnitBase.IsVisible) return;

        var node = RowCollisionNode(addon, rowIndex);
        if (node == null) return;

        var scale = addon->AtkUnitBase.Scale;
        var min = new Vector2(node->ScreenX, node->ScreenY);
        var max = min + new Vector2(node->Width * scale, node->Height * scale);

        // The collision node is a bit larger than the visible row (hit-area
        // padding) and runs to the panel edge, so the raw rect blooms past the
        // row and clips the panel. Inset it so the border hugs the row content.
        var insetX = 4f * scale;
        var insetY = 3f * scale;
        min += new Vector2(insetX, insetY);
        max -= new Vector2(insetX, insetY);
        if (max.X <= min.X || max.Y <= min.Y) return;

        DrawEmissiveRect(dl, min, max, col, emissive, pulse);
    }

    /// <summary>Match the focus's EntityId to its displayed party row via
    /// AgentHUD; returns the row Index or -1 if the focus isn't a displayed
    /// party member.</summary>
    private static int FindHudRowIndex(uint focusEntityId)
    {
        if (focusEntityId == 0 || focusEntityId == 0xE0000000) return -1;

        var hud = AgentHUD.Instance();
        if (hud == null) return -1;

        var count = hud->PartyMemberCount;
        var members = hud->PartyMembers;
        for (var i = 0; i < count && i < members.Length; i++)
        {
            if (members[i].EntityId == focusEntityId)
                return members[i].Index;
        }
        return -1;
    }

    /// <summary>The collision node (full row hit area) for display row
    /// <paramref name="index"/>. Tries the player-party array first, then the
    /// trust / duty-support array, returning whichever has a visible node so
    /// the highlight works in both regular parties and Trust duties.</summary>
    private static AtkResNode* RowCollisionNode(AddonPartyList* addon, int index)
    {
        if (index < 0 || index >= 8) return null;

        var party = (AtkResNode*)addon->PartyMembers[index].Collision;
        if (IsVisible(party)) return party;

        var trust = (AtkResNode*)addon->TrustMembers[index].Collision;
        if (IsVisible(trust)) return trust;

        return null;
    }

    private static bool IsVisible(AtkResNode* node)
        => node != null && (node->NodeFlags & NodeFlags.Visible) != 0;

    // ── Emissive rounded-rect (party-row border) ─────────────────────────────
    //
    // Mirrors the interact indicator's emissive stack (wide low-alpha glow
    // passes, dark contour, bright core, hot inner) but as a rounded rect
    // framing the row, plus a bright left-edge bar so the focused row reads
    // instantly even in a dense party list.

    private static void DrawEmissiveRect(ImDrawListPtr dl, Vector2 min, Vector2 max, uint col, bool emissive, float pulse)
    {
        const float rounding = 4f;
        const ImDrawFlags flags = ImDrawFlags.RoundCornersAll;

        // Glow grows INWARD-biased (small outward expansion) so it never blooms
        // past the row / panel edge like the first cut did.
        if (emissive)
        {
            dl.AddRect(min - new Vector2(1.5f), max + new Vector2(1.5f), InteractIndicator.WithAlpha(col, 0.16f * pulse), rounding + 1.5f, flags, 4f);
            dl.AddRect(min - new Vector2(0.5f), max + new Vector2(0.5f), InteractIndicator.WithAlpha(col, 0.28f * pulse), rounding + 0.5f, flags, 2.5f);
        }

        dl.AddRect(min, max, 0xB0000000u, rounding, flags, 2.0f);                       // dark contour
        dl.AddRect(min, max, InteractIndicator.Brighten(col, 0.45f), rounding, flags, 1.6f); // bright core
        dl.AddRect(min, max, InteractIndicator.Brighten(col, 0.85f), rounding, flags, 1.0f); // hot inner

        // Bright left-edge bar — the at-a-glance "this row" cue (kept inside the
        // inset rect so it doesn't add to the width).
        var barCol = InteractIndicator.Brighten(col, 0.55f);
        dl.AddRectFilled(new Vector2(min.X, min.Y), new Vector2(min.X + 2.5f, max.Y), barCol, 1.5f);
    }
}
