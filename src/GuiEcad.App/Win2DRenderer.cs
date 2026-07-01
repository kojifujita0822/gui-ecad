using System.Numerics;
using GuiEcad.Model;
using GuiEcad.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using WinColor = Windows.UI.Color;

namespace GuiEcad_App;

/// <summary>
/// Win2D（CanvasDrawingSession）による <see cref="IRenderer"/> 実装。
/// 描画はワールド座標 mm。base 変換（mm→DIP・ズーム・パン）を session.Transform に積む。
/// 線幅・フォントサイズも mm 指定→変換で DIP にスケールされる。
/// </summary>
internal sealed class Win2DRenderer : IRenderer
{
    // 挿入画像のビットマップキャッシュ（ファイルパス→CanvasBitmap）。
    // CanvasBitmap.LoadAsync は非同期のため、Draw イベント（同期）内では読み込めない。
    // 画像追加時・ドキュメント読込時に PreloadImageAsync で事前ロードし、Draw では同期参照のみ行う。
    private static readonly Dictionary<string, CanvasBitmap> _bitmapCache = new();

    private readonly CanvasDrawingSession _g;
    private readonly Stack<Matrix3x2> _stack = new();
    private readonly Stack<CanvasActiveLayer> _layers = new();
    private Matrix3x2 _current;

    public Win2DRenderer(CanvasDrawingSession g, Matrix3x2 baseTransform)
    {
        _g = g;
        _current = baseTransform;
        _g.Transform = _current;
    }

    public void PushTransform(double translateX, double translateY, double scale = 1.0)
    {
        _stack.Push(_current);
        var local = Matrix3x2.CreateScale((float)scale) *
                    Matrix3x2.CreateTranslation((float)translateX, (float)translateY);
        _current = local * _current;
        _g.Transform = _current;
    }

    public void PopTransform()
    {
        if (_stack.Count > 0) { _current = _stack.Pop(); _g.Transform = _current; }
    }

    public void PushClip(Rect2D rect)
        => _layers.Push(_g.CreateLayer(1f, new Windows.Foundation.Rect(rect.X, rect.Y, rect.Width, rect.Height)));

    public void PopClip()
    {
        if (_layers.Count > 0) _layers.Pop().Dispose();
    }

    public void DrawLine(Point2D a, Point2D b, StrokeStyle stroke)
        => _g.DrawLine(V(a), V(b), Col(stroke.Color), W(stroke), Style(stroke));

    public void DrawPolyline(ReadOnlySpan<Point2D> points, StrokeStyle stroke)
    {
        for (int i = 1; i < points.Length; i++)
            _g.DrawLine(V(points[i - 1]), V(points[i]), Col(stroke.Color), W(stroke), Style(stroke));
    }

