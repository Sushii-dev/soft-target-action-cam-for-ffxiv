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
    private int frameCounter = ScanIntervalFrames;
    private IGameObject? cachedBest;

    private readonly Configuration config;

    public TargetSelector(Configuration config)
    {
        this.config = config;
    }

    public void Update(float cameraHRotation)
    {
        if (++frameCounter >= ScanIntervalFrames)
        {
            frameCounter = 0;
            cachedBest = FindBestTarget(cameraHRotation, config);
        }

        if (config.WriteMouseOverTarget)
        {
            // Dalamud's ITargetManager.MouseOverTarget setter only writes the
            // logical pointer at TargetSystem+0xD0. The yellow-outline renderer
            // reads from TargetOutlineInfo at +0x70 (a separate struct). We need
            // to write both, every frame, to keep the highlight after the game's
            // own per-frame cursor hit-test clears them.
            var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
            var go = cachedBest != null
                ? (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)cachedBest.Address
                : null;
            ts->MouseOverTarget = go;
            ts->TargetOutlineInfo.MouseOverTarget = go;
        }
        if (config.WriteSoftTarget) Plugin.TargetManager.SoftTarget = cachedBest;
        if (config.WriteHardTarget) Plugin.TargetManager.Target     = cachedBest;
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
