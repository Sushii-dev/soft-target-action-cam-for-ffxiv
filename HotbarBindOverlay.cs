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
    private static readonly string[] ActionBarAddons =
    {
        "_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04",
        "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09",
        "_ActionCross", "_ActionDoubleCrossL", "_ActionDoubleCrossR", "_ActionBarEx",
    };

    // Minimal mirror of the per-slot UI struct (FFXIVClientStructs ActionBarSlot,
    // layout confirmed against SimpleTweaks): only the icon component node is
    // needed, at 0xB8. The StdVector strides by the real ActionBarSlot size, so
    // casting the returned pointer to this layout is safe.
    [StructLayout(LayoutKind.Explicit, Size = 0xC8)]
    private struct SlotLayout
    {
        [FieldOffset(0xB8)] public AtkComponentNode* Icon;
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
        var shadowCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.9f));

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

                DrawLabels(dl, node, binds, textCol, shadowCol);
            }
        }
    }

    // Top-left corner, no background box — just shadowed text, sized to track the
    // slot's HUD scale. The modifier glyph (↑ / A / C) is drawn smaller than the
    // button text, mirroring the game's native keybind-hint look. Multiple binds
    // on one slot stack downward.
    private static void DrawLabels(ImDrawListPtr dl, AtkResNode* node,
        List<(string Mod, string Btn)> binds, uint textCol, uint shadowCol)
    {
        float scale = 1f;
        for (var n = node; n != null; n = n->ParentNode)
            scale *= n->ScaleX;

        var font     = ImGui.GetFont();
        var baseSize = ImGui.GetFontSize();
        // Track HUD scale but stay legible; clamp so tiny bars don't vanish.
        var btnSize  = MathClamp(baseSize * scale * 0.85f, 10f, baseSize);
        var modSize  = btnSize * 0.7f;

        var pos    = new Vector2(node->ScreenX, node->ScreenY) + new Vector2(scale, scale);
        var lineH  = btnSize + 1f;

        foreach (var (mod, btn) in binds)
        {
            var x = pos.X;
            if (mod.Length > 0)
            {
                // Smaller, top-aligned (slightly raised) modifier prefix.
                Text(dl, font, modSize, new Vector2(x, pos.Y), mod, textCol, shadowCol);
                x += ImGui.CalcTextSize(mod).X * (modSize / baseSize) + 1f;
            }
            Text(dl, font, btnSize, new Vector2(x, pos.Y), btn, textCol, shadowCol);
            pos.Y += lineH;
        }
    }

    private static void Text(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos,
        string s, uint col, uint shadow)
    {
        dl.AddText(font, size, pos + new Vector2(1f, 1f), shadow, s);
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