    public void DrawRectangle(Rect2D rect, StrokeStyle stroke)
        => _g.DrawRectangle((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height, Col(stroke.Color), W(stroke), Style(stroke));

    public void FillRectangle(Rect2D rect, Color color)
        => _g.FillRectangle((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height, Col(color));

    public void DrawCircle(Point2D center, double radius, StrokeStyle stroke)
        => _g.DrawCircle(V(center), (float)radius, Col(stroke.Color), W(stroke), Style(stroke));

    public void FillCircle(Point2D center, double radius, Color color)
        => _g.FillCircle(V(center), (float)radius, Col(color));

    public void DrawEllipse(Point2D center, double radiusX, double radiusY, StrokeStyle stroke)
        => _g.DrawEllipse(V(center), (float)radiusX, (float)radiusY, Col(stroke.Color), W(stroke), Style(stroke));

    public void DrawArc(Point2D center, double radius, double startDeg, double sweepDeg, StrokeStyle stroke)
    {
        double a0 = startDeg * Math.PI / 180.0, sweep = sweepDeg * Math.PI / 180.0;
        var start = new Vector2((float)(center.X + radius * Math.Cos(a0)), (float)(center.Y + radius * Math.Sin(a0)));
        using var pb = new CanvasPathBuilder(_g.Device);
        pb.BeginFigure(start);
        pb.AddArc(new Vector2((float)center.X, (float)center.Y), (float)radius, (float)radius, (float)a0, (float)sweep);
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geo = CanvasGeometry.CreatePath(pb);
        _g.DrawGeometry(geo, Col(stroke.Color), W(stroke), Style(stroke));
    }

    public void DrawText(string text, Point2D position, TextStyle style)
    {
        using var layout = Layout(text, style);
        var b = layout.LayoutBounds;
        double x = style.HAlign switch { HAlign.Center => position.X - b.Width / 2, HAlign.Right => position.X - b.Width, _ => position.X };
        // VAlign は描画原点(レイアウト上端)を基準に position.Y へ合わせる。
        // Baseline はフォントのベースライン、Bottom は下辺。PDF/SVG と同一意味で揃える。
        double y = style.VAlign switch
        {
            VAlign.Middle => position.Y - b.Height / 2,
            VAlign.Bottom => position.Y - b.Height,
            VAlign.Baseline => position.Y - (layout.LineMetrics.Length > 0 ? layout.LineMetrics[0].Baseline : b.Height),
            _ => position.Y,   // Top
        };
        _g.DrawTextLayout(layout, new Vector2((float)x, (float)y), Col(style.Color));
    }

    public Size2D MeasureText(string text, TextStyle style)
    {
        using var layout = Layout(text, style);
        return new Size2D(layout.LayoutBounds.Width, layout.LayoutBounds.Height);
    }

    public void DrawImage(string filePath, Rect2D bounds)
    {
        if (_bitmapCache.TryGetValue(filePath, out var bmp))
            _g.DrawImage(bmp, new Windows.Foundation.Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    /// <summary>画像ファイルを事前ロードしてキャッシュに格納する。画像追加時・ドキュメント読込時に呼ぶ。</summary>
    public static async Task PreloadImageAsync(CanvasDevice device, string filePath)
    {
        if (_bitmapCache.ContainsKey(filePath)) return;
        var bmp = await CanvasBitmap.LoadAsync(device, filePath);
        _bitmapCache[filePath] = bmp;
    }

    /// <summary>画像の px サイズを取得する（事前ロード後に呼ぶ）。未ロードなら null。</summary>
    public static Size2D? GetImagePixelSize(string filePath)
        => _bitmapCache.TryGetValue(filePath, out var bmp) ? new Size2D(bmp.SizeInPixels.Width, bmp.SizeInPixels.Height) : null;

    /// <summary>キャッシュ済み画像を破棄する（同じファイルを参照する画像が他になくなったとき呼ぶ）。</summary>
    public static void EvictImage(string filePath)
    {
        if (_bitmapCache.Remove(filePath, out var bmp)) bmp.Dispose();
    }

    /// <summary>全キャッシュを破棄する（新規作成・別ファイルを開くとき呼ぶ）。</summary>
    public static void ClearImageCache()
    {
        foreach (var bmp in _bitmapCache.Values) bmp.Dispose();
        _bitmapCache.Clear();
    }

    private CanvasTextLayout Layout(string text, TextStyle style)
    {
        using var fmt = new CanvasTextFormat
        {
            FontFamily = style.FontFamily,
            FontSize = (float)style.FontSizeMm,
            FontWeight = style.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = style.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            WordWrapping = CanvasWordWrapping.NoWrap,   // 幅0指定での1文字ずつ縦折り返しを防ぐ（横書き固定）
        };
        return new CanvasTextLayout(_g.Device, text, fmt, 0f, 0f);
    }

    private static Vector2 V(Point2D p) => new((float)p.X, (float)p.Y);
    private static WinColor Col(Color c) => WinColor.FromArgb(c.A, c.R, c.G, c.B);
    private static float W(StrokeStyle s) => (float)Math.Max(s.Width, DrawingTheme.MinStrokeWidthMm);

    private static CanvasStrokeStyle Style(StrokeStyle s)
    {
        var cs = new CanvasStrokeStyle { StartCap = Cap(s.Cap), EndCap = Cap(s.Cap), DashCap = Cap(s.Cap) };
        // 破線は線幅倍数のカスタムパターンで PDF/SVG と同一比率に揃える（CanvasDashStyle のネイティブ値はライブラリ依存のため使わない）。
        switch (s.Style)
        {
            case LineStyle.Dashed:
                cs.CustomDashStyle = new[] { (float)DrawingTheme.DashOn, (float)DrawingTheme.DashOff };
                break;
            case LineStyle.Dotted:
                cs.CustomDashStyle = new[] { (float)DrawingTheme.DotOn, (float)DrawingTheme.DotOff };
                break;
            default:
                cs.DashStyle = CanvasDashStyle.Solid;
                break;
        }
        return cs;
    }

    private static CanvasCapStyle Cap(LineCap c) => c switch
    {
        LineCap.Round => CanvasCapStyle.Round,
        LineCap.Square => CanvasCapStyle.Square,
        _ => CanvasCapStyle.Flat,
    };
}
