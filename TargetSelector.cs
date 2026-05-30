using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace ActionCamera;

/// <summary>
/// Scans the object table periodically and writes the most centred hostile
/// enemy in the configured FOV cone to both MouseOverTarget (yellow outline)
/// and SoftTarget (red ring) each frame.
/// </summary>
public sealed unsafe class TargetSelector
{
    private const int ScanIntervalFrames = 3;

    // Post-LMB-release skip-write window (v0.6.11). After this many
    // milliseconds with no LMB activity, plugin resumes per-frame
    // MouseOverTarget + SoftTarget writes. Long enough to bridge the
    // game's release-handler completing its work; short enough that
    // the user perceives the ring as steady during normal play.
    private const int SoftTargetSuppressWindowMs = 200;
    private int frameCounter = ScanIntervalFrames;
    private IGameObject? cachedBest;
    // Entity whose outline we last painted yellow. Tracked so we can reset its
    // DrawObject.OutlineColor when the cone moves to a different target or empty —
    // the game's own outline updater doesn't clean up direct Highlight() writes.
    private ulong lastHighlightedId;
    // Edge tracking for the hard-target pause. Set on the frame we first see
    // a non-null hard target so we can scrub our soft-target/outline writes
    // exactly once at the transition, rather than every frame while paused.
    private bool wasPausedByHardTarget;

    // Exposed for HardTargetSuppressor + SoftTargetGuard: address of the cone's
    // current pick, or 0. IsValid() gated — SoftTargetGuard writes this raw into
    // TargetSystem.SoftTarget every frame, so a stale (despawned) address here is
    // a use-after-free the moment a targeting Agent reads SoftTarget. Returning 0
    // for an invalidated pick makes the guard skip the write.
    public nint CachedBestAddress => (cachedBest != null && cachedBest.IsValid())
        ? cachedBest.Address
        : nint.Zero;

    // Exposed for the manual hard-target key. Null when no valid candidate.
    public IGameObject? CachedBest => cachedBest;

    private readonly Configuration config;

    public TargetSelector(Configuration config)
    {
        this.config = config;
    }

