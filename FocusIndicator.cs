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
            InteractIndicator.DrawMarker(dl, focus, config.FocusIndicatorStyle, col, emissive, pulse);

        if (config.FocusIndicatorPartyList)
            DrawPartyListHighlight(dl, focus, col, emissive, pulse);
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
        const float rounding = 5f;
        const ImDrawFlags flags = ImDrawFlags.RoundCornersAll;

        if (emissive)
        {
            dl.AddRect(min - new Vector2(4f), max + new Vector2(4f), InteractIndicator.WithAlpha(col, 0.10f * pulse), rounding + 4f, flags, 7f);
            dl.AddRect(min - new Vector2(2.5f), max + new Vector2(2.5f), InteractIndicator.WithAlpha(col, 0.18f * pulse), rounding + 2.5f, flags, 5f);
            dl.AddRect(min - new Vector2(1f), max + new Vector2(1f), InteractIndicator.WithAlpha(col, 0.30f * pulse), rounding + 1f, flags, 3f);
        }

        dl.AddRect(min, max, 0xB0000000u, rounding, flags, 2.4f);                       // dark contour
        dl.AddRect(min, max, InteractIndicator.Brighten(col, 0.45f), rounding, flags, 2.0f); // bright core
        dl.AddRect(min, max, InteractIndicator.Brighten(col, 0.85f), rounding, flags, 1.0f); // hot inner

        // Bright left-edge bar — the at-a-glance "this row" cue.
        var barCol = InteractIndicator.Brighten(col, 0.55f);
        dl.AddRectFilled(new Vector2(min.X - 3f, min.Y), new Vector2(min.X, max.Y), barCol, 1.5f);
    }
}
