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
    private void UpdateFindResults()
    {
        _find.Search(_document, FindBox.Text.Trim());
        if (_find.Index >= 0) JumpToFindResult();   // 最初の一致へ（別シートなら切替）
        UpdateFindStatus();
        RefreshSearchResultPanel();
        Canvas.Invalidate();
    }

    /// <summary>現在の検索インデックスの一致要素へジャンプ（別シートなら切替＋ビューを中央へパン）。</summary>
    private void JumpToFindResult()
    {
        if (_find.Current is not { } hit) return;
        // シート切替は SwitchToSheet 一本化（編集中コミット・選択/テストセッション整合を担保）。
        if (hit.Sheet != _sheet) SwitchToSheet(hit.Sheet);
        CenterViewOnElement(hit.El);   // 一致要素が画面中央に来るようパン（「移動しない」対策）
        Canvas.Invalidate();
    }

    /// <summary>指定要素が作図エリアの中央に来るようビューをパンする（ズーム倍率は変えない）。</summary>
    private void CenterViewOnElement(ElementInstance el)
    {
        double cw = Canvas.ActualWidth, ch = Canvas.ActualHeight;
        if (cw <= 0 || ch <= 0) return;
        var (l, right) = PartResolver.BoundarySpan(el, _document.Library);
        double exMm = (_geo.X(l) + _geo.X(right)) / 2;   // 要素中心の mm 座標
        double eyMm = _geo.YRow(el.Pos.Row);
        double scale = _viewport.Scale;                  // mm → DIP
        _viewport.PanX = cw / 2 - exMm * scale;
        _viewport.PanY = ch / 2 - eyMm * scale;
    }

    private void UpdateFindStatus()
    {
        if (_find.Count == 0)
            FindStatus.Text = FindBox.Text.Length > 0 ? "見つかりません" : string.Empty;
        else
            FindStatus.Text = $"{_find.Index + 1} / {_find.Count}";
    }

    private void OnFindBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { OnFindNext(sender, e); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { CloseFindBar(); e.Handled = true; }
    }

    private void OnFindNext(object sender, RoutedEventArgs e)
    {
        if (_find.Count == 0) return;
        _find.Next();
        JumpToFindResult();
        UpdateFindStatus();
    }

    private void OnFindPrev(object sender, RoutedEventArgs e)
    {
        if (_find.Count == 0) return;
        _find.Prev();
        JumpToFindResult();
        UpdateFindStatus();
    }

    private void OnReplaceOne(object sender, RoutedEventArgs e)
    {
        if (_find.Current is not { } cur) return;
        string newName = ReplaceBox.Text.Trim();
        if (string.IsNullOrEmpty(newName)) return;

        var (sheet, elem) = cur;
        _history.Execute(new RenameDeviceCommand(sheet, elem, elem.DeviceName, newName));
        UpdateFindResults();
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        string query = FindBox.Text.Trim();
        string newName = ReplaceBox.Text.Trim();
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(newName)) return;

        foreach (var (sheet, elem) in FindController.Matches(_document, query))
            _history.Execute(new RenameDeviceCommand(sheet, elem, elem.DeviceName, newName));
        UpdateFindResults();
    }

    private void OnFindClose(object sender, RoutedEventArgs e) => CloseFindBar();

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _find.Clear();
        Canvas.Invalidate();
    }

    private void ToggleFindBar()
    {
        if (FindBar.Visibility == Visibility.Visible) CloseFindBar();
        else { FindBar.Visibility = Visibility.Visible; FindBox.Focus(FocusState.Programmatic); }
    }

    // ===== 下部出力パネル (P3) =====

}
