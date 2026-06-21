using GuiEcad.Model;
using GuiEcad.Simulation;

namespace GuiEcad.Rendering;

/// <summary>描画オプション。</summary>
public sealed class RenderOptions
{
    public double CellMm { get; init; } = 9.0;
    public double MarginMm { get; init; } = 15.0;
    public bool ShowDeviceNames { get; init; } = true;
    public bool ShowWireNumbers { get; init; } = true;
    /// <summary>左母線の左側に行番号（1 始まり）を表示する。</summary>
    public bool ShowRowNumbers { get; init; } = true;
    /// <summary>接続検査モード: 接続済み配線を青、未結線を黒で表示（docs/rendering.md）。</summary>
    public bool ConnectivityCheck { get; init; }
}

/// <summary>
/// ドキュメント（幾何）を走査して <see cref="IRenderer"/> を呼ぶ上位描画器。画面/PDFで共用する。
/// 座標は mm。グリッド座標 (Row, 列境界) → mm へ変換して記号・配線・線番を描く。
/// </summary>
public sealed class DiagramRenderer
{
    private readonly DrawingTheme _theme;
    private readonly RenderOptions _opt;
    private readonly GridGeometry _geo;
    private PartLibrary? _lib;

    public DiagramRenderer(DrawingTheme? theme = null, RenderOptions? options = null)
    {
        _theme = theme ?? DrawingTheme.Default;
        _opt = options ?? new RenderOptions();
        _geo = new GridGeometry(_opt.CellMm, _opt.MarginMm);
    }

    /// <summary>描画に用いるグリッド幾何（ヒットテスト等で共有）。</summary>
    public GridGeometry Geometry => _geo;

    private double Cell => _geo.CellMm;
    private double BusPad => Cell * 0.5;   // 母線と端の要素列の間に設ける余白（mm）
    private double X(int boundary) => _geo.X(boundary);
    private double X(double boundary) => _geo.X(boundary);   // 0.5 刻み境界（縦コネクタ）用
    private double YRow(int row) => _geo.YRow(row);
    private double LeftBusX => X(0) - BusPad;
    private double RightBusX(int columns) => X(columns) + BusPad;

    private const double TitleBlockH = 14.0;   // 表題欄の高さ (mm)
    private const double RevRowH     = 7.0;    // 改定欄 データ行の高さ (mm)
    private const double RevHdrH     = 5.0;    // 改定欄 ヘッダ行の高さ (mm)
    private const double A4W         = 297.0;  // A4横 幅 (mm)
    private const double A4H         = 210.0;  // A4横 高さ (mm)

    private double RevisionBlockH(DocumentInfo info)
        => info.Revisions.Count == 0 ? 0 : RevHdrH + info.Revisions.Count * RevRowH;

    /// <summary>描画に必要なページサイズ(mm)。enableBorder=true のとき A4横固定。</summary>
    public Size2D PageSize(Sheet sheet, CrossReference? xref = null, DocumentInfo? info = null,
                           bool enableBorder = false)
    {
        if (enableBorder) return new Size2D(A4W, A4H);
        int maxRow = 0;
        foreach (var e in sheet.Elements) maxRow = Math.Max(maxRow, e.Pos.Row);
        double w = RightBusX(sheet.Grid.Columns) + _opt.MarginMm;
        double diagramH = _opt.MarginMm + (maxRow + 1) * Cell + _opt.MarginMm;
        double tableH = xref is not null ? CalcTableHeight(xref) : 0.0;
        double revH = info is not null ? RevisionBlockH(info) : 0.0;
        double titleH = info is not null ? TitleBlockH : 0.0;
        return new Size2D(w, diagramH + tableH + revH + titleH);
    }

