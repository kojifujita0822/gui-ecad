using System.Text.Json;
using GuiEcad.Model;
using GuiEcad_App.Commands;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace GuiEcad_App;

public sealed partial class MainPage : Page
{
    // ===== ショートカットキー設定（カスタマイズ可能コマンドの動的 KeyboardAccelerator 管理） =====
    //
    // カスタマイズ可能: Undo/Redo・保存・削除・検索・コメント編集・行追加削除・コピペ・
    //                   ツール切替(F5-F8)・ズーム(拡大/縮小/全体表示)・新規作成・開く。
    //                   いずれも単一目的で文脈分岐が無く、再割当てしても既存ロジックに影響しない。
    // 予約（カスタマイズ不可・固定）: Escape・Enter・Tab・Space・Backspace。
    //                   文脈依存の多重分岐（Escapeの8分岐、Enterの機器名編集/キーボード配置確定の
    //                   二重用途等）を持ち、自由な再割当ては動作保証が困難なため対象外とする。

    private sealed class CommandDef
    {
        public required string Id;
        public required string Label;
        public VirtualKey DefaultKey;
        public VirtualKeyModifiers DefaultModifiers;
        /// <summary>true=処理した（KeyboardAcceleratorInvokedEventArgs.Handled にする）。</summary>
        public required Func<bool> Execute;
    }

    private sealed class KeyBindingDto
    {
        public string CommandId { get; set; } = "";
        public string Key { get; set; } = "";
        public string Modifiers { get; set; } = "";
    }

    private List<CommandDef>? _commandDefs;
    private Dictionary<string, (VirtualKey Key, VirtualKeyModifiers Mods)> _keyBindings = new();
    private readonly List<KeyboardAccelerator> _dynamicAccelerators = new();

    private static string KeyBindingsSettingPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GuiEcad", "keybindings.json");

    private List<CommandDef> CommandDefs => _commandDefs ??= new List<CommandDef>
    {
        new() { Id = "Undo", Label = "元に戻す", DefaultKey = VirtualKey.Z, DefaultModifiers = VirtualKeyModifiers.Control,
                Execute = () => { DoUndo(); return true; } },
        new() { Id = "Redo", Label = "やり直し", DefaultKey = VirtualKey.Y, DefaultModifiers = VirtualKeyModifiers.Control,
                Execute = () => { DoRedo(); return true; } },
        new() { Id = "Save", Label = "上書き保存", DefaultKey = VirtualKey.S, DefaultModifiers = VirtualKeyModifiers.Control,
                Execute = () => { _ = SaveCurrentAsync(); return true; } },
        new() { Id = "Delete", Label = "削除", DefaultKey = VirtualKey.Delete, DefaultModifiers = VirtualKeyModifiers.None,
                // テキスト入力中（インライン編集・プロパティパネル・検索バー）は要素を消さず、テキスト編集に委ねる。
                Execute = () =>
                {
                    if (IsInlineEditing || IsTextInputFocused()) return false;
                    DeleteSelected();
                    return true;
                } },
        new() { Id = "Find", Label = "検索・置換", DefaultKey = VirtualKey.F, DefaultModifiers = VirtualKeyModifiers.Control,
                Execute = () => { ToggleFindBar(); return true; } },
        new() { Id = "CommentEdit", Label = "コメント編集", DefaultKey = VirtualKey.F2, DefaultModifiers = VirtualKeyModifiers.None,
                Execute = () =>
                {
                    if (_testMode || _editingElement is not null || _editingComment is not null || _editingFrame is not null) return false;
                    if (FindBar.Visibility == Visibility.Visible) return false;
                    if (_selected is null) return false;
                    ShowCommentEditor(_selected);
                    return true;
                } },
        new() { Id = "InsertRow", Label = "行追加", DefaultKey = VirtualKey.Up, DefaultModifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
                Execute = () =>
                {
                    if (_testMode) return false;
                    _history.Execute(new InsertLastRowCommand(_sheet));
                    Canvas.Invalidate();
                    return true;
                } },
        new() { Id = "DeleteRow", Label = "行削除", DefaultKey = VirtualKey.Down, DefaultModifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
                Execute = () =>
                {
                    if (_testMode || _sheet.Grid.Rows <= 1) return false;
                    _history.Execute(new DeleteLastRowCommand(_sheet));
                    Canvas.Invalidate();
                    return true;
                } },
        new() { Id = "Copy", Label = "コピー", DefaultKey = VirtualKey.C, DefaultModifiers = VirtualKeyModifiers.Control,
                Execute = () => { if (_testMode) return false; CopySelection(); return true; } },
        new() { Id = "Paste", Label = "貼り付け", DefaultKey = VirtualKey.V, DefaultModifiers = VirtualKeyModifiers.Control,
                Execute = () => { if (_testMode) return false; PasteSelection(); return true; } },
        new() { Id = "ToolContactNO", Label = "ツール: a接点", DefaultKey = VirtualKey.F5, DefaultModifiers = VirtualKeyModifiers.None,
                Execute = () => { ActivateTool("ContactNO"); return true; } },
        new() { Id = "ToolContactNC", Label = "ツール: b接点", DefaultKey = VirtualKey.F6, DefaultModifiers = VirtualKeyModifiers.None,
                Execute = () => { ActivateTool("ContactNC"); return true; } },
        new() { Id = "ToolCoil", Label = "ツール: コイル", DefaultKey = VirtualKey.F7, DefaultModifiers = VirtualKeyModifiers.None,
                Execute = () => { ActivateTool("Coil"); return true; } },
        new() { Id = "ToolPushButtonNO", Label = "ツール: 押しボタン", DefaultKey = VirtualKey.F8, DefaultModifiers = VirtualKeyModifiers.None,
                Execute = () => { ActivateTool("PushButtonNO"); return true; } },
        new() { Id = "ZoomIn", Label = "拡大", DefaultKey = VirtualKey.Add, DefaultModifiers = VirtualKeyModifiers.Control,
                Execute = () => { ZoomBy(1.2, ViewCenter()); return true; } },
        new() { Id = "ZoomOut", Label = "縮小", DefaultKey = VirtualKey.Subtract, DefaultModifiers = VirtualKeyModifiers.Control,
                Execute = () => { ZoomBy(1 / 1.2, ViewCenter()); return true; } },
        new() { Id = "ZoomFit", Label = "全体表示", DefaultKey = VirtualKey.Number0, DefaultModifiers = VirtualKeyModifiers.Control,
                Execute = () => { _viewport.Reset(); Canvas.Invalidate(); return true; } },
        new() { Id = "New", Label = "新規作成", DefaultKey = VirtualKey.N, DefaultModifiers = VirtualKeyModifiers.Control,
                Execute = () => { OnMenuNew(this, new RoutedEventArgs()); return true; } },
        new() { Id = "Open", Label = "開く", DefaultKey = VirtualKey.O, DefaultModifiers = VirtualKeyModifiers.Control,
                Execute = () => { OnMenuOpen(this, new RoutedEventArgs()); return true; } },
    };

