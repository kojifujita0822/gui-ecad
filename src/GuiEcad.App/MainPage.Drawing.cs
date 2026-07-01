using System.Numerics;
using System.Text;
using GuiEcad.Model;
using GuiEcad.Pdf;
using GuiEcad.Persistence;
using GuiEcad.Rendering;
using GuiEcad.Simulation;
using GuiEcad_App.Commands;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;
// x:Name="Canvas"（CanvasControl）と型名 Canvas が衝突するため、添付プロパティ用に別名を使う。
using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;

namespace GuiEcad_App;

public sealed partial class MainPage : Page
{
    // ===== 描画 =====

    // 自由直線のスナップ細かさ: セルを何分割した格子に吸着するか（4=約2.25mm刻み）。
    // 整数分割なので列境界(セル境界)・行中心も格子点に含まれ、記号端子に合わせつつ微調整もできる。
    private const double LineSnapDiv = 4.0;

    // ポインタ mm を細分格子に吸着する。
    private (double X, double Y) SnapLine(double xMm, double yMm)
    {
        double step = _geo.CellMm / LineSnapDiv;
        double x = Math.Round((xMm - _geo.MarginMm) / step) * step + _geo.MarginMm;
        double y = Math.Round((yMm - _geo.MarginMm) / step) * step + _geo.MarginMm;
        return (x, y);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        double scale = DipsPerMm * _viewport.Zoom;
        var transform = Matrix3x2.CreateScale((float)scale) * Matrix3x2.CreateTranslation((float)_viewport.PanX, (float)_viewport.PanY);

        var renderer = new Win2DRenderer(args.DrawingSession, transform);
        var dr = new DiagramRenderer(_drawingTheme, new RenderOptions
        {
            ConnectivityCheck = _connectivityCheck,
            ShowGrid = _showGrid,
            PaperSize = _document.Settings.PaperSize,
        });
        dr.Render(renderer, _sheet, _document.Library, _testSession?.State);

        // 図面枠ON時は、PDF出力1ページ分（A4縦）の範囲を仮枠ガイドで表示する。
        // 長い図面でどこがページ境界かを作図中に把握できるようにする。
        if (_document.Settings.EnableBorder)
            DrawPageGuides(renderer, dr);

        // 検索ハイライト（現在シートの一致のみ描画）
        for (int i = 0; i < _find.Results.Count; i++)
        {
            if (_find.Results[i].Sheet != _sheet) continue;
            DrawElementHighlight(renderer, _find.Results[i].El,
                i == _find.Index ? new Color(200, 255, 140, 0) : new Color(100, 255, 220, 0));
        }

        // 選択ハイライト
        foreach (var e in _selectedSet)
            DrawElementHighlight(renderer, e, new Color(80, 0, 120, 255));
        foreach (var vc in _selectedConnectorSet)
            renderer.DrawLine(new(_geo.X(vc.Column), _geo.YRow(vc.TopRow)),
                              new(_geo.X(vc.Column), _geo.YRow(vc.BottomRow)),
                              new StrokeStyle(DrawingTheme.Blue, 0.5));
        foreach (var fl in _selectedLineSet)
            renderer.DrawLine(new(fl.X1Mm, fl.Y1Mm), new(fl.X2Mm, fl.Y2Mm),
                              new StrokeStyle(DrawingTheme.Blue, 0.5));
        foreach (var f in _selectedFrameSet)
        {
            double fx = f.VisualXMm ?? _geo.X(f.TopLeft.Column);
            double fy = f.VisualYMm ?? (_geo.YRow(f.TopLeft.Row) - _geo.CellMm * 0.4);
            double fw = f.VisualWidthMm ?? f.Width * _geo.CellMm;
            double fh = f.VisualHeightMm ?? f.Height * _geo.CellMm;
            renderer.DrawRectangle(new(fx, fy, fw, fh), new StrokeStyle(DrawingTheme.Blue, 0.5));
        }
        foreach (var d in _selectedDotSet)
            renderer.DrawCircle(new(d.XMm, d.YMm), _geo.CellMm * 0.22,
                                new StrokeStyle(DrawingTheme.Blue, 0.3));

        if (_rangeSelecting)
        {
            int r1 = Math.Min(_rangeStart.Row, _rangeEnd.Row);
            int r2 = Math.Max(_rangeStart.Row, _rangeEnd.Row);
            int c1 = Math.Min(_rangeStart.Column, _rangeEnd.Column);
            int c2 = Math.Max(_rangeStart.Column, _rangeEnd.Column);
            double rx = _geo.X(c1), ry = _geo.YRow(r1) - _geo.CellMm * 0.5;
            double rw = (c2 - c1 + 1) * _geo.CellMm, rh = (r2 - r1 + 1) * _geo.CellMm;
            renderer.DrawRectangle(new(rx, ry, rw, rh),
                new StrokeStyle(DrawingTheme.Blue, 0.2, LineStyle.Dashed));
        }

        if (_selected is not null) DrawSelection(renderer, _selected);
        if (_selectedConnector is VerticalConnector sc)
            renderer.DrawLine(new(_geo.X(sc.Column), _geo.YRow(sc.TopRow)),
                              new(_geo.X(sc.Column), _geo.YRow(sc.BottomRow)),
                              new StrokeStyle(DrawingTheme.Blue, 0.5));

        // 縦コネクタ配置プレビュー（ドラッグ中の仮線）
        if (_connStartRow is int cs)
        {
            double x = _geo.X(_connBoundary);
            int top = Math.Min(cs, _connCurRow), bot = Math.Max(cs, _connCurRow);
            renderer.DrawLine(new(x, _geo.YRow(top)), new(x, _geo.YRow(bot)),
                new StrokeStyle(DrawingTheme.Blue, 0.3));
        }

        // 直線ドラッグ中のプレビュー
        if (_lineStartMm is (double lsx, double lsy))
            renderer.DrawLine(new(lsx, lsy), new(_lineCurMm.X, _lineCurMm.Y),
                              new StrokeStyle(DrawingTheme.Blue, 0.3));

        // 枠ドラッグ中のプレビュー（mm 連続座標）
        if (_frameStartMm is (double sx, double sy))
        {
            double fx = Math.Min(sx, _frameCurMm.X), fy = Math.Min(sy, _frameCurMm.Y);
            double fw = Math.Abs(_frameCurMm.X - sx), fh = Math.Abs(_frameCurMm.Y - sy);
            renderer.DrawRectangle(new(fx, fy, fw, fh), new StrokeStyle(DrawingTheme.Blue, 0.25, LineStyle.Dashed));
        }

        // 選択中の自由直線ハイライト
        if (_selectedLine is FreeLine sl)
            renderer.DrawLine(new(sl.X1Mm, sl.Y1Mm), new(sl.X2Mm, sl.Y2Mm),
                              new StrokeStyle(new Color(160, 0, 120, 220), 0.8));

        // 選択中の接続点ハイライト（青い輪）
        if (_selectedDot is ConnectionDot sd)
            renderer.DrawCircle(new(sd.XMm, sd.YMm), _geo.CellMm * 0.22,
                                new StrokeStyle(DrawingTheme.Blue, 0.3));

        // 記号配置ツール選択中：マウス位置（キーボード配置モード中はフォーカスセル）に
        // 半透明の配置プレビュー（ゴースト）を描く。配置可能なセル範囲（列内）にいるときだけ表示する。
        var previewCell = _keyboardModeActive ? _focusCell : _hoverCell;
        if (!_testMode && PlaceKind is ElementKind pk
            && previewCell.Row >= 0 && previewCell.Column >= 0 && previewCell.Column < _sheet.Grid.Columns)
        {
            var ghost = new ElementInstance
            {
                Kind = pk,
                Pos = previewCell,
                PartId = PlacePartId,
                CellWidth = _document.Library?.Get(PlacePartId)?.WidthCells
                            ?? ElementCatalog.DefaultCellWidth(pk),
            };
            if (PlaceOrient is not null) ghost.Params[ParamKeys.Orient] = PlaceOrient;
            dr.DrawPreview(renderer, ghost, new Color(120, 0, 120, 255));   // 半透明の青紫
        }

        // キーボード配置モード中：フォーカスセルを矩形で強調表示する（配置ツール未選択時も見えるように）。
        if (_keyboardModeActive && _focusCell.Row >= 0 && _focusCell.Column >= 0
            && _focusCell.Column < _sheet.Grid.Columns)
        {
            double fx = _geo.X(_focusCell.Column), fy = _geo.YRow(_focusCell.Row) - _geo.CellMm * 0.5;
            renderer.DrawRectangle(new(fx, fy, _geo.CellMm, _geo.CellMm),
                new StrokeStyle(new Color(220, 255, 140, 0), 0.4));   // オレンジ枠
        }

        // 選択中の枠ハイライト
        if (_selectedFrame is GroupFrame sf)
        {
            double fx = sf.VisualXMm ?? _geo.X(sf.TopLeft.Column);
            double fy = sf.VisualYMm ?? (_geo.YRow(sf.TopLeft.Row) - _geo.CellMm * 0.4);
            double fw = sf.VisualWidthMm ?? sf.Width * _geo.CellMm;
            double fh = sf.VisualHeightMm ?? sf.Height * _geo.CellMm;
            renderer.DrawRectangle(new(fx, fy, fw, fh), new StrokeStyle(DrawingTheme.Blue, 0.4));
        }

        // DRC ジャンプ先ハイライト（選択した診断の行を半透明オレンジで強調）
        if (_drcHighlightRow >= 0)
            DrawDrcRowHighlight(renderer, _drcHighlightRow);

        // テストモード: 作動中（計時中）の限時タイマ接点の上に残り時間を小窓表示。
        DrawTimerCountdowns(renderer);
    }

