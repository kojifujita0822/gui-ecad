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
    // ===== 図形フォルダ（メニューバー「図形(G)」） =====

    // 起動時: フォルダ作成・基本図形シード・走査・メニュー構築（失敗してもアプリは起動継続）。
    private void InitShapeFolder()
    {
        _folderStore = PartFolderStore.CreateDefault();
        _pinnedStore = PinnedPartStore.CreateDefault();
        _pinnedIds = _pinnedStore.Load();
        try
        {
            _folderStore.EnsureFolders();
            // 基本図形の実フォルダへのシードは廃止（配置は自作図形のみ・たたき台はコードの組込み図形から）
        }
        catch { /* フォルダ作成不可でもメニューだけは出す */ }

        _shapeMenuItem = ShapeMenuBarItem;   // XAML のプレースホルダを起点に差し替えていく
        RefreshFolderEntries();
        RebuildShapeMenu();
    }

    private void RefreshFolderEntries()
    {
        try { _folderEntries = _folderStore.Enumerate(); }
        catch { _folderEntries = Array.Empty<PartFolderEntry>(); }
    }

    // フォルダ走査結果を「図形」メニューへフォルダ階層（基本図形=直下 / 自作=サブメニュー）で展開する。
    // MenuBarItem は Items を Clear/Add しても表示が更新されないことがあるため、毎回 MenuBarItem ごと差し替える。
    private void RebuildShapeMenu()
    {
        var menu = new MenuBarItem { Title = "図形(G)" };

        _folderPartMap.Clear();
        foreach (var e in _folderEntries) _folderPartMap[e.Definition.Id] = e;

        // その他図形 = 組込みのその他記号 ＋ 自作図形（左パネル「その他▼」と同じ並び）
        var other = new MenuFlyoutSubItem { Text = "その他図形" };
        AddOtherBuiltins(other.Items);

        // 組み込みパーツ（EmbeddedResource: thermal-relay 等）
        foreach (var part in _builtinParts)
        {
            var item = new MenuFlyoutItem { Text = part.Name, Tag = $"builtin-part:{part.Id}" };
            item.Click += OnPlaceBuiltinPart;
            other.Items.Add(item);
        }

        // ピン留め済み自作図形（直接配置・サブメニューなし）
        var pinnedEntries = _folderEntries
            .Where(e => _pinnedIds.Contains(e.Definition.Id))
            .ToList();
        if (pinnedEntries.Count > 0)
        {
            other.Items.Add(new MenuFlyoutSeparator());
            foreach (var e in pinnedEntries)
            {
                var item = new MenuFlyoutItem { Text = e.Definition.Name, Tag = e.Definition.Id };
                item.Click += OnPlacePinnedPart;
                other.Items.Add(item);
            }
        }

        other.Items.Add(new MenuFlyoutSeparator());
        other.Items.Add(BuildCustomShapesSubItem());
        menu.Items.Add(other);

        menu.Items.Add(new MenuFlyoutSeparator());

        var create = new MenuFlyoutItem { Text = "自作図形を作成..." };
        create.Click += OnCreateFolderPart;
        menu.Items.Add(create);

        var open = new MenuFlyoutItem { Text = "自作図形を読み込んで編集..." };
        open.Click += OnLoadAndEditPart;
        menu.Items.Add(open);

        menu.Items.Add(new MenuFlyoutSeparator());

        var export = new MenuFlyoutItem
        {
            Text = "自作パーツをエクスポート (.gcadparts)...",
            IsEnabled = _document.Library?.ById.Count > 0,
        };
        export.Click += OnExportLibrary;
        menu.Items.Add(export);

        var import = new MenuFlyoutItem { Text = "自作パーツをインポート (.gcadparts / .gcadpart)..." };
        import.Click += OnImportLibrary;
        menu.Items.Add(import);

        // 同じ位置（表示(V)とヘルプ(H)の間）へ差し替える
        int idx = TopMenuBar.Items.IndexOf(_shapeMenuItem);
        if (idx >= 0)
        {
            TopMenuBar.Items.RemoveAt(idx);
            TopMenuBar.Items.Insert(idx, menu);
        }
        else
        {
            TopMenuBar.Items.Add(menu);
        }
        _shapeMenuItem = menu;
    }

    // 「自作図形」サブメニュー（図形/自作/ 配下を配置/編集/削除）。上メニューと左パネル「その他▼」で共有。
    private MenuFlyoutSubItem BuildCustomShapesSubItem()
    {
        var sub = new MenuFlyoutSubItem { Text = "自作図形" };
        var customs = _folderEntries
            .Where(e => e.Category == "自作" || e.Category.StartsWith("自作/", StringComparison.Ordinal))
            .OrderBy(e => e.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (customs.Count == 0)
        {
            sub.Items.Add(new MenuFlyoutItem { Text = "(なし)", IsEnabled = false });
            return sub;
        }
        foreach (var e in customs) sub.Items.Add(BuildShapeSubMenu(e));
        return sub;
    }

    // 自作図形1件のサブメニュー（配置／編集／削除）。
    private MenuFlyoutSubItem BuildShapeSubMenu(PartFolderEntry e)
    {
        var def = e.Definition;
        var sub = new MenuFlyoutSubItem { Text = string.IsNullOrEmpty(def.Name) ? "(無名)" : def.Name };

        var place = new MenuFlyoutItem { Text = "配置", Tag = def.Id };
        place.Click += OnPlaceFolderPart;
        sub.Items.Add(place);

        var edit = new MenuFlyoutItem { Text = "編集...", Tag = def.Id };
        edit.Click += OnEditFolderPart;
        sub.Items.Add(edit);

        var del = new MenuFlyoutItem { Text = "削除", Tag = def.Id };
        del.Click += OnDeleteFolderPart;
        sub.Items.Add(del);

        var pinLabel = _pinnedIds.Contains(def.Id) ? "その他図形のピン留めを解除" : "その他図形にピン留め";
        var pin = new MenuFlyoutItem { Text = pinLabel, Tag = def.Id };
        pin.Click += OnTogglePinPart;
        sub.Items.Add(pin);

        return sub;
    }

    // ピン留め済み自作図形を配置対象にする（その他図形メニューからの直接配置）。
    private void OnPlacePinnedPart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not string id
            || !_folderPartMap.TryGetValue(id, out var entry)) return;

        _document.Library ??= new PartLibrary();
        _document.Library.ById[entry.Definition.Id] = entry.Definition;

        _frameStartMm = null;
        _lineStartMm = null;
        _connStartRow = null;
        ClearToolRadios();

        _tool = new ToolState(ToolMode.PlaceElement, ElementKind.ContactNO, PartId: entry.Definition.Id);
        OtherPartButton.Content = entry.Definition.Name;
        Canvas.Invalidate();
    }

    // 自作図形のピン留めをトグル（自作図形サブメニューから）。
    private void OnTogglePinPart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not string id) return;

        if (_pinnedIds.Contains(id))
            _pinnedIds.Remove(id);
        else
            _pinnedIds.Add(id);

        _pinnedStore.Save(_pinnedIds);
        RebuildShapeMenu();
        RebuildOtherPartMenu();
    }

    // 組み込みパーツ（EmbeddedResource）を配置対象にする。ドキュメント Library へ埋め込み維持。
    private void OnPlaceBuiltinPart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not string tag) return;
        var id = tag["builtin-part:".Length..];
        var part = _builtinParts.FirstOrDefault(p => p.Id == id);
        if (part is null) return;

        _document.Library ??= new PartLibrary();
        _document.Library.ById[part.Id] = part;

        ClearToolRadios();
        _frameStartMm = null;
        _lineStartMm = null;
        _connStartRow = null;

        // PartId 指定時は Kind が無視されるが既定値を置く（OnPlaceFolderPart と同様）。
        _tool = new ToolState(ToolMode.PlaceElement, ElementKind.ContactNO, PartId: part.Id);
        OtherPartButton.Content = part.Name;
        Canvas.Invalidate();
    }

    // フォルダの図形を配置対象にする。ドキュメント Library へ埋め込み（.GCAD 自己完結）を維持する。
    private void OnPlaceFolderPart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not string id || !_folderPartMap.TryGetValue(id, out var entry)) return;

        _document.Library ??= new PartLibrary();
        _document.Library.ById[entry.Definition.Id] = entry.Definition;   // 埋め込み維持

        _connStartRow = null;
        ClearToolRadios();

        // PartId 指定時は Kind が無視されるが既定値を置く。
        _tool = new ToolState(ToolMode.PlaceElement, ElementKind.ContactNO, PartId: entry.Definition.Id);
        Canvas.Invalidate();
    }

    private void OnEditFolderPart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is string id && _folderPartMap.TryGetValue(id, out var entry))
            OpenFolderPartEditor(entry.Definition, oldPath: entry.FilePath);
    }

    private void OnCreateFolderPart(object sender, RoutedEventArgs e) => OpenFolderPartEditor(null, oldPath: null);

    // 図形フォルダ向けのパーツエディタを開く。保存で「図形/自作」へ書き出し＋埋め込み＋メニュー再構成。
    private void OpenFolderPartEditor(PartDefinition? edit, string? oldPath)
    {
        var win = new PartEditorWindow(edit);
        win.Saved += def =>
        {
            try
            {
                string newPath = _folderStore.SaveCustom(def);
                // 改名で別ファイル名になった場合は旧ファイルを除去（自作の上書き編集を保つ）
                if (oldPath is not null &&
                    !string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
                    _folderStore.Delete(oldPath);
            }
            catch (Exception ex) { _ = ShowErrorAsync(ex.Message); }

            _document.Library ??= new PartLibrary();
            _document.Library.ById[def.Id] = def;   // 埋め込みも更新
            RefreshFolderEntries();
            RebuildShapeMenu();
            RebuildOtherPartMenu();
            Canvas.Invalidate();
        };
        win.Activate();
    }

    private async void OnDeleteFolderPart(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not string id || !_folderPartMap.TryGetValue(id, out var entry)) return;

        var dlg = new ContentDialog
        {
            Title = "図形の削除",
            Content = $"自作図形「{entry.Definition.Name}」をフォルダから削除しますか？",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try { _folderStore.Delete(entry.FilePath); }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); return; }

        RefreshFolderEntries();
        RebuildShapeMenu();
        RebuildOtherPartMenu();
    }

    // .gcadpart ファイルを開き、同じパスへ上書き保存できる状態でエディタを起動する。
    private async void OnLoadAndEditPart(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(GetPickerWindowId());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".gcadpart");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        PartDefinition loaded;
        try { loaded = PartLibrarySerializer.LoadOne(file.Path); }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); return; }

        OpenFolderPartEditor(loaded, oldPath: file.Path);
    }

    private async void OnExportLibrary(object sender, RoutedEventArgs e)
    {
        if (_document.Library is not { } lib || lib.ById.Count == 0)
        {
            await ShowErrorAsync("エクスポートするパーツがありません。");
            return;
        }

        var picker = new FileSavePicker(GetPickerWindowId());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "parts";
        picker.FileTypeChoices.Add("パーツライブラリ", new List<string> { ".gcadparts" });

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        int count = lib.ById.Count;
        try
        {
            PartLibrarySerializer.Save(lib, file.Path);
            var dlg = new ContentDialog
            {
                Title = "エクスポート完了",
                Content = $"{count} 件の自作パーツをエクスポートしました。",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
            };
            await ShowDialogAsync(dlg);
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async void OnImportLibrary(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(GetPickerWindowId());
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".gcadparts");
        picker.FileTypeFilter.Add(".gcadpart");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            IReadOnlyList<PartDefinition> parts;
            if (file.Path.EndsWith(".gcadpart", StringComparison.OrdinalIgnoreCase))
                parts = new[] { PartLibrarySerializer.LoadOne(file.Path) };
            else
                parts = PartLibrarySerializer.Load(file.Path);

            _document.Library ??= new PartLibrary();
            foreach (var def in parts)
                _document.Library.ById[def.Id] = def;

            MarkDirty();
            RebuildOtherPartMenu();
            RebuildShapeMenu();
            Canvas.Invalidate();
            var dlg = new ContentDialog
            {
                Title = "インポート完了",
                Content = $"{parts.Count} 件の自作パーツをインポートしました（同名パーツは上書き）。",
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot,
            };
            await ShowDialogAsync(dlg);
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    // タグ（キーボードショートカット等）から配置ツールを起動し、対応するパレットのラジオも同期する。
    private void ActivateTool(string tag)
    {
        ResetDragState();
        ClearMultiSelection();
        _frameStartMm = null;
        _lineStartMm = null;
        _connStartRow = null;
        _tool = ToolFromTag(tag);
        if (_tool.Mode == ToolMode.Select)
            BtnSelect.IsChecked = true;
        else if (_tool.Mode == ToolMode.PlaceElement)
            foreach (UIElement child in ToolStackPanel.Children)
                if (child is RadioButton rb && rb.Tag?.ToString() == tag)
                { rb.IsChecked = true; break; }
        UpdateHintText();
        Canvas.Invalidate();
    }

    private void UpdateHintText()
    {
        if (HintText is null) return;
        HintText.Text = (_testMode, _tool.Mode) switch
        {
            (true,  _)                       => "クリック: 接点 ON/OFF | ホイール: ズーム",
            (false, ToolMode.Select) when _selected is not null
                => "ドラッグ: 移動 | DEL: 削除 | Enter: 機器名入力 | F2: コメント",
            (false, ToolMode.Select) when _selectedConnector is not null
                => "ドラッグ: 列移動 | DEL: 削除",
            (false, ToolMode.Select) when _selectedFrame is not null
                => "ドラッグ: 移動 | DEL: 削除 | ダブルクリック: ラベル編集",
            (false, ToolMode.Select) when _selectedLine is not null
                => "ドラッグ: 移動 | DEL: 削除",
            (false, ToolMode.Select) when _selectedDot is not null
                => "ドラッグ: 移動 | DEL: 削除",
            (false, ToolMode.Select) when _selectedSet.Count + _selectedConnectorSet.Count
                    + _selectedLineSet.Count + _selectedFrameSet.Count + _selectedDotSet.Count > 0
                => "ドラッグ: 一括移動 | DEL: 一括削除 | Ctrl+C: コピー",
            (false, ToolMode.Select)
                => "クリック: 選択 | ドラッグ: 範囲選択",
            (false, ToolMode.PlaceElement)   => "クリック: 配置 | 右クリック: メニュー | Esc: 選択に戻る",
            (false, ToolMode.PlaceConnector) => "クリック: 分岐配置 | Esc: 選択に戻る",
            (false, ToolMode.PlaceFrame)     => "ドラッグ: 枠描画 | Esc: 選択に戻る",
            (false, ToolMode.PlaceLine)      => "クリック: 始点/終点 | Esc: キャンセル",
            (false, ToolMode.PlaceDot)       => "クリック: 接続点配置 | Esc: 選択に戻る",
            (false, ToolMode.PlaceWireBreak) => "クリック: 断線配置 | Esc: 選択に戻る",
            _                                => "",
        };
    }

}
