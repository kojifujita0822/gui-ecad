using GuiEcad.Model;

namespace GuiEcad.Rendering;

// すべてワールド座標 = mm(double)。各バックエンドが mm→デバイス単位へ変換する。
public readonly record struct Point2D(double X, double Y);
public readonly record struct Size2D(double Width, double Height);
public readonly record struct Rect2D(double X, double Y, double Width, double Height);
public readonly record struct Color(byte A, byte R, byte G, byte B);

public enum LineCap { Butt, Round, Square }
public enum HAlign { Left, Center, Right }
public enum VAlign { Top, Middle, Baseline, Bottom }

public readonly record struct StrokeStyle(
    Color Color, double Width /*mm*/, LineStyle Style = LineStyle.Solid, LineCap Cap = LineCap.Butt);

public readonly record struct TextStyle(
    string FontFamily, double FontSizeMm, Color Color,
    bool Bold = false, bool Italic = false,
    HAlign HAlign = HAlign.Left, VAlign VAlign = VAlign.Baseline);

/// <summary>
/// 描画専用の抽象。画面(Win2D)とPDF(PDFsharp)を同一コードで両対応するための中核。
/// ヒットテスト・選択・状態管理は持たない（グリッド座標上でデータモデルに対して行う）。
/// </summary>
public interface IRenderer
{
    void PushTransform(double translateX, double translateY, double scale = 1.0);
    void PopTransform();
    void PushClip(Rect2D rect);
    void PopClip();

    void DrawLine(Point2D a, Point2D b, StrokeStyle stroke);
    void DrawPolyline(ReadOnlySpan<Point2D> points, StrokeStyle stroke);
    void DrawRectangle(Rect2D rect, StrokeStyle stroke);
    void FillRectangle(Rect2D rect, Color color);
    void DrawCircle(Point2D center, double radius, StrokeStyle stroke);   // コイル・ランプ外形
    void FillCircle(Point2D center, double radius, Color color);          // 分岐ドット ●
    void DrawEllipse(Point2D center, double radiusX, double radiusY, StrokeStyle stroke);
    void DrawArc(Point2D center, double radius, double startDeg, double sweepDeg, StrokeStyle stroke);
    void DrawText(string text, Point2D position, TextStyle style);        // 配置は HAlign/VAlign 基準
    Size2D MeasureText(string text, TextStyle style);                    // レイアウト用計測
}

/// <summary>複数ページ出力（主にPDF）用の上位抽象。画面は CanvasControl 側で IRenderer を直接生成する。</summary>
public interface IRenderSurface : IDisposable
{
    IRenderer BeginPage(Size2D pageSizeMm);
    void EndPage();
}
