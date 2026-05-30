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
        var chipCol   = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.55f));

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
                if (!labels.TryGetValue((barId, (uint)s), out var label)) continue;

                // StdVector<ActionBarSlot>.First strides by the real
                // ActionBarSlot size; cast the element to our minimal layout.
                var first = addon->ActionBarSlotVector.First;
                if (first == null) continue;
                var slot = (SlotLayout*)(first + s);

                var node = (AtkResNode*)slot->Icon;
                if (node == null) continue;
                if ((node->NodeFlags & NodeFlags.Visible) == 0) continue;

                DrawLabel(dl, node, label, textCol, shadowCol, chipCol);
            }
        }
    }

    // Slot screen position comes straight from the node's resolved ScreenX/Y
    // (already folds in the addon offset + parent transform). Size is the node's
    // unscaled W/H times the accumulated scale up the parent chain.
    private static void DrawLabel(ImDrawListPtr dl, AtkResNode* node, string label,
        uint textCol, uint shadowCol, uint chipCol)
    {
        float scale = 1f;
        for (var n = node; n != null; n = n->ParentNode)
            scale *= n->ScaleX;

        var origin = new Vector2(node->ScreenX, node->ScreenY);

        var ts  = ImGui.CalcTextSize(label);
        var pad = new Vector2(3f, 1f);

        // Top-left corner chip — clear of the centred recast number and the
        // native keybind hint (bottom), so it never overlaps either.
        var chipMin = origin + new Vector2(scale, scale);
        var chipMax = chipMin + ts + pad * 2f;
        dl.AddRectFilled(chipMin, chipMax, chipCol, 3f);

        var tpos = chipMin + pad;
        dl.AddText(tpos + new Vector2(1f, 1f), shadowCol, label);
        dl.AddText(tpos, textCol, label);
    }

    private Dictionary<(uint Bar, uint Slot), string> BuildLabelMap()
    {
        var map = new Dictionary<(uint, uint), string>();
        foreach (var b in config.MouseBinds)
        {
            if (b == null || b.Button == VirtualKey.NO_KEY) continue;

            var key = (b.HotbarId, b.SlotId);
            var lbl = FormatBind(b);
            map[key] = map.TryGetValue(key, out var existing) ? existing + "/" + lbl : lbl;
        }
        return map;
    }

    private static string FormatBind(MouseBind b)
    {
        var btn = b.Button switch
        {
            VirtualKey.LBUTTON  => "LMB",
            VirtualKey.RBUTTON  => "RMB",
            VirtualKey.MBUTTON  => "MMB",
            VirtualKey.XBUTTON1 => "MB4",
            VirtualKey.XBUTTON2 => "MB5",
            _                   => b.Button.ToString(),
        };
        // Match the game's native keybind-hint modifier glyphs: an up-arrow for
        // Shift, a capital letter for Ctrl / Alt.
        var mod = b.Modifier switch
        {
            MouseBindModifier.Shift => "↑", // ↑
            MouseBindModifier.Ctrl  => "C",
            MouseBindModifier.Alt   => "A",
            _                       => string.Empty,
        };
        return mod + btn;
    }

    private static bool IsAddonVisible(string name)
    {
        var addon = Plugin.GameGui.GetAddonByName(name, 1);
        return addon.Address != 0 && ((AtkUnitBase*)addon.Address)->IsVisible;
    }
}
