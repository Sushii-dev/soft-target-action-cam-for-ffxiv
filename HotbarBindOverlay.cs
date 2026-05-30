using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ActionCamera;

/// <summary>
/// Draws a small label ("M1", "S+M2", …) in the corner of every hotbar slot
/// that a mouse bind targets — the same idea as the game's native keybind
/// hints, so the player can see at a glance what each bound slot fires on.
///
/// Pure ImGui foreground overlay: it READS the live action-bar addons for
/// slot screen positions and never mutates a native node, so it can't fight
/// the game's own per-frame text re-assertion (the reason we don't reuse the
/// native ControlHintTextNode the way SimpleTweaks' mirroring tweak does).
///
/// Resolution is addon-driven rather than hotbarId→addon hardcoded: every
/// action-bar addon reports the RaptureHotbarId it currently hosts, so cross-
/// hotbar set switching / WXHB / pet bar all resolve correctly without a fixed
/// mapping. Slots past <c>SlotCount</c> (12–15 on standard bars) are never
/// touched because the loop is bounded by the addon's own SlotCount.
/// </summary>
public sealed unsafe class HotbarBindOverlay
{
    private readonly Configuration config;

    // Every addon that can host a RaptureHotbar. The live RaptureHotbarId is
    // read off each one, so this is just the set of places to look — not an
    // index map.
    // Standard hotbars + pet/extra bar only. The cross hotbars are deliberately
    // excluded: this mod isn't used with a controller, and a cross hotbar set to
    // SHARE a standard bar reports that bar's RaptureHotbarId, which made the
    // labels bleed onto the (inactive) cross hotbar.
    private static readonly string[] ActionBarAddons =
    {
        "_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04",
        "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09",
        "_ActionBarEx",
    };

    // Minimal mirror of the per-slot UI struct (FFXIVClientStructs ActionBarSlot,
    // layout confirmed against SimpleTweaks): only the icon component node is
    // needed, at 0xB8. The StdVector strides by the real ActionBarSlot size, so
    // casting the returned pointer to this layout is safe.
    [StructLayout(LayoutKind.Explicit, Size = 0xC8)]
    private struct SlotLayout
    {
        [FieldOffset(0xB8)] public AtkComponentNode* Icon;
        // The native keybind-hint text node — we anchor our label to its
        // ScreenX/Y so it shares the game's exact hint position. Typed as the
        // base node since we only read transform fields.
        [FieldOffset(0xC0)] public AtkResNode* ControlHintTextNode;
    }

    public HotbarBindOverlay(Configuration config) => this.config = config;

