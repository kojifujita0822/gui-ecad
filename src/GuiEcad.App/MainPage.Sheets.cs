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
    private void RebuildNavTree()
    {
        // ページ番号をリスト順に 1..N へ正規化（CR/PDF/DRC の位置表示が "0-1" になるのを防ぐ）。
        // シート変更時は必ず本メソッドを通るため、ここを単一の正規化ポイントにする。
        for (int i = 0; i < _document.Sheets.Count; i++)
            _document.Sheets[i].PageNumber = i + 1;

        _suppressNavEvents = true;
        NavTree.RootNodes.Clear();
        _sheetNodeMap.Clear();
        for (int i = 0; i < _document.Sheets.Count; i++)
        {
            var sheet = _document.Sheets[i];
            var node = new TreeViewNode { Content = SheetDisplayName(sheet, i) };
            _sheetNodeMap[node] = sheet;
            NavTree.RootNodes.Add(node);
            if (sheet == _sheet) NavTree.SelectedNode = node;
        }
        _suppressNavEvents = false;
        UpdateStatusExtras();
    }

    private static string SheetDisplayName(Sheet sheet, int index)
        => string.IsNullOrWhiteSpace(sheet.Name) ? $"シート {index + 1}" : sheet.Name;

    /// <summary>再生成せずに指定シートのタブを選択状態にする（イベント抑制つき）。</summary>
    private void SelectNavTreeSheet(Sheet sheet)
    {
        _suppressNavEvents = true;
        foreach (var (node, s) in _sheetNodeMap)
            if (ReferenceEquals(s, sheet)) { NavTree.SelectedNode = node; break; }
        _suppressNavEvents = false;
    }

    private void OnNavTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_suppressNavEvents) return;
        if (NavTree.SelectedNode is TreeViewNode node && _sheetNodeMap.TryGetValue(node, out var sheet))
            SwitchToSheet(sheet);
    }

    private void SwitchToSheet(Sheet sheet)
    {
        if (ReferenceEquals(sheet, _sheet)) return;
        if (_editingElement != null) CommitDeviceName(accept: true);
        if (_editingComment != null) CommitComment(accept: true);
        if (_editingRungComment != null) CommitRungComment(accept: true);
        _sheet = sheet;
        _selected = null;
        _selectedConnector = null;
        _moving = null;
        RefreshPropertiesPanel();
        // テストセッションはシート単位。各シートの状態を保持して切り替える
        if (_testMode)
        {
            _testSession = GetOrCreateTestSession(_sheet);
            UpdateTestStatus();
        }
        SelectNavTreeSheet(_sheet);
        Canvas.Invalidate();
    }

    private void OnAddSheetBtn(object sender, RoutedEventArgs e)
    {
        CommitDeviceName(true);
        CommitComment(true);
        CommitRungComment(true);
        var def = _document.Settings.DefaultBus;
        var sheet = new Sheet
        {
            Grid = new GridSpec { Columns = _sheet.Grid.Columns, Rows = 8 },
            Bus = new BusConfig { LeftName = def.LeftName, RightName = def.RightName, PowerLabel = def.PowerLabel },
        };
        _document.Sheets.Add(sheet);
        _sheet = sheet;
        ClearSelection();
        _moving = null;
        if (_testMode) _testSession = GetOrCreateTestSession(_sheet);
        RebuildNavTree();
        RefreshPropertiesPanel();
        MarkDirty();
        Canvas.Invalidate();
    }

    private void OnDeleteSheetBtn(object sender, RoutedEventArgs e)
    {
        if (_document.Sheets.Count <= 1) return;
        CommitDeviceName(true);
        CommitComment(true);
        CommitRungComment(true);
        var sheet = _sheet;
        int idx = _document.Sheets.IndexOf(sheet);
        _document.Sheets.Remove(sheet);
        _history.RemoveCommandsForSheet(sheet);
        _testSessions.Remove(sheet);
        _sheet = _document.Sheets[Math.Clamp(idx, 0, _document.Sheets.Count - 1)];
        ClearSelection();
        _moving = null;
        if (_testMode) _testSession = GetOrCreateTestSession(_sheet);
        _find.RemoveSheet(sheet);
        RebuildNavTree();
        UpdateFindStatus();
        RefreshPropertiesPanel();
        MarkDirty();
        Canvas.Invalidate();
    }

    private void OnNavTreeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        => OnRenameSheetBtn(sender, e);   // ダブルクリック → 現在選択中シートの名前変更ダイアログ

    private async void OnRenameSheetBtn(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Text = _sheet.Name, PlaceholderText = "シート名" };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var dialog = new ContentDialog
        {
            Title = "シート名の変更",
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _sheet.Name = box.Text.Trim();
            RebuildNavTree();
            MarkDirty();
        }
    }

    // ===== コピー・ペースト =====

}
