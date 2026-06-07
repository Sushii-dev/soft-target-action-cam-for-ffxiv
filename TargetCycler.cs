using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace ActionCamera;

/// <summary>
/// Modifier + scroll-wheel HARD-target cycling.
///
/// While the camera is active, the cursor is hidden, and the configured
/// modifier (default Shift) is held, the mouse wheel no longer zooms — it walks
/// the hard target through a stable ring of every attackable object in range.
/// The soft-target cone is untouched: cycling only moves the hard target.
///
/// Reading the wheel: the scroll signal is the game-side
/// <c>Framework.Instance()->CursorInputs.MouseWheel</c> (int, −1/0/+1, set per
/// frame by the input poll, which runs before our framework-tick hook). ImGui's
/// MouseWheel is 0 here because Dalamud only feeds the wheel to ImGui when it
/// wants the mouse, and the cursor is hidden over the world. We also zero the
/// field after reading so the game's own camera-zoom never sees the notch — no
/// zoom, no twitch — and keep a Distance pin as belt-and-suspenders. Distance
/// itself is a live field (collision/auto-zoom) so it is never the signal.
///
/// Ring design (the robustness contract):
///  - ONE ordered list walked by an index. Wheel-up = +1, wheel-down = -1, both
///    wrapping. Scrolling one way eventually visits every target; scrolling
///    back returns to the exact previous selection.
///  - Order is a greedy nearest-neighbour chain seeded from the current target,
///    so a boss's parts/weakpoints (which sit on the boss) cluster adjacent and
///    cycle first, before distant adds.
///  - Keyed by GameObjectId; updated IN PLACE on membership change (drop dead,
///    insert new next to nearest neighbour) so order + reversibility survive
///    spawns/deaths and the selection is never yanked.
///
/// First scroll with no hard target locks onto the current soft/cone pick;
/// further scrolls cycle from there.
/// </summary>
public sealed unsafe class TargetCycler
{

    private readonly Configuration config;
    private readonly Func<bool> isCameraActive;
    private readonly Func<bool> isCursorVisible;
    private readonly Func<bool> isMenuOpen;
    private readonly Action<IGameObject> setHardTarget;

    private readonly List<ulong> ring = new();
    private ulong selectedId;

    private bool zoomLocked;
    private float lockedDistance;

    public TargetCycler(
        Configuration config,
        Func<bool> isCameraActive,
        Func<bool> isCursorVisible,
        Func<bool> isMenuOpen,
        Action<IGameObject> setHardTarget)
    {
        this.config = config;
        this.isCameraActive = isCameraActive;
        this.isCursorVisible = isCursorVisible;
        this.isMenuOpen = isMenuOpen;
        this.setHardTarget = setHardTarget;
    }

    /// <summary>
    /// Whole cycle pass — called from <c>Plugin.OnFrameworkUpdate</c>. Reads the
    /// game-side wheel, freezes + swallows the zoom, and steps the hard target.
    /// All in the framework tick: the wheel field is live there, and camera
    /// writes only stick from the framework tick (a DrawUI write lands after the
    /// game built the frame's camera and is discarded).
    /// </summary>
    public void Update()
    {
        if (!config.CycleEnabled || !IsEngaged()) { zoomLocked = false; return; }

        var fw = GameFramework.Instance();

        // Read the notch BEFORE swallowing it. MouseWheel is −1/0/+1 per frame.
        var wheel = fw != null ? fw->CursorInputs.MouseWheel : 0;

        // Freeze zoom (pin) AND swallow the wheel so the game's camera-zoom
        // never applies the notch — kills the zoom twitch at the source.
        FreezeZoom();
        if (fw != null) fw->CursorInputs.MouseWheel = 0;

        if (wheel == 0) return;

        var dir = wheel > 0 ? 1 : -1; // +1 = scroll up = next target
        if (config.CycleInvertScroll) dir = -dir;
        Cycle(dir);
    }

    private bool IsEngaged()
        // IsDownRaw is focus-gated, so an out-of-focus modifier never engages.
        => isCameraActive()
           && !isCursorVisible()
           && !isMenuOpen()
           && InputBinding.IsDownRaw(config.CycleModifierKey);

    /// <summary>Pin the in-game camera's zoom to the value captured on the
    /// first engaged frame. Uses the Game.Control camera (Distance @0x124,
    /// typed here) — NOT the Graphics.Scene render camera, whose layout
    /// differs and which earlier writes wrongly targeted.</summary>
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

    private void Cycle(int step)
    {
        var hadHardTarget = Plugin.TargetManager.Target != null;

        EnsureRing();
        if (ring.Count == 0) return;

        var idx = ring.IndexOf(selectedId);
        if (idx < 0) idx = 0;

        // With no hard target yet, the first scroll just locks onto the seed
        // (the soft/cone pick) without stepping; subsequent scrolls cycle.
        if (hadHardTarget)
        {
            var n = ring.Count;
            idx = ((idx + step) % n + n) % n; // positive modulo, either direction
        }

        selectedId = ring[idx];

        var obj = Plugin.ObjectTable.SearchById(selectedId);
        if (obj != null && obj.IsValid())
            setHardTarget(obj);
    }

