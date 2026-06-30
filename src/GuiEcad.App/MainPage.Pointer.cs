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

namespace GuiEcad_App;

public sealed partial class MainPage
{
    // ===== ポインタ =====

    // 要素／縦コネクタ／枠／自由直線の選択は相互排他。選んだ1種以外を null にする（分岐ごとの重複クリアを集約）。
    private void SelectElement(ElementInstance e) { _selected = e; _selectedConnector = null; _selectedFrame = null; _selectedLine = null; _selectedDot = null; }
    private void SelectConnector(VerticalConnector c) { _selectedConnector = c; _selected = null; _selectedFrame = null; _selectedLine = null; _selectedDot = null; }
    private void SelectFrame(GroupFrame f) { _selectedFrame = f; _selected = null; _selectedConnector = null; _selectedLine = null; _selectedDot = null; }
    private void SelectLine(FreeLine l) { _selectedLine = l; _selected = null; _selectedConnector = null; _selectedFrame = null; _selectedDot = null; }
    private void SelectDot(ConnectionDot d) { _selectedDot = d; _selected = null; _selectedConnector = null; _selectedFrame = null; _selectedLine = null; }
    private void ClearSelection() { _selected = null; _selectedConnector = null; _selectedFrame = null; _selectedLine = null; _selectedDot = null; }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 右クリックはコンテキストメニュー専用（RightTapped で処理）。
        // 左用ロジックに流すと Canvas.CapturePointer によりタップ系ジェスチャが破棄され RightTapped が発火しない。
        // e.Handled も立てない（立てると同様に RightTapped が発火しなくなる）。
        if (e.GetCurrentPoint(Canvas).Properties.IsRightButtonPressed) return;

        // 描画エリアクリックで DRC ハイライトを消す
        if (_drcHighlightRow >= 0)
        {
            _drcHighlightRow = -1;
            Canvas.Invalidate();
        }

        // 前操作のドラッグ残骸を破棄（キャプチャ喪失等で固まるのを防ぐ）＋キーボード操作のためフォーカス確保
        _connStartRow = null;
        _frameStartMm = null;
        if (_editingElement is null && _editingComment is null && _editingRungComment is null) Canvas.Focus(FocusState.Programmatic);

        var pos = e.GetCurrentPoint(Canvas).Position;
        var (xMm, yMm) = ToWorld(pos);