    /// <summary>
    /// 図面を描画する。<paramref name="sim"/> を渡すとテストモード: 通電評価を行い、
    /// 通電配線・励磁要素を通電色でハイライトする（画面のテストモード用）。
    /// <paramref name="xref"/> を渡すと図面下部にクロスリファレンス一覧表を描画する。
    /// <paramref name="info"/> を渡すと最下部に表題欄を描画する。
    /// </summary>
    public void Render(IRenderer r, Sheet sheet, PartLibrary? library = null, SimState? sim = null,
                       CrossReference? xref = null, DocumentInfo? info = null,
                       int pageNumber = 1, int totalPages = 1, bool enableBorder = false)
    {
        _lib = library;
        var netlist = NetlistBuilder.Build(sheet, library);
        var report = _opt.ConnectivityCheck ? ConnectivityChecker.Check(netlist) : null;

        HashSet<int>? powered = null;
        Dictionary<string, bool>? energized = null;
        if (sim is not null)
        {
            var eval = new Evaluator(netlist).Evaluate(sim);
            powered = eval.PoweredNets;
            energized = eval.State.Energized;
        }

        // 要素 Id → (左net, 右net)
        var elemNet = new Dictionary<Guid, (int A, int B)>();
        foreach (var c in netlist.Components) elemNet[c.SourceElementId] = (c.NetA, c.NetB);

        int columns = sheet.Grid.Columns;
        int maxRow = 0;
        foreach (var e in sheet.Elements) maxRow = Math.Max(maxRow, e.Pos.Row);

        DrawRails(r, columns, maxRow);
        DrawBusLabels(r, sheet, columns);
        DrawRowNumbers(r, Math.Max(sheet.Grid.Rows, maxRow + 1));
        DrawRungWires(r, sheet, columns, elemNet, netlist, report, powered);
        DrawConnectors(r, sheet);
        DrawFrames(r, sheet);
        foreach (var e in sheet.Elements) DrawElement(r, e, energized, sim?.Inputs);
        DrawRungComments(r, sheet, columns);

        double contentBottom = _opt.MarginMm + (maxRow + 1) * Cell + _opt.MarginMm;
        if (xref is not null)
        {
            DrawCrossRefTable(r, columns, contentBottom, xref);
            contentBottom += CalcTableHeight(xref);
        }
        if (info is not null)
        {
            double revH = RevisionBlockH(info);
            if (revH > 0)
                DrawRevisionBlock(r, info, LeftBusX, RightBusX(columns), contentBottom);
            DrawTitleBlock(r, info, LeftBusX, RightBusX(columns), contentBottom + revH,
                           pageNumber, totalPages);
        }
        if (enableBorder)
            DrawBorder(r);
    }

    // 母線（左右の縦線）
    private void DrawRails(IRenderer r, int columns, int maxRow)
    {
        var s = _theme.Get(StrokeRole.BusRail);
        double yTop = _opt.MarginMm;
        double yBot = _opt.MarginMm + (maxRow + 1) * Cell;
        r.DrawLine(new(LeftBusX, yTop), new(LeftBusX, yBot), s);
        r.DrawLine(new(RightBusX(columns), yTop), new(RightBusX(columns), yBot), s);
    }

    // 母線名（各母線の上端）と母線間電圧（最上部に左右を結ぶ両矢印）
    private void DrawBusLabels(IRenderer r, Sheet sheet, int columns)
    {
        double lx = LeftBusX, rx = RightBusX(columns);
        // 第1ステップ（1行目の機器名ラベルは母線上端付近に出る）と離すため、
        // ヘッダーは上端寄りに置いて余白を確保する。
        double yLabel = Math.Max(2.5, _opt.MarginMm - 8.5);

        var nameStyle = _theme.Text(TextRole.DeviceName) with
        {
            FontSizeMm = 2.4, Bold = true, HAlign = HAlign.Center, VAlign = VAlign.Middle,
        };
        if (!string.IsNullOrEmpty(sheet.Bus.LeftName))
            r.DrawText(sheet.Bus.LeftName, new(lx, yLabel), nameStyle);
        if (!string.IsNullOrEmpty(sheet.Bus.RightName))
            r.DrawText(sheet.Bus.RightName, new(rx, yLabel), nameStyle);

        // 母線間電圧の両矢印（PowerLabel が設定されている場合のみ）
        string? voltage = sheet.Bus.PowerLabel;
        if (!string.IsNullOrWhiteSpace(voltage))
        {
            double gap = Cell * 0.9;             // 母線名を避ける左右の余白
            double x1 = lx + gap, x2 = rx - gap;
            if (x2 > x1)
            {
                DrawDoubleArrow(r, x1, x2, yLabel, _theme.Get(StrokeRole.Wire));
                var voltStyle = _theme.Text(TextRole.DeviceName) with
                {
                    FontSizeMm = 2.2, HAlign = HAlign.Center, VAlign = VAlign.Bottom,
                };
                r.DrawText(voltage, new((x1 + x2) / 2, yLabel - 0.8), voltStyle);
            }
        }
    }