    /// <summary>
    /// Refresh the ring against the live object table. Builds it on first use;
    /// thereafter updates in place so order + reversibility survive membership
    /// churn. Re-seats the selection if the hard target was changed externally.
    /// </summary>
    private void EnsureRing()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) { ring.Clear(); return; }

        var maxSq = config.CycleMaxDistance * config.CycleMaxDistance;
        var current = new List<(ulong id, Vector3 pos)>();
        foreach (var o in Plugin.ObjectTable)
        {
            if (!TargetSelector.IsAttackable(o, lp)) continue;
            var d = o.Position - lp.Position;
            if (d.LengthSquared() > maxSq) continue;
            current.Add((o.GameObjectId, o.Position));
        }

        var curIds = new HashSet<ulong>(current.Select(c => c.id));
        var posById = current.ToDictionary(c => c.id, c => c.pos);

        // External hard-target change → adopt it as the selection anchor.
        var ht = Plugin.TargetManager.Target?.GameObjectId;
        if (ht != null && ht.Value != selectedId && curIds.Contains(ht.Value))
            selectedId = ht.Value;

        if (ring.Count == 0)
        {
            var seed = SeedId(current, posById, ht, lp.Position);
            BuildChain(ring, current, posById, seed);
            if (!ring.Contains(selectedId))
                selectedId = ring.Count > 0 ? ring[0] : 0ul;
            return;
        }

        if (curIds.Count == ring.Count && curIds.SetEquals(ring))
        {
            if (!ring.Contains(selectedId))
                selectedId = ring.Count > 0 ? ring[0] : 0ul;
            return;
        }

        ring.RemoveAll(id => !curIds.Contains(id));
        foreach (var c in current)
        {
            if (ring.Contains(c.id)) continue;
            InsertNearest(ring, posById, c.id, c.pos);
        }

        if (ring.Count == 0 || !ring.Contains(selectedId))
            selectedId = ring.Count > 0 ? ring[0] : 0ul;
    }

    /// <summary>Seed the chain: hard target, else the soft/cone pick, else the
    /// last selection, else the attackable nearest the player.</summary>
    private ulong SeedId(
        List<(ulong id, Vector3 pos)> current,
        Dictionary<ulong, Vector3> posById,
        ulong? ht,
        Vector3 playerPos)
    {
        if (ht != null && posById.ContainsKey(ht.Value)) return ht.Value;

        var sft = Plugin.TargetManager.SoftTarget?.GameObjectId;
        if (sft != null && posById.ContainsKey(sft.Value)) return sft.Value;

        if (selectedId != 0 && posById.ContainsKey(selectedId)) return selectedId;

        var best = 0ul;
        var bestSq = float.MaxValue;
        foreach (var c in current)
        {
            var dsq = (c.pos - playerPos).LengthSquared();
            if (dsq < bestSq) { bestSq = dsq; best = c.id; }
        }
        return best;
    }

    /// <summary>Greedy nearest-neighbour ordering from <paramref name="seed"/>.</summary>
    private static void BuildChain(
        List<ulong> outRing,
        List<(ulong id, Vector3 pos)> current,
        Dictionary<ulong, Vector3> posById,
        ulong seed)
    {
        outRing.Clear();
        if (current.Count == 0) return;

        var remaining = new HashSet<ulong>(current.Select(c => c.id));
        if (!remaining.Contains(seed)) seed = current[0].id;

        var curId = seed;
        var curPos = posById[curId];
        outRing.Add(curId);
        remaining.Remove(curId);

        while (remaining.Count > 0)
        {
            var bestId = 0ul;
            var bestSq = float.MaxValue;
            foreach (var id in remaining)
            {
                var dsq = (posById[id] - curPos).LengthSquared();
                if (dsq < bestSq) { bestSq = dsq; bestId = id; }
            }
            outRing.Add(bestId);
            curPos = posById[bestId];
            remaining.Remove(bestId);
        }
    }

    private static void InsertNearest(
        List<ulong> ring,
        Dictionary<ulong, Vector3> posById,
        ulong newId,
        Vector3 newPos)
    {
        var bestIdx = -1;
        var bestSq = float.MaxValue;
        for (var i = 0; i < ring.Count; i++)
        {
            if (!posById.TryGetValue(ring[i], out var p)) continue;
            var dsq = (p - newPos).LengthSquared();
            if (dsq < bestSq) { bestSq = dsq; bestIdx = i; }
        }
        if (bestIdx < 0) ring.Add(newId);
        else ring.Insert(bestIdx + 1, newId);
    }
}
