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
    private void CopySelection()
    {
        // 要素：範囲選択 → 単一選択の順で対象を決める。
        var elems = _selectedSet.Count > 0
            ? _selectedSet.ToList()
            : _selected is not null ? new List<ElementInstance> { _selected } : new();
        // 分岐線（縦コネクタ）：範囲選択 → 単一選択の順。
        var conns = _selectedConnectorSet.Count > 0
            ? _selectedConnectorSet.ToList()
            : _selectedConnector is not null ? new List<VerticalConnector> { _selectedConnector } : new();
        // 自由直線：範囲選択 → 単一選択の順。
        var lines = _selectedLineSet.Count > 0
            ? _selectedLineSet.ToList()
            : _selectedLine is not null ? new List<FreeLine> { _selectedLine } : new();
        // 接続点：範囲選択 → 単一選択の順。
        var dots = _selectedDotSet.Count > 0
            ? _selectedDotSet.ToList()
            : _selectedDot is not null ? new List<ConnectionDot> { _selectedDot } : new();
        if (elems.Count == 0 && conns.Count == 0 && lines.Count == 0 && dots.Count == 0) return;

        // 選択全体の左上を原点として相対座標に変換する（要素・分岐線・自由直線・接続点を含めて最小を取る）。
        int minRow = int.MaxValue, minCol = int.MaxValue;
        foreach (var e in elems) { minRow = Math.Min(minRow, e.Pos.Row); minCol = Math.Min(minCol, e.Pos.Column); }
        foreach (var c in conns) { minRow = Math.Min(minRow, c.TopRow); minCol = Math.Min(minCol, (int)Math.Floor(c.Column)); }
        foreach (var l in lines)
        {
            minRow = Math.Min(minRow, _geo.RowAt(Math.Min(l.Y1Mm, l.Y2Mm)));
            minCol = Math.Min(minCol, _geo.ColAt(Math.Min(l.X1Mm, l.X2Mm)));
        }
        foreach (var d in dots)
        {
            minRow = Math.Min(minRow, _geo.RowAt(d.YMm));
            minCol = Math.Min(minCol, _geo.ColAt(d.XMm));
        }
        // 自由直線・接続点は mm 座標なので、原点セルの mm 位置を引いて相対化する。
        double oxMm = _geo.X(minCol), oyMm = _geo.YRow(minRow) - _geo.CellMm * 0.5;

        _clipboard = new ClipboardData(
            Elements: elems.Select(e =>
            {
                var clone = e.DeepClone();
                clone.Pos = new GridPos(e.Pos.Row - minRow, e.Pos.Column - minCol);
                return clone;
            }).ToList(),
            Connectors: conns.Select(c => new VerticalConnector
            {
                Column = c.Column - minCol,        // double のまま（.5 の中央位置も維持）
                TopRow = c.TopRow - minRow,
                BottomRow = c.BottomRow - minRow,
            }).ToList(),
            FreeLines: lines.Select(l => new FreeLine
            {
                X1Mm = l.X1Mm - oxMm, Y1Mm = l.Y1Mm - oyMm,
                X2Mm = l.X2Mm - oxMm, Y2Mm = l.Y2Mm - oyMm,
                Style = l.Style,
            }).ToList(),
            Dots: dots.Select(d => new ConnectionDot
            {
                XMm = d.XMm - oxMm, YMm = d.YMm - oyMm,
            }).ToList(),
            OriginRow: minRow,
            OriginCol: minCol);
    }

    private void PasteSelection()
    {
        if (_clipboard is null) return;
        // 貼り付け基準はマウス位置セル（クリップボードの左上が来る）。
        // 範囲選択コピー後は _selected が null になるため、マウス位置を優先する。
        int baseRow = _hoverCell.Row;
        int baseCol = _hoverCell.Column;

        var placedElems = _clipboard.Elements
            .Select(e =>
            {
                var placed = e.DeepClone();
                placed.Pos = new GridPos(baseRow + e.Pos.Row, baseCol + e.Pos.Column);
                // 機器名はそのまま複製（同名＝同一機器を指す。自動リネームしない）
                return placed;
            })
            .ToList();
        var placedConns = _clipboard.Connectors
            .Select(c => new VerticalConnector
            {
                Column = baseCol + c.Column,
                TopRow = baseRow + c.TopRow,
                BottomRow = baseRow + c.BottomRow,
            })
            .ToList();
        // 自由直線・接続点：原点セルの mm 位置を足して貼り付け位置へ平行移動。
        double bxMm = _geo.X(baseCol), byMm = _geo.YRow(baseRow) - _geo.CellMm * 0.5;
        var placedLines = _clipboard.FreeLines
            .Select(l => new FreeLine
            {
                X1Mm = bxMm + l.X1Mm, Y1Mm = byMm + l.Y1Mm,
                X2Mm = bxMm + l.X2Mm, Y2Mm = byMm + l.Y2Mm,
                Style = l.Style,
            })
            .ToList();
        var placedDots = _clipboard.Dots
            .Select(d => new ConnectionDot { XMm = bxMm + d.XMm, YMm = byMm + d.YMm })
            .ToList();

        var cmds = placedElems.Select(p => (IUndoCommand)new PlaceElementCommand(_sheet, p))
            .Concat(placedConns.Select(c => (IUndoCommand)new AddConnectorCommand(_sheet, c)))
            .Concat(placedLines.Select(l => (IUndoCommand)new PlaceFreeLineCommand(_sheet, l)))
            .Concat(placedDots.Select(d => (IUndoCommand)new PlaceDotCommand(_sheet, d)))
            .ToList();
        if (cmds.Count == 0) return;
        _history.Execute(new BatchCommand(_sheet, cmds));

        // 貼り付け直後は新要素・分岐線・自由直線・接続点を選択状態にして、続けて移動・削除できるようにする。
        _selectedSet = new HashSet<ElementInstance>(placedElems);
        _selectedConnectorSet = new HashSet<VerticalConnector>(placedConns);
        _selectedLineSet = new HashSet<FreeLine>(placedLines);
        _selectedDotSet = new HashSet<ConnectionDot>(placedDots);
        ClearSelection();
        RefreshDevicePanel();
        Canvas.Invalidate();
    }

}
