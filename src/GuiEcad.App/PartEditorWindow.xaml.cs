using System.Numerics;
using GuiEcad.Model;
using GuiEcad.Persistence;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;

namespace GuiEcad_App;

/// <summary>
/// 自作パーツ（<see cref="PartDefinition"/>）の記号エディタ（別ウィンドウ）。
/// 基準範囲(W×H セル)のグリッド上に図形プリミティブと接続点(ポート)を配置する。
/// 座標は Core の規約に一致: 原点 = 最左ポート境界・行中心線、+x 右 / +y 下、単位 = セル。
/// 保存時に <see cref="Saved"/> を発火し、呼び出し側が <see cref="PartLibrary"/> へ登録する。
/// </summary>
public sealed partial class PartEditorWindow : Window
{
    // ===== 編集対象の状態 =====
    private readonly string _id;                                  // 既存編集なら元 Id を維持
    private readonly List<PartPrimitive> _prims = new();
    private readonly List<PortDef> _ports = new();
    private int _w = 1, _h = 1;                                   // 基準範囲（セル）。新規は接点標準の 1×1
    private readonly IReadOnlyList<PartDefinition> _templates = BasicPartTemplates.All();   // 読み込み用たたき台

    // 描画ツール（"select" / "line" / "polyline" / "rect" / "circle" / "arc" / "text" / "port"）
    private string _tool = "select";

    // ドラッグ／作図中の一時状態（パーツローカル＝セル単位）
    private bool _dragging;
    private Vector2 _dragStart, _dragCur;
    private readonly List<Vector2> _polyPts = new();             // 折れ線の確定済み頂点

    // 選択（プリミティブ index か ポート index のいずれか・-1 で非選択）
    private int _selPrim = -1, _selPort = -1;
    private bool _movingSel;
    private Vector2 _moveLast;
    private bool _suppressArc;                                   // 扁平率 NumberBox のプログラム更新ガード

    // 回転ドラッグ（rotate ツール・図形中心まわり・15度スナップ）
    private bool _rotating;
    private PartPrimitive? _rotOrig;                             // ドラッグ開始時の元図形（累積誤差を避ける基準）
    private Vector2 _rotCenter;
    private double _rotStartAng;

    // Undo/Redo（プリミティブ・ポート・サイズ・役割のスナップショット）
    private readonly Stack<EditorSnapshot> _undo = new();
    private readonly Stack<EditorSnapshot> _redo = new();

    // ドラッグ確定用の保留 Undo（掴んだ時点の状態。実際に変化したらリリース時に確定する＝空 Undo を積まない）
    private EditorSnapshot? _pendingUndo;
    private bool _dragChanged;

    // 画面レイアウト（OnDraw で更新・ヒットテストで共有）
    private double _cellPx = 60, _originX, _originY;

    // ズーム／パン（_cellPx = fit * _zoom、原点に _panX/_panY を加算）
    private double _zoom = 1.0;
    private double _panX, _panY;
    private bool _panning;
    private Point _panLast;
    private bool _spaceDown;   // スペース押下中は左ドラッグでパン

    /// <summary>保存ボタン押下時に確定した定義を渡す。呼び出し側がライブラリ登録・UI 反映する。</summary>
    public event Action<PartDefinition>? Saved;

    public PartEditorWindow(PartDefinition? edit = null)
    {
        InitializeComponent();

        if (edit is not null)
        {
            _id = edit.Id;
            _w = Math.Max(1, edit.WidthCells);
            _h = Math.Max(1, edit.HeightCells);
            _prims.AddRange(edit.Primitives);
            _ports.AddRange(edit.Ports);
        }
        else
        {
            _id = Guid.NewGuid().ToString("N");
        }

        NameBox.Text = edit?.Name ?? "";
        WidthBox.Value = _w;
        HeightBox.Value = _h;
        SelectRole(edit?.Role ?? PartRole.ContactNO);
        BuildTemplateMenu();
        UpdateStatus();
    }

    // ===== 組込み図形（たたき台）読み込み =====

    private void BuildTemplateMenu()
    {
        foreach (var def in _templates)
        {
            var item = new MenuFlyoutItem { Text = def.Name, Tag = def.Id };
            item.Click += OnLoadTemplate;
            TemplateFlyout.Items.Add(item);
        }
    }

