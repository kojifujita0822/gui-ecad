using GuiEcad.Persistence;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GuiEcad_App;

public sealed partial class MainPage : Page
{
    // ===== オートセーブ（<元ファイル名>.autosave.GCAD へ定期バックグラウンド保存） =====

    private const int AutosaveIntervalMinMinutes = 1;
    private const int AutosaveIntervalMaxMinutes = 10;
    private const int AutosaveIntervalDefaultMinutes = 5;

    private DispatcherTimer? _autosaveTimer;
    private int _autosaveIntervalMinutes = AutosaveIntervalDefaultMinutes;

    private static string AutosaveIntervalSettingPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GuiEcad", "autosave-interval.txt");

    /// <summary>保存対象ファイルパスから、そのオートセーブ先パスを導出する（例: foo.gcad → foo.autosave.GCAD）。</summary>
    private static string AutosavePath(string path) => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(path) ?? "",
        System.IO.Path.GetFileNameWithoutExtension(path) + ".autosave.GCAD");

    private static void DeleteAutosaveIfExists(string path)
    {
        try
        {
            string autosavePath = AutosavePath(path);
            if (File.Exists(autosavePath)) File.Delete(autosavePath);
        }
        catch { /* オートセーブ控えの削除失敗は致命的でない */ }
    }

    // 起動時に保存済みの間隔設定を復元する（未保存なら既定 5 分）。
    private void LoadAutosaveInterval()
    {
        try
        {
            if (File.Exists(AutosaveIntervalSettingPath)
                && int.TryParse(File.ReadAllText(AutosaveIntervalSettingPath).Trim(), out int minutes)
                && minutes is >= AutosaveIntervalMinMinutes and <= AutosaveIntervalMaxMinutes)
                _autosaveIntervalMinutes = minutes;
        }
        catch { /* 設定読込失敗は既定値で続行 */ }
    }

    private void StartAutosaveTimer()
    {
        _autosaveTimer ??= new DispatcherTimer();
        _autosaveTimer.Tick -= OnAutosaveTick;
        _autosaveTimer.Tick += OnAutosaveTick;
        _autosaveTimer.Interval = TimeSpan.FromMinutes(_autosaveIntervalMinutes);
        _autosaveTimer.Start();
    }

    private void OnAutosaveTick(object? sender, object e)
    {
        // 未保存ファイル（_currentPath なし）はオートセーブ先を持たないため対象外。
        if (_currentPath is null || !IsDirty) return;
        try { GcadSerializer.Save(_document, AutosavePath(_currentPath)); }
        catch { /* オートセーブ失敗は致命的でない（次回のタイマーに委ねる） */ }
    }

    // ===== オートセーブ間隔設定ダイアログ（ファイル(F)メニューから） =====

    private async void OnMenuAutosaveSettings(object sender, RoutedEventArgs e)
    {
        var box = new NumberBox
        {
            Value = _autosaveIntervalMinutes,
            Minimum = AutosaveIntervalMinMinutes,
            Maximum = AutosaveIntervalMaxMinutes,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Header = $"オートセーブ間隔（{AutosaveIntervalMinMinutes}〜{AutosaveIntervalMaxMinutes}分）",
        };
        var dialog = new ContentDialog
        {
            Title = "オートセーブ設定",
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        if (double.IsNaN(box.Value)) return;

        _autosaveIntervalMinutes = Math.Clamp((int)box.Value, AutosaveIntervalMinMinutes, AutosaveIntervalMaxMinutes);
        StartAutosaveTimer();
        try
        {
            System.IO.Directory.CreateDirectory(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "\\GuiEcad");
            File.WriteAllText(AutosaveIntervalSettingPath, _autosaveIntervalMinutes.ToString());
        }
        catch { /* 設定保存失敗は致命的でない */ }
    }

    // ===== 起動時／ファイルを開く時のオートセーブ復元確認 =====

    private async Task<bool> ConfirmRestoreAutosaveAsync(DateTime autosaveUtc)
    {
        var local = autosaveUtc.ToLocalTime();
        var dialog = new ContentDialog
        {
            Title = "オートセーブされたバックアップがあります",
            Content = $"このファイルより新しい自動保存データが見つかりました（{local:yyyy-MM-dd HH:mm}）。\n復元しますか？",
            PrimaryButtonText = "復元",
            CloseButtonText = "破棄",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }
}