    // 水平の両矢印（左右端に矢じり）。
    private static void DrawDoubleArrow(IRenderer r, double x1, double x2, double y, StrokeStyle s)
    {
        const double hx = 1.8, hy = 1.0;   // 矢じりの大きさ(mm)
        r.DrawLine(new(x1, y), new(x2, y), s);
        r.DrawLine(new(x1, y), new(x1 + hx, y - hy), s);   // 左矢じり
        r.DrawLine(new(x1, y), new(x1 + hx, y + hy), s);
        r.DrawLine(new(x2, y), new(x2 - hx, y - hy), s);   // 右矢じり
        r.DrawLine(new(x2, y), new(x2 - hx, y + hy), s);
    }

    // 行番号（左母線の左側に 1 始まりで表示）
    private void DrawRowNumbers(IRenderer r, int rowCount)
    {
        if (!_opt.ShowRowNumbers) return;
        var style = _theme.Text(TextRole.LineNumber) with
        {
            FontSizeMm = 2.0,
            HAlign = HAlign.Right,
            VAlign = VAlign.Middle,
        };
        double x = LeftBusX - 1.0;   // 左母線のさらに左（右寄せで左へ伸ばす）
        for (int row = 0; row < rowCount; row++)
            r.DrawText((row + 1).ToString(), new(x, YRow(row)), style);
    }

    // 各行の横配線（要素間・母線端）。接続検査時はネット色（青/黒）で描く。
    private void DrawRungWires(IRenderer r, Sheet sheet, int columns,
        Dictionary<Guid, (int A, int B)> elemNet, Netlist netlist, ConnectivityReport? report, HashSet<int>? powered)
    {
        var byRow = new Dictionary<int, List<ElementInstance>>();
        foreach (var e in sheet.Elements)
        {
            if (!byRow.TryGetValue(e.Pos.Row, out var list)) { list = new(); byRow[e.Pos.Row] = list; }
            list.Add(e);
        }

        foreach (var (row, list) in byRow)
        {
            list.Sort((a, b) => a.Pos.Column.CompareTo(b.Pos.Column));
            double y = YRow(row);

            for (int k = 0; k < list.Count; k++)
            {
                var e = list[k];
                int lb = LeftBoundary(e), rb = RightBoundary(e);
                int? leftNet = elemNet.TryGetValue(e.Id, out var n1) ? n1.A : null;
                int? rightNet = elemNet.TryGetValue(e.Id, out var n2) ? n2.B : null;

                // 左側: 先頭要素は左母線へ（パディング分外側）、それ以外は前要素との隙間。
                // 母線延長区間に縦コネクタ(分岐点)があればそこで終端し、母線へは延ばさない。
                if (k == 0)
                {
                    double? lt = LeftTerminator(sheet, row, lb);
                    if (lt is null)
                        DrawWire(r, LeftBusX, y, X(lb), y, leftNet, netlist, report, powered);
                    else if (lt.Value < lb)
                        DrawWire(r, X(lt.Value), y, X(lb), y, leftNet, netlist, report, powered);
                    // lt == lb: 要素端が分岐点 → 母線へ延ばさない
                }
                else
                {
                    int prevRb = RightBoundary(list[k - 1]);
                    DrawWire(r, X(prevRb), y, X(lb), y, leftNet, netlist, report, powered);
                }
                // 末尾要素は右母線へ。延長区間に縦コネクタ(分岐点)があればそこで終端する。
                if (k == list.Count - 1)
                {
                    double? rt = RightTerminator(sheet, row, rb, columns);
                    if (rt is null)
                        DrawWire(r, X(rb), y, RightBusX(columns), y, rightNet, netlist, report, powered);
                    else if (rt.Value > rb)
                        DrawWire(r, X(rb), y, X(rt.Value), y, rightNet, netlist, report, powered);
                    // rt == rb: 要素端が分岐点 → 母線へ延ばさない
                }
            }
        }
    }

