using System.IO;
using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace GuiEcad_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    public Window? MainWindow => _window;
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();

            var cmdArgs = Environment.GetCommandLineArgs();
            if (cmdArgs.Length > 1
                && File.Exists(cmdArgs[1])
                && Path.GetExtension(cmdArgs[1]).Equals(".gcad", StringComparison.OrdinalIgnoreCase)
                && _window is MainWindow mainWindow)
            {
                _ = mainWindow.OpenFileOnStartupAsync(cmdArgs[1]);
            }
        }
        catch (Exception ex)
        {
            AppLog.Crash(ex);
            throw;
        }
    }
}
