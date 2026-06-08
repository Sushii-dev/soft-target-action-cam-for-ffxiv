using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

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
    private readonly MouseOverSuppressor? mouseOverSuppressor;
    private readonly SoftTargetSuppressor? softTargetSuppressor;
    private readonly InputStatusSuppressor? inputStatusSuppressor;
    private readonly SoundSuppressor? soundSuppressor;
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
        MouseOverSuppressor? mouseOverSuppressor,
        SoftTargetSuppressor? softTargetSuppressor,
        InputStatusSuppressor? inputStatusSuppressor,
        SoundSuppressor? soundSuppressor,
        Func<bool> isUserWantsActive,
        Func<bool> isCamActive,
        Func<bool> isMenuOpen,
        Func<bool> isRmbHeld)
    {
        this.cursorUpdateHook = cursorUpdateHook;
        this.mouseOverSuppressor = mouseOverSuppressor;
        this.softTargetSuppressor = softTargetSuppressor;
        this.inputStatusSuppressor = inputStatusSuppressor;
        this.soundSuppressor = soundSuppressor;
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
        DrawNativeMouseDelta();

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
        if (mouseOverSuppressor != null)
        {
            ImGui.Text("MouseOverSuppressor (v0.6.6+):");
            ImGui.Text($"  total calls:   {mouseOverSuppressor.CallCount}");
            ImGui.Text($"  suppressed:    {mouseOverSuppressor.SuppressedCount}");
            ImGui.Text($"  pass-through:  {mouseOverSuppressor.PassThroughCount}");
            ImGui.TextDisabled("  If 'total calls' is 0 while clicking, the hook");
            ImGui.TextDisabled("  isn't on the click-path function.");
        }
        else
        {
            ImGui.Text("MouseOverSuppressor: (null)");
        }

        ImGui.Separator();
        if (softTargetSuppressor != null)
        {
            ImGui.Text("SoftTargetSuppressor (v0.6.9+):");
            ImGui.Text($"  total calls:   {softTargetSuppressor.CallCount}");
            ImGui.Text($"  suppressed:    {softTargetSuppressor.SuppressedCount}");
            ImGui.Text($"  pass-through:  {softTargetSuppressor.PassThroughCount}");
            ImGui.TextDisabled("  Spikes on click = game-side click handler is");
            ImGui.TextDisabled("  calling SetSoftTarget directly. Should suppress.");
        }
        else
        {
            ImGui.Text("SoftTargetSuppressor: (null)");
        }

        ImGui.Separator();
        if (inputStatusSuppressor != null)
        {
            ImGui.Text("InputStatusSuppressor (v0.6.10+):");
            ImGui.Text($"  total calls:   {inputStatusSuppressor.CallCount}");
            ImGui.Text($"  suppressed:    {inputStatusSuppressor.SuppressedCount}");
            ImGui.Text($"  pass-through:  {inputStatusSuppressor.PassThroughCount}");
            ImGui.TextDisabled("  Suppresses LMB/RMB read for game's targeting");
            ImGui.TextDisabled("  layer. Should fire heavily during clicks.");
        }
        else
        {
            ImGui.Text("InputStatusSuppressor: (null)");
        }

        ImGui.Separator();
        if (soundSuppressor != null)
        {
            ImGui.Text("SoundSuppressor (v0.6.18+):");
            ImGui.Text($"  total calls:   {soundSuppressor.CallCount}");
            ImGui.Text($"  last id:       {soundSuppressor.LastId}");
            ImGui.Text($"  last id (cam): {soundSuppressor.LastIdInCam}");
            ImGui.Text($"  suppressed:    {soundSuppressor.SuppressedCount}");
            ImGui.TextDisabled("  'total calls' rising on any UI sound = hook alive.");
            ImGui.TextDisabled("  Switch soft target in cam, read 'last id (cam)'.");
        }
        else
        {
            ImGui.Text("SoundSuppressor: (null)");
        }

        ImGui.Separator();
        ImGui.TextDisabled("If renderer-byte transitions are happening while the");
        ImGui.TextDisabled("policy byte stays 0, the renderer is the culprit.");
        ImGui.TextDisabled("If both bytes stay 0 but you still see cursor — it's");
        ImGui.TextDisabled("an OS / Win32 cursor not the in-game sprite.");

        ImGui.End();
    }

    // ── Native mouse-delta probe (v0.6.71) ──────────────────────────────────
    //
    // The candidate replacement for the cursor-warp mouselook: the game's own
    // per-frame relative delta at Framework->CursorInputs.DeltaX/DeltaY. The
    // open question is whether it's CLEAN under XWayland/Niri — stays 0 when
    // the hand is still, doesn't stick nonzero on button press. consecNonZero
    // counts back-to-back nonzero frames: if it keeps climbing while you hold
    // the mouse still (especially with a button held), the signal is dirty and
    // the warp-free rewrite is off the table for this stack.
    private int consecNonZero;
    private int peakConsecNonZero;

    private void DrawNativeMouseDelta()
    {
        var fw = GameFramework.Instance();
        if (fw == null) { ImGui.Text("Framework: (null)"); return; }

        ref var ci = ref fw->CursorInputs;
        var dx = ci.DeltaX;
        var dy = ci.DeltaY;

        if (dx != 0 || dy != 0) { consecNonZero++; if (consecNonZero > peakConsecNonZero) peakConsecNonZero = consecNonZero; }
        else consecNonZero = 0;

        ImGui.Text("Native CursorInputs (warp-free candidate):");
        ImGui.Text($"  DeltaX / DeltaY:   {dx} / {dy}");
        ImGui.Text($"  PositionX / Y:     {ci.PositionX} / {ci.PositionY}");
        ImGui.Text($"  MouseWheel:        {ci.MouseWheel}");
        ImGui.Text($"  HeldFlags:         0x{(uint)ci.MouseButtonHeldFlags:X}");
        ImGui.Text($"  GameWindowFocused: {ci.IsGameWindowFocused}");
        ImGui.Text($"  consec nonzero:    {consecNonZero}   (peak {peakConsecNonZero})");
        ImGui.TextDisabled("  Hold still -> delta should read 0/0 and consec reset.");
        ImGui.TextDisabled("  Press/hold a button, stay still: consec must NOT climb.");
        if (ImGui.Button("reset peak")) peakConsecNonZero = 0;
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
