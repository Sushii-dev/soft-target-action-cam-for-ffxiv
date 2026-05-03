using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace ActionCamera;

public sealed class ReticleOverlay
{
    private readonly CameraController cameraController;
    private readonly Configuration config;

    public ReticleOverlay(CameraController cameraController, Configuration config)
    {
        this.cameraController = cameraController;
        this.config           = config;
    }

    public void Draw()
    {
        if (!config.ShowReticle || !cameraController.IsActive) return;

        var dl        = ImGui.GetForegroundDrawList();
        var center    = ImGui.GetIO().DisplaySize * 0.5f;
        var colSolid  = ImGui.ColorConvertFloat4ToU32(config.ReticleColor);
        var dimmed    = config.ReticleColor with { W = config.ReticleColor.W * 0.5f };
        var colDim    = ImGui.ColorConvertFloat4ToU32(dimmed);
        var hasTarget = Plugin.TargetManager.SoftTarget != null
                     || Plugin.TargetManager.Target != null;

        DrawDot(dl, center, colSolid);
        DrawTickMarks(dl, center, colSolid);

        if (hasTarget)
            DrawTargetRing(dl, center, colDim);
    }

    private static void DrawDot(ImDrawListPtr dl, Vector2 center, uint color)
    {
        dl.AddCircleFilled(center, 2.5f, color);
    }

    // Four tick marks at 45 ° / 135 ° / 225 ° / 315 ° — the GW2 "X" shape.
    private static void DrawTickMarks(ImDrawListPtr dl, Vector2 center, uint color)
    {
        const float innerR = 6f;
        const float outerR = 14f;

        for (var i = 0; i < 4; i++)
        {
            var angle = MathF.PI / 4f + i * (MathF.PI / 2f);
            var dir   = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            dl.AddLine(center + dir * innerR, center + dir * outerR, color, 1.5f);
        }
    }

    // Outer ring that appears once a soft target is acquired.
    private static void DrawTargetRing(ImDrawListPtr dl, Vector2 center, uint color)
    {
        dl.AddCircle(center, 22f, color, 32, 1.5f);
    }
}