    public void Update(float cameraHRotation)
    {
        // Scan unconditionally so CachedBest stays fresh for the manual
        // hard-target keybind, even while soft-target writes are paused.
        // That lets the user re-hard-target a different cone pick without
        // first having to clear the current hard target.
        if (++frameCounter >= ScanIntervalFrames)
        {
            frameCounter = 0;
            cachedBest = FindBestTarget(cameraHRotation, config);
        }

        // Pause writes while a hard target exists (set by Tab, click, our
        // hard-target keybind, etc.). Resumes automatically when the hard
        // target is cleared — by the clear-target keybind, by the enemy
        // dying / despawning, or by the player switching hard target via
        // any other means.
        if (Plugin.TargetManager.Target != null)
        {
            if (!wasPausedByHardTarget)
            {
                ClearOurOutputs();
                wasPausedByHardTarget = true;
            }
            return;
        }
        wasPausedByHardTarget = false;

        // v0.6.11: pause MouseOverTarget + SoftTarget writes during a
        // window after any LMB activity. v0.6.6–v0.6.10's three-prong
        // suppressor stack (GetMouseOverObject + SetSoftTarget +
        // GetInputStatus) didn't kill the click-target reattach
        // animation — confirmed via counters that none of the hooked
        // functions are on the path the game uses to queue the cue.
        // The game writes SoftTarget = null directly to memory during
        // the release event, then plugin's per-frame restore creates
        // a null→enemy pointer transition that fires the "newly
        // acquired" animation + SFX.
        //
        // Skipping plugin writes during the post-click window lets
        // whatever state the game left behind stand — no transition
        // for the game's UI subsystem to animate. Cost: red ring
        // briefly disappears around the click instead of flickering.
        // Trade subtle disappearance for the loud acquire cue.
        if (InputBinding.WasLmbDownWithin(SoftTargetSuppressWindowMs))
            return;

        if (config.WriteMouseOverTarget)
        {
            // Two parts:
            //   1. Write TargetSystem.MouseOverTarget (+0xD0) so ReAction's
            //      "Field Target" pronoun and other consumers see our pick.
            //   2. Paint the yellow outline by calling GameObject.Highlight
            //      directly (vfunc 26). Writing OutlineInfo.MouseOverTarget
            //      (+0x70) is futile because the game's ProcessMouseState
            //      clobbers it from the real cursor position each frame
            //      *before* UpdateOutline runs. Highlight writes
            //      DrawObject.OutlineColor directly; must be re-applied each
            //      frame because UpdateOutline resets prior highlights.
            // The game's outline-info cleanup never touches entities we
            // painted manually, so we have to reset the previous target's
            // color ourselves whenever the cone pick changes (or empties).
            var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
            // IsValid() gate: cachedBest can be a despawned actor (the cone scan's
            // ObjectTable snapshot lags a same-frame free). Writing its address to
            // MouseOverTarget or calling Highlight() (a vfunc) on freed memory is a
            // use-after-free crash. Treat invalid as null → clear, no vfunc call.
            var valid = cachedBest != null && cachedBest.IsValid();
            var go = valid
                ? (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)cachedBest!.Address
                : null;
            var curId = valid ? cachedBest!.GameObjectId : 0ul;
            if (lastHighlightedId != 0 && lastHighlightedId != curId)
                ResetHighlight(lastHighlightedId);
            ts->MouseOverTarget = go;
            if (go != null)
                go->Highlight(FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectHighlightColor.Yellow);
            lastHighlightedId = curId;
        }
        else if (lastHighlightedId != 0)
        {
            // Toggle went off while a highlight was active — clean it up.
            ResetHighlight(lastHighlightedId);
            lastHighlightedId = 0;
        }
        // SoftTarget direct field write (v0.6.5): Dalamud's
        // ITargetManager.SoftTarget setter calls into the game's
        // SetSoftTarget function which fires the "target acquired"
        // animation + SFX on value change. The game's own input pipeline
        // also goes through that function when clearing SoftTarget on
        // LMB / RMB release — by the time our next-frame write happens,
        // the null→entity transition triggers the cue. Writing the field
        // directly (same pattern as the MouseOverTarget write above)
        // bypasses the function and keeps the pointer in sync without
        // queueing any UI animation.
        if (config.WriteSoftTarget) DirectSetSoftTarget(cachedBest);
        if (config.WriteHardTarget) Plugin.TargetManager.Target = cachedBest;
    }

    /// <summary>
    /// Direct-field-write helper for <c>TargetSystem.SoftTarget</c>. Used in
    /// place of <see cref="Plugin.TargetManager"/>'s SoftTarget setter so the
    /// game's SetSoftTarget function (which fires the "target acquired" UI
    /// animation + SFX) is bypassed. Shared by every plugin code path that
    /// keeps SoftTarget pinned to our cone pick — see <c>MouseBindController</c>
    /// post-fire restore and <c>Plugin.DrawUI</c> render-time restore.
    /// </summary>
    public static unsafe void DirectSetSoftTarget(IGameObject? obj)
    {
        var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
        if (ts == null) return;
        DirectSetSoftTarget(ts, obj);
    }

