using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace ActionCamera;

/// <summary>
/// Scans the object table each frame and soft-targets the most centred hostile
/// enemy within the configured distance and field-of-view cone.
/// </summary>
public sealed class TargetSelector
{
    private const int ScanIntervalFrames = 3;
    private int frameCounter;

    private readonly Configuration config;

    public TargetSelector(Configuration config)
    {
        this.config = config;
    }

    public void Update(float cameraHRotation)
    {
        if (++frameCounter < ScanIntervalFrames) return;
        frameCounter = 0;

        var best = FindBestTarget(cameraHRotation, config);
        if (best != null)
            Plugin.TargetManager.SoftTarget = best;
    }

    private static IGameObject? FindBestTarget(float cameraYaw, Configuration config)
    {
        var localPlayer = Plugin.ClientState.LocalPlayer;
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
            if (!IsValidTarget(obj, localPlayer)) continue;

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

    private static bool IsValidTarget(IGameObject obj, IPlayerCharacter localPlayer)
    {
        // Skip the local player.
        if (obj.GameObjectId == localPlayer.GameObjectId) return false;

        // Only hostile battle NPCs.
        if (obj.ObjectKind != ObjectKind.BattleNpc) return false;

        // Must be visible and targetable.
        if (!obj.IsTargetable) return false;

        // IBattleNpc exposes SubKind; Enemy = hostile mob.
        if (obj is IBattleNpc { BattleNpcKind: BattleNpcSubKind.Enemy } battleNpc)
        {
            if (battleNpc.CurrentHp == 0) return false;
            return true;
        }

        return false;
    }
}
