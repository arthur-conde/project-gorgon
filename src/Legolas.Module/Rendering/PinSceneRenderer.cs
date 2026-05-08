using System.Numerics;
using Legolas.Domain;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.Mathematics;

namespace Legolas.Rendering;

/// <summary>
/// Pure draw logic for the D2D-rendered pin layer. Stateless static class —
/// every render reads from a <see cref="PinScene"/> snapshot, draws against
/// the supplied <see cref="ID2D1RenderTarget"/>, and uses a
/// <see cref="D2DBrushCache"/> to avoid per-call allocation. Stroke styles
/// are recreated each call; they're cheap factory-side objects in D2D and
/// optimising that comes in step G if it ever shows up in a profile.
///
/// Step C scope: route polyline (dashed) + active segment (dashed, thicker)
/// + bearing-uncertainty wedges. Pins, treatments, and the player anchor
/// land in steps D/E/F.
/// </summary>
internal static class PinSceneRenderer
{
    private const float RouteThickness = 2f;
    private const float ActiveSegmentThickness = 4f;
    private const float WedgeStrokeThickness = 1f;

    public static void Render(
        PinScene scene,
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        D2DBrushCache brushes)
    {
        // Wedges sit behind the route lines — drawing order matters; D2D has
        // no depth buffer for transparent geometry, so painters' algorithm.
        DrawWedges(scene, rt, factory, brushes);
        DrawRoute(scene, rt, factory, brushes);
        DrawActiveSegment(scene, rt, factory, brushes);
    }

    private static void DrawWedges(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes)
    {
        if (scene.Wedges.Count == 0) return;
        var fill = brushes.Get(scene.WedgeFillColor);
        var stroke = brushes.Get(scene.WedgeStrokeColor);
        if (fill is null || stroke is null) return;

        foreach (var wedge in scene.Wedges)
        {
            using var path = BuildWedgeGeometry(factory, wedge);
            rt.FillGeometry(path, fill);
            rt.DrawGeometry(path, stroke, WedgeStrokeThickness);
        }
    }

    private static void DrawRoute(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes)
    {
        if (scene.RoutePoints.Count < 2) return;
        var brush = brushes.Get(scene.RouteLineColor);
        if (brush is null) return;

        // Matches the WPF version's StrokeDashArray="4 2" on the static route polyline.
        using var dashStyle = CreateDashStyle(factory, new[] { 4f, 2f }, dashOffset: 0f);
        DrawPolyline(rt, scene.RoutePoints, brush, RouteThickness, dashStyle);
    }

    private static void DrawActiveSegment(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes)
    {
        if (scene.ActiveSegmentPoints.Count < 2) return;
        var brush = brushes.Get(scene.RouteLineColor);
        if (brush is null) return;

        // Matches the WPF Storyboard: StrokeDashArray="6 3", animating
        // StrokeDashOffset 0 → -9 over 0.6s. The clock advance lives in
        // step F's MarchingAntsClock; for step C we accept whatever value
        // the scene carries (default 0 = static dashes).
        using var dashStyle = CreateDashStyle(factory, new[] { 6f, 3f }, dashOffset: (float)scene.ActiveSegmentDashOffset);
        DrawPolyline(rt, scene.ActiveSegmentPoints, brush, ActiveSegmentThickness, dashStyle);
    }

    private static void DrawPolyline(
        ID2D1RenderTarget rt,
        IReadOnlyList<PixelPoint> points,
        ID2D1SolidColorBrush brush,
        float thickness,
        ID2D1StrokeStyle? style)
    {
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            rt.DrawLine(
                new Vector2((float)a.X, (float)a.Y),
                new Vector2((float)b.X, (float)b.Y),
                brush, thickness, style);
        }
    }

    private static ID2D1StrokeStyle CreateDashStyle(ID2D1Factory factory, float[] dashes, float dashOffset)
    {
        // CapStyle.Flat keeps dash ends crisp at the configured length; Round
        // would balloon them. LineJoin.Miter matches WPF's default polyline join.
        var props = new StrokeStyleProperties
        {
            StartCap = CapStyle.Flat,
            EndCap = CapStyle.Flat,
            DashCap = CapStyle.Flat,
            LineJoin = LineJoin.Miter,
            MiterLimit = 10f,
            DashStyle = DashStyle.Custom,
            DashOffset = dashOffset,
        };
        return factory.CreateStrokeStyle(props, dashes);
    }

    private static ID2D1PathGeometry BuildWedgeGeometry(ID2D1Factory factory, WedgeArc wedge)
    {
        var geom = factory.CreatePathGeometry();
        using var sink = geom.Open();
        var bMin = wedge.BearingRadians - wedge.HalfAngleRadians;
        var bMax = wedge.BearingRadians + wedge.HalfAngleRadians;
        var origin = new Vector2((float)wedge.Origin.X, (float)wedge.Origin.Y);
        var pStart = new Vector2(
            (float)(wedge.Origin.X + wedge.DistancePx * Math.Sin(bMin)),
            (float)(wedge.Origin.Y - wedge.DistancePx * Math.Cos(bMin)));
        var pEnd = new Vector2(
            (float)(wedge.Origin.X + wedge.DistancePx * Math.Sin(bMax)),
            (float)(wedge.Origin.Y - wedge.DistancePx * Math.Cos(bMax)));

        sink.BeginFigure(origin, FigureBegin.Filled);
        sink.AddLine(pStart);
        sink.AddArc(new ArcSegment
        {
            Point = pEnd,
            Size = new Size((float)wedge.DistancePx, (float)wedge.DistancePx),
            RotationAngle = 0,
            SweepDirection = SweepDirection.Clockwise,
            ArcSize = ArcSize.Small,
        });
        sink.AddLine(origin);
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geom;
    }
}
