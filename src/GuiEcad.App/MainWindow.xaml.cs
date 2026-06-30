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

    /// <summary>起動引数・ファイル関連付けによる初回ファイルオープン。未保存確認は不要（起動直後のため）。</summary>
    public async Task OpenFileOnStartupAsync(string path)
    {
        // Yield してウィンドウ・ページの初期化完了を待つ。
        await Task.Yield();
        if (RootFrame.Content is not MainPage page) return;
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
