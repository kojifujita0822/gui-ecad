using GuiEcad.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace GuiEcad_App;

public sealed partial class MainPage : Page
{
    // ===== キーボード配置モード（マウスレスで要素配置。矢印キー移動・数字キーでツール選択・Enterで配置・Escで終了） =====

    private bool _keyboardModeActive;
    private GridPos _focusCell;

    // 数字キー(1〜9,0)→ツールタグの対応。0 は選択ツールに戻る（KeyboardModeHint のテキストと対応させること）。
    private static readonly (VirtualKey Key, string Tag)[] KeyboardModeDigitTools =
    {
        (VirtualKey.Number1, "ContactNO"),
        (VirtualKey.Number2, "ContactNC"),
        (VirtualKey.Number3, "Coil"),
        (VirtualKey.Number4, "PushButtonNO"),
        (VirtualKey.Number5, "PushButtonNC"),
        (VirtualKey.Number6, "TimerContactNO"),
        (VirtualKey.Number7, "TimerContactNC"),
        (VirtualKey.Number8, "Lamp"),
        (VirtualKey.Number9, "Terminal"),
        (VirtualKey.Number0, "select"),
    };

    private void OnKeyboardModeToggle(object sender, RoutedEventArgs e)
    {
        _keyboardModeActive = KeyboardModeBtn.IsChecked == true;
        KeyboardModeHint.Visibility = _keyboardModeActive ? Visibility.Visible : Visibility.Collapsed;
        if (_keyboardModeActive)
        {
            // 初期フォーカスセルは直近のマウスホバーセルを再利用（無ければ左上）。
            _focusCell = _hoverCell.Row >= 0 && _hoverCell.Column >= 0 ? _hoverCell : new GridPos(0, 0);
        }
        Canvas.Invalidate();
    }

    private void ExitKeyboardMode()
    {
        _keyboardModeActive = false;
        KeyboardModeBtn.IsChecked = false;
        KeyboardModeHint.Visibility = Visibility.Collapsed;
        Canvas.Invalidate();
    }

    private void MoveFocusCell(int dRow, int dCol)
    {
        int row = Math.Max(0, _focusCell.Row + dRow);
        int col = Math.Clamp(_focusCell.Column + dCol, 0, Math.Max(0, _sheet.Grid.Columns - 1));
        _focusCell = new GridPos(row, col);
        StatusPos.Text = $"行: {row + 1}  列: {col + 1}";
        Canvas.Invalidate();
    }

    // 矢印キー・数字キーを RootGrid の KeyboardAccelerator（Modifiers=None）として登録する。
    // KeyboardAccelerator はバブリング経路の外側までフレームワークが独自に解決するため、
    // Canvas 等どの要素がフォーカスを持っていても確実に拾える（Canvas.Focus() の成否に依存しない）。
    // 起動時に1回だけ呼ぶ（常時登録し、有効/無効は HandleKeyboardModeKey 内の _keyboardModeActive で制御）。
    private void RegisterKeyboardModeAccelerators()
    {
        var keys = new[] { VirtualKey.Up, VirtualKey.Down, VirtualKey.Left, VirtualKey.Right }
            .Concat(KeyboardModeDigitTools.Select(t => t.Key));
        foreach (var key in keys)
        {
            var acc = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.None };
            acc.Invoked += (_, args) => { if (HandleKeyboardModeKey(key)) args.Handled = true; };
            RootGrid.KeyboardAccelerators.Add(acc);
        }
    }

    /// <summary>キーボード配置モード中の矢印キー・数字キーを処理する。処理したら true（呼び出し側で e.Handled にする）。
    /// テストモード中・テキスト入力中（インライン編集・検索バー等）は無効。</summary>
    private bool HandleKeyboardModeKey(VirtualKey key)
    {
        if (!_keyboardModeActive || _testMode || IsInlineEditing || IsTextInputFocused()) return false;

        switch (key)
        {
            case VirtualKey.Up:    MoveFocusCell(-1, 0); return true;
            case VirtualKey.Down:  MoveFocusCell(1, 0);  return true;
            case VirtualKey.Left:  MoveFocusCell(0, -1); return true;
            case VirtualKey.Right: MoveFocusCell(0, 1);  return true;
        }
        foreach (var (k, tag) in KeyboardModeDigitTools)
            if (key == k) { ActivateTool(tag); return true; }
        return false;
    }

    // ===== グローバルキー（Escape/Backspace/Space） =====
    //
    // 通常のマウス操作（MainPage.Pointer.cs の OnPointerPressed）は Canvas.Focus() を呼ぶため、
    // Canvas 上を一度でもクリックした状態では Page.OnKeyDown にキーが届かないことがある
    // （既知の制約）。矢印キー・数字キーと同じ理由で、これら3キーも Canvas のフォーカス状態に
    // 依存しない RootGrid の KeyboardAccelerator へ移行する。

    /// <summary>起動時に1回だけ呼ぶ。Escape・Backspace・Space を RootGrid の KeyboardAccelerator として登録する。</summary>
    private void RegisterGlobalKeyAccelerators()
    {
        void Add(VirtualKey key, Func<bool> handler)
        {
            var acc = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.None };
            acc.Invoked += (_, args) => { if (handler()) args.Handled = true; };
            RootGrid.KeyboardAccelerators.Add(acc);
        }
        Add(VirtualKey.Escape, HandleEscape);
        Add(VirtualKey.Back, HandleBackspace);
        Add(VirtualKey.Space, HandleSpaceDown);
    }

    // Escape の意味は文脈で7通りに分かれる（優先順位はコード順）。常に処理済み扱い（true）。
    private bool HandleEscape()
    {
        if (_rangeSelecting) { _rangeSelecting = false; ClearMultiSelection(); Canvas.Invalidate(); return true; }
        if (_editingElement != null) CommitDeviceName(accept: false);
        else if (_editingComment != null) CommitComment(accept: false);
        else if (_editingRungComment != null) CommitRungComment(accept: false);
        else if (_editingFrame != null) CommitFrameLabel(accept: false);
        else if (FindBar.Visibility == Visibility.Visible) CloseFindBar();
        else if (_tool.Mode != ToolMode.Select) ActivateTool("select");
        else if (_keyboardModeActive) ExitKeyboardMode();
        return true;
    }

    // テキスト入力中（インライン編集・プロパティパネル・検索バー）は要素を削除せず、
    // Handled=false のままにしてテキスト編集（文字削除）に委ねる。
    private bool HandleBackspace()
    {
        if (IsInlineEditing || IsTextInputFocused()) return false;
        DeleteSelected();
        return true;
    }

    // Space 押下でパン開始。KeyboardAccelerator に KeyUp 相当が無いため、離した検知は
    // MainPage.Pointer.cs の OnPointerMoved 内でポーリングする。編集中・検索バー表示中は無効。
    private bool HandleSpaceDown()
    {
        if (_testMode || _editingElement != null || _editingComment != null
            || _editingRungComment != null || _editingFrame != null
            || FindBar.Visibility == Visibility.Visible) return false;
        _spacePanActive = true;
        return true;
    }
}