    // 先頭要素の左母線延長区間 (0, lb] にある分岐点(縦コネクタ端点)のうち最も内側(右寄り)の境界。
    // 横線はそこで終端し母線へ延ばさない。境界0(母線)は対象外。なければ null（母線まで延ばす）。
    private static double? LeftTerminator(Sheet sheet, int row, int lb)
    {
        double? best = null;
        foreach (var c in sheet.Connectors)
            if ((c.TopRow == row || c.BottomRow == row) && c.Column > 0 && c.Column <= lb)
                best = best is null ? c.Column : Math.Max(best.Value, c.Column);
        return best;
    }

    // 末尾要素の右母線延長区間 [rb, columns) にある分岐点のうち最も内側(左寄り)の境界。
    // なければ null（母線まで延ばす）。rb 自身にある場合は rb を返し、横線は描かれない。
    private static double? RightTerminator(Sheet sheet, int row, int rb, int columns)
    {
        double? best = null;
        foreach (var c in sheet.Connectors)
            if ((c.TopRow == row || c.BottomRow == row) && c.Column >= rb && c.Column < columns)
                best = best is null ? c.Column : Math.Min(best.Value, c.Column);
        return best;
    }

    // 縦コネクタ（分岐）＋接合点ドット
    private void DrawConnectors(IRenderer r, Sheet sheet)
    {
        var wire = _theme.Get(StrokeRole.Wire);
        foreach (var c in sheet.Connectors)
        {
            double x = X(c.Column);
            r.DrawLine(new(x, YRow(c.TopRow)), new(x, YRow(c.BottomRow)), wire);
            // 接合点ドットは横配線が通過する合流点（上端）のみ。分岐枝の起点（下端）には付けない。
            r.FillCircle(new(x, YRow(c.TopRow)), Cell * 0.07, DrawingTheme.Black);
        }
    }

    // 設置場所グルーピング枠（点線矩形＋左上ラベル）
    private void DrawFrames(IRenderer r, Sheet sheet)
    {
        var baseStroke = _theme.Get(StrokeRole.GroupFrame);
        var labelStyle = _theme.Text(TextRole.DeviceName) with
        {
            FontSizeMm = 2.2,
            VAlign = VAlign.Bottom,
            HAlign = HAlign.Left,
        };
        double labelOffY = Cell * 0.25;   // ラベルを枠上辺より少し上に配置

        foreach (var f in sheet.Frames)
        {
            double x = f.VisualXMm ?? X(f.TopLeft.Column);
            double y = f.VisualYMm ?? (YRow(f.TopLeft.Row) - Cell * 0.4);
            double w = f.VisualWidthMm ?? f.Width * Cell;
            double h = f.VisualHeightMm ?? f.Height * Cell;

            var stroke = baseStroke with { Style = f.BorderStyle ?? LineStyle.Dashed };
            r.DrawRectangle(new(x, y, w, h), stroke);
            if (!string.IsNullOrEmpty(f.Label))
                r.DrawText(f.Label, new(x + 1.0, y - labelOffY), labelStyle);
        }
    }

    private void DrawWire(IRenderer r, double x1, double y1, double x2, double y2,
        int? net, Netlist netlist, ConnectivityReport? report, HashSet<int>? powered)
    {
        var stroke = _theme.Get(StrokeRole.Wire);
        if (powered is not null && net is int pid && powered.Contains(pid))
            stroke = stroke with { Color = DrawingTheme.Powered, Width = DrawingTheme.PoweredWireWidth };   // テスト: 通電
        else if (report is not null && net is int nid)
            stroke = stroke with { Color = report.Of(nid) == WireStatus.Connected ? DrawingTheme.Blue : DrawingTheme.Black };
        r.DrawLine(new(x1, y1), new(x2, y2), stroke);

        // 線番（母線ネットは WireNumber=0 で非表示）
        if (_opt.ShowWireNumbers && net is int id2 && netlist.Nets[id2].WireNumber > 0)
        {
            double mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
            r.DrawText(netlist.Nets[id2].WireNumber.ToString(), new(mx, my - Cell * 0.12), _theme.Text(TextRole.LineNumber));
        }
    }

    private int LeftBoundary(ElementInstance e)
    {
        var ports = PartResolver.Ports(e, _lib);
        int min = ports[0].BoundaryOffset;
        foreach (var p in ports) min = Math.Min(min, p.BoundaryOffset);
        return e.Pos.Column + min;
    }

