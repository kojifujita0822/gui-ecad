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
    // ===== フロートツールパレット（Jw_cad 風・ドラッグ移動／ドック⇄フロート切替／上下端へ吸着で横並び化） =====
    // ドック状態。値は永続化フォーマットの数値と一致させる（旧 "0/1" との後方互換: 0=Left, 1=Float）。
    private enum PaletteDock { Left = 0, Float = 1, Top = 2, Bottom = 3 }
    private PaletteDock _paletteDock = PaletteDock.Float;
    private bool _paletteDragging;
    private Point _paletteDragOffset;

    // 上下端への吸着しきい値（ToolOverlay 端からこの距離以内でドロップすると吸着）。
    private const double PaletteSnapThreshold = 40.0;

    private static string PalettePosPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GuiEcad", "palette-pos.txt");

    // 起動時に保存済みのドック状態／フロート位置を復元する（未保存なら作図エリア左上に浮かせる）。
    private void LoadPaletteState()
    {
        var dock = PaletteDock.Float;   // 既定: フロート
        double left = 4, top = 4;       // 既定: 作図エリア左上
        try
        {
            if (System.IO.File.Exists(PalettePosPath))
            {
                var parts = System.IO.File.ReadAllText(PalettePosPath).Trim().Split(',');
                if (parts.Length >= 3 &&
                    int.TryParse(parts[0], out int d) &&
                    double.TryParse(parts[1], out double l) &&
                    double.TryParse(parts[2], out double t))
                {
                    if (Enum.IsDefined(typeof(PaletteDock), d)) dock = (PaletteDock)d;
                    left = Math.Max(0, l);
                    top = Math.Max(0, t);
                }
            }
        }
        catch { /* 設定読込失敗は無視（既定で続行） */ }

        XamlCanvas.SetLeft(ToolPaletteFloat, left);
        XamlCanvas.SetTop(ToolPaletteFloat, top);
        SetPaletteDock(dock);
        UpdateFloatMaxHeight();
    }

    private void SavePaletteState()
    {
        try
        {
            double l = XamlCanvas.GetLeft(ToolPaletteFloat); if (double.IsNaN(l)) l = 4;
            double t = XamlCanvas.GetTop(ToolPaletteFloat);  if (double.IsNaN(t)) t = 4;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PalettePosPath)!);
            System.IO.File.WriteAllText(PalettePosPath,
                $"{(int)_paletteDock},{(int)Math.Round(l)},{(int)Math.Round(t)}");
        }
        catch { /* 設定保存失敗は致命的でない */ }
    }

    // ツール中身（ToolScroll）を各ホスト（左ドック／フロート／上下帯）へ付け替え、縦横を切り替える。
    private void SetPaletteDock(PaletteDock dock)
    {
        // 現在の親から外してから移す（同一要素は同時に1親のみ）
        ToolDockContent.Child = null;
        ToolFloatContent.Child = null;
        ToolTopDockContent.Child = null;
        ToolBottomDockContent.Child = null;

        // 既定はすべて非表示／ゼロサイズ。該当ホストだけ後で有効化する。
        ToolDockHost.Visibility = Visibility.Collapsed;
        ToolDockColumn.Width = new GridLength(0);
        ToolPaletteFloat.Visibility = Visibility.Collapsed;
        ToolTopDock.Visibility = Visibility.Collapsed;
        ToolBottomDock.Visibility = Visibility.Collapsed;

        bool horizontal = dock is PaletteDock.Top or PaletteDock.Bottom;
        SetPaletteOrientation(horizontal);

        switch (dock)
        {
            case PaletteDock.Left:
                ToolDockContent.Child = ToolScroll;
                ToolDockHost.Visibility = Visibility.Visible;
                ToolDockColumn.Width = GridLength.Auto;
                break;
            case PaletteDock.Top:
                ToolTopDockContent.Child = ToolScroll;
                ToolTopDock.Visibility = Visibility.Visible;
                break;
            case PaletteDock.Bottom:
                ToolBottomDockContent.Child = ToolScroll;
                ToolBottomDock.Visibility = Visibility.Visible;
                break;
            default: // Float
                ToolFloatContent.Child = ToolScroll;
                ToolPaletteFloat.Visibility = Visibility.Visible;
                break;
        }
        _paletteDock = dock;
    }

    // 縦並び（左ドック／フロート）と横並び（上下帯）の切り替え。区切り線も縦横を入れ替える。
    private void SetPaletteOrientation(bool horizontal)
    {
        ToolStackPanel.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        ToolStackPanel.Width = horizontal ? double.NaN : 64;
        // スクロールバーは常時表示（一番下の「その他▼」へ確実に到達できるよう）。
        ToolScroll.HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Visible : ScrollBarVisibility.Disabled;
        ToolScroll.VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Visible;

        foreach (var child in ToolStackPanel.Children)
        {
            // StackPanel 直下の Rectangle は区切り線のみ。横並びでは縦線・縦並びでは横線にする。
            if (child is Microsoft.UI.Xaml.Shapes.Rectangle r)
            {
                if (horizontal)
                {
                    r.Width = 1; r.Height = double.NaN;
                    r.HorizontalAlignment = HorizontalAlignment.Center;
                    r.VerticalAlignment = VerticalAlignment.Stretch;
                    r.Margin = new Thickness(1, 4, 1, 4);
                }
                else
                {
                    r.Height = 1; r.Width = double.NaN;
                    r.HorizontalAlignment = HorizontalAlignment.Stretch;
                    r.VerticalAlignment = VerticalAlignment.Center;
                    r.Margin = new Thickness(4, 1, 4, 1);
                }
            }
        }
    }

    // ドック⇄フロートのトグル。フロート以外 → フロート、フロート → 左ドック。
    private void OnPaletteDockToggle(object sender, RoutedEventArgs e)
    {
        bool wasHorizontal = _paletteDock is PaletteDock.Top or PaletteDock.Bottom;
        var next = _paletteDock == PaletteDock.Float ? PaletteDock.Left : PaletteDock.Float;
        // 上下端ドックから切り離すと旧フロート位置が画面外/ゼロサイズで残り「消える」ことがある。
        // 横ドック → フロート時は必ず作図エリア左上の見える位置へ戻す。
        if (next == PaletteDock.Float && wasHorizontal)
        {
            XamlCanvas.SetLeft(ToolPaletteFloat, 4);
            XamlCanvas.SetTop(ToolPaletteFloat, 4);
        }
        SetPaletteDock(next);
        if (next == PaletteDock.Float) UpdateFloatMaxHeight();
        SavePaletteState();
    }

    // フロートパレットの高さを作図エリアに連動させ、はみ出しを防ぐ（一回り小さめに収める）。
    // 収まらない分はスクロールバー（常時表示）で送る。
    private void UpdateFloatMaxHeight()
    {
        double avail = ToolOverlay.ActualHeight;
        if (avail <= 0) return;
        ToolFloatContent.MaxHeight = Math.Max(120, avail - 24);
    }

    private void OnPalettePressed(object sender, PointerRoutedEventArgs e)
    {
        _paletteDragging = true;
        var p = e.GetCurrentPoint(ToolOverlay).Position;
        double cl = XamlCanvas.GetLeft(ToolPaletteFloat); if (double.IsNaN(cl)) cl = 0;
        double ct = XamlCanvas.GetTop(ToolPaletteFloat);  if (double.IsNaN(ct)) ct = 0;
        _paletteDragOffset = new Point(p.X - cl, p.Y - ct);
        ToolPaletteHandle.CapturePointer(e.Pointer);
    }

    private void OnPaletteMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_paletteDragging) return;
        var p = e.GetCurrentPoint(ToolOverlay).Position;
        // 作図エリア（ToolOverlay）の内側にクランプ。右パネルの裏へ潜り込んで消えるのを防ぐ。
        double maxX = Math.Max(0, ToolOverlay.ActualWidth - ToolPaletteFloat.ActualWidth);
        double maxY = Math.Max(0, ToolOverlay.ActualHeight - ToolPaletteFloat.ActualHeight);
        XamlCanvas.SetLeft(ToolPaletteFloat, Math.Clamp(p.X - _paletteDragOffset.X, 0, maxX));
        XamlCanvas.SetTop(ToolPaletteFloat, Math.Clamp(p.Y - _paletteDragOffset.Y, 0, maxY));

        // 吸着ゾーンに入っていれば吸着先を半透明帯でプレビュー表示。
        UpdateSnapPreview(SnapTargetAt(p.Y));
    }

    // ドロップ位置（ToolOverlay 基準の Y）から吸着先を返す。吸着圏外なら null。
    private PaletteDock? SnapTargetAt(double y)
    {
        double h = ToolOverlay.ActualHeight;
        if (y <= PaletteSnapThreshold) return PaletteDock.Top;
        if (h > 0 && y >= h - PaletteSnapThreshold) return PaletteDock.Bottom;
        return null;
    }

    // 吸着プレビュー帯の表示更新（target=null で非表示）。
    private void UpdateSnapPreview(PaletteDock? target)
    {
        if (target is not (PaletteDock.Top or PaletteDock.Bottom))
        {
            ToolSnapPreview.Visibility = Visibility.Collapsed;
            return;
        }
        const double bandHeight = 52.0;
        ToolSnapPreview.Width = ToolOverlay.ActualWidth;
        ToolSnapPreview.Height = bandHeight;
        XamlCanvas.SetLeft(ToolSnapPreview, 0);
        XamlCanvas.SetTop(ToolSnapPreview,
            target == PaletteDock.Top ? 0 : Math.Max(0, ToolOverlay.ActualHeight - bandHeight));
        ToolSnapPreview.Visibility = Visibility.Visible;
    }

    // フロートパレットを作図エリア内へ収め直す（起動時の復元位置やウィンドウ/パネルのリサイズ対策）。
    private void ClampPaletteIntoView()
    {
        if (_paletteDock != PaletteDock.Float) return;
        double maxX = Math.Max(0, ToolOverlay.ActualWidth - ToolPaletteFloat.ActualWidth);
        double maxY = Math.Max(0, ToolOverlay.ActualHeight - ToolPaletteFloat.ActualHeight);
        double l = XamlCanvas.GetLeft(ToolPaletteFloat); if (double.IsNaN(l)) l = 0;
        double t = XamlCanvas.GetTop(ToolPaletteFloat);  if (double.IsNaN(t)) t = 0;
        XamlCanvas.SetLeft(ToolPaletteFloat, Math.Clamp(l, 0, maxX));
        XamlCanvas.SetTop(ToolPaletteFloat, Math.Clamp(t, 0, maxY));
    }

    private void OnToolOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFloatMaxHeight();
        ClampPaletteIntoView();
    }

    private void OnPaletteReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_paletteDragging) return;
        _paletteDragging = false;
        ToolPaletteHandle.ReleasePointerCapture(e.Pointer);
        ToolSnapPreview.Visibility = Visibility.Collapsed;

        // ドロップ位置が上端／下端の吸着圏内なら吸着して横並び化。
        var p = e.GetCurrentPoint(ToolOverlay).Position;
        var target = SnapTargetAt(p.Y);
        if (target.HasValue) SetPaletteDock(target.Value);

        SavePaletteState();
    }

    // ===== SVG ツールバーアイコン =====

    private async Task LoadToolIconsAsync()
    {
        var items = new (ElementKind kind, Image img)[]
        {
            (ElementKind.ContactNO,      IconContactNO),
            (ElementKind.ContactNC,      IconContactNC),
            (ElementKind.PushButtonNO,   IconPushButtonNO),
            (ElementKind.PushButtonNC,   IconPushButtonNC),
            (ElementKind.TimerContactNO, IconTimerContactNO),
            (ElementKind.TimerContactNC, IconTimerContactNC),
            (ElementKind.Coil,           IconCoil),
            (ElementKind.Lamp,           IconLamp),
            (ElementKind.Terminal,       IconTerminal),
        };
        // ダークテーマでは黒線アイコンが背景に埋もれるため明色で生成する。
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var iconColor = dark ? new Color(255, 220, 220, 225) : DrawingTheme.Black;
        foreach (var (kind, img) in items)
            img.Source = await SvgToImageSourceAsync(SvgRenderer.GenerateSymbolSvg(kind, color: iconColor));
    }

    private static async Task<SvgImageSource> SvgToImageSourceAsync(string svg)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(svg);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var source = new SvgImageSource();
        await source.SetSourceAsync(stream);
        return source;
    }
}
