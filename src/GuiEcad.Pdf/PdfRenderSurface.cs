using GuiEcad.Model;
using GuiEcad.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace GuiEcad.Pdf;

/// <summary>PDFsharp による <see cref="IRenderSurface"/> 実装。ベクターPDFを出力する。</summary>
public sealed class PdfRenderSurface : IRenderSurface
{
    private readonly PdfDocument _doc = new();
    private readonly string _path;
    private PdfRenderer? _current;

    static PdfRenderSurface()
    {
        // PDFsharp 6 はフォントリゾルバ必須。Windows フォントを読む（日本語フォント対応は別途）。
        GlobalFontSettings.FontResolver ??= new WindowsFontResolver();
    }

    public PdfRenderSurface(string path) => _path = path;

    public IRenderer BeginPage(Size2D pageSizeMm)
    {
        var page = _doc.AddPage();
        page.Width = XUnit.FromMillimeter(pageSizeMm.Width);
        page.Height = XUnit.FromMillimeter(pageSizeMm.Height);
        var gfx = XGraphics.FromPdfPage(page);   // 既定単位=ポイント。mm は係数で変換。
        _current = new PdfRenderer(gfx);
        return _current;
    }

    public void EndPage()
    {
        _current?.Dispose();
        _current = null;
    }

    public void Dispose()
    {
        EndPage();
        _doc.Save(_path);
        _doc.Dispose();
    }
}

/// <summary>
/// Windows フォントフォルダからフォントを読むリゾルバ。
/// familyName を元に以下の順でフォントを探す：
///   Gothic 系名称（"gothic"/"ゴシック"含む）→ Yu Gothic（.ttc）→ Meiryo/MS Gothic → 明朝 → Arial
///   それ以外 → Yu Mincho（日本語・単一ttf）→ Arial にフォールバック。
/// 注: PDFsharp 6.1 以降は TrueType Collection（.ttc）に対応するため、Yu Gothic 等の
///     .ttc を直接埋め込める（コレクション先頭フォントを使用）。
/// </summary>
internal sealed class WindowsFontResolver : IFontResolver
{
    private static readonly string Dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    // 明朝系（日本語・単一.ttf）
    private static readonly string[] MinchoRegular = { "yumin.ttf", "arial.ttf" };
    private static readonly string[] MinchoBold = { "yumindb.ttf", "arialbd.ttf" };

    // ゴシック系（Yu Gothic .ttc を優先。なければ Meiryo / MS Gothic、最後に明朝/Arial）
    private static readonly string[] GothicRegular = { "YuGothR.ttc", "meiryo.ttc", "msgothic.ttc", "yumin.ttf", "arial.ttf" };
    private static readonly string[] GothicBold = { "YuGothB.ttc", "meiryob.ttc", "msgothic.ttc", "yumindb.ttf", "arialbd.ttf" };

    private static bool IsGothicFamily(string name) =>
        name.Contains("Gothic", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ゴシック", StringComparison.Ordinal) ||
        name.Contains("gothic", StringComparison.OrdinalIgnoreCase);

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        bool gothic = IsGothicFamily(familyName);
        string key = (gothic ? "g" : "m") + (isBold ? "b" : "r");
        return new FontResolverInfo(key);
    }

    public byte[]? GetFont(string faceName)
    {
        var candidates = faceName switch
        {
            "gb" => GothicBold,
            "gr" => GothicRegular,
            "mb" => MinchoBold,
            _    => MinchoRegular,
        };
        foreach (var f in candidates)
        {
            string path = Path.Combine(Dir, f);
            if (File.Exists(path)) return ExtractSingleFont(File.ReadAllBytes(path));
        }
        return null;
    }

