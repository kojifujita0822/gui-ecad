using System.IO;
using Microsoft.UI.Xaml;

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
