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

namespace GuiEcad_App;

public sealed partial class MainPage
{
    // ===== ドキュメント情報（表題欄） =====

    private async void OnDocumentInfo(object sender, RoutedEventArgs e)
    {
        var info = _document.Info;
        var fields = new (string Header, string Value, Action<string> Set)[]
        {
            ("図面名称", info.Title,    v => info.Title    = v),
            ("図番",     info.DrawingNo, v => info.DrawingNo = v),
            ("顧客",     info.Customer,  v => info.Customer  = v),
            ("設計",     info.Designer,  v => info.Designer  = v),
            ("製図",     info.Drafter,   v => info.Drafter   = v),
            ("確認",     info.Checker,   v => info.Checker   = v),
            ("日付",     info.Date ?? "", v => info.Date = string.IsNullOrEmpty(v) ? null : v),
        };

        var panel = new StackPanel { Spacing = 8 };
        var boxes = fields.Select(f => new TextBox { Text = f.Value, Header = f.Header }).ToArray();
        foreach (var box in boxes) panel.Children.Add(box);

        // 図面枠 ON/OFF トグル
        var borderToggle = new ToggleSwitch
        {
            Header = "図面枠を描画（A4横・PDF出力時）",
            IsOn = _document.Settings.EnableBorder,
            Margin = new Thickness(0, 8, 0, 0),
        };
        panel.Children.Add(borderToggle);

        // 改定欄セクション
        panel.Children.Add(new TextBlock
        {
            Text = "改定履歴",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, 12, 0, 0),
        });

        // 改定エントリのUI（動的に追加・削除）
        var revPanel = new StackPanel { Spacing = 4 };
        var revEntries = new List<(RevisionEntry Entry, TextBox[] Boxes)>();

