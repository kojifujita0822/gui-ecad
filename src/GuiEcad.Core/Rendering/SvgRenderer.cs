using System.Globalization;
using System.Text;
using GuiEcad.Model;

namespace GuiEcad.Rendering;

/// <summary>
/// SVG XML を出力する IRenderer。ToolbarIcon 等の小図形生成に使用。
/// 座標単位は mm（SymbolGlyphs の world 座標と同一）。
/// </summary>
public sealed class SvgRenderer : IRenderer
{
    private readonly StringBuilder _sb;
    private double _tx, _ty, _scale = 1.0;
    private readonly Stack<(double tx, double ty, double scale)> _stack = new();

    public SvgRenderer(StringBuilder sb) => _sb = sb;

    public void PushTransform(double translateX, double translateY, double scale = 1.0)
    {
        _stack.Push((_tx, _ty, _scale));
        _tx = translateX * _scale + _tx;
        _ty = translateY * _scale + _ty;
        _scale *= scale;
    }

    public void PopTransform()
    {
        if (_stack.Count > 0) (_tx, _ty, _scale) = _stack.Pop();
    }

    public void PushClip(Rect2D rect) { }
    public void PopClip() { }

    public void DrawLine(Point2D a, Point2D b, StrokeStyle stroke)
    {
        var (ax, ay) = T(a);
        var (bx, by) = T(b);
        _sb.Append($"  <line x1=\"{F(ax)}\" y1=\"{F(ay)}\" x2=\"{F(bx)}\" y2=\"{F(by)}\" {Stroke(stroke)}/>\n");
    }

    public void DrawPolyline(ReadOnlySpan<Point2D> points, StrokeStyle stroke)
    {
        if (points.Length < 2) return;
        var pts = new StringBuilder();
        for (int i = 0; i < points.Length; i++)
        {
            var (x, y) = T(points[i]);
            if (i > 0) pts.Append(' ');
            pts.Append($"{F(x)},{F(y)}");
        }
        _sb.Append($"  <polyline points=\"{pts}\" {Stroke(stroke)}/>\n");
    }

    public void DrawRectangle(Rect2D rect, StrokeStyle stroke)
    {
        var (x, y) = T(new Point2D(rect.X, rect.Y));
        _sb.Append($"  <rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(rect.Width * _scale)}\" height=\"{F(rect.Height * _scale)}\" {Stroke(stroke)}/>\n");
    }

    public void FillRectangle(Rect2D rect, Color color)
    {
        var (x, y) = T(new Point2D(rect.X, rect.Y));
        _sb.Append($"  <rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(rect.Width * _scale)}\" height=\"{F(rect.Height * _scale)}\" fill=\"{SvgColor(color)}\" stroke=\"none\"/>\n");
    }

    public void DrawCircle(Point2D center, double radius, StrokeStyle stroke)
    {
        var (cx, cy) = T(center);
        _sb.Append($"  <circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(radius * _scale)}\" {Stroke(stroke)}/>\n");
    }

    public void FillCircle(Point2D center, double radius, Color color)
    {
        var (cx, cy) = T(center);
        _sb.Append($"  <circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(radius * _scale)}\" fill=\"{SvgColor(color)}\" stroke=\"none\"/>\n");
    }

    public void DrawEllipse(Point2D center, double radiusX, double radiusY, StrokeStyle stroke)
    {
        var (cx, cy) = T(center);
        _sb.Append($"  <ellipse cx=\"{F(cx)}\" cy=\"{F(cy)}\" rx=\"{F(radiusX * _scale)}\" ry=\"{F(radiusY * _scale)}\" {Stroke(stroke)}/>\n");
    }

    public void DrawArc(Point2D center, double radius, double startDeg, double sweepDeg, StrokeStyle stroke)
    {
        double a0 = startDeg * Math.PI / 180.0;
        double a1 = (startDeg + sweepDeg) * Math.PI / 180.0;
        double r = radius * _scale;
        var (cx, cy) = T(center);
        double sx = cx + r * Math.Cos(a0), sy = cy + r * Math.Sin(a0);
        double ex = cx + r * Math.Cos(a1), ey = cy + r * Math.Sin(a1);
        int largeArc = Math.Abs(sweepDeg) > 180 ? 1 : 0;
        int sweepFlag = sweepDeg > 0 ? 1 : 0;
        _sb.Append($"  <path d=\"M{F(sx)},{F(sy)} A{F(r)},{F(r)} 0 {largeArc} {sweepFlag} {F(ex)},{F(ey)}\" {Stroke(stroke)}/>\n");
    }

    public void DrawText(string text, Point2D position, TextStyle style) { }

    public Size2D MeasureText(string text, TextStyle style)
        => new(text.Length * style.FontSizeMm * 0.6 * _scale, style.FontSizeMm * _scale);

    // ツールバーアイコン用 SVG 文字列を生成する。
    // cell=8mm, vpad=cell*0.62 でタイマ△(高さ cell*0.58)まで収まる。
    public static string GenerateSymbolSvg(ElementKind kind, double strokeWidthMm = 0.35)
    {
        const double cell = 8.0;
        const double hpad = 0.5;
        double vpad = cell * 0.62;
        double vbW = cell + hpad * 2;
        double vbH = vpad * 2;

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{F(-hpad)} {F(-vpad)} {F(vbW)} {F(vbH)}\" width=\"{F(vbW)}\" height=\"{F(vbH)}\">\n");

        var stroke = new StrokeStyle(DrawingTheme.Black, strokeWidthMm);
        var renderer = new SvgRenderer(sb);
        SymbolGlyphs.Draw(renderer, stroke, kind, cell, cell);

        sb.Append("</svg>");
        return sb.ToString();
    }

    private (double x, double y) T(Point2D p) => (p.X * _scale + _tx, p.Y * _scale + _ty);
    private static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string SvgColor(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private string Stroke(StrokeStyle s)
    {
        string w = F(s.Width * _scale);
        string dash = s.Style == LineStyle.Dashed
            ? $" stroke-dasharray=\"{F(s.Width * _scale * 3)},{F(s.Width * _scale * 2)}\""
            : "";
        string cap = s.Cap switch
        {
            LineCap.Round => " stroke-linecap=\"round\"",
            LineCap.Square => " stroke-linecap=\"square\"",
            _ => ""
        };
        return $"stroke=\"{SvgColor(s.Color)}\" stroke-width=\"{w}\" fill=\"none\"{dash}{cap}";
    }
}