    private static unsafe void DirectSetSoftTarget(
        FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem* ts, IGameObject? obj)
    {
        // CRITICAL: never write a stale actor pointer into SoftTarget. obj.Address
        // is captured at wrap time; if that actor despawned since (e.g. a mob dies
        // / the duty's Character Destructor wave fires between our snapshot and this
        // write), the pointer dangles. The game's targeting Agents read SoftTarget
        // (+0x88) every tick and dereference it — a freed pointer there is a native
        // use-after-free that crashes deep in Agent::Update, far from our code.
        // IsValid() re-checks the object is still live this frame; if not, clear
        // SoftTarget rather than point it at freed memory.
        ts->SoftTarget = obj != null && obj.IsValid()
            ? (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address
            : null;
    }

    private static IGameObject? FindBestTarget(float cameraYaw, Configuration config)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return null;

        var playerPos = localPlayer.Position;

        // Camera forward in the XZ plane (FFXIV is Y-up, right-handed-ish).
        // HRotation = 0 → camera looks toward negative-Z (south in game coords).
        var camForward = new Vector3(-MathF.Sin(cameraYaw), 0f, -MathF.Cos(cameraYaw));

        var maxAngle  = config.AutoTargetFovDegrees * (MathF.PI / 180f);
        var maxDistSq = config.AutoTargetMaxDistance * config.AutoTargetMaxDistance;
        var angleW    = config.AutoTargetAngleWeight;

        IGameObject? bestObj = null;
        var bestScore = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (!IsValidTarget(obj, localPlayer, config.RequireAggro)) continue;

            var toTarget = obj.Position - playerPos;
            var distSq = toTarget.LengthSquared();
            if (distSq > maxDistSq || distSq < 0.01f) continue;

            // Project onto XZ plane (ignore height difference for angle test).
            var toTargetXZ = new Vector3(toTarget.X, 0f, toTarget.Z);
            var len = toTargetXZ.Length();
            if (len < 0.01f) continue;

            var dot   = Vector3.Dot(toTargetXZ / len, camForward);
            var angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
            if (angle > maxAngle) continue;

            // Score: lower is better. Weight angle heavily so the most
            // centred target wins ties over the closest one.
            var score = angle * angleW + MathF.Sqrt(distSq) * 0.05f;
            if (score < bestScore)
            {
                bestScore = score;
                bestObj   = obj;
            }
        }

        return bestObj;
    }

    /// <summary>
    /// Clears any yellow outline we painted. Call on deactivation / dispose
    /// so the previous cone target doesn't keep a stale highlight.
    /// </summary>
    public void ClearMouseOverHighlight()
    {
        if (lastHighlightedId != 0)
        {
            ResetHighlight(lastHighlightedId);
            lastHighlightedId = 0;
        }
        cachedBest = null;
        wasPausedByHardTarget = false;
    }

    /// <summary>
    /// Scrub our own soft-target/outline writes on the transition into the
    /// hard-target pause. We deliberately do NOT null cachedBest — the scan
    /// continues so the manual hard-target keybind can swap to a different
    /// cone pick.
    ///
    /// MouseOverTarget intentionally isn't touched: the game's ProcessMouseState
    /// rewrites it from the real cursor position every frame, so it'll clear
    /// itself on the next tick once we stop re-asserting our pick.
    /// </summary>
    private void ClearOurOutputs()
    {
        if (lastHighlightedId != 0)
        {
            ResetHighlight(lastHighlightedId);
            lastHighlightedId = 0;
        }
        // Only clear SoftTarget if it still matches our last cone pick — we
        // shouldn't clobber a soft target the user or another plugin set
        // explicitly after we wrote ours.
        if (cachedBest != null
            && Plugin.TargetManager.SoftTarget != null
            && Plugin.TargetManager.SoftTarget.GameObjectId == cachedBest.GameObjectId)
        {
            DirectSetSoftTarget(null);
        }
    }

    private static void ResetHighlight(ulong objectId)
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.GameObjectId != objectId) continue;
            ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address)
                ->Highlight(FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectHighlightColor.None);
            return;
        }
    }

    private static bool IsValidTarget(IGameObject obj, IGameObject localPlayer, bool requireAggro)
    {
        if (obj.GameObjectId == localPlayer.GameObjectId) return false;
        if (obj.ObjectKind != ObjectKind.BattleNpc) return false;
        if (obj is not IBattleNpc npc || (byte)npc.BattleNpcKind != 5) return false;
        if (npc.CurrentHp == 0) return false;
        if (!obj.IsTargetable) return false;
        if (requireAggro && !IsTargetingParty(obj, localPlayer)) return false;
        return true;
    }

    private static bool IsTargetingParty(IGameObject obj, IGameObject localPlayer)
    {
        var tid = obj.TargetObjectId;
        // 0xE0000000 is the game's sentinel for "no target"
        if (tid == 0 || tid == 0xE0000000) return false;
        if (tid == localPlayer.GameObjectId) return true;
        foreach (var member in Plugin.PartyList)
            if (member.GameObject?.GameObjectId == tid) return true;
        return false;
    }
}
