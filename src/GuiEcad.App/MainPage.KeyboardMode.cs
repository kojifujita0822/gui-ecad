using GuiEcad.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            Canvas.Focus(FocusState.Programmatic);
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
}
