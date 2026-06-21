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

    // 要素／縦コネクタ／枠の選択は相互排他。選んだ1種以外を null にする（分岐ごとの重複クリアを集約）。
    private void SelectElement(ElementInstance e) { _selected = e; _selectedConnector = null; _selectedFrame = null; }
    private void SelectConnector(VerticalConnector c) { _selectedConnector = c; _selected = null; _selectedFrame = null; }
    private void SelectFrame(GroupFrame f) { _selectedFrame = f; _selected = null; _selectedConnector = null; }
    private void ClearSelection() { _selected = null; _selectedConnector = null; _selectedFrame = null; }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 右クリックはコンテキストメニュー専用（RightTapped で処理）。
        // 左用ロジックに流すと Canvas.CapturePointer によりタップ系ジェスチャが破棄され RightTapped が発火しない。
        // e.Handled も立てない（立てると同様に RightTapped が発火しなくなる）。
        if (e.GetCurrentPoint(Canvas).Properties.IsRightButtonPressed) return;

        // 前操作のドラッグ残骸を破棄（キャプチャ喪失等で固まるのを防ぐ）＋キーボード操作のためフォーカス確保
        _connStartRow = null;
        _frameStartMm = null;
        if (_editingElement is null && _editingComment is null && _editingRungComment is null) Canvas.Focus(FocusState.Programmatic);

        var pos = e.GetCurrentPoint(Canvas).Position;
        var (xMm, yMm) = ToWorld(pos);

        // スペースキー保持中は全ツール共通でパン（テストモード除く）
        // KeyDown フラグ＋GetKeyStateForCurrentThread の二重判定（Canvas がキーイベントを吸収する環境に対応）
        bool spaceDown = !_testMode && (_spacePanActive ||
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Space) & CoreVirtualKeyStates.Down) != 0);
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
                double rightBusEdge = _geo.X(_sheet.Grid.Columns) + _geo.CellMm * 0.5;
                if (xMm > rightBusEdge && row >= 0 && row < _sheet.Grid.Rows)
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

        // 作画モード：枠ツール（ドラッグ開始・mm 連続座標）
        if (_placeFrame)
        {
            if (xMm >= 0 && yMm >= 0)
            {
                _frameStartMm = (xMm, yMm);
                _frameCurMm = (xMm, yMm);
                _selectedFrame = null;
                Canvas.CapturePointer(e.Pointer);
            }
            return;
        }

        // 作画モード：縦コネクタ配置（列境界をドラッグ開始）
        if (_placeConnector)
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
        if (_placeKind is ElementKind kind)
        {
            if (row >= 0 && col >= 0 && col < _sheet.Grid.Columns && CellEmpty(row, col, null))
            {
                var part = _document.Library?.Get(_placePartId);
                var el = new ElementInstance
                {
                    Kind = kind,
                    Pos = new GridPos(row, col),
                    PartId = part is not null ? _placePartId : null,
                    CellWidth = part?.WidthCells ?? ElementCatalog.DefaultCellWidth(kind),
                };
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
            SelectElement(hitElem);
            _moving = hitElem;
            _moveStartPos = hitElem.Pos;
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
        else
        {
            ClearSelection();
            _selectedSet.Clear();
            if (row >= 0 && col >= 0)
            {
                _rangeSelecting = true;
                _rangeStart = new GridPos(row, col);
                _rangeEnd = _rangeStart;
            }
            else
            {
                _panning = true;
                _lastPointer = pos;
            }
        }
        Canvas.CapturePointer(e.Pointer);
        RefreshPropertiesPanel();
        Canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(Canvas).Position;

        var (sxMm, syMm) = ToWorld(pos);
        int sRow = _geo.RowAt(syMm), sCol = _geo.ColAt(sxMm);
        StatusPos.Text = $"行: {Math.Max(sRow + 1, 1)}  列: {Math.Max(sCol + 1, 1)}";

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
            if (row >= 0 && col >= 0 && col < _sheet.Grid.Columns
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
            _panX += pos.X - _lastPointer.X;
            _panY += pos.Y - _lastPointer.Y;
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
            Canvas.ReleasePointerCapture(e.Pointer);
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
                _selectedFrame = frame;
            }
            Canvas.ReleasePointerCapture(e.Pointer);
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

        // 枠ドラッグ移動の確定
        if (_movingFrame && _selectedFrame is GroupFrame movFr2
            && (movFr2.VisualXMm != _moveFrameOriginX || movFr2.VisualYMm != _moveFrameOriginY))
            _history.Execute(new MoveFrameCommand(_sheet, movFr2, _moveFrameOriginX, _moveFrameOriginY,
                movFr2.VisualXMm!.Value, movFr2.VisualYMm!.Value));
        _movingFrame = false;

        // ドラッグ移動のコマンド登録（位置が変わっていれば）
        if (_moving is not null && _moving.Pos != _moveStartPos)
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
        _movingFrame = false;
        _movingConnector = null;
        _rangeSelecting = false;
        _connStartRow = null;
        _frameStartMm = null;
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
        double scale = DipsPerMm * _zoom;
        double x = _geo.X(frame.TopLeft.Column) * scale + _panX;
        double y = (_geo.YRow(frame.TopLeft.Row) - _geo.CellMm * 0.4) * scale + _panY - 18;
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

        double scale = DipsPerMm * _zoom;
        double cellDip = _geo.CellMm * scale;
        double x = _geo.X(elem.Pos.Column) * scale + _panX + cellDip * 0.5 - 45;
        double y = _geo.YRow(elem.Pos.Row) * scale + _panY - 14;
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

        double scale = DipsPerMm * _zoom;
        double cellDip = _geo.CellMm * scale;
        double x = _geo.X(elem.Pos.Column) * scale + _panX + cellDip * 0.5 - 45;
        double y = _geo.YRow(elem.Pos.Row) * scale + _panY - 14 + 28;   // 機器名Boxの下
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

        double scale = DipsPerMm * _zoom;
        double rightEdgeDip = (_geo.X(_sheet.Grid.Columns) + _geo.CellMm * 0.5) * scale + _panX + 4;
        double y = _geo.YRow(rc.Row) * scale + _panY - 14;
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
