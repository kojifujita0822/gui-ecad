# テストモード：接点右クリックメニュー

## 概要

テストモード中に `ContactNO`（a 接点）または `ContactNC`（b 接点）を右クリックすると、コンテキストメニューから接点状態を手動で強制できる。

通常のシミュレーション（コイル励磁による接点動作）とは独立した「手動強制」状態であり、視覚的に区別するため **縦棒間を半透明青で塗りつぶす**。

---

## 機能詳細

### 右クリックメニュー

| 項目 | 説明 |
|---|---|
| `接点：CR1  [シミュレーション依存]` | 現在の状態を表示（無効項目） |
| `手動でON（強制閉路）` | 接点を強制的に閉路状態にする。既に手動ONならグレーアウト |
| `手動を解除（シミュレーション依存に戻す）` | 強制を解除し、シミュレーション結果に戻す。未強制ならグレーアウト |

操作対象は `ContactNO` / `ContactNC` のみ。PushButton・SelectSwitch・Timer 等は対象外（それぞれ別の操作方法がある）。

### 手動強制の視覚表示

| 状態 | 記号の描画 |
|---|---|
| 通常（シミュレーション依存） | 縦棒2本のみ（黒線） |
| コイル励磁により通電中 | 縦棒2本（橙色線） |
| **手動強制ON中** | 縦棒2本 ＋ **縦棒間を半透明青で塗りつぶし** |
| 手動強制ON ＋ 通電中 | 縦棒2本（橙色線）＋ 縦棒間を半透明青で塗りつぶし |

手動強制（青塗り）と通電表示（橙色）は独立して表示されるため、「ユーザーが意図的に操作した」か「シミュレーションで自動的に通電した」かが一目で区別できる。

---

## 技術実装

### 問題の経緯

Win2D の `CanvasControl` は DirectX スワップチェーンパネルをラップしており、右クリック（`WM_RBUTTONDOWN`）を Win32 メッセージレベルで処理するため、以下の WinUI 3 マネージドイベントはすべて届かない：

- `RightTapped` イベント
- `PointerPressed` + `IsRightButtonPressed` チェック
- `ContextRequested` イベント

### 解決策：WndProc サブクラス化

ウィンドウの WndProc を差し替えて `WM_CONTEXTMENU`（0x007B）を直接捕捉する。`WM_CONTEXTMENU` は Windows が右クリック後に必ず送出するメッセージであり、Win2D も抑制しない。

```
右クリック
  └─ Windows → WM_RBUTTONDOWN → Win2D が消費（マネージドイベントに届かない）
              → WM_RBUTTONUP   → Win2D が消費
              → WM_CONTEXTMENU → 差し替えた WndProc で捕捉 ✓
```

### 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `src/GuiEcad.Core/Rendering/DrawingTheme.cs` | `ManualForced` 色定数追加（半透明青 ARGB: 110,0,80,220） |
| `src/GuiEcad.Core/Rendering/SymbolGlyphs.cs` | `Draw()` に `manualFill` パラメータ追加。`ContactNO`/`ContactNC` で縦棒間を `FillRectangle` |
| `src/GuiEcad.Core/Rendering/DiagramRenderer.cs` | `DrawElement()` で `inputs` を参照し手動強制を検出、青塗りを `SymbolGlyphs` へ伝達 |
| `src/GuiEcad.App/MainPage.xaml` | `ContextRequested="OnCanvasContextRequested"` 追加（バックアップ） |
| `src/GuiEcad.App/MainPage.xaml.cs` | WndProc フック、`ShowContactContextMenu()`、`ContextRequested` ハンドラ追加 |

### WndProc フックの実装概要

```csharp
// Loaded 時に設置
private void InstallContextMenuHook()
{
    var hwnd = WindowNative.GetWindowHandle(((App)Application.Current).MainWindow!);
    _wndProcDelegate = CanvasWndProc;
    _originalWndProc = SetWindowLong32(hwnd, -4 /* GWLP_WNDPROC */,
        Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
}

// WM_CONTEXTMENU を捕捉
private IntPtr CanvasWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
{
    const uint WM_CONTEXTMENU = 0x007B;
    if (msg == WM_CONTEXTMENU && _testMode && _testSession is not null)
    {
        DispatcherQueue.TryEnqueue(() => ShowContactContextMenu(_contextMenuPointer));
        return IntPtr.Zero; // システム既定メニューを抑制
    }
    return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
}
```

