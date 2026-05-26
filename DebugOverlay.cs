using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ActionCamera;

/// <summary>
/// Toggleable ImGui overlay that exposes the live state of every cursor-
/// related byte the plugin can read. Built to confirm or reject the
/// hypothesis that <c>Cursor.IsCursorVisible</c> (render gate) flips while
/// <c>AtkCursor.IsVisible</c> (policy flag) stays at 0 — the conjecture
/// the v0.5.18.0 cursor-flicker work hangs on.
///
/// Render with <see cref="Draw"/> from <c>UiBuilder.Draw</c>. The overlay
/// is a no-op when <see cref="Enabled"/> is false, so it's safe to keep
/// the call site in the draw loop permanently.
/// </summary>
internal sealed unsafe class DebugOverlay
{
    private readonly CursorUpdateHook? cursorUpdateHook;
    private readonly Func<bool> isUserWantsActive;
    private readonly Func<bool> isCamActive;
    private readonly Func<bool> isMenuOpen;
    private readonly Func<bool> isRmbHeld;

    // Edge counters for the two visibility bytes — bumped whenever we
    // observe a transition between frames. Useful for "during scroll,
    // how many times did IsCursorVisible flip?" answers.
    private bool prevAtkVisible;
    private bool prevSwVisible;
    private long atkTransitions;
    private long swTransitions;

    // Last-flip wall-clock timestamps for both bytes. Useful for "did the
    // render-state byte just flip 12ms ago — i.e. mid-frame?" diagnostics.
    private DateTime lastAtkFlip = DateTime.MinValue;
    private DateTime lastSwFlip  = DateTime.MinValue;

    public bool Enabled { get; set; }

    public DebugOverlay(
        CursorUpdateHook? cursorUpdateHook,
        Func<bool> isUserWantsActive,
        Func<bool> isCamActive,
        Func<bool> isMenuOpen,
        Func<bool> isRmbHeld)
    {
        this.cursorUpdateHook = cursorUpdateHook;
        this.isUserWantsActive = isUserWantsActive;
        this.isCamActive = isCamActive;
        this.isMenuOpen = isMenuOpen;
        this.isRmbHeld = isRmbHeld;
    }

    public void Draw()
    {
        TickTransitionCounters();

        if (!Enabled) return;

        ImGui.SetNextWindowSize(new Vector2(360, 0), ImGuiCond.Once);
        if (!ImGui.Begin("Veiled Aim — cursor debug",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        ImGui.TextDisabled("Two-byte cursor state. The renderer reads the");
        ImGui.TextDisabled("software-cursor byte; the AtkCursor byte is policy.");

        ImGui.Separator();

        var atkVisible = ReadAtkCursorVisible();
        var swVisible  = ReadSoftwareCursorVisible();

        DrawDot("AtkCursor.IsVisible    (policy):", atkVisible, atkTransitions, lastAtkFlip);
        DrawDot("Cursor.IsCursorVisible (render):", swVisible,  swTransitions,  lastSwFlip);

        ImGui.Separator();
        ImGui.Text($"userWantsActive:  {isUserWantsActive()}");
        ImGui.Text($"cam active:       {isCamActive()}");
        ImGui.Text($"RMB held:         {isRmbHeld()}");
        ImGui.Text($"menu open:        {isMenuOpen()}");

        ImGui.Separator();
        if (cursorUpdateHook != null)
        {
            ImGui.Text($"UpdateCursor hook: {(cursorUpdateHook.IsInstalled ? "installed" : "FAILED")}");
            ImGui.Text($"  suppressed:  {cursorUpdateHook.SuppressedCount}");
            ImGui.Text($"  passthrough: {cursorUpdateHook.PassThroughCount}");
        }
        else
        {
            ImGui.Text("UpdateCursor hook: (null)");
        }

        ImGui.Separator();
        ImGui.TextDisabled("If renderer-byte transitions are happening while the");
        ImGui.TextDisabled("policy byte stays 0, the renderer is the culprit.");
        ImGui.TextDisabled("If both bytes stay 0 but you still see cursor — it's");
        ImGui.TextDisabled("an OS / Win32 cursor not the in-game sprite.");

        ImGui.End();
    }

    private void TickTransitionCounters()
    {
        var atkVisible = ReadAtkCursorVisible();
        var swVisible  = ReadSoftwareCursorVisible();

        if (atkVisible != prevAtkVisible)
        {
            atkTransitions++;
            lastAtkFlip = DateTime.UtcNow;
            prevAtkVisible = atkVisible;
        }
        if (swVisible != prevSwVisible)
        {
            swTransitions++;
            lastSwFlip = DateTime.UtcNow;
            prevSwVisible = swVisible;
        }
    }

    private static bool ReadAtkCursorVisible()
    {
        var stage = AtkStage.Instance();
        if (stage == null) return false;
        return stage->AtkCursor.IsVisible;
    }

    private static bool ReadSoftwareCursorVisible()
    {
        // Cursor.Instance() may return null if FFXIVClientStructs' singleton
        // sig misses on this game build. Treat null as "unknown / false".
        var cursor = Cursor.Instance();
        if (cursor == null) return false;
        return cursor->IsCursorVisible;
    }

    private static void DrawDot(string label, bool value, long transitions, DateTime lastFlip)
    {
        var color = value ? new Vector4(0.96f, 0.32f, 0.32f, 1f)
                          : new Vector4(0.32f, 0.78f, 0.42f, 1f);
        ImGui.TextColored(color, value ? "●" : "○");
        ImGui.SameLine();
        ImGui.Text($"{label} {(value ? "true " : "false")}  flips:{transitions}");
        if (lastFlip != DateTime.MinValue)
        {
            var since = DateTime.UtcNow - lastFlip;
            ImGui.SameLine();
            ImGui.TextDisabled($"  Δ {since.TotalMilliseconds:F0} ms");
        }
    }
}