        void AddRevRow(RevisionEntry rev)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var bRev  = new TextBox { Text = rev.Rev,         Width = 50,  PlaceholderText = "Rev" };
            var bDate = new TextBox { Text = rev.Date,        Width = 90,  PlaceholderText = "日付" };
            var bDesc = new TextBox { Text = rev.Description, Width = 200, PlaceholderText = "内容" };
            var bBy   = new TextBox { Text = rev.By,          Width = 70,  PlaceholderText = "担当" };
            var del   = new Button  { Content = "削除", Padding = new Thickness(8, 0, 8, 0) };
            del.Click += (_, _) => { revPanel.Children.Remove(rowPanel); revEntries.RemoveAll(x => x.Boxes[0] == bRev); };
            rowPanel.Children.Add(bRev); rowPanel.Children.Add(bDate);
            rowPanel.Children.Add(bDesc); rowPanel.Children.Add(bBy); rowPanel.Children.Add(del);
            revPanel.Children.Add(rowPanel);
            revEntries.Add((rev, new[] { bRev, bDate, bDesc, bBy }));
        }

        // 既存エントリを表示
        foreach (var rev in info.Revisions) AddRevRow(rev);

        var addRevBtn = new Button { Content = "+ 改定を追加", Margin = new Thickness(0, 4, 0, 0) };
        addRevBtn.Click += (_, _) => AddRevRow(new RevisionEntry());

        panel.Children.Add(revPanel);
        panel.Children.Add(addRevBtn);

        var dialog = new ContentDialog
        {
            Title = "ドキュメント情報",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        boxes[0].Loaded += (_, _) => boxes[0].Focus(FocusState.Programmatic);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        for (int i = 0; i < fields.Length; i++)
            fields[i].Set(boxes[i].Text.Trim());

        // 図面枠設定を保存
        _document.Settings.EnableBorder = borderToggle.IsOn;

        // 改定履歴を保存（現在のUIの並び順で上書き）
        info.Revisions.Clear();
        foreach (var (rev, bs) in revEntries)
        {
            rev.Rev = bs[0].Text.Trim(); rev.Date = bs[1].Text.Trim();
            rev.Description = bs[2].Text.Trim(); rev.By = bs[3].Text.Trim();
            if (!string.IsNullOrEmpty(rev.Rev) || !string.IsNullOrEmpty(rev.Description))
                info.Revisions.Add(rev);
        }
        MarkDirty();   // ドキュメント情報は Undo 対象外。変更を保存対象として記録
    }

    // ===== シート設定（母線名・グリッドサイズ） =====

    private async void OnSheetSettings(object sender, RoutedEventArgs e)
    {
        var leftBox  = new TextBox { Text = _sheet.Bus.LeftName,  PlaceholderText = "左母線名（例: R200）", Header = "左母線名" };
        var rightBox = new TextBox { Text = _sheet.Bus.RightName, PlaceholderText = "右母線名（例: S200）", Header = "右母線名" };
        var voltageBox = new TextBox { Text = _sheet.Bus.PowerLabel ?? "", PlaceholderText = "母線間電圧（例: AC200V）", Header = "電圧（母線間・任意）" };
        var colBox   = new NumberBox
        {
            Value = _sheet.Grid.Columns,
            Minimum = 2, Maximum = 20,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Header = "列数（2〜20）",
        };
        var rowBox   = new NumberBox
        {
            Value = _sheet.Grid.Rows,
            Minimum = 1, Maximum = 60,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Header = "行数（1〜60）",
        };

        var defaultCheck = new CheckBox { Content = "この母線名を新規シートの既定にする" };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(leftBox);
        panel.Children.Add(rightBox);
        panel.Children.Add(voltageBox);
        panel.Children.Add(defaultCheck);
        panel.Children.Add(colBox);
        panel.Children.Add(rowBox);

        var dialog = new ContentDialog
        {
            Title = "シート設定",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        leftBox.Loaded += (_, _) => leftBox.Focus(FocusState.Programmatic);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _sheet.Bus = new BusConfig
        {
            LeftName  = leftBox.Text.Trim().Length > 0 ? leftBox.Text.Trim() : _sheet.Bus.LeftName,
            RightName = rightBox.Text.Trim().Length > 0 ? rightBox.Text.Trim() : _sheet.Bus.RightName,
            PowerLabel = voltageBox.Text.Trim().Length > 0 ? voltageBox.Text.Trim() : null,
        };

        // 新規シートの既定母線名として保存（チェック時）
        if (defaultCheck.IsChecked == true)
            _document.Settings.DefaultBus = new BusConfig
            {
                LeftName = _sheet.Bus.LeftName,
                RightName = _sheet.Bus.RightName,
                PowerLabel = _sheet.Bus.PowerLabel,
            };

        int newCols = double.IsNaN(colBox.Value) ? _sheet.Grid.Columns : (int)colBox.Value;
        newCols = Math.Clamp(newCols, 2, 20);
        int minCols = _sheet.Elements.Count > 0
            ? _sheet.Elements.Max(el => el.Pos.Column + el.CellWidth)
            : 1;
        _sheet.Grid.Columns = Math.Max(newCols, minCols);

        int newRows = double.IsNaN(rowBox.Value) ? _sheet.Grid.Rows : (int)rowBox.Value;
        newRows = Math.Clamp(newRows, 1, 60);
        int minRows = _sheet.Elements.Count > 0
            ? _sheet.Elements.Max(el => el.Pos.Row + 1)
            : 1;
        _sheet.Grid.Rows = Math.Max(newRows, minRows);

        MarkDirty();   // シート設定（母線名・グリッド）は Undo 対象外。変更を保存対象として記録
        Canvas.Invalidate();
    }

    // ===== 部品リスト（BOM）=====

    private async void OnBomEditor(object sender, RoutedEventArgs e)
    {
        // 図面中の全機器名を収集（機器表と同じロジック）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var devices = new List<(string Name, string Label, ElementKind Kind)>();
        foreach (var sheet in _document.Sheets)
            foreach (var elem in sheet.Elements.OrderBy(el => el.Pos.Row).ThenBy(el => el.Pos.Column))
            {
                if (string.IsNullOrEmpty(elem.DeviceName)) continue;
                if (!seen.Add(elem.DeviceName)) continue;
                var kind = PartResolver.ComponentKind(elem, _document.Library);
                devices.Add((elem.DeviceName, DeviceKindLabel(kind), kind));
            }
        devices.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        foreach (double w in new[] { 110.0, 55.0, 160.0, 120.0, 80.0 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void AddHeader(int col, string text)
        {
            var tb = new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12 };
            Grid.SetColumn(tb, col); Grid.SetRow(tb, 0); grid.Children.Add(tb);
        }
        AddHeader(0, "機器名"); AddHeader(1, "種別"); AddHeader(2, "型式"); AddHeader(3, "メーカー"); AddHeader(4, "数量");

        var rows = new List<(string Name, ElementKind Kind, TextBox Model, TextBox Maker, NumberBox Qty)>();
        int r = 1;
        foreach (var (name, label, kind) in devices)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _document.Devices.ByName.TryGetValue(name, out var dev);

            var nameTb = new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var labelTb = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
            var modelBox = new TextBox { Text = dev?.Model ?? "", FontSize = 12 };
            var makerBox = new TextBox { Text = dev?.Maker ?? "", FontSize = 12 };
            var qtyBox = new NumberBox
            {
                Value = dev?.Quantity ?? 1,
                Minimum = 0, Maximum = 9999, SmallChange = 1,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };

            Grid.SetColumn(nameTb, 0); Grid.SetRow(nameTb, r); grid.Children.Add(nameTb);
            Grid.SetColumn(labelTb, 1); Grid.SetRow(labelTb, r); grid.Children.Add(labelTb);
            Grid.SetColumn(modelBox, 2); Grid.SetRow(modelBox, r); grid.Children.Add(modelBox);
            Grid.SetColumn(makerBox, 3); Grid.SetRow(makerBox, r); grid.Children.Add(makerBox);
            Grid.SetColumn(qtyBox, 4); Grid.SetRow(qtyBox, r); grid.Children.Add(qtyBox);

            rows.Add((name, kind, modelBox, makerBox, qtyBox));
            r++;
        }

        FrameworkElement body = devices.Count == 0
            ? new TextBlock { Text = "機器名が設定された要素がありません。", FontSize = 12 }
            : new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 };

        var dialog = new ContentDialog
        {
            Title = "部品リスト(BOM)",
            Content = body,
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
            MinWidth = 560,
        };
        if (devices.Count == 0) dialog.PrimaryButtonText = "";

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // 入力を DeviceTable へ書き戻す（変更があればダーティ化）
        bool changed = false;
        foreach (var (name, kind, modelBox, makerBox, qtyBox) in rows)
        {
            string model = modelBox.Text.Trim();
            string maker = makerBox.Text.Trim();
            int qty = double.IsNaN(qtyBox.Value) ? 1 : Math.Max(0, (int)qtyBox.Value);

            if (!_document.Devices.ByName.TryGetValue(name, out var dev))
            {
                dev = new Device { Name = name, Class = MapDeviceClass(kind) };
                _document.Devices.ByName[name] = dev;
            }
            string? newModel = model.Length > 0 ? model : null;
            string? newMaker = maker.Length > 0 ? maker : null;
            if (dev.Model != newModel || dev.Maker != newMaker || dev.Quantity != qty) changed = true;
            dev.Model = newModel;
            dev.Maker = newMaker;
            dev.Quantity = qty;
        }

        if (changed) MarkDirty();   // BOM 変更は Undo 対象外。確実にダーティ表示にする
    }

    private static DeviceClass MapDeviceClass(ElementKind kind) => kind switch
    {
        ElementKind.Coil or ElementKind.Timer
            or ElementKind.ContactNO or ElementKind.ContactNC => DeviceClass.Relay,
        ElementKind.PushButtonNO or ElementKind.PushButtonNC
            or ElementKind.EmergencyStop => DeviceClass.PushButton,
        ElementKind.SelectSwitch => DeviceClass.SelectSwitch,
        ElementKind.Lamp => DeviceClass.Lamp,
        ElementKind.TimerContactNO or ElementKind.TimerContactNC => DeviceClass.Timer,
        ElementKind.Terminal => DeviceClass.Terminal,
        _ => DeviceClass.Other,
    };

}