- `_contextMenuPointer` は `OnPointerMoved` で常時更新される Canvas 座標。右クリック直前のカーソル位置として使用する。
- `DispatcherQueue.TryEnqueue` で UI スレッドに切り替えてから `MenuFlyout.ShowAt()` を呼ぶ。

### 塗りつぶし領域の座標（SymbolGlyphs.cs）

ContactNO / ContactNC の縦棒座標は中心 `cx` からの相対値（セル単位）：

```
左棒: x = cx - 0.158 * cell
右棒: x = cx + 0.158 * cell
高さ: y = -0.317 * cell ～ +0.317 * cell

塗り領域:
  Rect(cx - 0.158*cell, -0.317*cell, 幅: 0.316*cell, 高さ: 0.634*cell)
```

塗りつぶしは縦棒の `DrawLine` の **前に** 実行することで、棒の線が塗りの上に描かれ視認性を確保している。

---

## 動作手順

1. ツールバーの **テスト** ボタンを押してテストモードに切り替える
2. 図面上の `ContactNO`（a 接点）または `ContactNC`（b 接点）の記号上で**右クリック**
3. コンテキストメニューが表示される
4. 「手動でON（強制閉路）」を選択 → 縦棒間が青く塗られ、シミュレーションが再評価される
5. 「手動を解除」を選択 → 青塗りが消え、コイル励磁状態に戻る

左クリックによる従来のトグル動作（全種別の接点に対応）は引き続き有効。

---

## 既知の不具合・未解決問題（2026-06-20 調査）

### 問題1: `SetWindowLongW` が x64 で動作しない（フック未インストール）

```csharp
// 現状コード ← x64 では GWLP_WNDPROC に失敗する
[DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
```

`SetWindowLongW` はポインタを 32bit 幅として扱う。x64 では GWLP_WNDPROC (`-4`) に対して
64bit ポインタをセットできず、戻り値 0・エラーで**フックが実際には未インストール**になっている。

正しい修正:

```csharp
[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
```

### 問題2: 右クリック座標を `lParam` から取っていない

`WM_CONTEXTMENU` の `lParam` の下位 16bit に画面座標 X、上位 16bit に画面座標 Y が入っている。
現状コードは `_contextMenuPointer`（`OnPointerMoved` で記録した Canvas 座標）を使っているが、
右クリック前にマウスを動かさなかった場合に位置がずれる。

正しい修正:

```csharp
private IntPtr CanvasWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
{
    const uint WM_CONTEXTMENU = 0x007B;
    if (msg == WM_CONTEXTMENU && _testMode && _testSession is not null)
    {
        // lParam の低/高 16bit から画面座標を取り出す
        int screenX = (short)(lParam.ToInt64() & 0xFFFF);
        int screenY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        DispatcherQueue.TryEnqueue(() => ShowContactContextMenuAtScreen(screenX, screenY));
        return IntPtr.Zero;
    }
    return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
}
```

### 代替アプローチ: `OnPointerPressed` 右ボタン検出（未検証）

WndProc フックより単純な方法として、`OnPointerPressed` の先頭で右ボタンを直接チェックする
方法が使える可能性がある。Win2D の `CanvasControl` は `SwapChainPanel` を継承しており、
`PointerPressed` は左右ボタン両方で発火することが多い（`ContextRequested` / `RightTapped`
が届かないのとは別）。

```csharp
private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
{
    var pt = e.GetCurrentPoint(Canvas);
    if (pt.Properties.IsRightButtonPressed)
    {
        ShowContactContextMenu(pt.Position);
        e.Handled = true;
        return;
    }
    // ... 既存の左クリック処理
}
```

**この方法が動けば WndProc フック全体を削除できる。まず動作確認を推奨。**

### 対応優先順位

1. `OnPointerPressed` 右ボタン検出を試す（動けばコード最小変更）
2. ダメなら `SetWindowLongW` → `SetWindowLongPtrW` に変更 ＋ `lParam` 座標取得に修正
