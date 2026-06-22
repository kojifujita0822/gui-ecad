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
    private void ShowDrawingContextMenu(Point pos)
    {
        if (_testMode || _menuShowing) return;
        _menuShowing = true;
        var (xMm, yMm) = ToWorld(pos);
        int row = _geo.RowAt(yMm), col = _geo.ColAt(xMm);
        var menu = new MenuFlyout();
        var ctxElem = HitTest(row, col);

        // --- コピー＆貼り付け（要素上／範囲選択中はコピー、クリップボードがあればクリック位置へ貼り付け） ---
        if (ctxElem is not null || _selectedSet.Count > 0 || _selectedConnectorSet.Count > 0 || _selectedLineSet.Count > 0)
            AddItem(menu, "コピー(Ctrl+C)", () =>
            {
                if (_selectedSet.Count == 0 && _selectedConnectorSet.Count == 0 && _selectedLineSet.Count == 0 && ctxElem is not null)
                    SelectElement(ctxElem);
                CopySelection();
            });
        if (_clipboard is not null && row >= 0 && col >= 0)
            AddItem(menu, "貼り付け(Ctrl+V)", () =>
            {
                _hoverCell = new GridPos(row, col);   // クリック位置を左上に貼り付け
                PasteSelection();
            });
        int afterCopyPaste = menu.Items.Count;

        if (ctxElem is ElementInstance hitElem)
        {
            AddItem(menu, "削除(Del)", () =>
            {
                _history.Execute(new DeleteElementCommand(_sheet, hitElem));
                _selected = null;
                RefreshDevicePanel();
                Canvas.Invalidate();
            });
            AddItem(menu, "コメント編集(F2)", () => { SelectElement(hitElem); ShowCommentEditor(hitElem); });
            AddItem(menu, "機器名変更(Enter)", () => { SelectElement(hitElem); ShowDeviceNameEditor(hitElem); });
        }
        else if (HitTestConnector(xMm, yMm) is VerticalConnector hitVc)
        {
            AddItem(menu, "縦コネクタ削除", () =>
            {
                _history.Execute(new DeleteConnectorCommand(_sheet, hitVc));
                _selectedConnector = null;
                Canvas.Invalidate();
            });
        }
        else if (HitTestFrame(xMm, yMm) is GroupFrame hitFrame)
        {
            AddItem(menu, "ラベル編集", () => ShowFrameLabelEditor(hitFrame, pos));
            AddItem(menu, "削除", () =>
            {
                _history.Execute(new DeleteFrameCommand(_sheet, hitFrame));
                _selectedFrame = null;
                Canvas.Invalidate();
            });
            menu.Items.Add(new MenuFlyoutSeparator());
            var styleSub = new MenuFlyoutSubItem { Text = "線種" };
            foreach (var style in new[] { LineStyle.Solid, LineStyle.Dashed, LineStyle.Dotted })
            {
                var s = style;
                string label = s switch
                {
                    LineStyle.Solid => "実線",
                    LineStyle.Dashed => "破線（既定）",
                    LineStyle.Dotted => "点線",
                    _ => s.ToString(),
                };
                var item = new MenuFlyoutItem { Text = label };
                item.Click += (_, _) =>
                {
                    _history.Execute(new SetFrameBorderStyleCommand(_sheet, hitFrame, hitFrame.BorderStyle, s));
                    Canvas.Invalidate();
                };
                styleSub.Items.Add(item);
            }
            menu.Items.Add(styleSub);
        }
        else if (row >= 0 && row < _sheet.Grid.Rows)
        {
            AddItem(menu, $"行 {row + 1} の前に行を挿入", () =>
            {
                _history.Execute(new InsertRowCommand(_sheet, row));
                Canvas.Invalidate();
            });
            AddItem(menu, "末尾に行を追加", () =>
            {
                _history.Execute(new InsertLastRowCommand(_sheet));
                Canvas.Invalidate();
            });
            if (_sheet.Grid.Rows > 1)
                AddItem(menu, $"行 {row + 1} を削除", () =>
                {
                    _history.Execute(new DeleteRowCommand(_sheet, row));
                    Canvas.Invalidate();
                });
        }

        // コピー＆貼り付けと以降の項目の間に区切り線を挿入
        if (afterCopyPaste > 0 && menu.Items.Count > afterCopyPaste)
            menu.Items.Insert(afterCopyPaste, new MenuFlyoutSeparator());

        if (menu.Items.Count > 0)
        {
            menu.Closed += (_, _) => _menuShowing = false;
            menu.ShowAt(Canvas, pos);
        }
        else
        {
            _menuShowing = false;
        }
    }

    private static void AddItem(MenuFlyout m, string text, Action click)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => click();
        m.Items.Add(item);
    }

    // 接点右クリックメニュー本体（WndProc フックと ContextRequested の両方から呼ばれる）
    private void ShowContactContextMenu(Point pos)
    {
        if (!_testMode || _testSession is null) return;

        var (xMm, yMm) = ToWorld(pos);
        int row = _geo.RowAt(yMm), col = _geo.ColAt(xMm);
        var hit = HitTest(row, col);

        if (hit is null || hit.DeviceName is null) return;
        if (hit.Kind is not (ElementKind.ContactNO or ElementKind.ContactNC)) return;

        string dev = hit.DeviceName;
        bool isForced = _testSession.State.Inputs.TryGetValue(dev, out var cur) && cur;

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = $"接点：{dev}  [{(isForced ? "手動ON中" : "シミュレーション依存")}]",
            IsEnabled = false,
        });
        menu.Items.Add(new MenuFlyoutSeparator());

        var forceOnItem = new MenuFlyoutItem { Text = "手動でON（強制閉路）", IsEnabled = !isForced };
        forceOnItem.Click += (_, _) =>
        {
            _testSession.SetInput(dev, true);
            UpdateTestStatus();
            Canvas.Invalidate();
        };
        menu.Items.Add(forceOnItem);

        var releaseItem = new MenuFlyoutItem { Text = "手動を解除（シミュレーション依存に戻す）", IsEnabled = isForced };
        releaseItem.Click += (_, _) =>
        {
            _testSession.SetInput(dev, false);
            UpdateTestStatus();
            Canvas.Invalidate();
        };
        menu.Items.Add(releaseItem);

        menu.ShowAt(Canvas, pos);
    }

    // 右クリック — 作画/テスト両モードのメインルート（CanvasControl は UIElement なので RightTapped で発火）
    private void OnCanvasRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var pos = e.GetPosition(Canvas);
        e.Handled = true;
        if (_testMode && _testSession is not null) ShowContactContextMenu(pos);
        else if (!_testMode) ShowDrawingContextMenu(pos);
    }

    // ===== ズーム/パン =====

}