    private int RightBoundary(ElementInstance e)
    {
        var ports = PartResolver.Ports(e, _lib);
        int max = ports[0].BoundaryOffset;
        foreach (var p in ports) max = Math.Max(max, p.BoundaryOffset);
        return e.Pos.Column + max;
    }

    // ---- クロスリファレンス一覧表 ----

    private const double DevColW = 15.0;   // 機器名列幅 (mm)
    private const double CoilColW = 28.0;  // コイル列幅 (mm)
    // 接点列は残り幅を使う

    private double TableRowH => Cell * 0.65;
    private double TableGap => Cell * 0.8;

    private double CalcTableHeight(CrossReference xref)
    {
        int rows = xref.CoilEntries.Count() + 1;   // +1 = ヘッダ行
        return TableGap + rows * TableRowH + Cell * 0.4;
    }

    private void DrawCrossRefTable(IRenderer r, int columns, double startY, CrossReference xref)
    {
        var entries = xref.CoilEntries.ToList();
        if (entries.Count == 0) return;

        double rh = TableRowH;
        double y0 = startY + TableGap;
        double x0 = X(0);
        double x1 = x0 + DevColW;
        double x2 = x1 + CoilColW;
        double x3 = X(columns);

        var outline = _theme.Get(StrokeRole.SymbolOutline) with { Width = DrawingTheme.TableLineWidth };
        var headerText = _theme.Text(TextRole.CrossRef) with { Bold = true, FontSizeMm = 2.4, VAlign = VAlign.Middle };
        var cellText = _theme.Text(TextRole.CrossRef) with { FontSizeMm = 2.2, VAlign = VAlign.Middle };
        double pad = 1.0;   // セル内左余白(mm)

        // ヘッダ行
        double yh = y0;
        DrawTableRow(r, outline, x0, yh, x1, x2, x3, rh, fill: true);
        DrawCellText(r, "機器", x0, yh, rh, pad, headerText);
        DrawCellText(r, "コイル", x1, yh, rh, pad, headerText);
        DrawCellText(r, "接点", x2, yh, rh, pad, headerText);

        // データ行
        for (int i = 0; i < entries.Count; i++)
        {
            double yi = y0 + (i + 1) * rh;
            DrawTableRow(r, outline, x0, yi, x1, x2, x3, rh, fill: false);
            var e = entries[i];
            DrawCellText(r, e.DeviceName, x0, yi, rh, pad, cellText);
            DrawCellText(r, FormatRefs(e.Coils), x1, yi, rh, pad, cellText);
            DrawCellText(r, FormatRefs(e.Contacts), x2, yi, rh, pad, cellText);
        }
    }

    private static void DrawTableRow(IRenderer r, StrokeStyle s,
        double x0, double y, double x1, double x2, double x3, double rh, bool fill)
    {
        if (fill) r.FillRectangle(new(x0, y, x3 - x0, rh), DrawingTheme.TableHeaderFill);
        r.DrawRectangle(new(x0, y, x3 - x0, rh), s);
        r.DrawLine(new(x1, y), new(x1, y + rh), s);
        r.DrawLine(new(x2, y), new(x2, y + rh), s);
    }

    private static void DrawCellText(IRenderer r, string text, double cellX, double rowY, double rh,
        double pad, TextStyle style)
        => r.DrawText(text, new(cellX + pad, rowY + rh / 2), style);

    private static string FormatRefs(IEnumerable<CircuitRef> refs)
    {
        var list = refs.ToList();
        if (list.Count == 0) return "—";
        // 単一ページ図面はページ番号を省略、複数ページは "P-N" 形式
        bool multiPage = list.Select(c => c.PageNumber).Distinct().Count() > 1
                      || list[0].PageNumber != 1;
        return string.Join("  ", list.Select(c =>
            multiPage ? $"{c.PageNumber}-{c.CircuitNumber}" : c.CircuitNumber.ToString()));
    }