    /// <summary>
    /// TrueType Collection（.ttc）の場合は先頭フォントを単一 sfnt として抽出して返す。
    /// PDFsharp のカスタムフォントリゾルバは TTC を直接扱えない（'ttcf' ヘッダで例外）ため、
    /// テーブルを再配置した独立 TTF へ組み直す。単一フォントならそのまま返す。
    /// </summary>
    private static byte[] ExtractSingleFont(byte[] data)
    {
        if (data.Length < 16 ||
            data[0] != (byte)'t' || data[1] != (byte)'t' || data[2] != (byte)'c' || data[3] != (byte)'f')
            return data; // TTC ではない（単一フォント）

        uint ReadU32(int p) => ((uint)data[p] << 24) | ((uint)data[p + 1] << 16) | ((uint)data[p + 2] << 8) | data[p + 3];
        ushort ReadU16(int p) => (ushort)((data[p] << 8) | data[p + 1]);

        int tableDir = (int)ReadU32(12);          // 先頭フォント（index 0）の Offset Table 位置
        ushort numTables = ReadU16(tableDir + 4);

        // 各テーブルレコード（tag/checksum/offset/length）を収集
        var rec = new (uint tag, uint sum, uint off, uint len)[numTables];
        for (int i = 0; i < numTables; i++)
        {
            int rp = tableDir + 12 + i * 16;
            rec[i] = (ReadU32(rp), ReadU32(rp + 4), ReadU32(rp + 8), ReadU32(rp + 12));
        }

        int headerSize = 12 + numTables * 16;     // Offset Table(12) + TableRecord(16)×n
        var newOff = new uint[numTables];
        int cursor = headerSize;
        for (int i = 0; i < numTables; i++)
        {
            newOff[i] = (uint)cursor;
            cursor += (int)rec[i].len;
            cursor = (cursor + 3) & ~3;            // 4 バイト境界
        }

        void WU32(byte[] b, int p, uint v) { b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16); b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v; }

        using var ms = new MemoryStream(cursor);
        var hdr = new byte[12];
        Array.Copy(data, tableDir, hdr, 0, 12);   // sfnt version / numTables / searchRange / entrySelector / rangeShift
        ms.Write(hdr, 0, 12);

        var rb = new byte[16];
        for (int i = 0; i < numTables; i++)
        {
            WU32(rb, 0, rec[i].tag);
            WU32(rb, 4, rec[i].sum);
            WU32(rb, 8, newOff[i]);
            WU32(rb, 12, rec[i].len);
            ms.Write(rb, 0, 16);
        }
        for (int i = 0; i < numTables; i++)
        {
            while (ms.Length < newOff[i]) ms.WriteByte(0);
            ms.Write(data, (int)rec[i].off, (int)rec[i].len);
        }
        return ms.ToArray();
    }
}

/// <summary>1ページ分の <see cref="IRenderer"/>。mm→pt 変換して PDFsharp の XGraphics へ描く。</summary>
internal sealed class PdfRenderer : IRenderer, IDisposable
{
    private const double K = 72.0 / 25.4;   // mm → pt
    private readonly XGraphics _g;
    private readonly Stack<XGraphicsState> _states = new();

    public PdfRenderer(XGraphics g) => _g = g;

    public void Dispose() => _g.Dispose();

    public void PushTransform(double translateX, double translateY, double scale = 1.0)
    {
        _states.Push(_g.Save());
        _g.TranslateTransform(translateX * K, translateY * K);
        if (scale != 1.0) _g.ScaleTransform(scale);
    }

    public void PopTransform() { if (_states.Count > 0) _g.Restore(_states.Pop()); }

    public void PushClip(Rect2D rect)
    {
        _states.Push(_g.Save());
        _g.IntersectClip(new XRect(rect.X * K, rect.Y * K, rect.Width * K, rect.Height * K));
    }

    public void PopClip() { if (_states.Count > 0) _g.Restore(_states.Pop()); }

    public void DrawLine(Point2D a, Point2D b, StrokeStyle stroke)
        => _g.DrawLine(Pen(stroke), a.X * K, a.Y * K, b.X * K, b.Y * K);

