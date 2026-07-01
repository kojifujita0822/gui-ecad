using GuiEcad.Model;
using GuiEcad.Pdf;
using GuiEcad.Rendering;
using GuiEcad.Simulation;
using Xunit;
using static GuiEcad.Tests.TestHelper;

namespace GuiEcad.Tests;

public class RenderingTests
{
    // 検証用サンプル図（self-hold＋分岐＋各種記号）を作る
    private static Sheet SampleSheet()
    {
        var sheet = new Sheet
        {
            Grid = new GridSpec { Columns = 8, Rows = 8 },
            Bus = new BusConfig { LeftName = "R200", RightName = "S200" },
        };
        // 行0: L —[ST]—[SP/]— (CR1) — R
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "ST"));
        sheet.Elements.Add(El(ElementKind.PushButtonNC, 0, 2, "SP"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 7, "CR1"));
        // 行1: 自己保持接点 CR1（列1で母線側へ縦コネクタ合流）
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "CR1"));
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 1 });
        // 行3: L —[CR1]— (PL) — R （表示灯）
        sheet.Elements.Add(El(ElementKind.ContactNO, 3, 0, "CR1"));
        sheet.Elements.Add(El(ElementKind.Lamp, 3, 7, "PL"));
        // 行5: 端子台つき出力 L —[CR1]— ◎ — (SOL) — ◎ — R
        sheet.Elements.Add(El(ElementKind.ContactNO, 5, 0, "CR1"));
        sheet.Elements.Add(El(ElementKind.Terminal, 5, 3, "TB1"));
        sheet.Elements.Add(El(ElementKind.Coil, 5, 5, "SOL"));
        sheet.Elements.Add(El(ElementKind.Terminal, 5, 7, "TB2"));
        return sheet;
    }

    // IRenderer のテストダブル: 線分のみ記録する。
    private sealed class RecordingRenderer : Rendering.IRenderer
    {
        public readonly List<(Point2D A, Point2D B)> Lines = new();
        public void DrawLine(Point2D a, Point2D b, StrokeStyle s) => Lines.Add((a, b));
        public void PushTransform(double tx, double ty, double scale = 1.0) { }
        public void PopTransform() { }
        public void PushClip(Rect2D r) { }
        public void PopClip() { }
        public void DrawPolyline(ReadOnlySpan<Point2D> p, StrokeStyle s) { }
        public void DrawRectangle(Rect2D r, StrokeStyle s) { }
        public void FillRectangle(Rect2D r, Color c) { }
        public void DrawCircle(Point2D c, double r, StrokeStyle s) { }
        public void FillCircle(Point2D c, double r, Color col) { }
        public void DrawEllipse(Point2D c, double rx, double ry, StrokeStyle s) { }
        public void DrawArc(Point2D c, double r, double sd, double sw, StrokeStyle s) { }
        public void DrawText(string t, Point2D p, TextStyle s) { }
        public Size2D MeasureText(string t, TextStyle s) => new(t.Length * s.FontSizeMm * 0.5, s.FontSizeMm);
        public readonly List<string> DrawnImages = new();
        public void DrawImage(string filePath, Rect2D bounds) => DrawnImages.Add(filePath);
    }

    // 挿入画像: トレース用下絵は画面表示（IncludeTracingImages=true 既定）でのみ描画され、
    // PDF出力（IncludeTracingImages=false）では除外される。恒久貼付はどちらでも描画される。
    [Fact]
    public void DrawImages_TracingOnlyExcludedWhenIncludeTracingImagesFalse()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 8, Rows = 8 } };
        sheet.Images.Add(new ImageInsert { FilePath = "trace.png", XMm = 0, YMm = 0, WidthMm = 50, HeightMm = 50, IsTracingOnly = true });
        sheet.Images.Add(new ImageInsert { FilePath = "permanent.png", XMm = 0, YMm = 0, WidthMm = 50, HeightMm = 50, IsTracingOnly = false });

        var recScreen = new RecordingRenderer();
        new DiagramRenderer(options: new RenderOptions { IncludeTracingImages = true }).Render(recScreen, sheet);
        Assert.Contains("trace.png", recScreen.DrawnImages);
        Assert.Contains("permanent.png", recScreen.DrawnImages);

        var recPdf = new RecordingRenderer();
        new DiagramRenderer(options: new RenderOptions { IncludeTracingImages = false }).Render(recPdf, sheet);
        Assert.DoesNotContain("trace.png", recPdf.DrawnImages);
        Assert.Contains("permanent.png", recPdf.DrawnImages);
    }

    // 末尾要素の横線が、母線延長区間にある縦コネクタ(分岐点)で終端し、母線まで伸びないこと。
    [Fact]
    public void RungWire_TerminatesAtConnector_NotBus()
    {
        var sheet = new Sheet
        {
            Grid = new GridSpec { Columns = 11, Rows = 8 },
            Bus = new BusConfig { LeftName = "R", RightName = "S" },
        };
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 3, "A"));   // 縦コネクタ上側の相手
        sheet.Elements.Add(El(ElementKind.ContactNO, 3, 0, "G"));   // 下行・末尾要素（右端境界=1）
        sheet.Connectors.Add(new VerticalConnector { Column = 4, TopRow = 1, BottomRow = 3 });

        var dr = new DiagramRenderer();
        var rec = new RecordingRenderer();
        dr.Render(rec, sheet);

        var g = dr.Geometry;
        double yRow3 = g.YRow(3), xConn = g.X(4), x1 = g.X(1);
        double rightBus = g.X(11) + g.CellMm * 0.5;

        var horiz = rec.Lines
            .Where(l => Math.Abs(l.A.Y - l.B.Y) < 1e-6 && Math.Abs(l.A.Y - yRow3) < 1e-6)
            .ToList();
        Assert.NotEmpty(horiz);

        double maxX = horiz.Max(l => Math.Max(l.A.X, l.B.X));
        Assert.True(maxX <= xConn + 1e-6, $"母線({rightBus})まで伸びず分岐点({xConn})で終端すべき。実際の右端={maxX}");
        Assert.True(maxX > x1 + 1e-6, "横線が要素右端から分岐点まで描かれていること");
    }

    // 延長区間に縦コネクタが無い末尾要素は従来どおり母線まで横線が伸びること。
    [Fact]
    public void RungWire_ExtendsToBus_WhenNoConnector()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 6, Rows = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));

        var dr = new DiagramRenderer();
        var rec = new RecordingRenderer();
        dr.Render(rec, sheet);

        var g = dr.Geometry;
        double yRow0 = g.YRow(0), rightBus = g.X(6) + g.CellMm * 0.5;
        double maxX = rec.Lines
            .Where(l => Math.Abs(l.A.Y - l.B.Y) < 1e-6 && Math.Abs(l.A.Y - yRow0) < 1e-6)
            .Max(l => Math.Max(l.A.X, l.B.X));
        Assert.True(Math.Abs(maxX - rightBus) < 1e-6, $"母線({rightBus})まで延ばすべき。実際={maxX}");
    }

    [Fact]
    public void Generates_SamplePdf()
    {
        var sheet = SampleSheet();
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp");
        dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(dir);

        Render(sheet, Path.Combine(dir, "render_plain.pdf"), connectivity: false);
        Render(sheet, Path.Combine(dir, "render_check.pdf"), connectivity: true);

        Assert.True(File.Exists(Path.Combine(dir, "render_plain.pdf")));
        Assert.True(File.Exists(Path.Combine(dir, "render_check.pdf")));
    }

    [Fact]
    public void Generates_SymbolSheet()
    {
        // 全種別を1行ずつ配置して記号を一覧出力（目視検証用）
        var kinds = new (ElementKind Kind, string Label)[]
        {
            (ElementKind.ContactNO, "a接点"), (ElementKind.ContactNC, "b接点"),
            (ElementKind.PushButtonNO, "PB-NO"), (ElementKind.PushButtonNC, "PB-NC"),
            (ElementKind.TimerContactNO, "TMR-a"), (ElementKind.TimerContactNC, "TMR-b"),
            (ElementKind.EmergencyStop, "非常停止"), (ElementKind.ThermalOverload, "OL"),
            (ElementKind.SelectSwitch, "SS"), (ElementKind.Terminal, "端子"),
            (ElementKind.Coil, "コイル"), (ElementKind.Lamp, "表示灯"),
            (ElementKind.Motor, "モータ"),
        };
        var sheet = new Sheet { Grid = new GridSpec { Columns = 7, Rows = kinds.Length } };
        for (int i = 0; i < kinds.Length; i++)
        {
            int w = kinds[i].Kind == ElementKind.Motor ? 3 : 1;
            sheet.Elements.Add(El(kinds[i].Kind, i, 3, kinds[i].Label, w));
        }

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        Render(sheet, Path.Combine(dir, "symbols.pdf"), connectivity: false);
        Assert.True(File.Exists(Path.Combine(dir, "symbols.pdf")));
    }

    [Fact]
    public void Generates_TestModePdf()
    {
        // self-hold サンプルで ST 押下→通電状態を描画（通電ハイライト確認）
        var sheet = SampleSheet();
        var ts = new GuiEcad.Simulation.TestSession(sheet);
        ts.SetInput("ST", true);   // CR1 励磁 → 自己保持・出力通電

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        var dr = new DiagramRenderer();
        using var surface = new PdfRenderSurface(Path.Combine(dir, "test_energized.pdf"));
        var r = surface.BeginPage(dr.PageSize(sheet));
        dr.Render(r, sheet, null, ts.State);
        surface.EndPage();

        Assert.True(File.Exists(Path.Combine(dir, "test_energized.pdf")));
    }

    [Fact]
    public void Generates_GroupFramePdf()
    {
        // 設置場所グルーピング枠（破線矩形＋ラベル）の描画確認
        var sheet = SampleSheet();
        sheet.Frames.Add(new GroupFrame { Label = "中継ボックス", TopLeft = new GridPos(0, 0), Width = 4, Height = 2 });
        sheet.Frames.Add(new GroupFrame { Label = "MR盤", TopLeft = new GridPos(3, 0), Width = 8, Height = 3 });

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        var dr = new DiagramRenderer();
        using var surface = new PdfRenderSurface(Path.Combine(dir, "group_frame.pdf"));
        var r = surface.BeginPage(dr.PageSize(sheet));
        dr.Render(r, sheet);
        surface.EndPage();

        Assert.True(File.Exists(Path.Combine(dir, "group_frame.pdf")));
    }

    [Fact]
    public void Generates_CrossRefTablePdf()
    {
        // SampleSheet の自己保持回路でクロスリファレンス一覧表を描画
        var sheet = SampleSheet();
        sheet.PageNumber = 1;
        var doc = new LadderDocument();
        doc.Sheets.Add(sheet);
        CircuitNumberer.Number(doc);

        var xref = CrossReferenceBuilder.Build(doc);

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        var dr = new DiagramRenderer();
        using var surface = new PdfRenderSurface(Path.Combine(dir, "crossref_table.pdf"));
        var r = surface.BeginPage(dr.PageSize(sheet, xref));
        dr.Render(r, sheet, null, null, xref);
        surface.EndPage();

        Assert.True(File.Exists(Path.Combine(dir, "crossref_table.pdf")));
    }

    [Fact]
    public void Generates_TitleBlockPdf()
    {
        var sheet = SampleSheet();
        sheet.PageNumber = 1;
        var doc = new LadderDocument
        {
            Info = new DocumentInfo
            {
                CompanyName = "テスト電機株式会社",
                Title = "制御盤シーケンス回路図", DrawingNo = "E-001",
                Customer = "株式会社テスト", Designer = "山田太郎",
                Drafter = "鈴木一郎", Checker = "田中次郎", Date = "2026-06-19",
            },
        };
        doc.Sheets.Add(sheet);
        CircuitNumberer.Number(doc);
        var xref = CrossReferenceBuilder.Build(doc);

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        var dr = new DiagramRenderer();
        var info = doc.Info;
        using var surface = new PdfRenderSurface(Path.Combine(dir, "titleblock_test.pdf"));
        var r = surface.BeginPage(dr.PageSize(sheet, xref, info));
        dr.Render(r, sheet, null, null, xref, info);
        surface.EndPage();

        Assert.True(File.Exists(Path.Combine(dir, "titleblock_test.pdf")));
    }

    [Fact]
    public void TitleBlock_WithBorder_RendersCompanyRowWithoutException()
    {
        // 図面枠あり（A4縦・右下固定配置）でも社名行を含む3行構成の表題欄が例外なく描画できること。
        var sheet = SampleSheet();
        sheet.PageNumber = 1;
        var doc = new LadderDocument
        {
            Info = new DocumentInfo { CompanyName = "テスト電機株式会社", Title = "制御盤" },
        };
        doc.Sheets.Add(sheet);

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "titleblock_border_test.pdf");
        File.Delete(path);   // 前回実行の残骸で誤検知しないよう、本テストの検証対象は必ず新規生成させる
        var dr = new DiagramRenderer();
        Exception? ex;
        using (var surface = new PdfRenderSurface(path))
        {
            var r = surface.BeginPage(dr.PageSize(sheet, null, doc.Info, enableBorder: true));
            ex = Record.Exception(() =>
                dr.Render(r, sheet, null, null, xref: null, info: doc.Info, enableBorder: true));
            surface.EndPage();
        }   // Dispose() で実際にPDFがディスクへ保存される

        Assert.Null(ex);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void PaperSize_A3_RendersAtA3DimensionsWithoutException()
    {
        // 用紙サイズ A3 でも例外なく描画でき、PageSize が A3 縦(297×420mm)を返すこと。
        var sheet = SampleSheet();
        sheet.PageNumber = 1;
        var doc = new LadderDocument
        {
            Info = new DocumentInfo { CompanyName = "テスト電機株式会社", Title = "制御盤" },
            Settings = new DocumentSettings { PaperSize = PaperSize.A3 },
        };
        doc.Sheets.Add(sheet);

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "a3_test.pdf");
        File.Delete(path);
        var dr = new DiagramRenderer(options: new RenderOptions { PaperSize = doc.Settings.PaperSize });

        var pageSize = dr.PageSize(sheet, null, doc.Info, enableBorder: true);
        Assert.Equal(297.0, pageSize.Width);
        Assert.Equal(420.0, pageSize.Height);

        Exception? ex;
        using (var surface = new PdfRenderSurface(path))
        {
            var r = surface.BeginPage(pageSize);
            ex = Record.Exception(() =>
                dr.Render(r, sheet, null, null, xref: null, info: doc.Info, enableBorder: true));
            surface.EndPage();
        }

        Assert.Null(ex);
        Assert.True(File.Exists(path));
    }

    private static void Render(Sheet sheet, string path, bool connectivity)
    {
        var dr = new DiagramRenderer(DrawingTheme.Default, new RenderOptions { ConnectivityCheck = connectivity });
        using var surface = new PdfRenderSurface(path);
        var r = surface.BeginPage(dr.PageSize(sheet));
        dr.Render(r, sheet);
        surface.EndPage();
    }

    // G-1: Comment フィールド付き要素を含む図面の描画 → 例外なく PDF 生成
    [Fact]
    public void Generates_ElementWithComment_NoException()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        var elem = new ElementInstance
        {
            Kind = ElementKind.ContactNO,
            Pos = new GridPos(0, 0),
            DeviceName = "CR1",
            Comment = "起動",
        };
        sheet.Elements.Add(elem);
        sheet.Elements.Add(new ElementInstance
        {
            Kind = ElementKind.Coil, Pos = new GridPos(0, 3), DeviceName = "OUT"
        });

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        var dr = new DiagramRenderer();
        using (var surface = new PdfRenderSurface(Path.Combine(dir, "comment_elem.pdf")))
        {
            var r = surface.BeginPage(dr.PageSize(sheet));
            dr.Render(r, sheet);
            surface.EndPage();
        }
        Assert.True(File.Exists(Path.Combine(dir, "comment_elem.pdf")));
    }

    // G-2: SelectSwitch（CellWidth=3）要素の描画 → 例外なく PDF 生成
    [Fact]
    public void Generates_SelectSwitch_NoException()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 6 } };
        var ss = new ElementInstance
        {
            Kind = ElementKind.SelectSwitch,
            Pos = new GridPos(0, 0),
            CellWidth = 3,
            DeviceName = "SS1",
        };
        ss.Params["Position"] = "1";
        sheet.Elements.Add(ss);
        sheet.Elements.Add(new ElementInstance
        {
            Kind = ElementKind.Coil, Pos = new GridPos(0, 5), DeviceName = "OUT"
        });

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        var dr = new DiagramRenderer();
        using (var surface = new PdfRenderSurface(Path.Combine(dir, "select_switch.pdf")))
        {
            var r = surface.BeginPage(dr.PageSize(sheet));
            dr.Render(r, sheet);
            surface.EndPage();
        }
        Assert.True(File.Exists(Path.Combine(dir, "select_switch.pdf")));
    }

    // G-3: 重なり合う GroupFrame 2 枚 → 例外なく PDF 生成
    [Fact]
    public void Generates_OverlappingGroupFrames_NoException()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 8, Rows = 6 } };
        sheet.Elements.Add(new ElementInstance
        {
            Kind = ElementKind.ContactNO, Pos = new GridPos(0, 0), DeviceName = "A"
        });
        sheet.Elements.Add(new ElementInstance
        {
            Kind = ElementKind.Coil, Pos = new GridPos(0, 7), DeviceName = "C"
        });
        sheet.Frames.Add(new GroupFrame { Label = "盤A", TopLeft = new GridPos(0, 0), Width = 5, Height = 4 });
        sheet.Frames.Add(new GroupFrame { Label = "盤B", TopLeft = new GridPos(2, 2), Width = 6, Height = 3 }); // 重なり

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        var dr = new DiagramRenderer();
        using (var surface = new PdfRenderSurface(Path.Combine(dir, "overlapping_frames.pdf")))
        {
            var r = surface.BeginPage(dr.PageSize(sheet));
            dr.Render(r, sheet);
            surface.EndPage();
        }
        Assert.True(File.Exists(Path.Combine(dir, "overlapping_frames.pdf")));
    }
}