    // 要素記号（ローカル座標へ平行移動して描く）。energized で通電色、inputs で手動強制の青塗りを制御。
    private void DrawElement(IRenderer r, ElementInstance e, Dictionary<string, bool>? energized,
                             Dictionary<string, bool>? inputs = null)
    {
        int lb = LeftBoundary(e), rb = RightBoundary(e);
        double width = e.CellWidth * Cell;
        bool on = energized is not null && e.DeviceName is not null
                  && energized.TryGetValue(e.DeviceName, out var v) && v;
        var stroke = on ? _theme.Get(StrokeRole.SymbolOutline) with { Color = DrawingTheme.Powered }
                        : _theme.Get(StrokeRole.SymbolOutline);

        // ContactNO/NC の縦棒間を半透明青で塗る（ユーザーが手動強制した接点の明示）
        bool isContact = e.Kind is ElementKind.ContactNO or ElementKind.ContactNC;
        bool manuallyForced = isContact && e.DeviceName is not null && inputs is not null
                              && inputs.TryGetValue(e.DeviceName, out var mv) && mv;
        Color? fill = manuallyForced ? DrawingTheme.ManualForced : null;

        var part = _lib?.Get(e.PartId);
        r.PushTransform(X(lb), YRow(e.Pos.Row));
        if (part is not null) PartDrawing.Draw(r, _theme, part, Cell, stroke);
        else SymbolGlyphs.Draw(r, stroke, e.Kind, width, Cell, fill);
        r.PopTransform();

        // 表示灯の中央にランプ色（色記号）を記入
        if (e.Kind == ElementKind.Lamp &&
            e.Params.TryGetValue("LampColor", out var lampColor) && !string.IsNullOrEmpty(lampColor))
        {
            var cs = _theme.Text(TextRole.DeviceName) with
            {
                FontSizeMm = 2.4, HAlign = HAlign.Center, VAlign = VAlign.Middle,
            };
            r.DrawText(lampColor, new(X(lb) + width / 2, YRow(e.Pos.Row)), cs);
        }

        DrawElementLabel(r, e, lb, rb, width);
    }

    // 機器名ラベル＋コメントを記号の上・中央に描く。
    // Params["LabelDy"] (mm, 正で上へ) で要素ごとに高さオフセットを調整できる（密集時の重なり回避）。
    private void DrawElementLabel(IRenderer r, ElementInstance e, int lb, int rb, double width)
    {
        if (!_opt.ShowDeviceNames) return;
        bool hasName = !string.IsNullOrEmpty(e.DeviceName);
        bool hasComment = !string.IsNullOrEmpty(e.Comment);
        if (!hasName && !hasComment) return;

        double cx = X(lb) + width / 2;
        // 個別の LabelDy があればそれ、無ければ種別の既定オフセット。
        double dy = e.Params.TryGetValue("LabelDy", out var s) &&
            double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v : ElementCatalog.DefaultLabelDy(e.Kind);

        double yn = YRow(e.Pos.Row) - Cell * 0.50 - dy;   // dy>0 で上へ
        var nameStyle = _theme.Text(TextRole.DeviceName);
        if (hasName) r.DrawText(e.DeviceName!, new(cx, yn), nameStyle);
        if (hasComment) r.DrawText(e.Comment!, new(cx, yn + 2.2), nameStyle with { FontSizeMm = 1.7 });
    }

    // 右母線の右側コメント
    private void DrawRungComments(IRenderer r, Sheet sheet, int columns)
    {
        if (sheet.RungComments.Count == 0) return;
        double x = RightBusX(columns) + 2.0;
        var style = _theme.Text(TextRole.DeviceName) with { HAlign = HAlign.Left, FontSizeMm = 2.0 };
        foreach (var rc in sheet.RungComments)
            if (!string.IsNullOrEmpty(rc.Text))
                r.DrawText(rc.Text, new(x, YRow(rc.Row)), style);
    }

