using GuiEcad.Model;
using GuiEcad.Rendering;

namespace GuiEcad.Tests;

internal static class TestHelper
{
    internal static ElementInstance El(ElementKind kind, int row, int col,
        string? dev = null, int width = 1)
        => new()
        {
            Kind = kind,
            Pos = new GridPos(row, col),
            CellWidth = width,
            DeviceName = dev,
        };
}

/// <summary>何も記録しない検証用 IRenderer（描画呼び出しが例外を投げないことの確認用）。
/// PushClip/PopClip の釣り合いを ClipDepth で確認できる。</summary>
internal sealed class NullRenderer : IRenderer
{
    public int ClipDepth { get; private set; }
    /// <summary>PushClip が呼ばれた累計回数（ClipDepth と異なり Pop されても減らない）。
    /// 「クリップが一度も呼ばれていない」ことを区別するために使う。</summary>
    public int PushClipCount { get; private set; }
    public void PushTransform(double tx, double ty, double scale = 1.0) { }
    public void PopTransform() { }
    public void PushClip(Rect2D r) { ClipDepth++; PushClipCount++; }
    public void PopClip() => ClipDepth--;
    public void DrawLine(Point2D a, Point2D b, StrokeStyle s) { }
    public void DrawPolyline(ReadOnlySpan<Point2D> p, StrokeStyle s) { }
    public void DrawRectangle(Rect2D r, StrokeStyle s) { }
    public void FillRectangle(Rect2D r, Color c) { }
    public void DrawCircle(Point2D c, double r, StrokeStyle s) { }
    public void FillCircle(Point2D c, double r, Color col) { }
    public void DrawEllipse(Point2D c, double rx, double ry, StrokeStyle s) { }
    public void DrawArc(Point2D c, double r, double sd, double sw, StrokeStyle s) { }
    public void DrawText(string t, Point2D p, TextStyle s) { }
    public Size2D MeasureText(string t, TextStyle s) => new(t.Length * s.FontSizeMm * 0.5, s.FontSizeMm);
}