        // スペースキー保持中は全ツール共通でパン（テストモード除く）
        // KeyDown フラグ＋GetKeyStateForCurrentThread の二重判定（Canvas がキーイベントを吸収する環境に対応）
        // 物理的にスペースが押されていなければ KeyUp の取りこぼしに備えてフラグを必ず落とす。
        // （残ると以降のクリックが常にパンになり、要素を選択できなくなる）
        bool spacePhysical =
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Space) & CoreVirtualKeyStates.Down) != 0;
        if (!spacePhysical) _spacePanActive = false;
        bool spaceDown = !_testMode && (_spacePanActive || spacePhysical);
        if (spaceDown)
        {
            _panning = true;
            _lastPointer = pos;
            Canvas.CapturePointer(e.Pointer);
            return;
        }
        int row = _geo.RowAt(yMm), col = _geo.ColAt(xMm);

        // 自前ダブルクリック検出: 選択/分岐ツールは CapturePointer で DoubleTapped が来ないため、
        // ツール非依存で機器名・枠ラベルのインライン編集を起動する（作画モードのみ）。
        if (!_testMode)
        {
            var now = DateTime.UtcNow;
            bool isDouble = (now - _lastClickTime).TotalMilliseconds < DoubleClickMs
                && Math.Abs(pos.X - _lastClickPos.X) < 8 && Math.Abs(pos.Y - _lastClickPos.Y) < 8;
            _lastClickTime = now;
            _lastClickPos = pos;
            if (isDouble)
            {
                if (HitTest(row, col) is ElementInstance dhit)
                {
                    SelectElement(dhit);
                    ShowDeviceNameEditor(dhit);
                    Canvas.Invalidate();
                    return;
                }
                if (HitTestFrame(xMm, yMm) is GroupFrame dframe) { ShowFrameLabelEditor(dframe, pos); return; }
                // 右母線右側 ダブルクリック → 行コメント編集
                // 要素配置は行数無制限で図面が下に伸びるため、行コメントも「実効描画行数」まで許可する
                // （Grid.Rows と 要素最大行+1 の大きい方。従来は Grid.Rows=8 固定で 9 行目以降に付けられなかった）。
                int drawnRows = _sheet.Elements.Count > 0
                    ? Math.Max(_sheet.Grid.Rows, _sheet.Elements.Max(el => el.Pos.Row) + 1)
                    : _sheet.Grid.Rows;
                double rightBusEdge = _geo.X(_sheet.Grid.Columns) + _geo.CellMm * 0.5;
                if (xMm > rightBusEdge && row >= 0 && row < drawnRows)
                {
                    var existing = _sheet.RungComments.Find(rc => rc.Row == row);
                    if (existing is null) { existing = new RungComment { Row = row }; _history.Execute(new AddRungCommentCommand(_sheet, existing)); }
                    ShowRungCommentEditor(existing, pos);
                    return;
                }
            }
        }

        // テストモード
        if (_testMode && _testSession != null)
        {
            var hit = HitTest(row, col);
            if (hit?.DeviceName is string dev)
            {
                if (hit.Kind is ElementKind.PushButtonNO or ElementKind.PushButtonNC)
                {
                    _testSession.SetInput(dev, true);
                    _testPressedDevice = dev;
                    Canvas.CapturePointer(e.Pointer);
                }
                else if (hit.Kind == ElementKind.SelectSwitch)
                {
                    CycleSelectSwitch(hit);
                }
                else
                {
                    _testSession.ToggleInput(dev);
                }
                UpdateTestStatus();
                Canvas.Invalidate();
            }
            else
            {
                // 空欄クリック：作画モードと同様にパン開始
                _panning = true;
                _lastPointer = pos;
                Canvas.CapturePointer(e.Pointer);
            }
            return;
        }

        // 作画モード：直線ツール（2点ドラッグ・格子点スナップ）
        if (_tool.Mode == ToolMode.PlaceLine)
        {
            if (xMm >= 0 && yMm >= 0)
            {
                var p = SnapLine(xMm, yMm);
                _lineStartMm = p;
                _lineCurMm = p;
                ClearSelection();
                Canvas.CapturePointer(e.Pointer);
            }
            return;
        }

        // 作画モード：接続点ツール（1クリックで●を配置・格子点スナップ）
        if (_tool.Mode == ToolMode.PlaceDot)
        {
            if (xMm >= 0 && yMm >= 0)
            {
                var (dx, dy) = SnapLine(xMm, yMm);
                _history.Execute(new PlaceDotCommand(_sheet, new ConnectionDot { XMm = dx, YMm = dy }));
                Canvas.Invalidate();
            }
            return;
        }

        // 作画モード：配線分断ツール（クリックしたセルの中央境界にトグル配置）。
        // 同一行の自動横配線を断ち切り、別ネットに分ける（短絡回避・線番分割）。
        if (_tool.Mode == ToolMode.PlaceWireBreak)
        {
            if (row >= 0 && col >= 0 && col < _sheet.Grid.Columns)
            {
                double boundary = col + 0.5;   // セル中央（整数ポート境界を避ける）
                var existing = _sheet.WireBreaks.Find(b => b.Row == row && Math.Abs(b.Boundary - boundary) < 0.01);
                if (existing is not null)
                    _history.Execute(new DeleteWireBreakCommand(_sheet, existing));
                else
                    _history.Execute(new AddWireBreakCommand(_sheet, new WireBreak { Row = row, Boundary = boundary }));
                Canvas.Invalidate();
            }
            return;
        }

        // 作画モード：枠ツール（ドラッグ開始・mm 連続座標）
        if (_tool.Mode == ToolMode.PlaceFrame)
        {
            if (xMm >= 0 && yMm >= 0)
            {
                _frameStartMm = (xMm, yMm);
                _frameCurMm = (xMm, yMm);
                ClearSelection();
                Canvas.CapturePointer(e.Pointer);
            }
            return;
        }

        // 作画モード：縦コネクタ配置（列境界をドラッグ開始）
        if (_tool.Mode == ToolMode.PlaceConnector)
        {
            double b = _geo.BoundaryAtHalf(xMm);   // セル中央（0.5）にもスナップ
            if (b >= 0 && b <= _sheet.Grid.Columns && row >= 0)
            {
                _connStartRow = row;
                _connCurRow = row;
                _connBoundary = b;
                Canvas.CapturePointer(e.Pointer);
            }
            return;
        }

        // 作画モード：配置
        if (PlaceKind is ElementKind kind)
        {
            if (row >= 0 && col >= 0 && col < _sheet.Grid.Columns && CellEmpty(row, col, null))
            {
                var part = _document.Library?.Get(PlacePartId);
                var el = new ElementInstance
                {
                    Kind = kind,
                    Pos = new GridPos(row, col),
                    PartId = part is not null ? PlacePartId : null,
                    CellWidth = part?.WidthCells ?? ElementCatalog.DefaultCellWidth(kind),
                };
                if (PlaceOrient is not null) el.Params[ParamKeys.Orient] = PlaceOrient;   // 主回路記号の向き
                _history.Execute(new PlaceElementCommand(_sheet, el));
                _selected = el;
                RefreshDevicePanel();
                RefreshPropertiesPanel();
                Canvas.Invalidate();
            }
            return;
        }

        // 作画モード：選択・移動（要素→縦コネクタ→枠の順にヒット判定）
        var hitElem = HitTest(row, col);
        if (hitElem is not null)
        {
            if (_selectedSet.Count > 1 && _selectedSet.Contains(hitElem))
            {
                // 範囲選択中の要素をドラッグ → 全選択要素・分岐線・自由直線を一括移動
                // SelectElement は呼ばない（多重選択ハイライトを維持する）
                _moving = hitElem;
                _moveStartPos = hitElem.Pos;
                _multiMoveOrigins = _selectedSet.ToDictionary(e => e, e => e.Pos);
                _multiMoveConnectorOrigins = _selectedConnectorSet.ToDictionary(
                    c => c, c => (c.Column, c.TopRow, c.BottomRow));
                _multiMoveLineOrigins = _selectedLineSet.ToDictionary(
                    l => l, l => (l.X1Mm, l.Y1Mm, l.X2Mm, l.Y2Mm));
                _multiMoveFrameOrigins = _selectedFrameSet.ToDictionary(
                    f => f, f => (f.TopLeft, f.VisualXMm, f.VisualYMm));
                _multiMoveDotOrigins = _selectedDotSet.ToDictionary(
                    d => d, d => (d.XMm, d.YMm));
            }
            else
            {
                // 範囲選択外の要素をクリック → 多重選択を解除して単体選択
                ClearMultiSelection();
                SelectElement(hitElem);
                _moving = hitElem;
                _moveStartPos = hitElem.Pos;
            }
        }
        else if (HitTestConnector(xMm, yMm) is VerticalConnector hitConn)
        {
            SelectConnector(hitConn);
            _movingConnector = hitConn;
            _connMoveStartColumn = hitConn.Column;
        }
        else if (HitTestFrame(xMm, yMm) is GroupFrame hitFrame)
        {
            SelectFrame(hitFrame);
            _movingFrame = true;
            _moveFrameOriginX = hitFrame.VisualXMm ?? _geo.X(hitFrame.TopLeft.Column);
            _moveFrameOriginY = hitFrame.VisualYMm ?? (_geo.YRow(hitFrame.TopLeft.Row) - _geo.CellMm * 0.4);
            _moveFrameClickX = xMm;
            _moveFrameClickY = yMm;
        }
        else if (HitTestDot(xMm, yMm) is ConnectionDot hitDot)
        {
            SelectDot(hitDot);   // 線の交点上に置かれるため自由直線より先に判定
        }
        else if (HitTestFreeLine(xMm, yMm) is FreeLine hitLine)
        {
            SelectLine(hitLine);
            _movingLine = true;
            _lineMoveClick = (xMm, yMm);
            _lineOrig = (hitLine.X1Mm, hitLine.Y1Mm, hitLine.X2Mm, hitLine.Y2Mm);
        }
        else
        {
            ClearSelection();
            ClearMultiSelection();
            if (row >= 0)
            {
                // 左母線より左（col < 0）でも1列目から選択できるよう 0 にクランプする。
                _rangeSelecting = true;
                _rangeStart = new GridPos(row, Math.Max(0, col));
                _rangeEnd = _rangeStart;
            }
            else
            {
                _panning = true;
                _lastPointer = pos;
            }
        }
        Canvas.CapturePointer(e.Pointer);
        UpdateHintText();
        RefreshPropertiesPanel();
        Canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(Canvas).Position;

        var (sxMm, syMm) = ToWorld(pos);
        int sRow = _geo.RowAt(syMm), sCol = _geo.ColAt(sxMm);
        StatusPos.Text = $"行: {Math.Max(sRow + 1, 1)}  列: {Math.Max(sCol + 1, 1)}";
        if (sRow >= 0 && sCol >= 0) _hoverCell = new GridPos(sRow, sCol);   // ペースト基準に使用
        // 記号配置ツール選択中はマウス追従の配置プレビューを再描画する。
        if (!_testMode && _tool.Mode == ToolMode.PlaceElement) Canvas.Invalidate();

        if (_rangeSelecting)
        {
            _rangeEnd = new GridPos(Math.Max(0, sRow), Math.Max(0, sCol));
            Canvas.Invalidate();
            return;
        }

        // 縦コネクタのドラッグ中：終端行を追従してプレビュー
        if (_connStartRow is not null)
        {
            _connCurRow = Math.Max(0, sRow);
            Canvas.Invalidate();
            return;
        }

        // 直線ドラッグ中：終端を格子点スナップで追従
        if (_lineStartMm is not null)
        {
            _lineCurMm = SnapLine(sxMm, syMm);
            Canvas.Invalidate();
            return;
        }

        // 枠ドラッグ中：終端座標を追従してプレビュー（mm 連続座標）
        if (_frameStartMm is not null)
        {
            _frameCurMm = (Math.Max(0, sxMm), Math.Max(0, syMm));
            Canvas.Invalidate();
            return;
        }

        if (_movingConnector is not null)
        {
            var (xMm, _) = ToWorld(pos);
            double newCol = _geo.BoundaryAtHalf(xMm);
            if (newCol > 0 && newCol < _sheet.Grid.Columns && newCol != _movingConnector.Column)
            {
                _movingConnector.Column = newCol;
                Canvas.Invalidate();
            }
            return;
        }

        // 自由直線のドラッグ移動（細分格子スナップで平行移動）
        if (_movingLine && _selectedLine is FreeLine ml)
        {
            double step = _geo.CellMm / LineSnapDiv;
            double dx = Math.Round((sxMm - _lineMoveClick.X) / step) * step;
            double dy = Math.Round((syMm - _lineMoveClick.Y) / step) * step;
            ml.X1Mm = _lineOrig.X1 + dx; ml.Y1Mm = _lineOrig.Y1 + dy;
            ml.X2Mm = _lineOrig.X2 + dx; ml.Y2Mm = _lineOrig.Y2 + dy;
            Canvas.Invalidate();
            return;
        }

        if (_movingFrame && _selectedFrame is GroupFrame movFr)
        {
            movFr.VisualXMm = _moveFrameOriginX + (sxMm - _moveFrameClickX);
            movFr.VisualYMm = _moveFrameOriginY + (syMm - _moveFrameClickY);
            Canvas.Invalidate();
            return;
        }

        if (_moving is not null)
        {
            var (xMm, yMm) = ToWorld(pos);
            int row = _geo.RowAt(yMm), col = _geo.ColAt(xMm);
            if (_multiMoveOrigins.Count > 0)
            {
                // 範囲選択の一括移動：アンカー要素の元位置からのデルタを全要素に適用する。
                int dRow = row - _moveStartPos.Row;
                int dCol = col - _moveStartPos.Column;
                if (dRow != _moving.Pos.Row - _moveStartPos.Row || dCol != _moving.Pos.Column - _moveStartPos.Column)
                {
                    bool valid = _multiMoveOrigins.Values.All(origin =>
                    {
                        int nr = origin.Row + dRow;
                        int nc = origin.Column + dCol;
                        return nr >= 0 && nc >= 0 && nc < _sheet.Grid.Columns;
                    });
                    if (valid)
                    {
                        foreach (var (elem, origin) in _multiMoveOrigins)
                            elem.Pos = new GridPos(origin.Row + dRow, origin.Column + dCol);
                        foreach (var (conn, orig) in _multiMoveConnectorOrigins)
                        {
                            conn.Column   = orig.Col + dCol;
                            conn.TopRow   = orig.TopRow + dRow;
                            conn.BottomRow = orig.BotRow + dRow;
                        }
                        double dxMm = dCol * _geo.CellMm, dyMm = dRow * _geo.CellMm;
                        foreach (var (line, orig) in _multiMoveLineOrigins)
                        {
                            line.X1Mm = orig.X1 + dxMm; line.Y1Mm = orig.Y1 + dyMm;
                            line.X2Mm = orig.X2 + dxMm; line.Y2Mm = orig.Y2 + dyMm;
                        }
                        foreach (var (frame, orig) in _multiMoveFrameOrigins)
                        {
                            frame.TopLeft = new GridPos(orig.TopLeft.Row + dRow, orig.TopLeft.Column + dCol);
                            frame.VisualXMm = orig.VisX.HasValue ? orig.VisX.Value + dxMm : (double?)null;
                            frame.VisualYMm = orig.VisY.HasValue ? orig.VisY.Value + dyMm : (double?)null;
                        }
                        foreach (var (dot, orig) in _multiMoveDotOrigins)
                        {
                            dot.XMm = orig.X + dxMm; dot.YMm = orig.Y + dyMm;
                        }
                        Canvas.Invalidate();
                    }
                }
            }
            else if (row >= 0 && col >= 0 && col < _sheet.Grid.Columns
                && (row != _moving.Pos.Row || col != _moving.Pos.Column)
                && CellEmpty(row, col, _moving))
            {
                _moving.Pos = new GridPos(row, col);
                Canvas.Invalidate();
            }
            return;
        }

        if (_panning)
        {
            _viewport.PanX += pos.X - _lastPointer.X;
            _viewport.PanY += pos.Y - _lastPointer.Y;
            _lastPointer = pos;
            Canvas.Invalidate();
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_rangeSelecting)
        {
            _rangeSelecting = false;
            int r1 = Math.Min(_rangeStart.Row, _rangeEnd.Row);
            int r2 = Math.Max(_rangeStart.Row, _rangeEnd.Row);
            int c1 = Math.Min(_rangeStart.Column, _rangeEnd.Column);
            int c2 = Math.Max(_rangeStart.Column, _rangeEnd.Column);
            _selectedSet = new HashSet<ElementInstance>(
                _sheet.Elements.Where(e => e.Pos.Row >= r1 && e.Pos.Row <= r2
                                        && e.Pos.Column >= c1 && e.Pos.Column <= c2));
            // 分岐線（縦コネクタ）も範囲に完全に収まるものを選択に含める。
            // Column は列境界（0..Columns）なので、選択列 c1..c2 の境界範囲 [c1, c2+1] で判定。
            _selectedConnectorSet = new HashSet<VerticalConnector>(
                _sheet.Connectors.Where(vc => vc.TopRow >= r1 && vc.BottomRow <= r2
                                           && vc.Column >= c1 && vc.Column <= c2 + 1));
            // 自由直線：両端が選択範囲（mm 換算）に完全に収まるものを含める。
            double rxL = _geo.X(c1), rxR = _geo.X(c2 + 1);
            double ryT = _geo.YRow(r1) - _geo.CellMm * 0.5, ryB = _geo.YRow(r2) + _geo.CellMm * 0.5;
            _selectedLineSet = new HashSet<FreeLine>(
                _sheet.FreeLines.Where(fl =>
                    Math.Min(fl.X1Mm, fl.X2Mm) >= rxL - 0.01 && Math.Max(fl.X1Mm, fl.X2Mm) <= rxR + 0.01 &&
                    Math.Min(fl.Y1Mm, fl.Y2Mm) >= ryT - 0.01 && Math.Max(fl.Y1Mm, fl.Y2Mm) <= ryB + 0.01));
            // 枠線：TopLeft と右下隅が選択範囲内に完全に収まるものを含める。
            _selectedFrameSet = new HashSet<GroupFrame>(
                _sheet.Frames.Where(f =>
                    f.TopLeft.Row >= r1 && f.TopLeft.Row + f.Height - 1 <= r2 &&
                    f.TopLeft.Column >= c1 && f.TopLeft.Column + f.Width - 1 <= c2));
            // 接続点：座標（mm 換算）が選択範囲に完全に収まるものを含める。
            _selectedDotSet = new HashSet<ConnectionDot>(
                _sheet.ConnectionDots.Where(d =>
                    d.XMm >= rxL - 0.01 && d.XMm <= rxR + 0.01 &&
                    d.YMm >= ryT - 0.01 && d.YMm <= ryB + 0.01));
            Canvas.ReleasePointerCapture(e.Pointer);
            UpdateHintText();
            Canvas.Invalidate();
            return;
        }

        // 直線ドラッグの確定（格子点間）
        if (_lineStartMm is (double lsx, double lsy))
        {
            _lineStartMm = null;
            var (lex, ley) = _lineCurMm;
            if (Math.Abs(lex - lsx) > 0.01 || Math.Abs(ley - lsy) > 0.01)
                _history.Execute(new PlaceFreeLineCommand(_sheet,
                    new FreeLine { X1Mm = lsx, Y1Mm = lsy, X2Mm = lex, Y2Mm = ley }));
            Canvas.Invalidate();
            return;
        }

        // 枠ドラッグの確定（mm 連続座標で自由作成・グリッドにスナップしない）
        if (_frameStartMm is (double fsx, double fsy))
        {
            _frameStartMm = null;
            double x1 = Math.Min(fsx, _frameCurMm.X), y1 = Math.Min(fsy, _frameCurMm.Y);
            double wMm = Math.Abs(_frameCurMm.X - fsx), hMm = Math.Abs(_frameCurMm.Y - fsy);
            // 半セル未満の極小ドラッグは誤操作とみなし無視
            if (wMm >= _geo.CellMm * 0.5 && hMm >= _geo.CellMm * 0.5)
            {
                // 行挿入/削除ロジック用のグリッド近似値（フォールバック）も併せて設定
                int c1 = Math.Max(0, _geo.ColAt(x1));
                int r1 = Math.Max(0, _geo.RowAt(y1 + _geo.CellMm * 0.4));
                int gw = Math.Max(1, (int)Math.Round(wMm / _geo.CellMm));
                int gh = Math.Max(1, (int)Math.Round(hMm / _geo.CellMm));
                var frame = new GroupFrame
                {
                    TopLeft = new GridPos(r1, c1),
                    Width = gw,
                    Height = gh,
                    VisualXMm = x1,
                    VisualYMm = y1,
                    VisualWidthMm = wMm,
                    VisualHeightMm = hMm,
                };
                _history.Execute(new AddFrameCommand(_sheet, frame));
                SelectFrame(frame);
            }
            Canvas.ReleasePointerCapture(e.Pointer);
            UpdateHintText();
            Canvas.Invalidate();
            return;
        }

        // 縦コネクタ配置の確定（開始行と終端行が異なれば作成）
        if (_connStartRow is int sr)
        {
            int top = Math.Min(sr, _connCurRow), bot = Math.Max(sr, _connCurRow);
            if (top != bot)
                _history.Execute(new AddConnectorCommand(_sheet,
                    new VerticalConnector { Column = _connBoundary, TopRow = top, BottomRow = bot }));
            _connStartRow = null;
            Canvas.ReleasePointerCapture(e.Pointer);
            Canvas.Invalidate();
            return;
        }

        // テストモード：PushButton リリース
        if (_testMode && _testSession != null && _testPressedDevice != null)
        {
            _testSession.SetInput(_testPressedDevice, false);
            _testPressedDevice = null;
            UpdateTestStatus();
            Canvas.Invalidate();
        }

        // 縦コネクタドラッグ移動の確定
        if (_movingConnector is not null && _movingConnector.Column != _connMoveStartColumn)
            _history.Execute(new MoveConnectorCommand(_sheet, _movingConnector, _connMoveStartColumn, _movingConnector.Column));
        _movingConnector = null;

        // 枠ドラッグ移動の確定（実際に移動して Visual 座標が確定したときのみ）。
        // VisualXMm/YMm が null（=ドラッグせずクリックのみ・グリッド由来の枠）だと
        // 旧コードは null!.Value でアンラップして例外→クラッシュしていた。
        if (_movingFrame && _selectedFrame is GroupFrame movFr2
            && movFr2.VisualXMm is double newFx && movFr2.VisualYMm is double newFy
            && (newFx != _moveFrameOriginX || newFy != _moveFrameOriginY))
            _history.Execute(new MoveFrameCommand(_sheet, movFr2, _moveFrameOriginX, _moveFrameOriginY, newFx, newFy));
        _movingFrame = false;

        // 自由直線のドラッグ移動の確定（位置が変わっていれば）
        if (_movingLine && _selectedLine is FreeLine rl)
        {
            var (ox1, oy1, ox2, oy2) = _lineOrig;
            if (rl.X1Mm != ox1 || rl.Y1Mm != oy1 || rl.X2Mm != ox2 || rl.Y2Mm != oy2)
                _history.Execute(new MoveFreeLineCommand(_sheet, rl, ox1, oy1, ox2, oy2,
                    rl.X1Mm, rl.Y1Mm, rl.X2Mm, rl.Y2Mm));
        }
        _movingLine = false;

        // ドラッグ移動のコマンド登録（位置が変わっていれば）
        if (_multiMoveOrigins.Count > 0)
        {
            // 範囲選択の一括移動：移動した要素・分岐線・自由直線を BatchCommand で Undo/Redo 可能にする。
            var cmds = new List<IUndoCommand>();
            foreach (var (elem, from) in _multiMoveOrigins)
                if (elem.Pos != from)
                    cmds.Add(new MoveElementCommand(_sheet, elem, from, elem.Pos));
            foreach (var (conn, orig) in _multiMoveConnectorOrigins)
                if (conn.Column != orig.Col || conn.TopRow != orig.TopRow || conn.BottomRow != orig.BotRow)
                    cmds.Add(new MoveConnectorFullCommand(_sheet, conn,
                        orig.Col, orig.TopRow, orig.BotRow,
                        conn.Column, conn.TopRow, conn.BottomRow));
            foreach (var (line, orig) in _multiMoveLineOrigins)
                if (line.X1Mm != orig.X1 || line.Y1Mm != orig.Y1)
                    cmds.Add(new MoveFreeLineCommand(_sheet, line,
                        orig.X1, orig.Y1, orig.X2, orig.Y2,
                        line.X1Mm, line.Y1Mm, line.X2Mm, line.Y2Mm));
            foreach (var (frame, orig) in _multiMoveFrameOrigins)
                if (frame.TopLeft != orig.TopLeft || frame.VisualXMm != orig.VisX || frame.VisualYMm != orig.VisY)
                    cmds.Add(new MoveFrameFullCommand(_sheet, frame,
                        orig.TopLeft, orig.VisX, orig.VisY,
                        frame.TopLeft, frame.VisualXMm, frame.VisualYMm));
            if (cmds.Count > 0)
                _history.Execute(new BatchCommand(_sheet, cmds));
            _multiMoveOrigins.Clear();
            _multiMoveConnectorOrigins.Clear();
            _multiMoveLineOrigins.Clear();
            _multiMoveFrameOrigins.Clear();
        }
        else if (_moving is not null && _moving.Pos != _moveStartPos)
            _history.Execute(new MoveElementCommand(_sheet, _moving, _moveStartPos, _moving.Pos));

        _moving = null;
        _panning = false;
        Canvas.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>ドラッグ/パン等の進行中ポインタ状態を破棄する（ツール切替・モード切替・キャプチャ喪失時の保険）。</summary>
    private void ResetDragState()
    {
        _panning = false;
        _moving = null;
        _multiMoveOrigins.Clear();
        _multiMoveConnectorOrigins.Clear();
        _multiMoveLineOrigins.Clear();
        _multiMoveFrameOrigins.Clear();
        _movingFrame = false;
        _movingConnector = null;
        _rangeSelecting = false;
        _connStartRow = null;
        _frameStartMm = null;
        _lineStartMm = null;
        _movingLine = false;
    }

    // キャプチャ喪失（フライアウト表示・フォーカス移動等）で PointerReleased が来ないと
    // _panning 等が立ったまま残り、次の操作がパンになってしまう。確実に破棄する。
    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_testMode && _testSession != null && _testPressedDevice != null)
        {
            _testSession.SetInput(_testPressedDevice, false);
            UpdateTestStatus();
        }
        _testPressedDevice = null;
        ResetDragState();
        Canvas.Invalidate();
    }

    // ===== インライン編集（機器名 / 枠ラベル）。起動は OnPointerPressed の自前ダブルクリック検出 =====

    private void ShowFrameLabelEditor(GroupFrame frame, Windows.Foundation.Point screenPos)
    {
        _editingFrame = frame;
        FrameLabelBox.Text = frame.Label ?? string.Empty;
        double scale = DipsPerMm * _viewport.Zoom;
        double x = _geo.X(frame.TopLeft.Column) * scale + _viewport.PanX;
        double y = (_geo.YRow(frame.TopLeft.Row) - _geo.CellMm * 0.4) * scale + _viewport.PanY - 18;
        FrameLabelBox.Margin = new Thickness(x, Math.Max(0, y), 0, 0);
        FrameLabelBox.Visibility = Visibility.Visible;
        FrameLabelBox.Focus(FocusState.Programmatic);
        FrameLabelBox.SelectAll();
    }

    private void CommitFrameLabel(bool accept)
    {
        if (_editingFrame is null) return;
        var frame = _editingFrame;
        _editingFrame = null;
        FrameLabelBox.Visibility = Visibility.Collapsed;
        if (accept)
        {
            string newLabel = FrameLabelBox.Text.Trim();
            if (newLabel != (frame.Label ?? string.Empty))
                _history.Execute(new RenameFrameCommand(_sheet, frame, frame.Label ?? string.Empty, newLabel));
        }
        Canvas.Invalidate();
    }

    private void OnFrameLabelBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Tab)
        { CommitFrameLabel(accept: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape)
        { CommitFrameLabel(accept: false); e.Handled = true; }
    }

    private void OnFrameLabelBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingFrame is not null) CommitFrameLabel(accept: true);
    }

    private void ShowDeviceNameEditor(ElementInstance elem)
    {
        _editingElement = elem;
        DeviceNameBox.Text = elem.DeviceName ?? string.Empty;

        double scale = DipsPerMm * _viewport.Zoom;
        double cellDip = _geo.CellMm * scale;
        double x = _geo.X(elem.Pos.Column) * scale + _viewport.PanX + cellDip * 0.5 - 45;
        double y = _geo.YRow(elem.Pos.Row) * scale + _viewport.PanY - 14;
        DeviceNameBox.Margin = new Thickness(x, y, 0, 0);
        DeviceNameBox.Visibility = Visibility.Visible;
        DeviceNameBox.Focus(FocusState.Programmatic);
        DeviceNameBox.SelectAll();
    }

    private void CommitDeviceName(bool accept)
    {
        if (_editingElement is null) return;
        var elem = _editingElement;
        _editingElement = null;
        DeviceNameBox.Visibility = Visibility.Collapsed;

        if (accept)
        {
            string newName = DeviceNameBox.Text.Trim();
            if (newName != (elem.DeviceName ?? string.Empty))
            {
                _history.Execute(new RenameDeviceCommand(_sheet, elem, elem.DeviceName, newName));
                RefreshDevicePanel();
            }
        }
        Canvas.Invalidate();
    }

    private void OnDeviceNameBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Tab)
        { CommitDeviceName(accept: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape)
        { CommitDeviceName(accept: false); e.Handled = true; }
    }

    private void OnDeviceNameBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingElement is not null) CommitDeviceName(accept: true);
    }

    private void ShowCommentEditor(ElementInstance elem)
    {
        _editingComment = elem;
        CommentBox.Text = elem.Comment ?? string.Empty;

        double scale = DipsPerMm * _viewport.Zoom;
        double cellDip = _geo.CellMm * scale;
        double x = _geo.X(elem.Pos.Column) * scale + _viewport.PanX + cellDip * 0.5 - 45;
        // コメント表示位置（記号の下側・やや記号寄り）に合わせて編集ボックスも出す。
        double y = _geo.YRow(elem.Pos.Row) * scale + _viewport.PanY + cellDip * 0.5 - 1;
        CommentBox.Margin = new Thickness(x, y, 0, 0);
        CommentBox.Visibility = Visibility.Visible;
        CommentBox.Focus(FocusState.Programmatic);
        CommentBox.SelectAll();
    }

    private void CommitComment(bool accept)
    {
        if (_editingComment is null) return;
        var elem = _editingComment;
        _editingComment = null;
        CommentBox.Visibility = Visibility.Collapsed;

        if (accept)
        {
            string newComment = CommentBox.Text.Trim();
            string? old = elem.Comment;
            if (newComment != (old ?? string.Empty))
            {
                string? next = string.IsNullOrEmpty(newComment) ? null : newComment;
                _history.Execute(new SetCommentCommand(_sheet, elem, old, next));
            }
        }
        Canvas.Invalidate();
    }

    private void OnCommentBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Tab)
        { CommitComment(accept: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape)
        { CommitComment(accept: false); e.Handled = true; }
    }

    private void OnCommentBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingComment is not null) CommitComment(accept: true);
    }

    private void ShowRungCommentEditor(RungComment rc, Windows.Foundation.Point screenPos)
    {
        _editingRungComment = rc;
        RungCommentBox.Text = rc.Text;

        double scale = DipsPerMm * _viewport.Zoom;
        double rightEdgeDip = (_geo.X(_sheet.Grid.Columns) + _geo.CellMm * 0.5) * scale + _viewport.PanX + 4;
        double y = _geo.YRow(rc.Row) * scale + _viewport.PanY - 14;
        RungCommentBox.Margin = new Thickness(rightEdgeDip, y, 0, 0);
        RungCommentBox.Visibility = Visibility.Visible;
        RungCommentBox.Focus(FocusState.Programmatic);
        RungCommentBox.SelectAll();
    }

    private void CommitRungComment(bool accept)
    {
        if (_editingRungComment is null) return;
        var rc = _editingRungComment;
        _editingRungComment = null;
        RungCommentBox.Visibility = Visibility.Collapsed;

        if (accept)
        {
            string newText = RungCommentBox.Text.Trim();
            if (newText != rc.Text)
                _history.Execute(new SetRungCommentCommand(_sheet, rc, rc.Text, newText));
        }
        Canvas.Invalidate();
    }

    private void OnRungCommentBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Tab)
        { CommitRungComment(accept: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape)
        { CommitRungComment(accept: false); e.Handled = true; }
    }

    private void OnRungCommentBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingRungComment is not null) CommitRungComment(accept: true);
    }

}