    // 表題欄（タイトルブロック）を startY 位置に描画する。
    // レイアウト: 2行×複数列のグリッド枠。行1=図面名称+図番、行2=顧客/設計/製図/確認/日付。
    private void DrawTitleBlock(IRenderer r, DocumentInfo info, double x0, double x1, double startY,
                                int pageNumber = 1, int totalPages = 1)
    {
        double totalW = x1 - x0;
        double h1 = TitleBlockH * 0.45;   // 1行目高さ
        double h2 = TitleBlockH - h1;     // 2行目高さ
        var outline = _theme.Get(StrokeRole.SymbolOutline) with { Width = DrawingTheme.TableLineWidth };
        var labelStyle = _theme.Text(TextRole.CrossRef) with { FontSizeMm = 1.8, VAlign = VAlign.Middle, Bold = true };
        var dataStyle  = _theme.Text(TextRole.CrossRef) with { FontSizeMm = 2.2, VAlign = VAlign.Middle };
        double pad = 1.0;

        // ---- 行1: 図面名称 (50%) | 図番 (30%) | ページ (20%) ----
        double titleW = totalW * 0.50, drawNoW = totalW * 0.30, pageW = totalW * 0.20;
        double y1 = startY;
        DrawTitleCell(r, outline, x0, y1, titleW, h1, "図面名称", info.Title, labelStyle, dataStyle, pad);
        DrawTitleCell(r, outline, x0 + titleW, y1, drawNoW, h1, "図番", info.DrawingNo, labelStyle, dataStyle, pad);
        DrawTitleCell(r, outline, x0 + titleW + drawNoW, y1, pageW, h1, "ページ",
                      $"{pageNumber} / {totalPages}", labelStyle, dataStyle, pad);

        // ---- 行2: 顧客 | 設計 | 製図 | 確認 | 日付 ----
        double[] ratios = { 0.28, 0.18, 0.18, 0.18, 0.18 };
        string[] labels = { "顧客", "設計", "製図", "確認", "日付" };
        string[] values = { info.Customer, info.Designer, info.Drafter, info.Checker, info.Date ?? "" };
        double y2 = startY + h1;
        double cx = x0;
        for (int i = 0; i < labels.Length; i++)
        {
            double cw = totalW * ratios[i];
            DrawTitleCell(r, outline, cx, y2, cw, h2, labels[i], values[i], labelStyle, dataStyle, pad);
            cx += cw;
        }
    }

    private static void DrawTitleCell(IRenderer r, StrokeStyle s,
        double x, double y, double w, double h,
        string label, string value, TextStyle labelStyle, TextStyle dataStyle, double pad)
    {
        r.DrawRectangle(new(x, y, w, h), s);
        // ラベル（左上小文字）
        r.DrawText(label, new(x + pad, y + h * 0.22), labelStyle with { FontSizeMm = 1.7, VAlign = VAlign.Middle });
        // 値（中央やや下）
        r.DrawText(value, new(x + pad, y + h * 0.65), dataStyle);
    }

    // A4横用紙の図面枠（外枠線）を描画する。
    private void DrawBorder(IRenderer r)
    {
        const double margin = 5.0;   // 用紙端からの余白
        var thick = _theme.Get(StrokeRole.BusRail) with { Width = 0.5 };
        r.DrawRectangle(new(margin, margin, A4W - margin * 2, A4H - margin * 2), thick);
    }

    // 改定欄（表題欄の上）を描画する。最新エントリが上に来るよう逆順表示。
    private void DrawRevisionBlock(IRenderer r, DocumentInfo info, double x0, double x1, double startY)
    {
        double totalW = x1 - x0;
        var s = _theme.Get(StrokeRole.SymbolOutline) with { Width = DrawingTheme.TableLineWidth };
        var hdrStyle  = _theme.Text(TextRole.CrossRef) with { FontSizeMm = 1.8, VAlign = VAlign.Middle, Bold = true };
        var dataStyle = _theme.Text(TextRole.CrossRef) with { FontSizeMm = 2.0, VAlign = VAlign.Middle };
        double pad = 1.0;

        double[] ratios  = { 0.12, 0.18, 0.55, 0.15 };
        string[] headers = { "Rev", "日付", "内容", "担当" };

        // ヘッダ行
        double y = startY;
        double cx = x0;
        for (int i = 0; i < headers.Length; i++)
        {
            double cw = totalW * ratios[i];
            r.DrawRectangle(new(cx, y, cw, RevHdrH), s);
            r.DrawText(headers[i], new(cx + pad, y + RevHdrH / 2), hdrStyle);
            cx += cw;
        }

        // データ行（最新エントリが上）
        y += RevHdrH;
        foreach (var rev in Enumerable.Reverse(info.Revisions))
        {
            string[] vals = { rev.Rev, rev.Date, rev.Description, rev.By };
            cx = x0;
            for (int i = 0; i < vals.Length; i++)
            {
                double cw = totalW * ratios[i];
                r.DrawRectangle(new(cx, y, cw, RevRowH), s);
                r.DrawText(vals[i], new(cx + pad, y + RevRowH / 2), dataStyle);
                cx += cw;
            }
            y += RevRowH;
        }
    }
}