    private void OnLoadTemplate(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is string id &&
            _templates.FirstOrDefault(d => d.Id == id) is { } def)
            LoadTemplate(def);
    }

    // たたき台の図形・接続点・サイズ・役割をエディタへ展開する（現在の内容は Undo 可能な形で置き換え）。
    private void LoadTemplate(PartDefinition def)
    {
        PushUndo();
        _prims.Clear();
        _prims.AddRange(def.Primitives);
        _ports.Clear();
        _ports.AddRange(def.Ports);
        _w = Math.Max(1, def.WidthCells);
        _h = Math.Max(1, def.HeightCells);
        WidthBox.Value = _w;
        HeightBox.Value = _h;
        SelectRole(def.Role);
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = def.Name;
        _selPrim = _selPort = -1;
        EditCanvas?.Invalidate();
        UpdateStatus();
    }

    private void SelectRole(PartRole role)
    {
        for (int i = 0; i < RoleBox.Items.Count; i++)
            if (RoleBox.Items[i] is ComboBoxItem it && it.Tag as string == role.ToString())
            { RoleBox.SelectedIndex = i; return; }
    }

    private PartRole CurrentRole()
        => (RoleBox.SelectedItem as ComboBoxItem)?.Tag is string t
           && Enum.TryParse<PartRole>(t, out var r) ? r : PartRole.ContactNO;

    // ===== ツールバー =====

    private void OnDrawToolSelected(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            CommitPolyline();          // 別ツールへ切替時は作図中の折れ線を確定
            _tool = tag;
            _dragging = false;
            _rotating = false;
            _selPrim = _selPort = -1;
            UpdateStatus();
            EditCanvas?.Invalidate();
        }
    }

    private void OnSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!ReadSizeFromBoxes()) return;
        EditCanvas?.Invalidate();
    }

    // NumberBox の現在値から _w/_h を確定する。保存直前にも呼び、入力直後（ValueChanged 未発火）の保存に対応する。
    private bool ReadSizeFromBoxes()
    {
        if (WidthBox is null || HeightBox is null) return false;
        _w = (int)Math.Clamp(double.IsNaN(WidthBox.Value) ? 1 : WidthBox.Value, 1, 12);
        _h = (int)Math.Clamp(double.IsNaN(HeightBox.Value) ? 1 : HeightBox.Value, 1, 12);
        return true;
    }

    // ===== 座標変換（画面 DIP ↔ パーツローカル セル）=====

    private double Sx(double cx) => _originX + cx * _cellPx;
    private double Sy(double cy) => _originY + (cy + _h / 2.0) * _cellPx;
    private Vector2 S(Vector2 c) => new((float)Sx(c.X), (float)Sy(c.Y));
    private double Lx(double sx) => (sx - _originX) / _cellPx;
    private double Ly(double sy) => (sy - _originY) / _cellPx - _h / 2.0;
    private Vector2 L(Point p) => new((float)Lx(p.X), (float)Ly(p.Y));

    private const double SnapStep = 1.0 / 16;                            // スナップ刻み（セル単位）
    private static double Snap(double v) => Math.Round(v / SnapStep) * SnapStep;
    private Vector2 SnapV(Vector2 v) => new((float)Snap(v.X), (float)Snap(v.Y));

    // 弧（楕円弧）の外接矩形ドラッグ：始点-終点の矩形に内接する半楕円弧を作る。
    // 横幅=2rx・縦幅=2ry で扁平率が決まり、縦ドラッグ方向で弧の向き（上半分/下半分）を選ぶ。
    private static (double cx, double cy, double rx, double ry, double start) ArcParams(Vector2 s, Vector2 e)
    {
        double cx = (s.X + e.X) / 2.0, cy = (s.Y + e.Y) / 2.0;
        double rx = Math.Abs(e.X - s.X) / 2.0, ry = Math.Abs(e.Y - s.Y) / 2.0;
        double start = e.Y < s.Y ? 180 : 0;   // 上へドラッグ=上半分 / 下へ=下半分
        return (cx, cy, rx, ry, start);
    }

    private static bool ArcValid(Vector2 s, Vector2 e)
        => Math.Abs(e.X - s.X) / 2.0 > 0.05 && Math.Abs(e.Y - s.Y) / 2.0 > 0.05;

    private static PartArc MakeArc(Vector2 s, Vector2 e)
    {
        var (cx, cy, rx, ry, st) = ArcParams(s, e);
        return new PartArc(Snap(cx), Snap(cy), Snap(rx), st, 180, Snap(ry));
    }

    // ===== 描画 =====

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        double cw = sender.Size.Width, ch = sender.Size.Height;

        // 基準範囲がキャンバスに収まる倍率(fit)にズーム率を掛け、パン量だけ原点をずらす
        _cellPx = FitCell(cw, ch) * _zoom;
        _originX = (cw - _w * _cellPx) / 2 + _panX;
        _originY = (ch - _h * _cellPx) / 2 + _panY;

        DrawGrid(ds);
        foreach (var (p, i) in _prims.Select((p, i) => (p, i)))
            DrawPrimitive(ds, p, i == _selPrim);
        for (int i = 0; i < _ports.Count; i++) DrawPort(ds, _ports[i], i == _selPort);
        DrawPreview(ds);
    }

    private void DrawGrid(CanvasDrawingSession ds)
    {
        var faint = Color.FromArgb(40, 128, 128, 128);
        var line = Color.FromArgb(120, 128, 128, 128);
        var frame = Color.FromArgb(255, 96, 150, 230);
        var center = Color.FromArgb(150, 90, 90, 90);

        // 1/4 セルの薄グリッド
        for (double x = 0; x <= _w + 1e-6; x += 0.25)
            ds.DrawLine((float)Sx(x), (float)Sy(-_h / 2.0), (float)Sx(x), (float)Sy(_h / 2.0), faint, 0.5f);
        for (double y = -_h / 2.0; y <= _h / 2.0 + 1e-6; y += 0.25)
            ds.DrawLine((float)Sx(0), (float)Sy(y), (float)Sx(_w), (float)Sy(y), faint, 0.5f);

        // 整数境界（やや濃く）
        for (int x = 0; x <= _w; x++)
            ds.DrawLine((float)Sx(x), (float)Sy(-_h / 2.0), (float)Sx(x), (float)Sy(_h / 2.0), line, 0.7f);

        // 行中心線（y=0＝配線が通る基準線）
        ds.DrawLine((float)Sx(0), (float)Sy(0), (float)Sx(_w), (float)Sy(0), center, 1.2f);

        // 基準範囲の外形枠
        ds.DrawRectangle((float)Sx(0), (float)Sy(-_h / 2.0), (float)(_w * _cellPx), (float)(_h * _cellPx), frame, 1.5f);
    }

    private void DrawPrimitive(CanvasDrawingSession ds, PartPrimitive p, bool selected)
    {
        var col = selected ? Color.FromArgb(255, 0, 120, 215) : Color.FromArgb(255, 20, 20, 20);
        float wpx = selected ? 2.4f : 1.6f;

        switch (p)
        {
            case PartLine l:
                ds.DrawLine((float)Sx(l.X1), (float)Sy(l.Y1), (float)Sx(l.X2), (float)Sy(l.Y2), col, wpx);
                break;
            case PartCircle c:
                ds.DrawCircle((float)Sx(c.Cx), (float)Sy(c.Cy), (float)(c.R * _cellPx), col, wpx);
                break;
            case PartArc a:
                DrawArc(ds, a, col, wpx);
                break;
            case PartRect rc:
            {
                var prev = ds.Transform;
                if (rc.Rot != 0)
                    ds.Transform = Matrix3x2.CreateRotation((float)(rc.Rot * Math.PI / 180.0),
                        new Vector2((float)Sx(rc.X + rc.W / 2), (float)Sy(rc.Y + rc.H / 2))) * prev;
                ds.DrawRectangle((float)Sx(rc.X), (float)Sy(rc.Y), (float)(rc.W * _cellPx), (float)(rc.H * _cellPx), col, wpx);
                ds.Transform = prev;
                break;
            }
            case PartPolyline pl when pl.Points.Length >= 4:
                for (int i = 2; i < pl.Points.Length; i += 2)
                    ds.DrawLine((float)Sx(pl.Points[i - 2]), (float)Sy(pl.Points[i - 1]),
                                (float)Sx(pl.Points[i]), (float)Sy(pl.Points[i + 1]), col, wpx);
                break;
            case PartText t:
                using (var fmt = new CanvasTextFormat { FontSize = (float)(t.SizeCells * _cellPx), WordWrapping = CanvasWordWrapping.NoWrap })
                    ds.DrawText(t.Text, (float)Sx(t.X), (float)Sy(t.Y), col, fmt);
                break;
        }
    }

    private void DrawArc(CanvasDrawingSession ds, PartArc a, Color col, float wpx)
    {
        double a0 = a.StartDeg * Math.PI / 180.0, sweep = a.SweepDeg * Math.PI / 180.0;
        double rx = a.R * _cellPx, ry = a.EffRy * _cellPx;
        var start = new Vector2((float)(Sx(a.Cx) + rx * Math.Cos(a0)), (float)(Sy(a.Cy) + ry * Math.Sin(a0)));
        using var pb = new CanvasPathBuilder(ds.Device);
        pb.BeginFigure(start);
        pb.AddArc(new Vector2((float)Sx(a.Cx), (float)Sy(a.Cy)), (float)rx, (float)ry, (float)a0, (float)sweep);
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geo = CanvasGeometry.CreatePath(pb);

        var prev = ds.Transform;
        if (a.Rot != 0)
            ds.Transform = Matrix3x2.CreateRotation((float)(a.Rot * Math.PI / 180.0),
                new Vector2((float)Sx(a.Cx), (float)Sy(a.Cy))) * prev;
        ds.DrawGeometry(geo, col, wpx);
        ds.Transform = prev;
    }

    private void DrawPort(CanvasDrawingSession ds, PortDef port, bool selected)
    {
        float x = (float)Sx(port.BoundaryOffset), y = (float)Sy(port.RowOffset);
        var fill = selected ? Color.FromArgb(255, 0, 120, 215) : Color.FromArgb(255, 220, 60, 60);
        ds.FillCircle(x, y, (float)(0.12 * _cellPx), fill);
        ds.DrawCircle(x, y, (float)(0.12 * _cellPx), Colors.White, 1.0f);
    }

    private void DrawPreview(CanvasDrawingSession ds)
    {
        var prev = Color.FromArgb(180, 0, 150, 0);

        if (_tool == "polyline" && _polyPts.Count > 0)
        {
            for (int i = 1; i < _polyPts.Count; i++)
                ds.DrawLine(S(_polyPts[i - 1]), S(_polyPts[i]), prev, 1.6f);
            ds.DrawLine(S(_polyPts[^1]), S(_dragCur), prev, 1.0f);
            return;
        }
        if (!_dragging) return;

        switch (_tool)
        {
            case "line":
                ds.DrawLine(S(_dragStart), S(_dragCur), prev, 1.6f);
                break;
            case "rect":
            {
                float x = (float)Sx(Math.Min(_dragStart.X, _dragCur.X));
                float y = (float)Sy(Math.Min(_dragStart.Y, _dragCur.Y));
                ds.DrawRectangle(x, y, (float)(Math.Abs(_dragCur.X - _dragStart.X) * _cellPx),
                                       (float)(Math.Abs(_dragCur.Y - _dragStart.Y) * _cellPx), prev, 1.6f);
                break;
            }
            case "circle":
            {
                double r = Vector2.Distance(_dragStart, _dragCur);
                ds.DrawCircle((float)Sx(_dragStart.X), (float)Sy(_dragStart.Y), (float)(r * _cellPx), prev, 1.6f);
                break;
            }
            case "arc":
            {
                var (cx, cy, rx, ry, st) = ArcParams(_dragStart, _dragCur);
                if (rx > 1e-4 && ry > 1e-4)
                    DrawArc(ds, new PartArc(cx, cy, rx, st, 180, ry), prev, 1.6f);
                break;
            }
        }
    }

    // ===== ポインタ操作 =====

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(EditCanvas);
        EditCanvas.Focus(FocusState.Pointer);   // スペースパンのキー入力を受けるため

        // 中ボタン、またはスペース＋左ドラッグでパン
        if (pt.Properties.IsMiddleButtonPressed || (_spaceDown && pt.Properties.IsLeftButtonPressed))
        {
            _panning = true;
            _panLast = pt.Position;
            EditCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        var local = SnapV(L(pt.Position));

        // 折れ線は右クリックで確定
        if (_tool == "polyline" && pt.Properties.IsRightButtonPressed) { CommitPolyline(); EditCanvas.Invalidate(); return; }

        switch (_tool)
        {
            case "select":
                HitTest(L(pt.Position));
                if (_selPrim >= 0 || _selPort >= 0)
                {
                    _movingSel = true;
                    _moveLast = local;
                    BeginDragUndo();
                    EditCanvas.CapturePointer(e.Pointer);
                }
                UpdateStatus();
                EditCanvas.Invalidate();
                break;

            case "rotate":
            {
                var raw = L(pt.Position);
                HitTest(raw);
                if (_selPrim >= 0)
                {
                    _rotOrig = _prims[_selPrim];
                    _rotCenter = Center(_rotOrig);
                    _rotStartAng = Math.Atan2(raw.Y - _rotCenter.Y, raw.X - _rotCenter.X);
                    _rotating = true;
                    BeginDragUndo();
                    EditCanvas.CapturePointer(e.Pointer);
                }
                UpdateStatus();
                EditCanvas.Invalidate();
                break;
            }

            case "polyline":
                _polyPts.Add(local);
                _dragCur = local;
                EditCanvas.Invalidate();
                break;

            case "text":
                _ = AddTextAsync(local);
                break;

            case "port":
                AddPort(L(pt.Position));
                EditCanvas.Invalidate();
                break;

            default:   // line / rect / circle / arc
                _dragging = true;
                _dragStart = _dragCur = local;
                EditCanvas.CapturePointer(e.Pointer);
                break;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(EditCanvas).Position;

        if (_panning)
        {
            _panX += pos.X - _panLast.X;
            _panY += pos.Y - _panLast.Y;
            _panLast = pos;
            EditCanvas.Invalidate();
            return;
        }

        if (_rotating && _rotOrig is not null)
        {
            var raw = L(pos);
            double deg = (Math.Atan2(raw.Y - _rotCenter.Y, raw.X - _rotCenter.X) - _rotStartAng) * 180.0 / Math.PI;
            deg = Math.Round(deg / 15.0) * 15.0;   // 15度スナップ
            _prims[_selPrim] = RotatePrim(_rotOrig, _rotCenter, deg);
            if (deg != 0) _dragChanged = true;
            EditCanvas.Invalidate();
            return;
        }

        var local = SnapV(L(pos));

        if (_tool == "polyline") { _dragCur = local; EditCanvas.Invalidate(); return; }

        if (_movingSel)
        {
            var d = local - _moveLast;
            if (d.LengthSquared() > 0)
            {
                if (_selPrim >= 0) _prims[_selPrim] = Translate(_prims[_selPrim], d.X, d.Y);
                else if (_selPort >= 0) MovePort(d);
                _moveLast = local;
                _dragChanged = true;
                EditCanvas.Invalidate();
            }
            return;
        }

        if (_dragging) { _dragCur = local; EditCanvas.Invalidate(); }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_panning) { _panning = false; EditCanvas.ReleasePointerCapture(e.Pointer); return; }

        EditCanvas.ReleasePointerCapture(e.Pointer);

        if (_rotating) { _rotating = false; _rotOrig = null; CommitDragUndo(); UpdateStatus(); return; }
        if (_movingSel) { _movingSel = false; CommitDragUndo(); UpdateStatus(); return; }
        if (!_dragging) return;
        _dragging = false;

        PartPrimitive? added = _tool switch
        {
            "line" when _dragStart != _dragCur => new PartLine(_dragStart.X, _dragStart.Y, _dragCur.X, _dragCur.Y),
            "rect" when _dragStart != _dragCur => new PartRect(
                Math.Min(_dragStart.X, _dragCur.X), Math.Min(_dragStart.Y, _dragCur.Y),
                Math.Abs(_dragCur.X - _dragStart.X), Math.Abs(_dragCur.Y - _dragStart.Y)),
            "circle" when Vector2.Distance(_dragStart, _dragCur) > 0.05 =>
                new PartCircle(_dragStart.X, _dragStart.Y, Snap(Vector2.Distance(_dragStart, _dragCur))),
            "arc" when ArcValid(_dragStart, _dragCur) => MakeArc(_dragStart, _dragCur),
            _ => null,
        };
        if (added is not null) { PushUndo(); _prims.Add(added); }
        UpdateStatus();
        EditCanvas.Invalidate();
    }

    // ===== ズーム／パン =====

    // 基準範囲がキャンバスに収まる基準セルサイズ（周囲 1.5 セル余白・最小 8px）。ズーム率はここに掛ける。
    private double FitCell(double cw, double ch)
    {
        const double marginCells = 1.5;
        double fit = Math.Min(cw / (_w + 2 * marginCells), ch / (_h + 2 * marginCells));
        return Math.Max(8, fit);
    }

    private Point CanvasCenter() => new(EditCanvas.Size.Width / 2, EditCanvas.Size.Height / 2);

    // anchor（スクリーン座標）下の図形位置を固定したまま倍率を factor 倍する。
    private void ZoomAt(Point anchor, double factor)
    {
        double newZoom = Math.Clamp(_zoom * factor, 0.2, 8.0);
        if (Math.Abs(newZoom - _zoom) < 1e-9 || _cellPx <= 0) return;

        // アンカー下のローカルセル座標（ズーム前）を求め、ズーム後も同じ画面位置に来るようパンを補正
        double cxLocal = (anchor.X - _originX) / _cellPx;
        double cyLocal = (anchor.Y - _originY) / _cellPx;

        double cw = EditCanvas.Size.Width, ch = EditCanvas.Size.Height;
        double newCell = FitCell(cw, ch) * newZoom;
        _panX = anchor.X - cxLocal * newCell - (cw - _w * newCell) / 2;
        _panY = anchor.Y - cyLocal * newCell - (ch - _h * newCell) / 2;
        _zoom = newZoom;
        EditCanvas.Invalidate();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(EditCanvas).Properties.MouseWheelDelta;
        if (delta == 0) return;
        ZoomAt(e.GetCurrentPoint(EditCanvas).Position, delta > 0 ? 1.15 : 1.0 / 1.15);
        e.Handled = true;
    }

    private void OnZoomInPart(object sender, RoutedEventArgs e) => ZoomAt(CanvasCenter(), 1.25);
    private void OnZoomOutPart(object sender, RoutedEventArgs e) => ZoomAt(CanvasCenter(), 1.0 / 1.25);
    private void OnZoomFitPart(object sender, RoutedEventArgs e)
    {
        _zoom = 1.0;
        _panX = _panY = 0;
        EditCanvas.Invalidate();
    }

    private void OnCanvasKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space) { _spaceDown = true; e.Handled = true; }
    }

    private void OnCanvasKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space)
        {
            _spaceDown = false;
            if (_panning) { _panning = false; EditCanvas.ReleasePointerCaptures(); }
            e.Handled = true;
        }
    }

    private async Task AddTextAsync(Vector2 at)
    {
        var box = new TextBox { PlaceholderText = "文字列", AcceptsReturn = false };
        var dlg = new ContentDialog
        {
            Title = "文字を入力",
            Content = box,
            PrimaryButtonText = "追加",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrEmpty(box.Text))
        {
            PushUndo();
            _prims.Add(new PartText(box.Text, at.X, at.Y));
            UpdateStatus();
            EditCanvas.Invalidate();
        }
    }

    private void AddPort(Vector2 local)
    {
        int b = Math.Clamp((int)Math.Round(local.X), 0, _w);
        int rowOff = Math.Clamp((int)Math.Round(local.Y), -(_h - 1), _h - 1);
        if (_ports.Any(p => p.BoundaryOffset == b && p.RowOffset == rowOff)) return;   // 重複は無視
        PushUndo();
        _ports.Add(new PortDef($"P{_ports.Count + 1}", rowOff, b));
        UpdateStatus();
    }

    private void MovePort(Vector2 d)
    {
        var p = _ports[_selPort];
        int b = Math.Clamp(p.BoundaryOffset + (int)Math.Round(d.X), 0, _w);
        int rowOff = Math.Clamp(p.RowOffset + (int)Math.Round(d.Y), -(_h - 1), _h - 1);
        _ports[_selPort] = p with { BoundaryOffset = b, RowOffset = rowOff };
    }

    private void CommitPolyline()
    {
        if (_polyPts.Count >= 2)
        {
            var pts = new double[_polyPts.Count * 2];
            for (int i = 0; i < _polyPts.Count; i++) { pts[2 * i] = _polyPts[i].X; pts[2 * i + 1] = _polyPts[i].Y; }
            PushUndo();
            _prims.Add(new PartPolyline(pts));
        }
        _polyPts.Clear();
        UpdateStatus();
    }

    // ===== ヒットテスト =====

    private void HitTest(Vector2 c)
    {
        _selPrim = _selPort = -1;
        double best = 0.3;   // しきい値（セル）

        for (int i = 0; i < _ports.Count; i++)   // ポート優先
        {
            double d = Vector2.Distance(c, new(_ports[i].BoundaryOffset, _ports[i].RowOffset));
            if (d < best) { best = d; _selPort = i; }
        }
        if (_selPort >= 0) return;

        best = 0.3;
        for (int i = 0; i < _prims.Count; i++)
        {
            double d = DistToPrimitive(_prims[i], c);
            if (d < best) { best = d; _selPrim = i; }
        }
    }

    private static double DistToPrimitive(PartPrimitive p, Vector2 c) => p switch
    {
        PartLine l => DistToSeg(c, new((float)l.X1, (float)l.Y1), new((float)l.X2, (float)l.Y2)),
        PartCircle ci => Math.Abs(Vector2.Distance(c, new((float)ci.Cx, (float)ci.Cy)) - ci.R),
        PartArc a => DistToEllipse(a.Rot == 0 ? c : RotVec(c, new((float)a.Cx, (float)a.Cy), -a.Rot), a),
        PartRect r => DistToRect(r.Rot == 0 ? c : RotVec(c, new((float)(r.X + r.W / 2), (float)(r.Y + r.H / 2)), -r.Rot), r),
        PartPolyline pl => DistToPolyline(pl, c),
        PartText t => Vector2.Distance(c, new((float)t.X, (float)t.Y)) - 0.3,
        _ => double.MaxValue,
    };

    private static double DistToEllipse(Vector2 c, PartArc a)
    {
        // 楕円上の最近点を角度近似で求める（選択用途には十分。弧範囲は無視し全周楕円で近似）。
        double ang = Math.Atan2((c.Y - a.Cy) / Math.Max(a.EffRy, 1e-6), (c.X - a.Cx) / Math.Max(a.R, 1e-6));
        var on = new Vector2((float)(a.Cx + a.R * Math.Cos(ang)), (float)(a.Cy + a.EffRy * Math.Sin(ang)));
        return Vector2.Distance(c, on);
    }

    private static double DistToRect(Vector2 c, PartRect r)
    {
        Vector2 a = new((float)r.X, (float)r.Y), b = new((float)(r.X + r.W), (float)r.Y),
                d = new((float)(r.X + r.W), (float)(r.Y + r.H)), e = new((float)r.X, (float)(r.Y + r.H));
        return Math.Min(Math.Min(DistToSeg(c, a, b), DistToSeg(c, b, d)),
                        Math.Min(DistToSeg(c, d, e), DistToSeg(c, e, a)));
    }

    private static double DistToPolyline(PartPolyline pl, Vector2 c)
    {
        double m = double.MaxValue;
        for (int i = 2; i < pl.Points.Length; i += 2)
            m = Math.Min(m, DistToSeg(c, new((float)pl.Points[i - 2], (float)pl.Points[i - 1]),
                                          new((float)pl.Points[i], (float)pl.Points[i + 1])));
        return m;
    }

    private static double DistToSeg(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared();
        if (len2 < 1e-9) return Vector2.Distance(p, a);
        double t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1);
        return Vector2.Distance(p, a + ab * (float)t);
    }

    // ===== プリミティブ平行移動 =====

    private static PartPrimitive Translate(PartPrimitive p, double dx, double dy) => p switch
    {
        PartLine l => new PartLine(l.X1 + dx, l.Y1 + dy, l.X2 + dx, l.Y2 + dy),
        PartCircle c => c with { Cx = c.Cx + dx, Cy = c.Cy + dy },
        PartArc a => a with { Cx = a.Cx + dx, Cy = a.Cy + dy },
        PartRect r => r with { X = r.X + dx, Y = r.Y + dy },
        PartPolyline pl => new PartPolyline(Shift(pl.Points, dx, dy)),
        PartText t => t with { X = t.X + dx, Y = t.Y + dy },
        _ => p,
    };

    private static double[] Shift(double[] pts, double dx, double dy)
    {
        var r = (double[])pts.Clone();
        for (int i = 0; i + 1 < r.Length; i += 2) { r[i] += dx; r[i + 1] += dy; }
        return r;
    }

    // ===== 回転（図形中心まわり）=====

    /// <summary>回転の軸となる図形中心（セル単位）。</summary>
    private static Vector2 Center(PartPrimitive p) => p switch
    {
        PartLine l => new((float)((l.X1 + l.X2) / 2), (float)((l.Y1 + l.Y2) / 2)),
        PartCircle c => new((float)c.Cx, (float)c.Cy),
        PartArc a => new((float)a.Cx, (float)a.Cy),
        PartRect r => new((float)(r.X + r.W / 2), (float)(r.Y + r.H / 2)),
        PartPolyline pl => PolyCenter(pl),
        PartText t => new((float)t.X, (float)t.Y),
        _ => new(0, 0),
    };

    private static Vector2 PolyCenter(PartPolyline pl)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i + 1 < pl.Points.Length; i += 2)
        {
            minX = Math.Min(minX, pl.Points[i]); maxX = Math.Max(maxX, pl.Points[i]);
            minY = Math.Min(minY, pl.Points[i + 1]); maxY = Math.Max(maxY, pl.Points[i + 1]);
        }
        return new((float)((minX + maxX) / 2), (float)((minY + maxY) / 2));
    }

    /// <summary>図形を中心 c まわりに deg 度回転。線/折れ線は座標へ焼き込み、矩形/弧は Rot へ加算。円/文字は不変。</summary>
    private static PartPrimitive RotatePrim(PartPrimitive p, Vector2 c, double deg) => p switch
    {
        PartLine l => RotLine(l, c, deg),
        PartPolyline pl => RotPoly(pl, c, deg),
        PartRect r => r with { Rot = r.Rot + deg },
        PartArc a => a with { Rot = a.Rot + deg },
        _ => p,
    };

    private static PartLine RotLine(PartLine l, Vector2 c, double deg)
    {
        var (x1, y1) = RotXY(l.X1, l.Y1, c, deg);
        var (x2, y2) = RotXY(l.X2, l.Y2, c, deg);
        return new PartLine(x1, y1, x2, y2);
    }

    private static PartPolyline RotPoly(PartPolyline pl, Vector2 c, double deg)
    {
        var pts = (double[])pl.Points.Clone();
        for (int i = 0; i + 1 < pts.Length; i += 2) { var (x, y) = RotXY(pts[i], pts[i + 1], c, deg); pts[i] = x; pts[i + 1] = y; }
        return new PartPolyline(pts);
    }

    private static (double x, double y) RotXY(double x, double y, Vector2 c, double deg)
    {
        double r = deg * Math.PI / 180.0, cos = Math.Cos(r), sin = Math.Sin(r);
        double dx = x - c.X, dy = y - c.Y;
        return (c.X + dx * cos - dy * sin, c.Y + dx * sin + dy * cos);
    }

    private static Vector2 RotVec(Vector2 p, Vector2 c, double deg)
    {
        var (x, y) = RotXY(p.X, p.Y, c, deg);
        return new((float)x, (float)y);
    }

    // ===== Undo/Redo =====

    // 図形・ポートに加えサイズ・役割も含む（LoadTemplate がこれらを変更するため Undo 復元対象にする）。
    private readonly record struct EditorSnapshot(
        List<PartPrimitive> Prims, List<PortDef> Ports, int W, int H, PartRole Role);

    private EditorSnapshot Snapshot() => new(new(_prims), new(_ports), _w, _h, CurrentRole());

    private void PushUndo()
    {
        _undo.Push(Snapshot());
        _redo.Clear();
    }

    // ドラッグ開始時に掴んだ状態を保留する。実際に変化したらリリース時に確定。
    private void BeginDragUndo()
    {
        _pendingUndo = Snapshot();
        _dragChanged = false;
    }

    // ドラッグ中に変化していれば保留分を Undo スタックへ確定する（無変化なら空 Undo を積まない）。
    private void CommitDragUndo()
    {
        if (_dragChanged && _pendingUndo is { } snap) { _undo.Push(snap); _redo.Clear(); }
        _pendingUndo = null;
        _dragChanged = false;
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        ReplaceAll(_undo.Pop());
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        ReplaceAll(_redo.Pop());
    }

    private void ReplaceAll(EditorSnapshot snap)
    {
        _prims.Clear(); _prims.AddRange(snap.Prims);
        _ports.Clear(); _ports.AddRange(snap.Ports);
        _w = snap.W; _h = snap.H;
        if (WidthBox is not null) WidthBox.Value = _w;
        if (HeightBox is not null) HeightBox.Value = _h;
        SelectRole(snap.Role);
        _selPrim = _selPort = -1;
        UpdateStatus();
        EditCanvas.Invalidate();
    }

    private void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        if (_selPrim >= 0) { PushUndo(); _prims.RemoveAt(_selPrim); _selPrim = -1; }
        else if (_selPort >= 0) { PushUndo(); _ports.RemoveAt(_selPort); _selPort = -1; }
        UpdateStatus();
        EditCanvas.Invalidate();
    }

    private void UpdateStatus()
    {
        if (StatusText is null) return;
        StatusText.Text = $"図形 {_prims.Count} / 接続点 {_ports.Count}    ツール: {ToolLabel(_tool)}"
            + (_tool == "polyline" ? "（右クリックで確定）"
               : _tool == "arc" ? "（外接矩形をドラッグ：横=幅・縦=高さで扁平率、上下方向で弧の向き）"
               : _tool == "rotate" ? "（図形をドラッグして15度単位で回転）" : "");
        UpdateArcEditor();
    }

    // 選択中プリミティブが弧のとき、フッタの扁平率 NumberBox を表示して ry/rx 比を反映する。
    private void UpdateArcEditor()
    {
        if (ArcFlatBox is null || ArcFlatLabel is null) return;
        bool isArc = _selPrim >= 0 && _prims[_selPrim] is PartArc;
        var vis = isArc ? Visibility.Visible : Visibility.Collapsed;
        ArcFlatBox.Visibility = ArcFlatLabel.Visibility = vis;
        if (isArc)
        {
            var a = (PartArc)_prims[_selPrim];
            _suppressArc = true;
            ArcFlatBox.Value = a.R > 1e-6 ? Math.Round(a.EffRy / a.R, 3) : 1.0;
            _suppressArc = false;
        }
    }

    private void OnArcFlatChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressArc || _selPrim < 0 || _prims[_selPrim] is not PartArc a) return;
        double ratio = double.IsNaN(args.NewValue) ? 1.0 : Math.Clamp(args.NewValue, 0.05, 20);
        PushUndo();
        _prims[_selPrim] = a with { Ry = a.R * ratio };   // 扁平率は連続値（スナップ不要）
        EditCanvas.Invalidate();
    }

    private static string ToolLabel(string t) => t switch
    {
        "select" => "選択", "line" => "線", "polyline" => "折れ線", "rect" => "矩形",
        "circle" => "円", "arc" => "弧", "rotate" => "回転", "text" => "文字", "port" => "接続点", _ => t,
    };

    // ===== 保存・閉じる =====

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        CommitPolyline();
        ReadSizeFromBoxes();   // 入力直後の保存でも最新の幅・高さを反映
        string name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) { await Warn("名前を入力してください。"); return; }
        if (_ports.Count < 2 && CurrentRole() != PartRole.NonSimulated)
        {
            await Warn("接続点（ポート）を2つ以上配置してください。"); return;
        }

        var def = new PartDefinition
        {
            Id = _id,
            Name = name,
            WidthCells = _w,
            HeightCells = _h,
            Role = CurrentRole(),
            Ports = _ports.OrderBy(p => p.BoundaryOffset).ToList(),   // 先頭=NetA・末尾=NetB
            Primitives = new(_prims),
        };
        Saved?.Invoke(def);
        Close();
    }

    private async Task Warn(string msg)
    {
        await new ContentDialog
        {
            Title = "確認", Content = msg, CloseButtonText = "OK", XamlRoot = Content.XamlRoot,
        }.ShowAsync();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