    // 作動中の限時タイマ接点の上に「残り秒数」を小窓（淡色バッジ）で表示する。ユーザーが時限を体感できるように。
    private void DrawTimerCountdowns(Win2DRenderer r)
    {
        if (!_testMode || _testSession is null) return;
        var st = _testSession.State;

        // デバイス→設定時間（秒）。同名のコイル/接点いずれかの Setpoint。コイル優先。
        var setpoints = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in _sheet.Elements)
        {
            if (string.IsNullOrEmpty(el.DeviceName)) continue;
            if (!el.Params.TryGetValue(ParamKeys.Setpoint, out var sp)) continue;
            if (!double.TryParse(sp, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
            if (el.Kind == ElementKind.Timer || !setpoints.ContainsKey(el.DeviceName))
                setpoints[el.DeviceName] = v;
        }
        if (setpoints.Count == 0) return;

        double cell = _geo.CellMm;
        var textStyle = DrawingTheme.Default.Text(TextRole.LineNumber) with
        {
            FontSizeMm = 2.4, Bold = true, HAlign = HAlign.Center, VAlign = VAlign.Middle,
            Color = new Color(255, 30, 30, 30),
        };

        foreach (var el in _sheet.Elements)
        {
            if (el.Kind is not (ElementKind.TimerContactNO or ElementKind.TimerContactNC)) continue;  // 限時接点のみ
            var dev = el.DeviceName;
            if (string.IsNullOrEmpty(dev)) continue;
            if (!setpoints.TryGetValue(dev, out var sp) || sp <= 0) continue;
            bool on = st.Energized.TryGetValue(dev, out var e) && e;          // タイマ励磁＝計時中
            if (!on) continue;
            double elapsed = st.TimerElapsed.TryGetValue(dev, out var t) ? t : 0;
            double remaining = Math.Max(0, sp - elapsed);
            if (remaining <= 0) continue;     // 時限到達後は非表示（接点はすでに動作）

            var (l, right) = PartResolver.BoundarySpan(el, _document.Library);
            double cx = (_geo.X(l) + _geo.X(right)) / 2;
            double cy = _geo.YRow(el.Pos.Row) - cell * 1.15;   // 記号の上
            double w = cell * 1.15, h = cell * 0.55;
            var rect = new Rect2D(cx - w / 2, cy - h / 2, w, h);
            r.FillRectangle(rect, new Color(230, 255, 246, 200));               // 淡黄の小窓
            r.DrawRectangle(rect, new StrokeStyle(new Color(255, 235, 170, 70), 0.25));
            r.DrawText(remaining.ToString("0.0") + "s", new(cx, cy), textStyle);
        }
    }

    private void DrawSelection(Win2DRenderer r, ElementInstance e)
    {
        var (l, right) = PartResolver.BoundarySpan(e, _document.Library);
        double pad = _geo.CellMm * 0.12;
        double x = _geo.X(l) - pad, y = _geo.YRow(e.Pos.Row) - _geo.CellMm * 0.5 - pad;
        double w = (right - l) * _geo.CellMm + 2 * pad, h = _geo.CellMm + 2 * pad;
        r.DrawRectangle(new(x, y, w, h), new StrokeStyle(DrawingTheme.Blue, 0.3));
    }

    // DRC ジャンプ先の行を半透明オレンジで強調表示する。
    private void DrawDrcRowHighlight(Win2DRenderer r, int row)
    {
        double xMm = 0;
        double yMm = _geo.YRow(row) - _geo.CellMm * 0.5;
        double wMm = _geo.X(_sheet.Grid.Columns);
        double hMm = _geo.CellMm;

        r.FillRectangle(new(xMm, yMm, wMm, hMm), new Color(55, 255, 140, 0));
        r.DrawRectangle(new(xMm, yMm, wMm, hMm), new StrokeStyle(new Color(200, 230, 80, 0), 1.5));
    }

    private void DrawElementHighlight(Win2DRenderer r, ElementInstance e, Color color)
    {
        var (l, right) = PartResolver.BoundarySpan(e, _document.Library);
        double pad = _geo.CellMm * 0.1;
        double x = _geo.X(l) - pad, y = _geo.YRow(e.Pos.Row) - _geo.CellMm * 0.5 - pad;
        double w = (right - l) * _geo.CellMm + 2 * pad, h = _geo.CellMm + 2 * pad;
        r.FillRectangle(new(x, y, w, h), color);
    }

    // 図面枠ON時の PDF 1ページ分（用紙縦）の境界を仮枠として描く。
    // 内容の高さに応じて縦方向にページ境界を繰り返し、どこがページ境界か作図中に分かるようにする。
    // 用紙幅・1ページ行数・ページ数はいずれも DiagramRenderer（現在の PaperSize 設定）から取る
    // （ローカルに固定値を持つとA4/A3切替時にガイドだけずれるため）。
    private void DrawPageGuides(Win2DRenderer r, DiagramRenderer dr)
    {
        double pageW = dr.PageSize(_sheet, enableBorder: true).Width;
        int rpp = dr.RowsPerPage;
        double cell = _geo.CellMm, margin = _geo.MarginMm;
        int pages = dr.RenderPageCount(_sheet);
        double bandH = rpp * cell;
        var guide = new StrokeStyle(new Color(150, 40, 90, 200), 0.3, LineStyle.Dashed);
        // 用紙幅 × rpp行 の帯を縦に積み、各帯が1ページに相当することを示す。
        for (int p = 0; p < pages; p++)
            r.DrawRectangle(new(0, margin + p * bandH, pageW, bandH), guide);
    }

    // ===== ツール選択 =====

}
