#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
public static class OrbitersGlow
{
    public static void DrawAnimatedGlows(MeshGenerationContext context, Rect rect)
    {
        double time = EditorApplication.timeSinceStartup;
        var painter = context.painter2D;
        var center = rect.center;

        DrawGlow(painter, center, rect, time, 0.10f, 0.18f, 0.38f, new Color(0.45f, 0.16f, 1f, 0.28f), 0.56f, 0.42f, 0.86f);
        DrawGlow(painter, center, rect, time, 0.64f, 0.46f, 0.46f, new Color(0.04f, 0.44f, 1f, 0.25f), 0.52f, 0.50f, 0.92f);
        DrawGlow(painter, center, rect, time, 1.28f, 0.66f, 0.32f, new Color(0.78f, 0.20f, 1f, 0.22f), 0.46f, 0.36f, 0.88f);
        DrawGlow(painter, center, rect, time, 2.02f, 0.36f, 0.54f, new Color(0.10f, 0.20f, 1f, 0.20f), 0.62f, 0.30f, 0.78f);
        DrawGlow(painter, center, rect, time, 2.76f, 0.52f, 0.26f, new Color(0.28f, 0.68f, 1f, 0.16f), 0.50f, 0.24f, 0.98f);
        DrawGlow(painter, center, rect, time, 3.38f, 0.78f, 0.50f, new Color(0.52f, 0.26f, 1f, 0.15f), 0.38f, 0.28f, 0.82f);
        DrawGlow(painter, center, rect, time, 4.10f, 0.28f, 0.20f, new Color(0.06f, 0.58f, 1f, 0.13f), 0.42f, 0.32f, 0.96f);
        DrawGlow(painter, center, rect, time, 4.82f, 0.58f, 0.60f, new Color(0.72f, 0.10f, 1f, 0.12f), 0.48f, 0.22f, 0.74f);
    }

    private static void DrawGlow(Painter2D painter, Vector2 center, Rect rect, double time, float phase, float xBias, float yBias, Color color, float baseSize, float speed, float heightScale)
    {
        float t = (float)(time * speed + phase);
        float driftX = Mathf.Sin(t * 0.73f) * rect.width * 0.08f;
        float driftY = Mathf.Cos(t * 0.61f) * rect.height * 0.055f;
        float pulse = 0.90f + Mathf.Sin(t * 0.82f) * 0.10f;
        float fade = 0.72f + Mathf.Sin(t * 0.33f + phase) * 0.28f;
        float rotation = t * 0.10f + phase;
        Vector2 glowCenter = new Vector2(
            Mathf.Lerp(rect.xMin, rect.xMax, xBias) + driftX,
            Mathf.Lerp(rect.yMin, rect.yMax, yBias) + driftY);
        glowCenter = Vector2.Lerp(glowCenter, center, 0.34f);

        Vector2 radii = new Vector2(rect.width * baseSize * pulse, rect.height * baseSize * heightScale * pulse);
        color.a *= fade;
        DrawSoftEllipse(painter, glowCenter, radii, rotation, color);
    }

    private static void DrawSoftEllipse(Painter2D painter, Vector2 center, Vector2 radii, float rotation, Color color)
    {
        const int layers = 6;
        for (int layer = layers; layer >= 1; layer--)
        {
            float layerT = layer / (float)layers;
            Color layerColor = color;
            layerColor.a *= Mathf.Pow(1f - layerT * 0.82f, 1.4f) * 0.42f;
            DrawEllipsePolygon(painter, center, radii * layerT, rotation, layerColor);
        }
    }

    private static void DrawEllipsePolygon(Painter2D painter, Vector2 center, Vector2 radii, float rotation, Color color)
    {
        const int segments = 24;
        float cos = Mathf.Cos(rotation);
        float sin = Mathf.Sin(rotation);

        painter.fillColor = color;
        painter.BeginPath();
        for (int i = 0; i <= segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.PI * 2f;
            float x = Mathf.Cos(angle) * radii.x;
            float y = Mathf.Sin(angle) * radii.y;
            var point = new Vector2(center.x + x * cos - y * sin, center.y + x * sin + y * cos);
            if (i == 0)
            {
                painter.MoveTo(point);
            }
            else
            {
                painter.LineTo(point);
            }
        }
        painter.ClosePath();
        painter.Fill(FillRule.NonZero);
    }
}
#endif
