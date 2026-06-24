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
    // ===== 配置ツールの状態（単一の相互排他モード＋パラメータ） =====
    // 旧 _placeKind/_placePartId/_placeOrient/_placeConnector/_placeFrame/_placeLine/_placeDot の
    // フラグ束を 1 つの _tool に集約する。モードは enum で本質的に排他なので、複数フラグの取りこぼし
    // （例: 直線/点ツールのまま記号配置が効かない・クリア漏れ）が構造的に起こらない。
    private enum ToolMode { Select, PlaceElement, PlaceConnector, PlaceFrame, PlaceLine, PlaceDot, PlaceWireBreak }

    /// <summary>現在の配置ツール。Kind/PartId/Orient は Mode==PlaceElement のときのみ意味を持つ。</summary>
    private readonly record struct ToolState(
        ToolMode Mode,
        ElementKind? Kind = null,
        string? PartId = null,
        string? Orient = null);

    private ToolState _tool = new(ToolMode.Select);

    // 読み取り用ショートカット（呼び出し側の可読性維持・すべて単一の _tool から導出）。
    private ElementKind? PlaceKind => _tool.Mode == ToolMode.PlaceElement ? _tool.Kind : null;
    private string? PlacePartId => _tool.PartId;
    private string? PlaceOrient => _tool.Orient;

    /// <summary>パレット/メニュー共通のタグ文字列から配置ツール状態を作る。
    /// 記号タグは "Kind"、主回路記号は向き付き "Kind#V/#H"。該当しなければ選択ツール。</summary>
    private static ToolState ToolFromTag(string? tag) => tag switch
    {
        "connector" => new ToolState(ToolMode.PlaceConnector),
        "wirebreak" => new ToolState(ToolMode.PlaceWireBreak),
        "frame" => new ToolState(ToolMode.PlaceFrame),
        "line" => new ToolState(ToolMode.PlaceLine),
        "dot" => new ToolState(ToolMode.PlaceDot),
        null or "select" => new ToolState(ToolMode.Select),
        _ => ParseSymbolTag(tag),
    };

    private static ToolState ParseSymbolTag(string tag)
    {
        string kindTag = tag;
        string? orient = null;
        int hash = tag.IndexOf('#');
        if (hash >= 0) { orient = tag[(hash + 1)..]; kindTag = tag[..hash]; }
        return Enum.TryParse<ElementKind>(kindTag, out var kind)
            ? new ToolState(ToolMode.PlaceElement, kind, Orient: orient)
            : new ToolState(ToolMode.Select);
    }

    // 縦パレットの接点系ラジオボタンの選択を解除（その他部品・フォルダ図形が配置対象であることを明示）。
    private void ClearToolRadios()
    {
        foreach (UIElement child in ToolStackPanel.Children)
            if (child is RadioButton rb) rb.IsChecked = false;
    }

    private void OnToolSelected(object sender, RoutedEventArgs e)
    {
        _tool = ToolFromTag((sender as RadioButton)?.Tag as string);
        _frameStartMm = null;
        _lineStartMm = null;
        _connStartRow = null;
        if (OtherPartButton is not null) OtherPartButton.Content = "その他部品";
    }

    // 「その他部品」メニューから記号を選択（ツールバーに常設しない記号の配置）
    private void OnOtherPartSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string tag) return;

        // 直前に「直線」等を選んでいた場合クリックがそちらに吸われ記号を配置できなくなるため、
        // 進行中の作画座標を解除しラジオ選択も外す（_tool 差し替えで配置モードは確定する）。
        _frameStartMm = null;
        _lineStartMm = null;
        _connStartRow = null;
        ClearToolRadios();

        // 自作パーツ（Tag = "part:<PartId>"）は Kind を既定値に、それ以外は記号タグから解釈する。
        var next = tag.StartsWith("part:", StringComparison.Ordinal)
            ? new ToolState(ToolMode.PlaceElement, ElementKind.ContactNO, PartId: tag["part:".Length..])
            : ToolFromTag(tag);
        if (next.Mode == ToolMode.Select) return;   // 未対応タグなら現状維持

        _tool = next;
        OtherPartButton.Content = item.Text;
        Canvas.Invalidate();
    }

    // 「自作パーツ作成...」: パーツエディタ（別ウィンドウ）を開き、保存時にライブラリへ登録する。
    private void OnCreatePart(object sender, RoutedEventArgs e) => OpenPartEditor(null);

    private void OpenPartEditor(PartDefinition? edit)
    {
        var win = new PartEditorWindow(edit);
        win.Saved += def =>
        {
            _document.Library ??= new PartLibrary();
            _document.Library.ById[def.Id] = def;
            RebuildOtherPartMenu();
            Canvas.Invalidate();
        };
        win.Activate();
    }

    // 既存の自作パーツをエディタで再編集する（Saved 時に同 Id で上書き）。
    private void OnEditPart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is string id && _document.Library?.Get(id) is { } def)
            OpenPartEditor(def);
    }

    // 自作パーツの削除（確認ダイアログ後にライブラリから除去）。配置済み要素は描画フォールバックされる。
    private async void OnDeletePart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not string id || _document.Library?.Get(id) is not { } def) return;

        int placed = _document.Sheets.Sum(s => s.Elements.Count(el => el.PartId == id));
        string body = placed > 0
            ? $"パーツ「{def.Name}」を削除しますか？\n配置済みの {placed} 個の要素は組込み記号で表示されます。"
            : $"パーツ「{def.Name}」を削除しますか？";

        var dlg = new ContentDialog
        {
            Title = "パーツの削除",
            Content = body,
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowDialogAsync(dlg) != ContentDialogResult.Primary) return;

        _document.Library!.ById.Remove(id);
        if (_tool.PartId == id)
        {
            _tool = new ToolState(ToolMode.Select);
            if (OtherPartButton is not null) OtherPartButton.Content = "その他部品";
        }
        RebuildOtherPartMenu();
        Canvas.Invalidate();
    }

    // パーツライブラリを外部ファイル（.gcadparts）へ書き出す。
    private async void OnExportParts(object sender, RoutedEventArgs e)
    {
        if (_document.Library is null || _document.Library.ById.Count == 0) return;

        var picker = new FileSavePicker(GetPickerWindowId());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "parts";
        picker.FileTypeChoices.Add("パーツライブラリ", new List<string> { ".gcadparts" });

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try { PartLibrarySerializer.Save(_document.Library, file.Path); }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    // 外部ファイルからパーツを読み込み、現ドキュメントのライブラリへ統合（同 Id は上書き）。
    private async void OnImportParts(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(GetPickerWindowId());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".gcadparts");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var parts = PartLibrarySerializer.Load(file.Path);
            if (parts.Count == 0) { await ShowErrorAsync("読み込むパーツがありません。"); return; }

            _document.Library ??= new PartLibrary();
            foreach (var p in parts) _document.Library.ById[p.Id] = p;
            RebuildOtherPartMenu();
            Canvas.Invalidate();
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    // 「その他部品」メニューを再生成（組込みの常設記号＋ドキュメントの自作パーツ）。
    private void RebuildOtherPartMenu()
    {
        if (OtherPartButton?.Flyout is not MenuFlyout flyout) return;
        flyout.Items.Clear();

        AddOtherBuiltins(flyout.Items);

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(BuildCustomShapesSubItem());   // 図形/自作/ の自作図形（上メニューと共有）

        flyout.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = "自作図形を作成..." };
        create.Click += OnCreateFolderPart;
        flyout.Items.Add(create);

        var import = new MenuFlyoutItem { Text = "パーツをインポート..." };
        import.Click += OnImportParts;
        flyout.Items.Add(import);

        var export = new MenuFlyoutItem { Text = "パーツをエクスポート..." };
        export.Click += OnExportParts;
        flyout.Items.Add(export);
    }

    // 自作パーツ1件分のサブメニュー（配置／編集／削除）。
    private MenuFlyoutSubItem BuildPartSubMenu(PartDefinition p)
    {
        var sub = new MenuFlyoutSubItem { Text = string.IsNullOrEmpty(p.Name) ? "(無名)" : p.Name };

        var place = new MenuFlyoutItem { Text = "配置", Tag = $"part:{p.Id}" };
        place.Click += OnOtherPartSelected;
        sub.Items.Add(place);

        var edit = new MenuFlyoutItem { Text = "編集...", Tag = p.Id };
        edit.Click += OnEditPart;
        sub.Items.Add(edit);

        var del = new MenuFlyoutItem { Text = "削除", Tag = p.Id };
        del.Click += OnDeletePart;
        sub.Items.Add(del);

        return sub;
    }

    // 「その他図形」の組込み記号（基本記号 a接点〜端子台 は含めない）。上メニューと左パネルで共有。
    private static readonly (string Label, string Tag)[] OtherBuiltins =
    {
        ("セレクトSW", "SelectSwitch"),
        ("サーマル(OL)", "ThermalOverload"),
        ("非常停止", "EmergencyStop"),
        ("三相モータ", "Motor"),
        // 主回路（三相動力）用の3極記号。タグ "Kind#V/#H" で配置時に向きを確定（切替不可）。
        // 型(NFB/MCCB/ELB)はブレーカ配置後にプロパティパネルで切替。
        ("ブレーカ(NFB/MCCB/ELB) 縦", "Breaker3P#V"),
        ("ブレーカ(NFB/MCCB/ELB) 横", "Breaker3P#H"),
        ("電磁接触器 主接点 縦", "ContactorMain3P#V"),
        ("電磁接触器 主接点 横", "ContactorMain3P#H"),
        ("サーマル(OL) 2極 縦", "ThermalOverload3P#V"),
        ("サーマル(OL) 2極 横", "ThermalOverload3P#H"),
    };

    private void AddOtherBuiltins(IList<MenuFlyoutItemBase> items)
    {
        foreach (var (label, tag) in OtherBuiltins)
        {
            var item = new MenuFlyoutItem { Text = label, Tag = tag };
            item.Click += OnOtherPartSelected;
            items.Add(item);
        }
    }

    // ===== テストモード =====

}
