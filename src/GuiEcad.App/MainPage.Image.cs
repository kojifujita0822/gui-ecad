using GuiEcad.Model;
using GuiEcad.Rendering;
using GuiEcad_App.Commands;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace GuiEcad_App;

public sealed partial class MainPage
{
    // ===== 画像挿入（BMP/PNG）=====

    // 画面 96 DPI 換算（WinUI の既定 DPI）。px → mm: px / 96 * 25.4。
    private const double PxToMm = 25.4 / 96.0;
    // 挿入直後の画像が図面に対して大きすぎないよう、初期表示サイズの上限（mm、長辺基準）。
    private const double InitialImageMaxMm = 120.0;

    private async void OnInsertImageClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(GetPickerWindowId());
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".png");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            await Win2DRenderer.PreloadImageAsync(Canvas.Device, file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"画像の読み込みに失敗しました: {ex.Message}");
            return;
        }

        var pxSize = Win2DRenderer.GetImagePixelSize(file.Path) ?? new Size2D(96, 96);
        double wMm = pxSize.Width * PxToMm, hMm = pxSize.Height * PxToMm;
        double scale = Math.Min(1.0, InitialImageMaxMm / Math.Max(wMm, hMm));
        wMm *= scale; hMm *= scale;

        double x = _hoverCell.Row >= 0 && _hoverCell.Column >= 0 ? _geo.X(_hoverCell.Column) : _geo.MarginMm;
        double y = _hoverCell.Row >= 0 && _hoverCell.Column >= 0 ? _geo.YRow(_hoverCell.Row) : _geo.MarginMm;

        var image = new ImageInsert { FilePath = file.Path, XMm = x, YMm = y, WidthMm = wMm, HeightMm = hMm };
        _history.Execute(new AddImageCommand(_sheet, image));
        SelectImage(image);
        RefreshPropertiesPanel();
        Canvas.Invalidate();
    }

    /// <summary>ドキュメントが持つ全画像を事前ロードし直す（新規作成・読込の直後に呼ぶ）。
    /// 旧ドキュメントのキャッシュは破棄し、新ドキュメントの画像を非同期ロードして完了ごとに再描画する。</summary>
    private void ReloadImageCacheForDocument(LadderDocument doc)
    {
        Win2DRenderer.ClearImageCache();
        var device = Canvas.Device;
        if (device is null) return;
        foreach (var path in doc.Sheets.SelectMany(s => s.Images).Select(i => i.FilePath).Distinct())
            _ = PreloadImageThenInvalidateAsync(device, path);
    }

    private async Task PreloadImageThenInvalidateAsync(CanvasDevice device, string filePath)
    {
        try
        {
            await Win2DRenderer.PreloadImageAsync(device, filePath);
            Canvas.Invalidate();
        }
        catch (Exception ex) { AppLog.Debug($"[IMAGE-LOAD-ERROR] {filePath}: {ex.Message}"); }
    }
}
