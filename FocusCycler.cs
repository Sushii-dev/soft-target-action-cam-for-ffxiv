using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace ActionCamera;

/// <summary>
/// Focus-target cycling through the party in party-list slot order
/// (pt1 → pt2 → pt3 → …) for healers. The FOCUS target is the dedicated heal
/// recipient (the ReAction beneficial stack points Focus Target → Self), so
/// cycling it lets a healer pick who to heal without disturbing the enemy
/// hard/soft target.
///
/// Two independent entry points, both routing through <see cref="CycleTo"/>:
///   • A dedicated keybind (<c>FocusCycleKey</c> / <c>FocusCycleReverseKey</c>)
///     — each press steps the focus one slot. Camera-independent.
///   • Hold the interact key + scroll (<c>FocusScrollOnInteractHold</c>) —
///     borrows the combat cycler's zoom-lock + game-side wheel swallow so the
///     wheel cycles the focus instead of zooming while interact is held in the
///     action cam.
///
/// Robustness contract:
///   • Order = live <c>IPartyList</c> slot order (matches the party box).
///   • The party-id list is rebuilt every step and the index re-derived from
///     the LIVE <c>FocusTarget</c> id, so member join/leave/zone churn can never
///     corrupt a stale index. No focus (or a focus that is not a party member)
///     resolves to pt1.
///   • Stepping wraps both directions with a positive modulo, so scrolling one
///     way visits every slot and reverses cleanly to the previous slot.
/// </summary>
public sealed unsafe class FocusCycler
{
    private readonly Configuration config;
    private readonly Func<bool> isCameraActive;
    private readonly Func<bool> isCursorVisible;
    private readonly Func<bool> isMenuOpen;

    private bool zoomLocked;
    private float lockedDistance;

    // Set true when the current interact-hold gesture has cycled the focus at
    // least once. Read by Plugin on the interact-key release frame to suppress
    // the normal interact tap; cleared by <see cref="ConsumeHoldDidCycle"/>.
    private bool holdDidCycle;

    public FocusCycler(
        Configuration config,
        Func<bool> isCameraActive,
        Func<bool> isCursorVisible,
        Func<bool> isMenuOpen)
    {
        this.config = config;
        this.isCameraActive = isCameraActive;
        this.isCursorVisible = isCursorVisible;
        this.isMenuOpen = isMenuOpen;
    }

    /// <summary>Dedicated-keybind path: step the focus by <paramref name="dir"/>
    /// (+1 = next slot, −1 = previous). No focus → pt1.</summary>
    public void Step(int dir) => CycleTo(dir);

    /// <summary>
    /// Interact-hold + scroll pass. Call every frame from the framework tick
    /// while the gesture is active (interact physically held in the action cam,
    /// cursor hidden, no menu). Pins the zoom and swallows the wheel so the
    /// notch cycles the focus instead of zooming. Marks <see cref="holdDidCycle"/>
    /// so the interact tap is suppressed on release.
    /// </summary>
    public void ScrollTick()
    {
        var fw = GameFramework.Instance();
        var wheel = fw != null ? fw->CursorInputs.MouseWheel : 0;

        FreezeZoom();
        if (fw != null) fw->CursorInputs.MouseWheel = 0;

        if (wheel == 0) return;

        var dir = wheel > 0 ? 1 : -1; // +1 = scroll up = next slot
        if (config.FocusCycleInvertScroll) dir = -dir;
        CycleTo(dir);
        holdDidCycle = true;
    }

    /// <summary>True iff the interact-hold gesture has cycled the focus this
    /// hold. Reading it CLEARS the flag and releases the zoom pin — call once
    /// on the interact-key release frame to decide whether to swallow the
    /// tap.</summary>
    public bool ConsumeHoldDidCycle()
    {
        var did = holdDidCycle;
        holdDidCycle = false;
        zoomLocked = false;
        return did;
    }

    /// <summary>Release the zoom pin without consuming the cycle flag. Call
    /// when the gesture is not active so a mid-hold loss of the gate (menu
    /// opened, cursor shown) doesn't leave the zoom stuck.</summary>
    public void ReleaseZoom() => zoomLocked = false;

    // ── Core ─────────────────────────────────────────────────────────────────

    private void CycleTo(int dir)
    {
        var slots = BuildPartyOrder();
        if (slots.Count == 0) return;

        var focus = Plugin.TargetManager.FocusTarget?.GameObjectId;

        int idx;
        if (focus == null)
        {
            idx = 0; // no focus → pt1
        }
        else
        {
            idx = slots.IndexOf(focus.Value);
            if (idx < 0)
            {
                idx = 0; // focus isn't a party member → snap to pt1
            }
            else
            {
                var n = slots.Count;
                idx = ((idx + dir) % n + n) % n; // positive modulo, either way
            }
        }

        var obj = Plugin.ObjectTable.SearchById(slots[idx]);
        if (obj != null && obj.IsValid())
            Plugin.TargetManager.FocusTarget = obj;
    }

    /// <summary>
    /// Party member GameObjectIds in party-list slot order. Members with no
    /// resolvable GameObject (out of zone / different instance) are skipped.
    /// Solo (empty party list) falls back to the local player as the sole
    /// slot so the keybind still self-targets.
    /// </summary>
    private List<ulong> BuildPartyOrder()
    {
        var list = new List<ulong>();
        var party = Plugin.PartyList;

        if (party.Length == 0)
        {
            var self = Plugin.ObjectTable.LocalPlayer;
            if (self != null) list.Add(self.GameObjectId);
            return list;
        }

        var localId = Plugin.ObjectTable.LocalPlayer?.GameObjectId;
        foreach (var m in party)
        {
            var go = m.GameObject;
            if (go == null) continue;
            if (!config.FocusCycleIncludeSelf && localId != null && go.GameObjectId == localId.Value)
                continue;
            list.Add(go.GameObjectId);
        }
        return list;
    }

    /// <summary>Pin the gameplay camera's zoom to the value captured on the
    /// first gesture frame — same mechanism as the combat cycler. Uses the
    /// Game.Control camera (Distance @0x124, typed).</summary>
    private void FreezeZoom()
    {
        var cm = CameraManager.Instance();
        if (cm == null) return;
        var cam = cm->Camera;
        if (cam == null) return;

        if (!zoomLocked)
        {
            lockedDistance = cam->Distance;
            zoomLocked = true;
        }

        cam->Distance = lockedDistance;
        cam->InterpDistance = lockedDistance;
    }
}