    private static string FormatModifiers(VirtualKeyModifiers mods)
        => mods == VirtualKeyModifiers.None ? "" : mods.ToString().Replace(", ", "+") + "+";

    private static string FormatBinding((VirtualKey Key, VirtualKeyModifiers Mods) b) => FormatModifiers(b.Mods) + b.Key;

    private static bool TryParseModifiers(string s, out VirtualKeyModifiers mods)
    {
        mods = VirtualKeyModifiers.None;
        if (string.IsNullOrEmpty(s)) return true;
        foreach (var part in s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Enum.TryParse<VirtualKeyModifiers>(part, out var m)) return false;
            mods |= m;
        }
        return true;
    }

    // 起動時に保存済みのキー割り当てを復元する（未保存・一部欠落は既定値で補完）。
    private void LoadKeyBindings()
    {
        _keyBindings = CommandDefs.ToDictionary(c => c.Id, c => (c.DefaultKey, c.DefaultModifiers));
        try
        {
            if (File.Exists(KeyBindingsSettingPath))
            {
                var list = JsonSerializer.Deserialize<List<KeyBindingDto>>(File.ReadAllText(KeyBindingsSettingPath));
                if (list is not null)
                    foreach (var dto in list)
                        if (_keyBindings.ContainsKey(dto.CommandId)
                            && Enum.TryParse<VirtualKey>(dto.Key, out var key)
                            && TryParseModifiers(dto.Modifiers, out var mods))
                            _keyBindings[dto.CommandId] = (key, mods);
            }
        }
        catch { /* 設定読込失敗は既定値で続行 */ }
    }

    private void SaveKeyBindings()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(KeyBindingsSettingPath)!);
            var list = _keyBindings.Select(kv => new KeyBindingDto
            {
                CommandId = kv.Key, Key = kv.Value.Key.ToString(), Modifiers = kv.Value.Mods.ToString(),
            }).ToList();
            File.WriteAllText(KeyBindingsSettingPath, JsonSerializer.Serialize(list));
        }
        catch { /* 保存失敗は致命的でない */ }
    }

    // 現在の _keyBindings の内容で RootGrid の動的 KeyboardAccelerator を再構築する。
    private void ApplyKeyBindings()
    {
        foreach (var acc in _dynamicAccelerators) RootGrid.KeyboardAccelerators.Remove(acc);
        _dynamicAccelerators.Clear();

        foreach (var cmd in CommandDefs)
        {
            var (key, mods) = _keyBindings[cmd.Id];
            var acc = new KeyboardAccelerator { Key = key, Modifiers = mods };
            acc.Invoked += (_, args) => { if (cmd.Execute()) args.Handled = true; };
            RootGrid.KeyboardAccelerators.Add(acc);
            _dynamicAccelerators.Add(acc);
        }
    }

    // ===== ショートカットキー設定ダイアログ（ファイル(F)メニューから） =====

    private async void OnMenuKeyBindingSettings(object sender, RoutedEventArgs e)
    {
        // 作業用コピー。キャンセル時は現在の _keyBindings に影響しない。
        var working = new Dictionary<string, (VirtualKey Key, VirtualKeyModifiers Mods)>(_keyBindings);
        var displays = new Dictionary<string, TextBlock>();

        var listPanel = new StackPanel { Spacing = 4 };
        foreach (var cmd in CommandDefs)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = cmd.Label, VerticalAlignment = VerticalAlignment.Center };
            var display = new TextBlock { Text = FormatBinding(working[cmd.Id]), VerticalAlignment = VerticalAlignment.Center };
            displays[cmd.Id] = display;
            var changeBtn = new Button { Content = "変更" };
            changeBtn.Click += async (_, _) =>
            {
                var captured = await CaptureKeyBindingAsync();
                if (captured is not (VirtualKey key, VirtualKeyModifiers mods)) return;   // キャンセル

                var conflict = working.FirstOrDefault(kv => kv.Key != cmd.Id && kv.Value == (key, mods));
                if (conflict.Key is not null)
                {
                    string conflictLabel = CommandDefs.First(c => c.Id == conflict.Key).Label;
                    await ShowErrorAsync($"「{conflictLabel}」({FormatBinding(conflict.Value)}) と競合しています。別のキーを選んでください。");
                    return;
                }
                working[cmd.Id] = (key, mods);
                display.Text = FormatBinding(working[cmd.Id]);
            };

            Grid.SetColumn(display, 1);
            Grid.SetColumn(changeBtn, 2);
            row.Children.Add(label); row.Children.Add(display); row.Children.Add(changeBtn);
            listPanel.Children.Add(row);
        }

        var resetBtn = new Button { Content = "すべて既定に戻す", Margin = new Thickness(0, 8, 0, 0) };
        resetBtn.Click += (_, _) =>
        {
            foreach (var cmd in CommandDefs)
            {
                working[cmd.Id] = (cmd.DefaultKey, cmd.DefaultModifiers);
                displays[cmd.Id].Text = FormatBinding(working[cmd.Id]);
            }
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(listPanel);
        panel.Children.Add(resetBtn);

        var dialog = new ContentDialog
        {
            Title = "ショートカットキー設定",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            PrimaryButtonText = "OK",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        _keyBindings = working;
        SaveKeyBindings();
        ApplyKeyBindings();
    }

    // 「キーを押してください」ダイアログを表示し、押されたキー(修飾キー込み)を返す。Escでキャンセル(null)。
    private async Task<(VirtualKey Key, VirtualKeyModifiers Mods)?> CaptureKeyBindingAsync()
    {
        (VirtualKey Key, VirtualKeyModifiers Mods)? result = null;

        var text = new TextBlock { Text = "割り当てたいキーを押してください（Escでキャンセル）", TextWrapping = TextWrapping.Wrap };
        var border = new Border { Child = text, Padding = new Thickness(16), MinWidth = 320, IsTabStop = true };

        var dialog = new ContentDialog
        {
            Title = "キー割り当て",
            Content = border,
            CloseButtonText = "キャンセル",
            XamlRoot = this.XamlRoot,
        };

        void OnCapturedKeyDown(object s, KeyRoutedEventArgs e)
        {
            e.Handled = true;
            if (e.Key == VirtualKey.Escape) { dialog.Hide(); return; }
            // 修飾キー単体では確定しない（Ctrl+何か、のように非修飾キーが押されるまで待つ）。
            if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
                or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;

            var mods = VirtualKeyModifiers.None;
            if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0) mods |= VirtualKeyModifiers.Control;
            if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0) mods |= VirtualKeyModifiers.Shift;
            if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0) mods |= VirtualKeyModifiers.Menu;

            result = (e.Key, mods);
            dialog.Hide();
        }

        border.KeyDown += OnCapturedKeyDown;
        // ContentDialog は表示時に既定ボタンへ自動フォーカスを割り当てる内部処理を持つため、
        // Border.Loaded（ビジュアルツリー装着直後）で Focus() すると、その後に走る既定フォーカス
        // 割り当てとの競合で border からフォーカスが奪われることがある。ContentDialog.Opened
        // （表示アニメーション・既定フォーカス処理が完了した後に発火）に移すことで競合を避ける。
        // FocusState も Programmatic → Keyboard に変更（コミュニティ実例で有効性が確認されている値）。
        dialog.Opened += (_, _) => border.Focus(FocusState.Keyboard);

        await ShowDialogAsync(dialog);
        border.KeyDown -= OnCapturedKeyDown;
        return result;
    }
}
