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
    private void OnRunDrc(object sender, RoutedEventArgs e)
    {
        _drcHighlightRow = -1;
        CircuitNumberer.Number(_document);
        _lastDrcResults = DesignRuleCheck.CheckCrossReference(_document, _document.Library)
            .Concat(DesignRuleCheck.CheckDeviceTypeConsistency(_document, _document.Library))
            .ToList();
        // 二重コイル（コイル直列）はネットリスト依存のためシート単位で検査して集約する。
        foreach (var sh in _document.Sheets)
        {
            var net = NetlistBuilder.Build(sh, _document.Library);
            _lastDrcResults.AddRange(DesignRuleCheck.CheckSeriesCoils(sh, net));
        }

        int errCnt = _lastDrcResults.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnCnt = _lastDrcResults.Count(d => d.Severity == DiagnosticSeverity.Warning);

        if (_lastDrcResults.Count == 0)
        {
            DrcListView.ItemsSource = new List<string> { "問題なし（全シート）" };
            DrcStatusText.Text = "OK";
        }
        else
        {
            DrcListView.ItemsSource = _lastDrcResults.Select(FormatDiagnostic).ToList();
            DrcStatusText.Text = $"エラー {errCnt}  警告 {warnCnt}";
        }

        // 展開してDRCタブを選択
        if (_outputPanelCollapsed) ExpandOutputPanel();
        OutputTabView.SelectedIndex = 0;
    }

    private void OnDrcItemSelected(object sender, SelectionChangedEventArgs e)
    {
        int idx = DrcListView.SelectedIndex;
        if (idx < 0 || idx >= _lastDrcResults.Count) return;
        var diag = _lastDrcResults[idx];
        if (diag.Locations.Count == 0) return;
        var loc = diag.Locations[0];
        var target = _document.Sheets.FirstOrDefault(s => s.PageNumber == loc.PageNumber);
        // シート切替は SwitchToSheet 一本化（編集中コミット・選択/テストセッション整合を担保）。
        if (target != null && target != _sheet) SwitchToSheet(target);
        _drcHighlightRow = loc.CircuitNumber > 0 ? loc.CircuitNumber - 1 : -1;
        if (_drcHighlightRow >= 0) CenterViewOnRow(_drcHighlightRow);
        Canvas.Invalidate();
    }

    private void CenterViewOnRow(int row)
    {
        double cw = Canvas.ActualWidth, ch = Canvas.ActualHeight;
        if (cw <= 0 || ch <= 0) return;
        double eyMm = _geo.YRow(row);
        double scale = _viewport.Scale;
        _viewport.PanY = ch / 2 - eyMm * scale;
    }

    private void RefreshSearchResultPanel()
    {
        if (_find.Results.Count == 0)
        {
            SearchResultPanelView.ItemsSource = null;
            return;
        }
        SearchResultPanelView.ItemsSource = _find.Results.Select((r, i) =>
        {
            string sheetName = string.IsNullOrWhiteSpace(r.Sheet.Name)
                ? $"P{r.Sheet.PageNumber}"
                : r.Sheet.Name;
            return $"{sheetName}: {r.El.DeviceName ?? "(無名)"} (行{r.El.Pos.Row + 1}, 列{r.El.Pos.Column + 1})";
        }).ToList();
    }

    private void OnSearchResultItemSelected(object sender, SelectionChangedEventArgs e)
    {
        int idx = SearchResultPanelView.SelectedIndex;
        if (idx < 0 || idx >= _find.Results.Count) return;
        _find.Index = idx;
        JumpToFindResult();
        UpdateFindStatus();
    }

    private void OnRunConnectivity(object sender, RoutedEventArgs e)
    {
        CircuitNumberer.Number(_document);
        var sheetNet = NetlistBuilder.Build(_sheet, _document.Library);
        var issues = DesignRuleCheck.CheckVerticalCrossings(_sheet, sheetNet)
            .Concat(DesignRuleCheck.CheckLoadReachability(_sheet, sheetNet))
            .Concat(DesignRuleCheck.CheckSeriesCoils(_sheet, sheetNet))
            .ToList();

        ConnectivityListView.ItemsSource = issues.Count == 0
            ? (object)new List<string> { $"問題なし（{SheetDisplayName(_sheet, _document.Sheets.IndexOf(_sheet))}）" }
            : issues.Select(FormatDiagnostic).ToList();

        if (_outputPanelCollapsed) ExpandOutputPanel();
        OutputTabView.SelectedIndex = 2;
    }

    private void OnOutputPanelToggle(object sender, RoutedEventArgs e)
    {
        if (_outputPanelCollapsed)
            ExpandOutputPanel();
        else
            CollapseOutputPanel();
    }

    // 折りたたみ時もタブ列（右端の▼ボタン）を残すための高さ。
    private const double OutputPanelCollapsedHeight = 40;

    private void CollapseOutputPanel()
    {
        _outputPanelCollapsed = true;
        AnimateSize(OutputPanel, isWidth: false, to: OutputPanelCollapsedHeight);
        OutputCollapseBtn.Content = "▲";
    }

    private void ExpandOutputPanel()
    {
        _outputPanelCollapsed = false;
        AnimateSize(OutputPanel, isWidth: false, to: OutputPanelDefaultHeight);
        OutputCollapseBtn.Content = "▼";
    }

    private static string FormatDiagnostic(Diagnostic d)
    {
        string sev = d.Severity switch
        {
            DiagnosticSeverity.Error => "E",
            DiagnosticSeverity.Warning => "W",
            _ => "I"
        };
        string loc = d.Locations.Count > 0
            ? $" [P{d.Locations[0].PageNumber} 行{d.Locations[0].CircuitNumber}]"
            : "";
        return $"[{sev}] {d.Code}{loc}  {d.Message}";
    }

    // ===== ヘルパー =====

}
