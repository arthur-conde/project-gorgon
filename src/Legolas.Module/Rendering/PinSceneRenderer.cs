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
        DrawSurveyPins(scene, rt, factory, brushes);
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

    private static void DrawSurveyPins(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes)
    {
        if (scene.SurveyPins.Count == 0) return;
        // Outer + centre painted once per pin. Step E will inject halo / glow
        // / scale-up / fill-swap for the selected pin in front of these.
        foreach (var pos in scene.SurveyPins)
        {
            DrawPinLayer(rt, factory, brushes, pos, scene.SurveyOuterDiameter, scene.SurveyOuter);
            DrawPinLayer(rt, factory, brushes, pos, scene.SurveyCenter.Size, scene.SurveyCenter);
        }
    }

    /// <summary>
    /// Draw one shape (fill + optional stroke) centred on the given pixel
    /// with the given outer-bound diameter. Stroke is inset so the outer
    /// extent of the rendered shape matches <paramref name="diameter"/>,
    /// matching the WPF version's <c>Stretch="Uniform"</c> behaviour where
    /// the stroke fits inside the layout slot rather than overflowing it.
    /// </summary>
    private static void DrawPinLayer(
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        D2DBrushCache brushes,
        PixelPoint center,
        double diameter,
        PinLayerStyle style)
    {
        if (style.Shape == PinShape.None || diameter <= 0) return;

        var thickness = style.StrokeStyle == PinStrokeStyle.None ? 0 : (float)style.StrokeThickness;
        // Outer-bound diameter -> geometry size = diameter - thickness so
        // the half-stroke on each side stays within the layout slot.
        var geomSize = (float)(diameter - thickness);
        if (geomSize <= 0) return;
        var halfGeom = geomSize / 2f;
        var cx = (float)center.X;
        var cy = (float)center.Y;

        var fill = brushes.Get(style.FillColor);
        var stroke = thickness > 0 ? brushes.Get(style.StrokeColor) : null;

        ID2D1StrokeStyle? strokeStyle = null;
        if (stroke is not null && style.StrokeStyle == PinStrokeStyle.Dashed)
        {
            // Matches LegolasPinShapeStyle's "4 2" dash convention from the
            // legacy WPF PinShapeStyleToDashArrayConverter.
            strokeStyle = CreateDashStyle(factory, new[] { 4f, 2f }, dashOffset: 0f);
        }
        try
        {
            switch (style.Shape)
            {
                case PinShape.Circle:
                    var ellipse = new Ellipse(new Vector2(cx, cy), halfGeom, halfGeom);
                    if (fill is not null) rt.FillEllipse(ellipse, fill);
                    if (stroke is not null) rt.DrawEllipse(ellipse, stroke, thickness, strokeStyle);
                    break;

                case PinShape.Square:
                    var rect = new Rect(cx - halfGeom, cy - halfGeom, geomSize, geomSize);
                    if (fill is not null) rt.FillRectangle(rect, fill);
                    if (stroke is not null) rt.DrawRectangle(rect, stroke, thickness, strokeStyle);
                    break;

                case PinShape.Diamond:
                    using (var diamond = BuildDiamondGeometry(factory, cx, cy, halfGeom))
                    {
                        if (fill is not null) rt.FillGeometry(diamond, fill);
                        if (stroke is not null) rt.DrawGeometry(diamond, stroke, thickness, strokeStyle);
                    }
                    break;

                case PinShape.Cross:
                    // Cross is stroke-only; ignore fill.
                    if (stroke is not null)
                    {
                        rt.DrawLine(new Vector2(cx - halfGeom, cy), new Vector2(cx + halfGeom, cy), stroke, thickness, strokeStyle);
                        rt.DrawLine(new Vector2(cx, cy - halfGeom), new Vector2(cx, cy + halfGeom), stroke, thickness, strokeStyle);
                    }
                    break;
            }
        }
        finally
        {
            strokeStyle?.Dispose();
        }
    }

    private static ID2D1PathGeometry BuildDiamondGeometry(ID2D1Factory factory, float cx, float cy, float half)
    {
        var geom = factory.CreatePathGeometry();
        using var sink = geom.Open();
        sink.BeginFigure(new Vector2(cx, cy - half), FigureBegin.Filled);
        sink.AddLine(new Vector2(cx + half, cy));
        sink.AddLine(new Vector2(cx, cy + half));
        sink.AddLine(new Vector2(cx - half, cy));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geom;
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