    public void DrawPolyline(ReadOnlySpan<Point2D> points, StrokeStyle stroke)
    {
        if (points.Length < 2) return;
        var pts = new XPoint[points.Length];
        for (int i = 0; i < points.Length; i++) pts[i] = new XPoint(points[i].X * K, points[i].Y * K);
        _g.DrawLines(Pen(stroke), pts);
    }

    public void DrawRectangle(Rect2D rect, StrokeStyle stroke)
        => _g.DrawRectangle(Pen(stroke), rect.X * K, rect.Y * K, rect.Width * K, rect.Height * K);

    public void FillRectangle(Rect2D rect, Color color)
        => _g.DrawRectangle(Brush(color), rect.X * K, rect.Y * K, rect.Width * K, rect.Height * K);

    public void DrawCircle(Point2D center, double radius, StrokeStyle stroke)
        => _g.DrawEllipse(Pen(stroke), (center.X - radius) * K, (center.Y - radius) * K, 2 * radius * K, 2 * radius * K);

    public void FillCircle(Point2D center, double radius, Color color)
        => _g.DrawEllipse(Brush(color), (center.X - radius) * K, (center.Y - radius) * K, 2 * radius * K, 2 * radius * K);

    public void DrawEllipse(Point2D center, double radiusX, double radiusY, StrokeStyle stroke)
        => _g.DrawEllipse(Pen(stroke), (center.X - radiusX) * K, (center.Y - radiusY) * K, 2 * radiusX * K, 2 * radiusY * K);

    public void DrawArc(Point2D center, double radius, double startDeg, double sweepDeg, StrokeStyle stroke)
        => _g.DrawArc(Pen(stroke), (center.X - radius) * K, (center.Y - radius) * K, 2 * radius * K, 2 * radius * K, startDeg, sweepDeg);

    public void DrawText(string text, Point2D position, TextStyle style)
        => _g.DrawString(text, Font(style), Brush(style.Color), position.X * K, position.Y * K, Format(style));

    public Size2D MeasureText(string text, TextStyle style)
    {
        var sz = _g.MeasureString(text, Font(style));
        return new Size2D(sz.Width / K, sz.Height / K);
    }

    private static XPen Pen(StrokeStyle s)
    {
        var pen = new XPen(XColor.FromArgb(s.Color.A, s.Color.R, s.Color.G, s.Color.B), Math.Max(s.Width, DrawingTheme.MinStrokeWidthMm) * K)
        {
            LineCap = s.Cap switch { LineCap.Round => XLineCap.Round, LineCap.Square => XLineCap.Square, _ => XLineCap.Flat },
        };
        // 破線は線幅倍数のカスタムパターンで Win2D/SVG と同一比率に揃える（XDashStyle のネイティブ値は使わない）。
        switch (s.Style)
        {
            case LineStyle.Dashed: pen.DashPattern = new[] { DrawingTheme.DashOn, DrawingTheme.DashOff }; break;
            case LineStyle.Dotted: pen.DashPattern = new[] { DrawingTheme.DotOn, DrawingTheme.DotOff }; break;
        }
        return pen;
    }

    private static XSolidBrush Brush(Color c) => new(XColor.FromArgb(c.A, c.R, c.G, c.B));

    private static XFont Font(TextStyle s)
    {
        var fs = (s.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (s.Italic ? XFontStyleEx.Italic : 0);
        return new XFont(s.FontFamily, s.FontSizeMm * K, fs);
    }

    private static XStringFormat Format(TextStyle s) => new()
    {
        Alignment = s.HAlign switch { HAlign.Center => XStringAlignment.Center, HAlign.Right => XStringAlignment.Far, _ => XStringAlignment.Near },
        LineAlignment = s.VAlign switch
        {
            VAlign.Top => XLineAlignment.Near,
            VAlign.Middle => XLineAlignment.Center,
            VAlign.Baseline => XLineAlignment.BaseLine,
            VAlign.Bottom => XLineAlignment.Far,
            _ => XLineAlignment.Far,
        },
    };
}
