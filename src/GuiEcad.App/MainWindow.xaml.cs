using System.IO;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace GuiEcad_App;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));

        // ×ボタンでの未保存破棄を防ぐ（一旦キャンセルして非同期確認後に閉じる）。
        AppWindow.Closing += OnAppWindowClosing;
    }

    private bool _allowClose;

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        if (RootFrame.Content is not MainPage page || !page.IsDirty) return;

        // WinUI の Closing は同期イベントのため、一旦キャンセルして非同期確認を行う。
        args.Cancel = true;
        if (await page.ConfirmDiscardIfDirtyAsync())
        {
            _allowClose = true;
            Close();
        }
    }

    private async void OnDragOver(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.OfType<StorageFile>().Any(f =>
                    f.Name.EndsWith(".gcad", StringComparison.OrdinalIgnoreCase)))
                {
                    e.AcceptedOperation = DataPackageOperation.Copy;
                    return;
                }
            }
            e.AcceptedOperation = DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<StorageFile>()
            .FirstOrDefault(f => f.Name.EndsWith(".gcad", StringComparison.OrdinalIgnoreCase));
        if (file is null) return;

        if (RootFrame.Content is not MainPage page) return;
        if (!await page.ConfirmDiscardIfDirtyAsync()) return;
        await page.LoadFileAsync(file.Path);
    }

    /// <summary>起動引数・ファイル関連付けによる初回ファイルオープン。未保存確認は不要（起動直後のため）。
    /// LoadFileAsync はオートセーブ復元確認等で ContentDialog.ShowAsync を呼びうるため、
    /// MainPage が Content にセットされるだけでなく、ビジュアルツリーへ接続され XamlRoot が
    /// 確定するまで待つ（未確定のまま ShowAsync すると XamlRoot 例外で握りつぶされ、
    /// 復元ダイアログもエラー表示も出ないまま無題で起動してしまう）。</summary>
    public async Task OpenFileOnStartupAsync(string path)
    {
        // MainPage が Content にセットされるまで最大 30 tick 待つ
        MainPage? page = null;
        for (int i = 0; i < 30 && page is null; i++)
        {
            await Task.Yield();
            page = RootFrame.Content as MainPage;
        }
        if (page is null) return;

        // 続けて XamlRoot が確定する（Loaded）まで待つ。二重チェックで取りこぼしを防ぐ
        // （Loaded 購読前に発火済みでも、購読直後の XamlRoot 確認で捕捉できる）。
        if (page.XamlRoot is null)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLoaded(object s, RoutedEventArgs e) => tcs.TrySetResult();
            page.Loaded += OnLoaded;
            if (page.XamlRoot is not null) tcs.TrySetResult();
            await tcs.Task;
            page.Loaded -= OnLoaded;
        }

        await page.LoadFileAsync(path);
    }

    /// <summary>
    /// 開いているファイル名（未保存は「無題」）と変更フラグをタイトルバーへ反映する。
    /// 形式: "&lt;ファイル名&gt;[*] - GuiEcad"。
    /// </summary>
    public void SetDocumentTitle(string? path, bool dirty)
    {
        string name = string.IsNullOrEmpty(path) ? "無題" : Path.GetFileNameWithoutExtension(path);
        string title = $"{name}{(dirty ? " *" : "")} - GuiEcad";
        AppTitleBar.Title = title;
        Title = title;
    }
}
