using GuiEcad.Model;
using GuiEcad.Persistence;
using GuiEcad.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GuiEcad_App;

public sealed partial class MainPage : Page
{
    // ===== 図面テンプレート（ビルトイン2種＋ユーザー保存分） =====

    private const string ControlTemplateName = "制御図面";
    private const string MainPlusControlTemplateName = "動力+制御図面";

    private static string TemplatesDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GuiEcad", "templates");

    // ビルトインテンプレートはディスクに触れず、都度コード生成する（palette-pos.txt 等の外部ファイル運用とは別）。
    private static LadderDocument CreateControlTemplate()
    {
        var doc = new LadderDocument();
        doc.Sheets.Add(CreateEmptySheet());
        return doc;
    }

    // 動力回路シート（母線R/S/T＋ブレーカー事前配置）＋制御回路シート（空）の2枚構成。
    private static LadderDocument CreateMainPlusControlTemplate()
    {
        var opt = new RenderOptions();
        var geo = new GridGeometry(opt.CellMm, opt.MarginMm);

        var mainSheet = new Sheet
        {
            Name = "主回路",
            MainCircuit = true,
            Grid = new GridSpec { Columns = 6, Rows = 10 },
        };
        // 母線 R/S/T（自由直線）。列境界1/2/3に、下のブレーカー(2セル幅)の3極が重なる位置。
        double yTop = geo.YRow(0) - geo.CellMm * 0.5;
        double yBot = geo.YRow(9) + geo.CellMm * 0.5;
        for (int col = 1; col <= 3; col++)
        {
            double x = geo.X(col);
            mainSheet.FreeLines.Add(new FreeLine { X1Mm = x, Y1Mm = yTop, X2Mm = x, Y2Mm = yBot });
        }
        mainSheet.Elements.Add(new ElementInstance
        {
            Kind = ElementKind.Breaker3P,
            Pos = new GridPos(2, 1),
            CellWidth = ElementCatalog.DefaultCellWidth(ElementKind.Breaker3P),
            Params = new Dictionary<string, string> { [ParamKeys.Orient] = "V" },
        });

        var doc = new LadderDocument();
        doc.Sheets.Add(mainSheet);
        doc.Sheets.Add(CreateEmptySheet());
        return doc;
    }

    // 一覧表示順（ビルトインが常に先頭）。
    private static (string Name, Func<LadderDocument> Factory)[] BuiltinTemplates() => new (string, Func<LadderDocument>)[]
    {
        (ControlTemplateName, CreateControlTemplate),
        (MainPlusControlTemplateName, CreateMainPlusControlTemplate),
    };

    // OnMenuNew と同じ初期化（Undo履歴・テストモード・パネル類のリセット）を、テンプレート読込とも共有する。
    private void ApplyNewDocument(LadderDocument doc, bool markDirty)
    {
        if (doc.Sheets.Count == 0) doc.Sheets.Add(CreateEmptySheet());
        _document = doc;
        _sheet = doc.Sheets[0];
        _currentPath = null;
        _selected = null;
        _history.Clear();
        _savedUndoDepth = 0;
        _testSessions.Clear();
        _testSession = null;
        _testMode = false;
        TestModeBtn.IsChecked = false;
        StopRealtimeTimer();
        TimerTickPanel.Visibility = Visibility.Collapsed;
        StatusMode.Text = "作画モード";
        _find.Clear();
        RebuildNavTree();
        RebuildOtherPartMenu();
        RefreshDevicePanel();
        ReloadImageCacheForDocument(doc);
        if (markDirty) MarkDirty();   // テンプレートは未保存の新規文書として扱う（保存を促す）
        Canvas.Invalidate();
    }

    private async void OnMenuNewFromTemplate(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardIfDirtyAsync()) return;

        var items = new List<(string Label, Func<LadderDocument> Load)>();
        foreach (var (name, factory) in BuiltinTemplates())
            items.Add((name, factory));
        try
        {
            if (System.IO.Directory.Exists(TemplatesDir))
                foreach (var path in System.IO.Directory.GetFiles(TemplatesDir, "*.gcad").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    string label = System.IO.Path.GetFileNameWithoutExtension(path);
                    items.Add((label, () => GcadSerializer.Load(path)));
                }
        }
        catch { /* ユーザーテンプレート一覧取得失敗はビルトインのみで続行 */ }

        var listBox = new ListBox { SelectionMode = SelectionMode.Single };
        foreach (var it in items) listBox.Items.Add(it.Label);
        if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            Title = "テンプレートから新規作成",
            Content = listBox,
            PrimaryButtonText = "作成",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        if (listBox.SelectedIndex < 0) return;

        LadderDocument loaded;
        try { loaded = items[listBox.SelectedIndex].Load(); }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); return; }

        ApplyNewDocument(loaded, markDirty: true);
    }

    private async void OnMenuSaveAsTemplate(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "テンプレート名", Header = "テンプレート名" };
        var dialog = new ContentDialog
        {
            Title = "テンプレートとして保存",
            Content = nameBox,
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        nameBox.Loaded += (_, _) => nameBox.Focus(FocusState.Programmatic);
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        string name = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        try
        {
            System.IO.Directory.CreateDirectory(TemplatesDir);
            string path = System.IO.Path.Combine(TemplatesDir, name + ".gcad");
            GcadSerializer.Save(_document, path);
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }
}