    public void Draw()
    {
        if (!config.BetaMouseBindsEnabled) return;
        if (!config.ShowMouseBindHints) return;
        if (config.MouseBinds == null || config.MouseBinds.Count == 0) return;

        // Don't draw over the HUD layout editor — addons sit on the edit canvas
        // and their coords reflect that, not live play.
        if (IsAddonVisible("_HudLayout")) return;

        var labels = BuildLabelMap();
        if (labels.Count == 0) return;

        var dl        = ImGui.GetForegroundDrawList();
        var textCol   = ImGui.ColorConvertFloat4ToU32(config.MouseBindHintColor);
        var outlineCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 1f));

        foreach (var name in ActionBarAddons)
        {
            var wrapper = Plugin.GameGui.GetAddonByName(name, 1);
            if (wrapper.Address == 0) continue;

            var addon = (AddonActionBarBase*)wrapper.Address;
            // AtkUnitBase sits at offset 0 of every addon struct.
            if (!((AtkUnitBase*)addon)->IsVisible) continue;

            uint barId    = addon->RaptureHotbarId;
            int  slotCount = addon->SlotCount;

            for (int s = 0; s < slotCount; s++)
            {
                if (!labels.TryGetValue((barId, (uint)s), out var binds)) continue;

                // StdVector<ActionBarSlot>.First strides by the real
                // ActionBarSlot size; cast the element to our minimal layout.
                var first = addon->ActionBarSlotVector.First;
                if (first == null) continue;
                var slot = (SlotLayout*)(first + s);

                var node = (AtkResNode*)slot->Icon;
                if (node == null) continue;
                if ((node->NodeFlags & NodeFlags.Visible) == 0) continue;

                DrawLabels(dl, node, slot->ControlHintTextNode, binds, textCol, outlineCol);
            }
        }
    }

    // Flush to the slot's top-left edge (matching the native keybind hint), no
    // background box — outlined text sized to track HUD scale. The modifier
    // glyph (↑ / A / C) is drawn smaller than the button text, native-hint style.
    // Multiple binds on one slot stack downward.
    private static void DrawLabels(ImDrawListPtr dl, AtkResNode* node, AtkResNode* hintNode,
        List<(string Mod, string Btn)> binds, uint textCol, uint outlineCol)
    {
        float scale = 1f;
        for (var n = node; n != null; n = n->ParentNode)
            scale *= n->ScaleX;

        var font     = ImGui.GetFont();
        var baseSize = ImGui.GetFontSize();
        // A touch smaller than native so the wider labels (↑LMB, ALMB) fit the
        // slot; clamp so small bars stay legible.
        var btnSize  = MathClamp(baseSize * scale * 0.8f, 9f, baseSize);
        var modSize  = btnSize * 0.72f;
        var modGap   = 1f;

        // Anchor to the native keybind-hint text node when present (exact same
        // vertical + left position the game uses for "3 / Q / E"); fall back to
        // the slot icon's top-left edge if the hint node is missing.
        var slotLeft  = node->ScreenX;
        var slotRight = node->ScreenX + node->Width * scale;
        var anchorX   = hintNode != null ? hintNode->ScreenX : slotLeft;
        var top       = hintNode != null ? hintNode->ScreenY : node->ScreenY;
        var lineH     = btnSize + 1f;

        var y = top;
        foreach (var (mod, btn) in binds)
        {
            var modW = mod.Length > 0
                ? ImGui.CalcTextSize(mod).X * (modSize / baseSize) + modGap
                : 0f;
            var btnW   = ImGui.CalcTextSize(btn).X * (btnSize / baseSize);
            var lineW  = modW + btnW;

            // Left-align to the native hint position, but if the line is wider
            // than the slot push it LEFT so its right edge sits at the slot edge
            // — leaking left is fine, leaking right is not.
            var x = anchorX;
            if (x + lineW > slotRight)
                x = slotRight - lineW;

            if (mod.Length > 0)
            {
                Text(dl, font, modSize, new Vector2(x, y), mod, textCol, outlineCol);
                x += modW;
            }
            Text(dl, font, btnSize, new Vector2(x, y), btn, textCol, outlineCol);
            y += lineH;
        }
    }

    // 8-direction black outline + solid fill — legible over bright ability icons.
    private static void Text(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos,
        string s, uint col, uint outline)
    {
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    dl.AddText(font, size, pos + new Vector2(dx, dy), outline, s);
        dl.AddText(font, size, pos, col, s);
    }

    private static float MathClamp(float v, float lo, float hi)
        => v < lo ? lo : v > hi ? hi : v;

    private Dictionary<(uint Bar, uint Slot), List<(string Mod, string Btn)>> BuildLabelMap()
    {
        var map = new Dictionary<(uint, uint), List<(string, string)>>();
        foreach (var b in config.MouseBinds)
        {
            if (b == null || b.Button == VirtualKey.NO_KEY) continue;

            var key = (b.HotbarId, b.SlotId);
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<(string, string)>();
            list.Add((FormatMod(b.Modifier), FormatButton(b.Button)));
        }
        return map;
    }

    private static string FormatButton(VirtualKey button) => button switch
    {
        VirtualKey.LBUTTON  => "LMB",
        VirtualKey.RBUTTON  => "RMB",
        VirtualKey.MBUTTON  => "MMB",
        VirtualKey.XBUTTON1 => "MB4",
        VirtualKey.XBUTTON2 => "MB5",
        _                   => button.ToString(),
    };

    // Native keybind-hint convention: up-arrow for Shift, capital for Ctrl / Alt.
    private static string FormatMod(MouseBindModifier mod) => mod switch
    {
        MouseBindModifier.Shift => "↑",
        MouseBindModifier.Ctrl  => "C",
        MouseBindModifier.Alt   => "A",
        _                       => string.Empty,
    };

    private static bool IsAddonVisible(string name)
    {
        var addon = Plugin.GameGui.GetAddonByName(name, 1);
        return addon.Address != 0 && ((AtkUnitBase*)addon.Address)->IsVisible;
    }
}
