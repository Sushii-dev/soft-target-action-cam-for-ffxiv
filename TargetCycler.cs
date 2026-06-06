using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace ActionCamera;

/// <summary>
/// Modifier + scroll-wheel HARD-target cycling.
///
/// While the camera is active, the cursor is hidden, and the configured
/// modifier (default Shift) is held, the mouse wheel no longer zooms — it walks
/// the hard target through a stable ring of every attackable object in range.
/// The soft-target cone is untouched: this only moves the hard target the user
/// has already committed (or, if none, seeds from the nearest attackable).
///
/// Ring design (the robustness contract):
///  - ONE ordered list walked by an index. Wheel-up = +1, wheel-down = -1, both
///    wrapping. Because it is literally ±1 on a fixed list, scrolling one way
///    eventually visits every target and scrolling back returns to the exact
///    previous selection.
///  - Order is a greedy nearest-neighbour chain seeded from the current target,
///    so a boss's parts/weakpoints (which sit physically on the boss) land
///    adjacent and cycle first, before distant adds. No knowledge of entity
///    parentage is needed — proximity does the clustering.
///  - The ring is keyed by GameObjectId. When the attackable set changes
///    (an add spawns, a part dies) the ring is updated IN PLACE — dead ids are
///    dropped, new ids inserted next to their nearest existing neighbour — so
///    the established order and reversibility survive across spawns/deaths and
///    the user is never yanked off their selection.
///
/// Zoom is locked hook-free: on the first engaged frame the active camera's
/// Distance is snapshotted and re-pinned every engaged frame, so the wheel that
/// the game also reads for zoom produces no visible change. Releasing the
/// modifier unlocks instantly.
/// </summary>
public sealed unsafe class TargetCycler
{
    // GameCamera field offsets (same struct CameraController writes via
    // CameraManager.Instance()->CurrentCamera). Stable across recent patches.
    private const int OffDistance = 0x124;       // float, current zoom distance
    private const int OffInterpDistance = 0x18C; // float, smoothing target

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
    /// Called once per render frame from <c>Plugin.DrawUI</c> — the only place
    /// ImGui's per-frame wheel delta is valid to read. Handles the engage gate,
    /// zoom lock, and the wheel-driven cycle step.
    /// </summary>
    public void Update()
    {
        if (!config.CycleEnabled) { ReleaseZoomLock(); return; }

        var gateOk = isCameraActive()
                     && !isCursorVisible()
                     && !isMenuOpen();
        // IsDownRaw is focus-gated, so an out-of-focus Shift never engages.
        var modDown = gateOk && InputBinding.IsDownRaw(config.CycleModifierKey);
        var hardTarget = Plugin.TargetManager.Target;
        var engaged = modDown && hardTarget != null;

        if (!engaged)
        {
            ReleaseZoomLock();
            return;
        }

        // Lock zoom for as long as the modifier is held with a hard target,
        // so the wheel is free to mean "cycle" instead of "zoom".
        EnsureZoomLock();
        PinZoom();

        var wheel = ImGui.GetIO().MouseWheel;
        if (wheel == 0f) return;

        var magnitude = Math.Max(1, (int)MathF.Round(MathF.Abs(wheel)));
        var dir = wheel > 0f ? 1 : -1;
        if (config.CycleInvertScroll) dir = -dir;
        Cycle(dir * magnitude);
    }

    private void Cycle(int step)
    {
        EnsureRing();
        if (ring.Count == 0) return;

        var idx = ring.IndexOf(selectedId);
        if (idx < 0) idx = 0;

        var n = ring.Count;
        idx = ((idx + step) % n + n) % n; // positive modulo for either direction
        selectedId = ring[idx];

        var obj = Plugin.ObjectTable.SearchById(selectedId);
        if (obj != null && obj.IsValid())
            setHardTarget(obj);
    }

    /// <summary>
    /// Refresh the ring against the live object table. Builds it on first use;
    /// thereafter updates in place so order + reversibility survive membership
    /// churn. Re-seats the selection if the hard target was changed externally
    /// (a click / the hard-target keybind picked something else).
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

        // External target change → adopt it as the selection anchor.
        var ht = Plugin.TargetManager.Target?.GameObjectId;
        if (ht != null && ht.Value != selectedId && curIds.Contains(ht.Value))
            selectedId = ht.Value;

        // First build (or after everything despawned): greedy chain from seed.
        if (ring.Count == 0)
        {
            var seed = SeedId(current, posById, ht, lp.Position);
            BuildChain(ring, current, posById, seed);
            if (!ring.Contains(selectedId))
                selectedId = ring.Count > 0 ? ring[0] : 0ul;
            return;
        }

        // Membership unchanged → keep the established order untouched.
        if (curIds.Count == ring.Count && curIds.SetEquals(ring))
        {
            if (!ring.Contains(selectedId))
                selectedId = ring.Count > 0 ? ring[0] : 0ul;
            return;
        }

        // Drop dead ids, preserving the order of survivors.
        ring.RemoveAll(id => !curIds.Contains(id));

        // Insert each new id next to its nearest existing ring neighbour, so
        // new adds slot in spatially without reshuffling the whole chain.
        foreach (var c in current)
        {
            if (ring.Contains(c.id)) continue;
            InsertNearest(ring, posById, c.id, c.pos);
        }

        if (ring.Count == 0 || !ring.Contains(selectedId))
            selectedId = ring.Count > 0 ? ring[0] : 0ul;
    }

    /// <summary>Pick the chain seed: current hard target, else last selection,
    /// else the attackable nearest the player.</summary>
    private ulong SeedId(
        List<(ulong id, Vector3 pos)> current,
        Dictionary<ulong, Vector3> posById,
        ulong? ht,
        Vector3 playerPos)
    {
        if (ht != null && posById.ContainsKey(ht.Value)) return ht.Value;
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

    // ── Zoom lock ─────────────────────────────────────────────────────────────

    private void EnsureZoomLock()
    {
        if (zoomLocked) return;
        if (TryReadDistance(out var dist))
        {
            lockedDistance = dist;
            zoomLocked = true;
        }
    }

    private void PinZoom()
    {
        if (!zoomLocked) return;
        var cam = CameraManager.Instance()->CurrentCamera;
        if (cam == null) return;
        var camBase = (nint)cam;
        *(float*)(camBase + OffDistance) = lockedDistance;
        *(float*)(camBase + OffInterpDistance) = lockedDistance;
    }

    private void ReleaseZoomLock() => zoomLocked = false;

    private bool TryReadDistance(out float dist)
    {
        dist = 0f;
        var cam = CameraManager.Instance()->CurrentCamera;
        if (cam == null) return false;
        dist = *(float*)((nint)cam + OffDistance);
        return true;
    }
}
