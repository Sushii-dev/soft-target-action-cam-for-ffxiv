using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace ActionCamera;

public sealed class ReticleOverlay
{
    private readonly CameraController cameraController;
    private readonly Configuration config;

    // ABGR packed colours used by ImGui draw primitives.
    private const uint ColSolid = 0xCCFFFFFF; // white 80 %
    private const uint ColDim   = 0x66FFFFFF; // white 40 %

    public ReticleOverlay(CameraController cameraController, Configuration config)
    {
        this.cameraController = cameraController;
        this.config           = config;
    }

    public void Draw()
    {
        if (!config.ShowReticle || !cameraController.IsActive) return;

        var dl     = ImGui.GetForegroundDrawList();
        var center = ImGui.GetIO().DisplaySize * 0.5f;
        var hasTarget = Plugin.TargetManager.SoftTarget != null;

        DrawDot(dl, center);
        DrawTickMarks(dl, center);

        if (hasTarget)
            DrawTargetRing(dl, center);
    }

    private static void DrawDot(ImDrawListPtr dl, Vector2 center)
    {
        dl.AddCircleFilled(center, 2.5f, ColSolid);
    }

    // Four tick marks at 45 ° / 135 ° / 225 ° / 315 ° — the GW2 "X" shape.
    private static void DrawTickMarks(ImDrawListPtr dl, Vector2 center)
    {
        const float innerR = 6f;
        const float outerR = 14f;

        for (var i = 0; i < 4; i++)
        {
            var angle = MathF.PI / 4f + i * (MathF.PI / 2f);
            var dir   = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            dl.AddLine(center + dir * innerR, center + dir * outerR, ColSolid, 1.5f);
        }
    }

    // Outer ring that appears once a soft target is acquired.
    private static void DrawTargetRing(ImDrawListPtr dl, Vector2 center)
    {
        dl.AddCircle(center, 22f, ColDim, 32, 1.5f);
    }
}
