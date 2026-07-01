# キーボード入力不具合 修正プラン（7.マウスレス配置／3.ショートカットキー設定）

> 起案 2026-07-01（隠密）。家老より、7)3)ともに複数回の手戻り（Enter不具合→修正→退行→再修正でも直らず）が発生しているため、原因診断に留まらず docs/three-phase-plan.md §8 と同粒度の設計プランとして起案する依頼を受けた。
> 家老の指示により、まず「WinUI3の構造的制約でこの方式自体が実現不可能ではないか」を最優先で判定した。結論は下記§0の通り**実現可能**であり、本プランをそのまま提出した。
>
> **【2026-07-01 殿承認済み・確定事項】**
> - **7) 方針B（KeyboardAccelerator方式へ全面移行）を採用**。案A分（`FocusCanvasForKeyboardMode`/`OnCanvasKeyDown`等）のコードは撤去する。
> - **3) 段階的対応を採用**。まず軽微な修正（`ContentDialog.Opened`時に変更＋`FocusState.Keyboard`）を適用し、実機確認で改善しなければ標準`Control`派生要素への差し替えに進む。
> - 本ファイルの内容のまま侍へ引き継ぐ（家老からも着手指示済み）。

## 0. 実現可能性の判定（最優先事項・結論）

**結論: 実現可能。** WinUI3にはUWPの`CoreWindow.KeyDown`のような「フォーカス非依存でグローバルにキーを拾う」公式APIは存在しない（[microsoft-ui-xaml Issue #3986](https://github.com/microsoft/microsoft-ui-xaml/issues/3986)「Gather keyboard input」がnot plannedでclose済み、確認済み）。しかし、**`KeyboardAccelerator`（`ScopeOwner`未指定＝グローバル、`Modifiers="None"`の単独キーも公式にサポート）は、通常のKeyDownバブリング経路とは別に「バブリング経路の外側までフレームワークが独自に走査して解決する」仕組み**であり（[公式ドキュメント](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-accelerators)の"Resolving accelerators"節で確認）、フォーカスの所在に依存しない。本アプリの`RootGrid`に登録済みのCtrl+Z等11個のアクセラレータが確実に動作しているのはこの仕組みによるもので、**既にこのプロジェクト内で実証済みの信頼できるパターン**である。

したがって、Canvas/ContentDialog個別要素のフォーカス状態に賭ける現行方式（案A）よりも、**KeyboardAcceleratorベースの方式へ寄せる方が構造的に堅牢**と判断する。

## 1. 現状（原因の再構成）

### 7. マウスレス配置（キーボード配置モード）

**時系列**:
1. 当初実装: `FocusCanvasForKeyboardMode()`が`Canvas.Focus(FocusState.Programmatic)`を**同期**呼び出し。ボタンクリック後のWinUI既定フォーカス割り当て（ボタン自身へ）と競合し、同期呼び出しが負けてボタン側にフォーカスが残存 → Enterキーがボタンの既定動作（トグルOFF）に奪われるバグが発生。
2. 侍が修正: `DispatcherQueue.TryEnqueue`で非同期化し、既定のフォーカス割り当てが終わった後に`Canvas.Focus()`を実行するよう変更（Enterキーバグは解消想定）。
3. **退行発生**: 矢印キー・数字キーが一切反応しなくなった。原因は`MainPage.xaml.cs:606-607`に既存コメントで明記されている既知の制約「CanvasControlがフォーカスを持つとPage.OnKeyDownが届かない場合がある」が、初めてCanvasが実際にフォーカスを獲得したことで発動したため（隠密が前回報告済み）。
4. 侍が案Aを適用: `Canvas.KeyDown="OnCanvasKeyDown"`（`MainPage.xaml:535`）を追加し、Canvas自身のKeyDownで`HandleKeyboardModeKey`を直接処理する方式に変更。
5. **それでも直らず**（今回の家老報告）。

**案Aが機能しなかった理由（今回の調査で判明）**: `Canvas.Focus(FocusState.Programmatic)`は、Canvas自身が「フォーカス可能な状態」になっていなければ**何も起きず静かに`false`を返すだけ**（例外なし）。Win2Dの`CanvasControl`は`UserControl`（`Control`系統）を継承しているが、コミュニティでの既知の実例（[Win2D Issue #686](https://github.com/Microsoft/Win2D/issues/686)）では「クリックしても自動ではフォーカスを取得せず、明示的に`IsTabStop="True"`を設定しなければならない」と報告されている。

**現在の`MainPage.xaml`のCanvas定義（525-535行）を確認したところ、`IsTabStop`属性が設定されていない**（`Background`属性も未設定）。すなわち、案Aは「Canvasがフォーカスを取得できている前提」でKeyDownハンドラだけを追加したが、**そもそもCanvasがフォーカスを取得できていない可能性が高い**。これが「案A適用後も直らない」の直接原因と推定する（`Canvas.Focus()`の戻り値をログ等で確認していないため断定はできないが、最有力仮説）。

### 3. ショートカットキー設定（キー再割当てダイアログ）

`MainPage.KeyBindings.cs`の`CaptureKeyBindingAsync()`（254-293行）は、`ContentDialog`内の`Border`要素に`IsTabStop = true`を設定し（260行）、`Loaded`イベントで`border.Focus(FocusState.Programmatic)`を呼んでいる（288行）。

一見、Win2D Issue #686の「解決策」と同じパターン（`IsTabStop=true` + 明示`Focus()`）を踏襲しているように見えるが、**それでも矢印キー・数字キー含め一切のキー入力を捕捉できない**という報告が来ている。考えられる原因（優先度順）:

1. **`ContentDialog`自身の既定フォーカス処理との競合**: `ContentDialog`は表示時、既定ボタン（`CloseButtonText`等）へ自動的にフォーカスを割り当てる内部処理を持つ。`Border.Loaded`イベントは要素がビジュアルツリーに載った直後に発火するが、`ContentDialog`側の既定フォーカス割り当てがそれより**後**に走った場合、7)で発生したのと同型の「フォーカス競合レース」が起き、`Border`から奪われる可能性がある（7)のボタン vs Canvasと同一パターン）。
2. **`FocusState.Programmatic`と`FocusState.Keyboard`の違い**: Win2D Issue #686の実例では`FocusState.Keyboard`を使って解決したと報告されている。本コードは`FocusState.Programmatic`を使用しており、状態種別の違いが何らかの内部処理（フォーカス視覚効果の抑制以外の副作用）に影響している可能性は**未確認**（公式ドキュメントからは断定できず）。
3. `ContentDialog`内要素の`Loaded`時点でのフォーカスツリー未確定問題は、今回の調査で**一次資料による裏付けは得られず**（「確認できず」として報告済み）。関連の可能性がある事象（TeachingTipの`IsOpen`非同期性に関する`microsoft-ui-xaml` Issue #3257）はあるが、`ContentDialog`固有の直接的な証拠ではない。

## 2. 方針

### 7. マウスレス配置

**2案を提示。方針Bを推奨。**

- **方針A（応急修正）**: 現行のCanvas.KeyDown方式を活かし、Canvas要素に`IsTabStop="True"`（および`Background="Transparent"`、未設定なら念のため）を追加し、`Canvas.Focus(FocusState.Programmatic)`の戻り値を検証（ログ出力等でデバッグ）した上で確実にフォーカスが移ることを確認する。変更範囲は最小だが、**Canvasのフォーカス状態に賭け続ける構造は変わらず**、将来また同種の退行（他機能がCanvasからフォーカスを奪う変更をした際等）が再発するリスクが残る。
- **方針B（推奨・根本対応）**: 矢印キー・数字キーの処理を、既にこのプロジェクトで実証済みの`KeyboardAccelerator`方式（`RootGrid`、`Modifiers="None"`）に置き換える。`MainPage.KeyBindings.cs`の`ApplyKeyBindings()`と同じ仕組み（動的`KeyboardAccelerator`追加）を使い、`_keyboardModeActive`のときだけ有効になるアクセラレータ（あるいは常時登録して`HandleKeyboardModeKey`内の`_keyboardModeActive`ガードで制御）を`RootGrid`に追加する。Canvasのフォーカス状態に一切依存しないため、案Aで露呈した「案Aを適用しても直らない」という不確実性の芽を構造的に断てる。

**方針Bの実装イメージ**:
```csharp
// MainPage.KeyboardMode.cs に追加。既存の HandleKeyboardModeKey はそのまま流用。
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
```
- `FocusCanvasForKeyboardMode()`（Canvas.Focus呼び出し）自体は**不要になり削除**（Enterキーバグ・矢印キー退行いずれも、根本原因であるCanvasフォーカス依存を無くすことで解消）。
- `Canvas.KeyDown="OnCanvasKeyDown"`（案A、`MainPage.xaml:535`）は撤去してよい（`HandleKeyboardModeKey`はアクセラレータ経由の呼び出しに一本化）。
- ただし、テキスト入力コントロール（`NumberBox`のプロパティパネル等）にフォーカスがある場合、矢印キーの組み込み動作（カーソル移動等）が優先される（公式ドキュメントで確認済み: 「フォーカス中コントロールの組み込みアクセラレータが優先」）ため、**既存の`IsInlineEditing`/`IsTextInputFocused()`ガードとの二重防御になり、むしろ安全性が上がる**。

### 3. ショートカットキー設定（キー再割当てダイアログ）

キャプチャダイアログは「未知の任意の1キーを拾う」用途のため、`KeyboardAccelerator`（事前に特定のキーを登録する方式）ではそのまま代替できない。以下の修正を提案する。

1. **フォーカス割り当てのタイミングを`Border.Loaded`から`ContentDialog.Opened`に変更**: `ContentDialog`の`Opened`イベントは、ダイアログの表示アニメーション・既定フォーカス処理が完了した後に発火するため、`ContentDialog`自身の内部フォーカス処理との競合（7)で発生したのと同型のレース）を回避できる可能性が高い。
2. `FocusState.Programmatic`を`FocusState.Keyboard`に変更（Win2D Issue #686の実例に倣う。副作用未確認だが、コミュニティで有効性が確認されている値のため、リスクの低い変更として採用）。
3. 上記1・2を適用してもなお改善しない場合の**予備方針**: `Border`の代わりに、確実にフォーカス可能であることが保証されている標準`Control`派生要素（例: `TextBox`を読み取り専用・非表示キャレットにして使う、あるいは`Button`をキー捕捉用に流用する等）に差し替える。

## 3. 実装イメージ（差分の要点）

**7)**:
- 削除: `FocusCanvasForKeyboardMode()`呼び出し（`OnKeyboardModeToggle`・`ActivateTool`内）、`Canvas.KeyDown="OnCanvasKeyDown"`（XAML）、`OnCanvasKeyDown`メソッド。
- 追加: `RegisterKeyboardModeAccelerators()`（`MainPage.KeyboardMode.cs`、起動時1回呼ぶ）。

**3)**:
- `CaptureKeyBindingAsync()`内、`border.Loaded += (_, _) => border.Focus(FocusState.Programmatic);`を`dialog.Opened += (_, _) => border.Focus(FocusState.Keyboard);`に変更。

## 4. 影響範囲・変更ファイル一覧

- `src/GuiEcad.App/MainPage.KeyboardMode.cs`（`FocusCanvasForKeyboardMode`削除、`RegisterKeyboardModeAccelerators`新設、`OnCanvasKeyDown`削除）
- `src/GuiEcad.App/MainPage.Parts.cs`（`ActivateTool`内の`FocusCanvasForKeyboardMode()`呼び出し削除、410行）
- `src/GuiEcad.App/MainPage.xaml`（Canvas要素の`KeyDown="OnCanvasKeyDown"`属性削除、535行）
- `src/GuiEcad.App/MainPage.KeyBindings.cs`（`CaptureKeyBindingAsync`のフォーカスタイミング・FocusState変更、287-288行）
- `src/GuiEcad.App/MainPage.xaml.cs`（起動時初期化箇所に`RegisterKeyboardModeAccelerators()`呼び出しを追加。既存の`ApplyKeyBindings()`呼び出し箇所と同じ初期化フローに乗せるのが妥当）

## 5. テスト方針

いずれもWinUI依存のUI操作でありxUnit自動テスト対象外（既存方針通り）。
- `dotnet build`/`dotnet test`で既存184テストの回帰が無いことを確認。
- **実機確認（忍者）**: 以下を必須シナリオとする。
  - 7): キーボード配置モードON→矢印キーでフォーカスセル移動→数字キーでツール切替→Enterで配置→Escで終了、の一連が期待通り動作すること。プロパティパネルの`NumberBox`にフォーカスがある状態で矢印キーがNumberBox側の増減に使われ、配置モードのフォーカスセル移動と衝突しないこと。
  - 3): ショートカットキー設定ダイアログで「変更」→キー押下→即座に捕捉されダイアログが閉じること。Escでのキャンセルも動作すること。
  - 両機能とも、UIA検証（忍者）で実際に`FocusManager.GetFocusedElement`相当の状態を確認できるとなお確実（可能であれば）。

## 6. 概算工数

- 7)（方針B）: 0.5〜1人日（既存`ApplyKeyBindings`と同型のロジックの流用のため低コスト。撤去作業込み）。
- 3)（フォーカスタイミング変更）: 0.25〜0.5人日（小さな変更だが、原因未確定のため実機確認で効果が出ない場合の追加調査を見込む）。

## 7. 要判断事項

1. **7)方針Bの採用可否**: Canvasフォーカスに依存しない設計へ転換するため、既存の`FocusCanvasForKeyboardMode`/`OnCanvasKeyDown`（案A分）を丸ごと撤去する。手戻りが重なった機能のため、家老・殿の承認を得てから侍へ引き継ぐのが望ましい。
2. **3)の修正が効かなかった場合の予備方針（Border→標準Control差し替え）まで実施するか**: まずは軽微な変更（Opened時変更＋FocusState.Keyboard）を試し、それでも改善しない場合に本格的な要素差し替えに進む、という段階的対応でよいか。
3. **7)のプロパティパネルNumberBoxとの矢印キー競合**: 公式ドキュメント上は「フォーカス中コントロールの組み込み動作が優先」とあるが、実機での最終確認が必要（この点のみ一次資料で断定できず、忍者確認必須）。

---

## ラウンド2（2026-07-01・方針B実装後に殿実機確認で発覚した2件）

> 侍が方針Bを実装・コミット（1f6c3c9）後、殿の実機確認（忍者UIA検証と一致）で以下2件が確定バグとして判明。家老の依頼により再度、実現可能性判定を含めたプランを追記する。

### 実現可能性の判定（両件とも）

**結論: 両件とも実現可能。** 詳細は各節参照。設計変更を伴うが、WinUI3の構造的制約で「不可能」となるものではない。

### R2-1. Escapeキーでキーボード配置モードが解除されない

**現状**（`MainPage.xaml.cs:636-676`）: 方針Bで矢印キー・数字キーは`RootGrid`の`KeyboardAccelerator`へ完全移行したが、**Escapeキーの処理だけは従来通り`Page.OnKeyDown`（651-661行）に残ったまま**。この分岐の最後（659行）に`else if (_keyboardModeActive) ExitKeyboardMode();`があるが、Escapeキー自体がPage.OnKeyDownに届いていなければ、この分岐に到達する前にそもそも呼ばれない。

**原因**: `FocusCanvasForKeyboardMode()`（Canvasへ明示的にフォーカスを移す処理）は方針Bで撤去されたが、**通常のマウス操作（`MainPage.Pointer.cs:95`の`OnPointerPressed`）は従来通り`Canvas.Focus(FocusState.Programmatic)`を呼んでいる**。したがって、ユーザーがキーボード配置モードに入る前に一度でもCanvas上をクリックしていれば（＝ごく普通の操作フロー）、Canvasが依然としてフォーカスを保持したままキーボード配置モードに入ることになり、既知の制約（`MainPage.xaml.cs:638-639`のコメント、「CanvasControlがフォーカスを持つとPage.OnKeyDownが届かない場合がある」）が発動し、Escapeが届かない。

**重要な気づき**: この問題は「キーボード配置モードがCanvasへフォーカスを強制する」ことに起因していたのではなく、**Canvasが通常のマウス操作で普通にフォーカスを持つ状態は日常的に発生し、その状態でPage.OnKeyDown経由のキーは全般的に不安定である**、という、方針Bへの移行前から潜在していたより広い問題の一角である。今回Escapeで顕在化したが、理論上はBackspace（643-646行）・Space（663-671行）も同条件下で同様に不安定になりうる（未報告だが要注意）。

**方針**: Escapeキーの処理全体（7分岐すべて）を、矢印キー・数字キーと同じ`KeyboardAccelerator`方式（`RootGrid`、`Modifiers="None"`、`Key=Escape`）へ移行する。Escapeは[公式ドキュメント](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-accelerators)で「単独キーアクセラレータとしてサポートされる」実例に明示的に挙げられている（`Esc`が例示リストに含まれる）ため、他のキーと同様に確実性が担保できる。

**実装イメージ**:
```csharp
// MainPage.KeyboardMode.cs 等に追加。既存の Escape 分岐ロジックをそのままメソッド化。
private void RegisterEscapeAccelerator()
{
    var acc = new KeyboardAccelerator { Key = VirtualKey.Escape, Modifiers = VirtualKeyModifiers.None };
    acc.Invoked += (_, args) => { HandleEscape(); args.Handled = true; };
    RootGrid.KeyboardAccelerators.Add(acc);
}

private void HandleEscape()
{
    if (_rangeSelecting) { _rangeSelecting = false; ClearMultiSelection(); Canvas.Invalidate(); return; }
    if (_editingElement != null) CommitDeviceName(accept: false);
    else if (_editingComment != null) CommitComment(accept: false);
    else if (_editingRungComment != null) CommitRungComment(accept: false);
    else if (_editingFrame != null) CommitFrameLabel(accept: false);
    else if (FindBar.Visibility == Visibility.Visible) CloseFindBar();
    else if (_tool.Mode != ToolMode.Select) ActivateTool("select");
    else if (_keyboardModeActive) ExitKeyboardMode();
}
```
- `MainPage.xaml.cs`の`OnKeyDown`から`case VirtualKey.Escape:`ブロック（651-661行）は削除し、`HandleEscape()`呼び出しへ一本化する。
- **要判断事項**: Backspace・Spaceも同種の潜在リスクを抱えているため、本件と合わせて同時にKeyboardAccelerator化するか、まずEscapeのみ対応して様子を見るか。最小実行の原則（要求範囲を超えない）を踏まえると、**今回はEscapeのみ対応し、Backspace/Spaceの同種リスクは`docs/todo.md`等に既知の潜在課題として記録するに留める**ことを推奨するが、家老・殿の判断を仰ぐ。

> **【2026-07-01 殿承認済み・追加決定】** Backspace・SpaceキーもEscapeと合わせて今回KeyboardAccelerator化する。ただし下記「Backspace/Space移行時の技術的注意」を参照のこと（Spaceは単純な移行ができない）。

#### Backspace/Space移行時の技術的注意

**Backspace**: Escapeと同様、単発の「押されたら削除実行」というステートレスな処理のため、`KeyboardAccelerator`（`Key=Back`, `Modifiers=None`）へそのまま移行可能。`IsInlineEditing || IsTextInputFocused()`のガードもInvokedハンドラ内にそのまま持ち込めばよい。

**Space（要注意・単純移行不可）**: `KeyboardAccelerator`は**押下時の単発発火（Invoked）のみ**で、離した瞬間（KeyUp相当）に対応するイベントを持たない。現状のSpaceキーパンは「押している間だけパン」という**連続状態管理**（`OnKeyDown`で`_spacePanActive = true`、`OnKeyUp`で`_spacePanActive = false`、`MainPage.xaml.cs:678-681`）に依存しているため、**押下側だけを単純にKeyboardAccelerator化すると、離した時にパンを止める手段が無くなり、パンモードが解除されなくなる回帰の恐れがある**。

**対応案**: `OnKeyUp`によるキー解放検知をやめ、`Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Space)`（`MainPage.KeyBindings.cs`の`CaptureKeyBindingAsync`で既に使用実績あり）で**Spaceキーの現在の押下状態をポーリング確認**する方式に切り替える。既存の`OnPointerMoved`（パン中は継続的に呼ばれる）内で、移動のたびにSpaceキー状態を確認し、離されていれば`_spacePanActive = false`にする。これにより「離した瞬間のイベント」に頼らず、KeyDown側のみKeyboardAccelerator化で確実性を上げつつ、パン終了もCanvasフォーカス状態に依存しない形にできる。

> **【2026-07-01 殿承認済み・最終確定】** Spaceキーは上記ポーリング方式（`GetKeyStateForCurrentThread`、`OnPointerMoved`内で継続確認）で対応する。Escape・Backspace・Spaceの3件とも本ラウンドで対応し、侍へ引き継ぐ。

**影響範囲・変更ファイル**: `src/GuiEcad.App/MainPage.xaml.cs`（OnKeyDownからEscape分岐削除）、`src/GuiEcad.App/MainPage.KeyboardMode.cs`（`RegisterEscapeAccelerator`/`HandleEscape`新設、起動時初期化に追加）。

**テスト方針**: 既存184テストの回帰確認＋実機確認（忍者）：範囲選択中・編集中（機器名/コメント/行コメント/枠ラベル）・検索バー表示中・ツール選択中・キーボード配置モード中のそれぞれでEscapeキーが期待通りの分岐を実行することを確認（7パターン網羅）。

**概算工数**: 0.25〜0.5人日（既存ロジックのメソッド化＋登録処理のみ、ロジック自体は変更なし）。

---

### R2-2. ショートカットキー設定: キー捕捉ダイアログが背面表示で見えない

**現状**（`MainPage.Dialogs.cs:28-49`）: `ShowDialogAsync`は`_dialogGate`（`SemaphoreSlim(1,1)`）でContentDialogの多重表示を防ぐラッパー。コメントに明記の通り、**WinUI3は「同時に複数のContentDialogを開けない」制約（例外になる）を持つため、直列化のために導入された**。

`OnMenuKeyBindingSettings`（`MainPage.KeyBindings.cs:184-252`）は、設定一覧を表示する`dialog`を`ShowDialogAsync(dialog)`（247行）で開く。この`await`は**設定ダイアログが閉じるまで完了しない**。ところが、その設定ダイアログの中の「変更」ボタン（201-216行）のClickハンドラは、ダイアログがまだ開いたままの状態で`CaptureKeyBindingAsync()`（254-293行）を呼び、その中でさらに`ShowDialogAsync(dialog)`（295行、キー捕捉用の別のContentDialog）を呼んでいる。

**原因（確定）**: 外側の設定ダイアログの`ShowDialogAsync`呼び出しは`_dialogGate`を保持したまま`dialog.ShowAsync()`の完了（＝設定ダイアログが閉じること）を待ち続けている。その状態で「変更」ボタンから呼ばれる内側の`ShowDialogAsync(captureDialog)`は`_dialogGate.WaitAsync()`で**ゲートの解放を待つが、外側が保持したまま設定ダイアログを閉じない限り解放されない**。つまり**キー捕捉ダイアログの`ShowAsync()`自体が呼ばれない**状態のまま停止する（UIスレッドは固まらないが、見た目には「何も起きない」）。これが「背面に表示されて見えない」という体感の実体と考えられる。

`_dialogGate`は「順番に1つずつ表示する」逐次シナリオ向けに設計されたものであり、**「あるダイアログを開いたまま、その中からさらに別のダイアログを開く」入れ子シナリオには本質的に対応していない**。これは実装ミスというより、そもそもWinUI3の「ContentDialogは同時に1つまで」という制約と、「設定ダイアログを開いたまま子ダイアログでキー入力を捕まえたい」というUI要件が、ContentDialogという入れ物を2重に使う設計そのものと相性が悪い、という構造的な問題である。

**方針（2案、方針1を推奨）**:
- **方針1（推奨）**: キー捕捉を別のContentDialogとして開かず、**設定ダイアログの中で完結するインライン捕捉**に変更する。「変更」ボタンを押すと、そのボタン（または表示テキスト部分）が「キーを押してください...」という表示に変わり、設定ダイアログ自身の`Content`ツリー内の既にフォーカス可能な要素（例: `changeBtn`自身、または新設する小さな`Border`/`TextBox`）へフォーカスを移して直接`KeyDown`を捕まえる。`TaskCompletionSource<(VirtualKey, VirtualKeyModifiers)?>`で待受け、ダイアログを二重に開く必要が無くなるため、`_dialogGate`の入れ子問題自体が発生しない。
- **方針2（見送り推奨）**: 設定ダイアログを`Hide()`してから捕捉ダイアログを開き、完了後に設定ダイアログを再構築して開き直す。実装は方針1よりシンプルに見えるが、ダイアログの開閉アニメーションが2回走りチラつく・スクロール位置等の状態が失われるUX上の欠点があり、根本解決にもならない（見た目の問題を先送りするだけ）ため見送りを推奨。

**方針1の実装イメージ**:
```csharp
// changeBtn.Click 内、CaptureKeyBindingAsync() の呼び出しを置き換える。
changeBtn.Click += async (_, _) =>
{
    changeBtn.IsEnabled = false;
    display.Text = "キーを押してください（Escでキャンセル）";
    var captured = await CaptureKeyBindingInlineAsync(changeBtn);   // 新設: 同一ダイアログ内で完結
    changeBtn.IsEnabled = true;
    if (captured is not (VirtualKey key, VirtualKeyModifiers mods))
    {
        display.Text = FormatBinding(working[cmd.Id]);   // キャンセル時は表示を戻す
        return;
    }
    // 以下、競合チェック・確定処理は現行のまま
    ...
};

// 新設: 既にフォーカス可能な要素(例: changeBtn自身)へKeyDownを一時アタッチして1キー分だけ捕まえる。
private async Task<(VirtualKey, VirtualKeyModifiers)?> CaptureKeyBindingInlineAsync(Button target)
{
    var tcs = new TaskCompletionSource<(VirtualKey, VirtualKeyModifiers)?>();
    void OnKeyDown(object s, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Key == VirtualKey.Escape) { tcs.TrySetResult(null); return; }
        if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
        var mods = VirtualKeyModifiers.None;
        if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0) mods |= VirtualKeyModifiers.Control;
        if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0) mods |= VirtualKeyModifiers.Shift;
        if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0) mods |= VirtualKeyModifiers.Menu;
        tcs.TrySetResult((e.Key, mods));
    }
    target.KeyDown += OnKeyDown;
    target.Focus(FocusState.Keyboard);   // 既にContentDialogが開いている＝フォーカスツリー確定済みのため確実
    var result = await tcs.Task;
    target.KeyDown -= OnKeyDown;
    return result;
}
```
- `target`（`changeBtn`）は**既に開いている設定ダイアログの中に元から存在する要素**であり、`Button`は標準`Control`なので`IsTabStop`は既定で有効・フォーカス取得も確実（R2-1・7)で判明したCanvas/Borderのフォーカス取得問題とは異なり、ContentDialogを新たに開く動作を伴わないため、ダイアログ表示タイミングとのレースも発生しない。
- 既存の`CaptureKeyBindingAsync()`（別ダイアログ版）は本方式採用時は不要になり削除可能。

**影響範囲・変更ファイル**:
- `src/GuiEcad.App/MainPage.KeyBindings.cs`（`changeBtn.Click`ハンドラの置き換え、`CaptureKeyBindingInlineAsync`新設、旧`CaptureKeyBindingAsync`削除）

**テスト方針**: 既存184テストの回帰確認＋実機確認（忍者）：「変更」ボタン押下→表示が「キーを押してください」に変わる→キー押下で即座に確定→表示更新、Escでキャンセルして元の表示に戻る、競合キー入力時のエラーダイアログ表示、の一連を確認。

**概算工数**: 0.5〜1人日（既存の別ダイアログ版キャプチャロジックをインライン方式へ書き換え）。

**要判断事項**:
- 方針1採用でよいか（方針2は見送り推奨だが、方針1の方が変更範囲がやや大きい）。
- 他のダイアログ（今回は該当なしと思われるが）で同様の「ダイアログを開いたまま別ダイアログを開く」パターンが無いか、`_dialogGate`利用箇所（`ShowDialogAsync`呼び出し16箇所）を将来的に横断チェックする余地があるか（本件の範囲を超えるため、今回は対象外・別途要検討事項として記録するに留める）。

---

## ラウンド3（2026-07-01・ラウンド2実装後に殿実機確認で発覚した3件）

> いずれも原因はコード読解のみで確定できたため、今回は外部の実現可能性調査は不要（WinUI3の構造的制約に起因する問題ではなく、実装上の見落とし・仕様未定義が原因）。3件とも実現可能・対応方針も明確。

### R3-1.【バグ】キーボード配置モードでEnter設置後、機器名を直接入力できない

**現状**（`MainPage.Menu.cs:39-52` `OnEnterAccelerator`）: キーボード配置モード中のEnterは`PlaceElementAt`のみを呼び、**意図的に**`ShowDeviceNameEditor`を呼ばない（38行のコメント「機器名編集は起動しない」）。一方、`PlaceElementAt`（`MainPage.Pointer.cs:82-133`）は配置成功時に`_selected = el;`（129行）を設定するが、`OnEnterAccelerator`はそれを使って名前編集を開始する処理を持たない。そのため配置後は何もフォーカスされた入力欄が無く、続けて文字キーを押しても行き先が無く入力できない。

**設計上の背景（前回までの意図の再構成）**: `OnEnterAccelerator`冒頭のガード（41行）`_editingElement is not null` 時は即return＝機器名編集中はこのアクセラレータ自体が何もしない設計になっている。これは、機器名編集中のEnterは`DeviceNameBox`自身の`KeyDown`ハンドラ（`OnDeviceNameBoxKeyDown`、`MainPage.Pointer.cs`内、Enter/Tabで`CommitDeviceName(true)`）に委ねる、といういう既存の役割分担と整合する。つまり**「配置後すぐに名前編集を開始し、名前確定時はDeviceNameBox側のEnterに任せる」という設計は既存の枠組みと矛盾なく実現できる**。単に配置後に`ShowDeviceNameEditor`を呼ぶ一手が抜けていただけ。

**方針**: `PlaceElementAt`の戻り値を`void`から`ElementInstance?`（配置成功時は配置した要素、拒否時は`null`）に変更し、`OnEnterAccelerator`のキーボード配置モード分岐で配置成功時に`ShowDeviceNameEditor(placed)`を呼ぶ。

**実装イメージ**:
```csharp
// MainPage.Pointer.cs: PlaceElementAt の戻り値を ElementInstance? に変更
private ElementInstance? PlaceElementAt(ElementKind kind, int row, int col)
{
    // ...既存の拒否チェック（既存要素途中への挿入・行末はみ出し）は return null; に変更
    // ...
    _selected = el;
    RefreshDevicePanel();
    RefreshPropertiesPanel();
    Canvas.Invalidate();
    return el;
}

// MainPage.Menu.cs: OnEnterAccelerator のキーボード配置モード分岐
if (_keyboardModeActive)
{
    if (PlaceKind is ElementKind kind && _focusCell.Row >= 0 && _focusCell.Column >= 0
        && _focusCell.Column < _sheet.Grid.Columns)
    {
        var placed = PlaceElementAt(kind, _focusCell.Row, _focusCell.Column);
        if (placed is not null) ShowDeviceNameEditor(placed);
    }
    args.Handled = true;
    return;
}
```
- 既存の呼び出し元（`MainPage.Pointer.cs:315`付近、マウスクリックでの配置）は戻り値を無視して従来通り使ってよい（`void`→`ElementInstance?`への変更は戻り値を使わない既存呼び出しに影響しない）。

**要判断事項**: 「配置した要素の種別によらず常に名前編集を自動起動する」でよいか（例えば端子台等、機器名を持たない/不要な種別がある場合は除外するか）。既存の`ElementCatalog`等に「機器名不要」を示すフラグがあるか要確認だが、現行の通常配置（マウス）でも種別を問わず`_selected is not null`のみでEnterが名前編集を起動する設計のため、**同じ基準（種別を問わない）で統一するのが一貫性がある**と判断する。

**【2026-07-01 追記・殿の仮説の検証結果】** 殿より「機器名編集中に数字キーを打つと、キーボード配置モードの数字キーKeyboardAccelerator（ツール切替）に横取りされるのではないか」との仮説が提起され、家老の指示で検証した。

一次資料（[公式ドキュメント](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-accelerators)の"Input event priority"節、[microsoft-ui-xaml Issue #1435](https://github.com/microsoft/microsoft-ui-xaml/issues/1435)）で確認した事実:
- アクセラレータ解決は`OnKeyDown`・`CharacterReceived`（文字確定）より**必ず先**に走る。
- **`Invoked`ハンドラは、対象キーにフォーカス中のTextBox等が既に処理済みかどうかに関わらず、`args.Handled`の値と無関係に毎回呼ばれる**（Issue #1435でMicrosoft自身が「本来はHandled=trueになっているべきだが実際はそうなっていない」と報告）。
- ただし`Handled`の既定値は`false`であり、`Handled=false`のままならKeyDownバブリング・CharacterReceived（文字確定）はブロックされない＝**文字入力自体は通常通り継続される可能性が高い**（ドキュメントに明記）。

**本アプリへの当てはめ**: `HandleKeyboardModeKey`（`MainPage.KeyboardMode.cs:78-92`）は`IsTextInputFocused()`を含むガードで、DeviceNameBox編集中は**`ActivateTool`を一切呼ばずfalseを返す**設計になっている。つまり「Invokedハンドラ自体は毎回呼ばれる」という一次資料の指摘は事実だが、**本アプリのコードは呼ばれた上で何もしない設計になっており、`args.Handled`もfalseのまま**＝ツール切替という副作用は起きず、数字自体も通常通りTextBoxに入力される、という理屈になる。したがって**殿の仮説が懸念する「ツール切替が起きてしまう」実害は、現状のガード設計により発生しないと推定される**。

**残る不確実性**: 一次資料は「Handled=falseなら文字入力は概ね継続される」としながらも「可能性が高い」という表現に留まり100%の断定はしていない。したがって**家老の指示通り、実機確認（忍者）で実際に機器名へ数字を含む名前（例: `CR11`）を問題なく入力できるかを明示的にテスト項目とする**。もし実機で数字が入力できない・意図せずツールが切り替わる等の不具合が確認された場合は、殿提案のF1〜F12キーへの切替（ツール選択を通常のテキスト入力と衝突しえないキー体系に変更）を改めて検討する。

**影響範囲・変更ファイル**: `src/GuiEcad.App/MainPage.Pointer.cs`（`PlaceElementAt`戻り値変更）、`src/GuiEcad.App/MainPage.Menu.cs`（`OnEnterAccelerator`のキーボード配置モード分岐）。

**テスト方針**: 既存184テストの回帰確認（`PlaceElementAt`はUI直結のためxUnit対象外、シグネチャ変更のみでロジック不変のため影響小）＋実機確認（忍者）：キーボード配置モードでEnter設置→即座に文字入力で機器名を入力→Enterで確定→続けて次の要素を配置、の連続フローを確認。**加えて、機器名に数字を含む場合（例: `CR11`）を必ずテストする**（数字キーのKeyboardAcceleratorに横取りされずTextBoxへ正しく入力されるか、意図せずツールが切り替わらないか。上記「殿の仮説の検証結果」参照。問題があればF1〜F12キーへの切替を改めて検討）。

**概算工数**: 0.25人日。

---

### R3-2.【仕様変更・殿決定済み】Spaceパン廃止→PageUp/PageDownで1行ずつ離散スクロール

**内容**: Spaceキー押しっぱなしでの連続パンを廃止し、PageUp/PageDownキー1回につき1行分（`CellMm`相当）スクロールする離散方式に変更する。

**現状撤去対象**:
- `_spacePanActive`フィールド、`HandleSpaceDown()`（`MainPage.KeyboardMode.cs:140-147`）、`RegisterGlobalKeyAccelerators()`内のSpace登録（112行）。
- `MainPage.Pointer.cs:161-164`（`OnPointerPressed`内のSpaceポーリング判定）、`435-437`（`OnPointerMoved`内のSpaceポーリング判定）、`165-171`のスペース起因パン開始処理。
- 通常のマウスドラッグパン（別途、選択ツール等で母線外をドラッグする既存のパン方式）は本件と無関係のため維持。

**新設**: PageUp/PageDownを`RegisterGlobalKeyAccelerators`と同様の方式（`RootGrid`の`KeyboardAccelerator`、`Modifiers=None`）で登録。Space同様の「離した検知」が不要（1回の押下＝1回の離散スクロールのため、Invokedの単発発火だけで完結）。

**実装イメージ**:
```csharp
Add(VirtualKey.PageUp,   () => { ScrollByRows(-1); return true; });
Add(VirtualKey.PageDown, () => { ScrollByRows(1);  return true; });

private void ScrollByRows(int rows)
{
    if (_testMode) return;
    double scale = DipsPerMm * _viewport.Zoom;
    _viewport.PanY -= rows * _geo.CellMm * scale;   // 符号は実機確認で方向を確定
    Canvas.Invalidate();
}
```
**要判断事項**: パン方向の符号（PageDownで下方向＝より下の行が見えるようにするか）は既存のドラッグパンの符号規則（`MainPage.Pointer.cs:570-571`）に合わせて実装し、**実機確認（忍者）で直感的な方向になっているか確認**する。編集中（インライン編集・検索バー等）は無効化するかどうかも要確認（Spaceの`HandleSpaceDown`は編集中無効化していたため、同様の扱いを踏襲するのが妥当と判断）。

**影響範囲・変更ファイル**: `src/GuiEcad.App/MainPage.KeyboardMode.cs`（Space関連削除、PageUp/PageDown登録追加）、`src/GuiEcad.App/MainPage.Pointer.cs`（Spaceポーリング処理削除）。

**テスト方針**: 既存184テストの回帰確認＋実機確認（忍者）：PageUp/PageDownでの1行スクロールの方向・量が直感的か、編集中に無効化されるか。

**概算工数**: 0.25〜0.5人日（削除＋単純な単発アクセラレータ追加）。

---

### R3-3.【バグ】ショートカットキー設定「変更」→キー押下しても無反応

**現状**（`MainPage.KeyBindings.cs:202-207`）:
```csharp
changeBtn.Click += async (_, _) =>
{
    changeBtn.IsEnabled = false;                      // ← ここで対象ボタンを無効化
    display.Text = "キーを押してください（Escでキャンセル）";
    var captured = await CaptureKeyBindingInlineAsync(changeBtn);   // ← 無効化した同じボタンにFocus()を試みる
    ...
```
`CaptureKeyBindingInlineAsync`（`MainPage.KeyBindings.cs:266-291`）内で`target.Focus(FocusState.Keyboard)`（287行）を呼ぶが、**`target`（＝`changeBtn`）は直前で`IsEnabled = false`にされている**。無効化されたコントロールはキーボードフォーカスを取得できない（WinUI/UWP共通の標準挙動）ため、`Focus()`は静かに失敗し、`KeyDown`ハンドラ（`OnCapturedKeyDown`）が一切発火しない。ラウンド2で解消したのは「ダイアログの入れ子」問題のみで、**今回の原因はそれとは独立した別のバグ**（`IsEnabled=false`とフォーカス取得の矛盾）。

**方針**: `changeBtn.IsEnabled = false`を行わない。多重クリック対策が必要なら、`IsEnabled`を操作するのではなく、ページ単位の簡易フラグ（例: `_capturingKeyBinding`）で多重起動をガードする（フォーカス可能な状態を維持したまま再入防止する）。

**実装イメージ**:
```csharp
private bool _capturingKeyBinding;   // ダイアログスコープでよいためローカル変数化も可

changeBtn.Click += async (_, _) =>
{
    if (_capturingKeyBinding) return;
    _capturingKeyBinding = true;
    display.Text = "キーを押してください（Escでキャンセル）";
    var captured = await CaptureKeyBindingInlineAsync(changeBtn);
    _capturingKeyBinding = false;
    ...
```

**影響範囲・変更ファイル**: `src/GuiEcad.App/MainPage.KeyBindings.cs`（`OnMenuKeyBindingSettings`内、`changeBtn.Click`ハンドラの`IsEnabled`操作をフラグ管理に置き換え）。

**テスト方針**: 既存184テストの回帰確認＋実機確認（忍者）：「変更」→表示が「キーを押してください」に変わる→キー押下で即座に確定・表示更新、Escでキャンセル、を確認。念のため「変更」ボタンを連打した場合に多重捕捉が起きないことも確認。

**概算工数**: 0.1〜0.25人日（原因が明確な小修正）。

---

## ラウンド4（2026-07-01・新規不具合：機器名入力後の方向キーでフォーカスが下部パネルに奪われる）

> 家老より、キーボード配置モードで機器設置→機器名入力→方向キー移動時にキー入力が下部パネル（DRC/検索結果/接続検査のTabView）に奪われる不具合の調査依頼。コード読解のみで原因を特定（実機再現・フォーカス移動先の裏付けは忍者が並行確認中）。

**現状**（`MainPage.Pointer.cs:846-863` `CommitDeviceName`）: `DeviceNameBox.Visibility = Visibility.Collapsed;`（851行）でエディタを閉じるのみで、**フォーカスをどこにも明示的に戻していない**。

**原因（推定・最有力仮説）**: `Canvas`（`MainPage.xaml:530-539`）は現状も`IsTabStop`が未設定のまま（ラウンド1調査で既知の制約、方針B移行後も未対応）で、キーボードフォーカスを取得できない。`DeviceNameBox`が`Collapsed`になりフォーカスを喪失すると、WinUIの既定挙動として、フォーカスはビジュアルツリー内の他のフォーカス可能要素へ自動的に再割り当てされる。`Canvas`がその受け皿になれないため、下部パネルの`OutputTabView`（`MainPage.xaml:666`、DRC/検索結果/接続検査）内の`TabViewItem`がフォーカスを引き継いでいると推定される。`TabViewItem`は標準コントロールとしてフォーカスを持つと左右矢印キーでタブ切替する組み込みキー処理を持ち、ラウンド1で確認済みの一次資料「フォーカス中コントロールの組み込みアクセラレータが優先」の原則により、以降の方向キーが`RootGrid`の`KeyboardAccelerator`（`RegisterKeyboardModeAccelerators`）より先にTabView側の組み込み処理に消費されると考えられる。これが「キー入力が下部パネルに奪われる」の実体。

**横展開（同型バグの可能性）**: 同じ「`Visibility=Collapsed`後にFocus復帰処理なし」パターンが`CommitFrameLabel`（803-816行）・`CommitComment`（894行台）・`CommitRungComment`（941行台）にも存在する。機器名編集固有ではなく、インライン編集全般に共通する構造。

**方針（案）**: Commit系メソッドで`Visibility=Collapsed`直後、フォーカスを安全な場所へ明示的に戻す。
- 案A: `Canvas`に`IsTabStop="True"`を追加した上で`Canvas.Focus(FocusState.Programmatic)`を呼ぶ（ラウンド1で指摘済みのWin2D Issue #686対応をここで併せて行う）。
- 案B: Canvasフォーカス依存を再導入しない方針Bの発想を踏襲し、`RootGrid`配下の何らかのフォーカス可能な非表示要素（またはPage自身）にFocusを戻す。
矢印キー処理自体は既に`RootGrid`の`KeyboardAccelerator`に集約されフォーカス非依存のはずだが、`TabView`のような「フォーカス時に矢印キーを消費する組み込み動作」を持つコントロールにフォーカスが渡ってしまうと機能しなくなるため、**Commit系メソッドがフォーカスを迷子にしない**ことが根本対応として必須と考える。実装方式（案A/B）の選定は侍・家老判断。

**影響範囲・変更ファイル（推定）**: `src/GuiEcad.App/MainPage.Pointer.cs`（`CommitDeviceName`/`CommitFrameLabel`/`CommitComment`/`CommitRungComment`）、案A採用時は`src/GuiEcad.App/MainPage.xaml`（Canvas`IsTabStop`追加）。

**テスト方針**: 実機確認（忍者）必須：キーボード配置モードで機器名入力確定→方向キー移動が下部パネルに奪われず正しくセル移動すること。横展開4箇所（機器名・枠ラベル・コメント・行コメント）すべてで同様の確認。

**概算工数**: 0.25〜0.5人日（Commit系4箇所への同一パターン適用）。

---

## ラウンド5（2026-07-01・cd60080後も再現した枠ラベル早期確定バグの再調査）

> 侍がcd60080（155行目に`_editingFrame is null`追加）を適用後も、忍者のUIA実機確認で再現。忍者の詳細切り分けにより「PointerPressed直後はFrameLabelBoxに正しくフォーカスがあるが、直後のPointerReleased発生時には既にFocusSinkButtonへ移りCommitFrameLabelが実行済み」と判明。発火源はPointerPressedではなくPointerReleased側にある可能性が高いとの依頼で再調査。

**確認したこと（事実）**:
- `OnPointerReleased`（`MainPage.Pointer.cs:557-750`）全体を通読したが、`FrameLabelBox`から明示的にフォーカスを奪うコード（`Focus()`呼び出し等）は存在しない。
- 忍者が疑った`_movingFrame`関連処理（672-676行目）は、今回のシナリオ（ドラッグを伴わない単純な2クリック）では無関係と判断する。理由: 1回目のクリック（`isDouble=false`）でHitTestFrameにヒットし`_movingFrame=true`がセットされるが、1回目のクリックに対応するPointerReleasedで672-676行目が評価され`_movingFrame=false`にリセットされる。2回目のクリック（`isDouble=true`）はOnPointerPressed内のisDoubleブロック（179行目）で`ShowFrameLabelEditor`を呼んだ後`return`しており、`_movingFrame`はセットされないため、2回目のPointerReleased時点では`_movingFrame`は既にfalseで672-676行目は素通りする。
- 唯一気になる箇所は`OnPointerReleased`末尾（749行目）の**無条件**`Canvas.ReleasePointerCapture(e.Pointer);`。他の早期returnパス（`_rangeSelecting`・`_frameStartMm`・`_connStartRow`等）は個別にReleasePointerCaptureを呼んでいるが、末尾のこの1行だけは、Commit系メソッドで使っている`_editingXxx`系ガードが一切適用されない。他の3パターン（機器名/コメント/行コメント編集中）でも理論上同じ経路を通りうるが、現時点で不具合報告があるのは枠ラベルのみ（枠は`_movingFrame`という他パターンにはない専用のドラッグ状態を経由する点が唯一の違い）。

**判定できなかったこと（不明・断定不可）**:
- `Canvas.ReleasePointerCapture`の呼び出し自体が`FrameLabelBox`のフォーカス喪失を引き起こすという直接的な一次資料の裏付けは得られていない。WinUIの「ポインタキャプチャ解放時に暗黙のフォーカス再計算が走る」ような挙動があるかどうかは未確認。
- 代替仮説として、2回目のクリックのPointerPressed処理中に`FrameLabelBox.Visibility`が`Collapsed→Visible`に変化することで、対応するPointerReleased（mouseup）発生時点のヒットテスト結果がCanvasからFrameLabelBoxへ変わり、その过程で何らかのWinUI内部処理（フォーカス再計算等）が働いている可能性があるが、これも一次資料での裏付けはなく推測の域を出ない。
- したがって、忍者が言う「PointerReleased側が発火源」という切り分けは事実として尊重しつつ、**コード読解のみでは一次原因を確定できなかった**。

**推奨案（優先度順）**:
1. **対症・低リスク**: `OnPointerReleased`末尾の`Canvas.ReleasePointerCapture(e.Pointer);`に、他のCommit系ガードと同型の条件（`_editingFrame is null && _editingElement is null && _editingComment is null && _editingRungComment is null`）を追加し、インライン編集中はこの呼び出し自体をスキップする。他3パターンとの対称性の観点でも妥当だが、効果があるかは未検証。
2. **根本確認・要実機**: `OnFrameLabelBoxLostFocus`に一時的なデバッグログ（呼び出しタイミング・`e.OriginalSource`・スタック情報等）を仕込み、実機で「PointerReleasedのどの処理段階で・何がLostFocusを引き起こしているか」を忍者に再確認してもらう。cd60080で削除した検証ログと同種のアプローチを、今度はLostFocus側に仕込む形。**これが最も確実な次の一手**と考える。
3. **構造的対応（大きめ）**: Commit系メソッドに短時間のデバウンス（`ShowXxxEditor`呼び出し直後の一定ミリ秒以内のLostFocusは無視する等）を導入し、同一ポインタジェスチャに起因する意図しない早期確定を構造的に防ぐ。副作用（正当なLostFocusも遅延する等）の検証が必要なため、まずは1・2を優先すべき。

**影響範囲・変更ファイル（案1採用時）**: `src/GuiEcad.App/MainPage.Pointer.cs`（`OnPointerReleased`末尾のガード追加のみ）。

**要判断事項**: 案1（対症）を先に試し、実機で直らなければ案2（デバッグログ）で一次原因を確定してから再修正する段階的対応でよいか。原因未確定のため、いきなり案3（構造変更）に進むのはリスクが高いと考える。

---

## ラウンド6（2026-07-01・案2ログにより真犯人判明：RefreshPropertiesPanelのChildren.Clear）

> 侍が仕込んだ一時ログにより、`newFocus`が`ScrollViewer`（`MainPage.xaml:779`、x:Name無し、プロパティタブ内、`PropertiesPanel`を包む要素）と判明。かつこのログはCommitFrameLabel呼び出し**前**（FocusSinkButtonへの退避処理より前）に記録されており、ラウンド4/5の修正・調査対象とは別の経路と確定。

**原因（確定度：高）**: `RefreshPropertiesPanel`（`MainPage.Properties.cs:124-146`）の`PropertiesPanel.Children.Clear()`（127行目）。`MainPage.xaml:779`で`PropertiesPanel`（`StackPanel`）は`ScrollViewer`（既定`IsTabStop=True`）に包まれている。

流れ:
1. 枠をダブルクリックする1回目のクリック（`isDouble=false`）で`HitTestFrame`にヒットし`SelectFrame(hitFrame)`（`MainPage.Pointer.cs:346`）が呼ばれる。`SelectFrame`は`_selected = null`にする（30-37行目、選択種別の相互排他ヘルパー）。
2. 同じ1回目のクリック処理の末尾、`MainPage.Pointer.cs:413`で`RefreshPropertiesPanel()`が呼ばれる。`_selected is null`のため136-146行目の分岐に入り、`PropertiesPanel.Children.Clear()`実行後「要素を選択してください」のTextBlockのみを追加する。
3. **もし直前に選択されていた要素（SelectSwitchのノッチ位置、Timerの設定時間等でNumberBoxを表示する種別）のプロパティ内NumberBoxがフォーカスを保持していた場合**、Clear()でそのNumberBoxがVisual Treeから消え、WinUIの既定フォーカス委譲ロジックにより、Visual Treeを親方向に遡って最初に見つかるフォーカス可能要素＝`ScrollViewer`（既定IsTabStop=True）へフォーカスが移る。
4. このフォーカス委譲はUIスレッドの次のレイアウト/コンポジションタイミングでわずかに遅延して発生すると推定される。そのため、直後（2回目のクリック、`isDouble=true`）で`ShowFrameLabelEditor`→`FrameLabelBox.Focus()`が実行された直後は正しくフォーカスされている（忍者のログ「PointerPressed直後は正常」と一致）が、その後の非同期タイミングで1回目のクリックが仕込んだフォーカス委譲が遅れて発動し、FrameLabelBoxのフォーカスを上書き→LostFocus発火→CommitFrameLabel実行、という順序になっていると推定される。

**枠のみで顕在化する理由**: 機器名/コメント/行コメントのダブルクリック編集は、1回目のクリックが編集対象の要素自体をヒットするため`_selected`はその要素のままか別要素に変わるだけで、`RefreshPropertiesPanel`は「別の内容」に置き換わるだけで済むことが多い。一方`SelectFrame`は`_selected`を必ず`null`にするため、「要素を選択してください」という完全に空の状態への遷移が強制され、直前にNumberBox等がフォーカスを持っていた場合にフォーカス委譲が起きやすい。

**断定できない点**: 「直前にプロパティパネルのNumberBox等が実際にフォーカスを持っていたか」は実機シナリオ（直前の操作）に依存しコード読解のみでは確認しきれない。またフォーカス委譲が同期か非同期かはWinUI内部実装の一次資料での裏付けは無い（観測結果からの推定）。ただし`Children.Clear()`→`ScrollViewer`という経路自体は`MainPage.xaml:779`の構造とWinUIの既定挙動（ScrollViewerの既定IsTabStop=True）から高い確度で説明がつく。

**推奨案**: `RefreshPropertiesPanel()`冒頭、`PropertiesPanel.Children.Clear()`を呼ぶ**前**に、既存のCommit系4箇所と同じパターンで`FocusSinkButton.Focus(FocusState.Programmatic)`を無条件に呼んでおく。これによりClear()実行時点でPropertiesPanel配下にフォーカスがある状態自体を作らず、既定フォーカス委譲（ScrollViewerへの意図しない移動）を未然に防げる。

**実装イメージ**:
```csharp
private void RefreshPropertiesPanel()
{
    // Children.Clear() 実行前にフォーカスを退避しないと、Clear対象の子要素(NumberBox等)が
    // フォーカスを持っていた場合、既定のフォーカス委譲で親のScrollViewer(IsTabStop既定True)へ
    // フォーカスが移り、他のインライン編集(枠ラベル等)からフォーカスを奪ってしまう。
    FocusSinkButton.Focus(FocusState.Programmatic);
    _refreshingProps = true;
    PropertiesPanel.Children.Clear();
    ...
```
既存のCommit系4箇所と発想は同一（Visual Treeから要素が消える操作の前にフォーカスを安全な場所へ逃がす）。

**影響範囲・変更ファイル**: `src/GuiEcad.App/MainPage.Properties.cs`（`RefreshPropertiesPanel`冒頭に1行追加）。

**テスト方針**: 実機確認（忍者）：SelectSwitch/Timer等NumberBoxを持つ要素を選択→プロパティパネルのNumberBoxをクリックしてフォーカスさせる→枠をダブルクリックして編集開始→文字入力できることを確認。既存の機器名/コメント/行コメント編集や通常のプロパティ編集（NumberBox操作等）に回帰が無いことも確認。

**概算工数**: 0.1人日（1行追加のみ）。

---

## ラウンド7（2026-07-01・7b3c16e適用後も再現。真因は未確定に後退）

> 侍が7b3c16e（`RefreshPropertiesPanel`冒頭に`FocusSinkButton.Focus()`追加）を適用後も、忍者確認で再現。ログも前回と同一内容（`newFocus=ScrollViewer`）。忍者より4項目の再検証依頼。

**検証結果（事実）**:
1. **`RefreshPropertiesPanel`の全呼び出し箇所**: `MainPage.Sheets.cs:82,110,133` / `MainPage.Image.cs:50` / `MainPage.Pointer.cs:133,413` / `MainPage.Properties.cs:499,503` / `MainPage.Menu.cs:31,32`（Undo/Redo）。このうち枠ダブルクリックのフローで実行されるのは`MainPage.Pointer.cs:413`（1回目のクリック、`SelectFrame`直後）**のみ**。2回目のクリック（`isDouble`ブロック、179行目）は`ShowFrameLabelEditor`呼び出し後に即`return`しており、310行目以降の`SelectFrame`/`RefreshPropertiesPanel`経路には到達しない。忍者の指摘通り、**2回目クリックからRefreshPropertiesPanelには到達しないことを再確認・確定**。
2. **`Children.Clear()`と同種の処理**: `RefreshDevicePanel`（`MainPage.Properties.cs:81-112`）が`DeviceListView.Items.Clear()`（102行目）を持つが、これは「機器表」タブ（非アクティブ想定）の中身操作であり、`_devicePanelVisible`が真の場合のみ実行される。枠選択のフローで`RefreshDevicePanel`が呼ばれる箇所は見当たらない（`SelectFrame`から辿れる経路にはない）。他に同種のClear操作は確認範囲内では見当たらず。
3. **`FocusSinkButton.Focus()`のタイミング**: 7b3c16eのdiff通り、`RefreshPropertiesPanel`冒頭でコード上`FocusSinkButton.Focus(FocusState.Programmatic)`→`PropertiesPanel.Children.Clear()`の順に記述されており、C#の同期実行順としては前者が完了してから後者が実行されるはず。ただし**戻り値(bool)は確認しておらず、実際に成功したか自体は未検証**。
4. **isDouble判定の到達可否**: 上記1と同一。179行目の`return`により、2回目クリックが310行目以降に到達しないことは確定。

**評価**: 上記1・4が確定した以上、**ラウンド6で結論づけた「RefreshPropertiesPanelのChildren.Clearが真因」という前回の推定は、根拠が崩れた**。もし本当にRefreshPropertiesPanel（1回目クリックのみで実行）が原因だったなら、2回目クリックのFrameLabelBox.Focus()の方が時間的に後であり、同期処理である限りは「後勝ち」でFrameLabelBoxがフォーカスを維持できるはずである。それでも直らないという事実は、**「1回目クリックのRefreshPropertiesPanelによる委譲が非同期的に遅延して2回目クリック後に発動する」というラウンド6の遅延仮説自体が誤りだった**可能性が高いことを示す。7b3c16eの修正（理論的には正しい対症療法）が効かなかったことも、この評価と整合する。

**現時点での判断**: **一次原因は未確定に後退した**。RefreshPropertiesPanel以外の経路、あるいはWinUIの既定フォーカス処理そのもの（PointerPressedイベントに対する何らかの既定動作が、アプリのFocus()呼び出しより後に働く競合レース、ラウンド1で発見したパターンと同型の可能性）を疑うべきだが、コード読解のみでは追加の手がかりが得られなかった。

**推奨する次の一手（優先度順）**:
1. **最優先・確度が高い**: 忍者に依頼し、以下3箇所のFocus呼び出しの**戻り値（bool）**を実機ログで確認する。
   - `MainPage.Pointer.cs:155`（`Canvas.Focus(FocusState.Programmatic)`）→ もしtrueを返しているなら、Canvasが想定に反してフォーカス可能になっている疑いがあり、重大な手がかりになる。
   - `MainPage.Pointer.cs:799`（`ShowFrameLabelEditor`内、`FrameLabelBox.Focus(FocusState.Programmatic)`）→ 本当に成功しているか。
   - `MainPage.Properties.cs`（`RefreshPropertiesPanel`冒頭、7b3c16eで追加した`FocusSinkButton.Focus()`）→ 本当に成功しているか。
2. `OnFrameLabelBoxLostFocus`側で、`FocusManager.GetFocusedElement`だけでなく、LostFocus発火の**タイミング**（1回目クリックの直後か、2回目クリックの直後か、PointerReleased中か）を区別できるログ（例えばカウンタやタイムスタンプ）を追加し、「どのクリックが引き金か」をより精密に切り分ける。
3. 上記1・2の結果次第で、疑うべき対象をWinUIの既定フォーカス処理（ラウンド1と同型の同期競合レース）側に移し、`ShowFrameLabelEditor`の`FrameLabelBox.Focus()`呼び出しを`DispatcherQueue.TryEnqueue`で1テンポ遅らせて実行する等の対応を検討する余地がある（ラウンド1で`FocusCanvasForKeyboardMode`に適用したのと同型の対処）。ただし原因未確定の段階での実装は時期尚早。

**要判断事項**: 原因が2ラウンド続けて確定できていないため、次は先にデバッグログ（上記1・2）で一次原因を確実に特定してから修正する、という順序を徹底したい。対症療法の繰り返しはリスクが高いと考える。

---

## ラウンド8（2026-07-01・時系列ログで核心に接近、WinUI3既知バグに到達）

> 忍者の時系列ログで、`FrameLabelBox.Focus()`成功(True)から72ms後に`OnFrameLabelBoxLostFocus`発火、かつその間に仕込んだ3箇所(Canvas.Focus/FrameLabelBox.Focus/FocusSinkButton.Focus)はどれも呼ばれていないと判明。72msの間には2回目のマウスアップ(PointerReleased)が挟まる。家老よりOnPointerReleased全処理の洗い出しと、フレームワーク側の一次資料確認を依頼された。

**確認1（コード側）**: `OnPointerReleased`（`MainPage.Pointer.cs:561-755`）を通読。753-754行目の`Canvas.ReleasePointerCapture`は既に21716b8で`_editingFrame is null`等のガードが追加済みで、編集中はスキップされる。それ以外の処理（`_movingFrame`/`_movingLine`/`_movingDot`/`_movingImage`/`_multiMoveOrigins`等の確定処理）は、今回のシナリオ（ドラッグなしの2クリックのみ）ではいずれも該当条件が偽で素通りする。**明示的にフォーカスへ影響するコードはコード内に存在しないことを再確認**。

**確認2（一次資料）**: WebSearchで一致度の高いMicrosoft公式Issueを発見。**[microsoft/microsoft-ui-xaml Issue #6179「Focus moves unexpected after PointerReleased event」](https://github.com/microsoft/microsoft-ui-xaml/issues/6179)**（not plannedでクローズ済み＝Microsoft側は修正しない方針）。
- 内容: 要素をクリックすると、その要素がPointerPressedを処理していても、**PointerReleased発生後にフォーカスがタブオーダー内の別要素（報告例ではTabIndex=0の要素）へ移ってしまう**、という既知の挙動。Tabキーでの移動は正常。
- Microsoftからの技術的な根本原因の説明は無く、「仕様として直さない」という扱い。回避策も本Issue内には明記なし。

**本アプリへの当てはめ（推定）**: この既知バグが今回の症状と一致する。2回目のクリックのPointerPressed処理内で`FrameLabelBox.Focus()`が同期的に成功する（忍者ログ通り）が、**そのクリックに対応するPointerReleasedが発生した際、WinUI3のこの既知バグにより、フレームワークが暗黙的に「タブオーダー上の別の有効なフォーカス候補」へフォーカスを移してしまう**。これはアプリコードのどのFocus()呼び出しも経由しない、フレームワーク内部の処理であるため、72ms間3箇所とも呼ばれていないという観測と完全に整合する。

**なぜ`FocusSinkButton`ではなく`ScrollViewer`が選ばれるのか（推定・未検証）**: `FocusSinkButton`（`MainPage.xaml:111-112`）は`IsHitTestVisible="False"`が設定されている。WinUIの既定フォーカス委譲がフォーカス候補を探索する際、**ヒットテスト不可能な要素は候補から除外される**可能性が高く、その場合次に見つかる有効な候補として「現在アクティブなプロパティタブの`ScrollViewer`」（`MainPage.xaml:779`、既定`IsTabStop=True`、`IsHitTestVisible`は既定でtrue）が選ばれていると推定される。**ただしこの「IsHitTestVisible=Falseだと除外される」という点はIssue本文に明記された一次資料ではなく、状況証拠からの推定**である。

**これまで3ラウンド原因を特定できなかった理由**: 真因がアプリコード内ではなくWinUI3フレームワーク自体の既知の挙動だったため、コード読解・アプリ内Focus呼び出し箇所の追跡だけでは原因に辿り着けなかった。

**推奨案（優先度順、いずれも未検証で実機確認必須）**:
1. **低リスク・最初に試す価値あり**: `FocusSinkButton`の`IsHitTestVisible`を`True`に変更する（`Width="0" Height="0" Opacity="0"`は維持、サイズ0のためクリックの妨げにはならないと推定）。既定フォーカス委譲の候補にFocusSinkButtonが含まれるようになれば、少なくとも「ScrollViewer(意図しないタブ)」ではなく「FocusSinkButton(無害な退避先)」へ委譲されるようになる可能性がある。
2. 上記1で解決しない場合、`OnFrameLabelBoxLostFocus`（および他のCommit系3箇所）で、`ShowXxxEditor`呼び出し直後の極短時間内（例:100-200ms）に発生したLostFocusは「WinUI既知バグ由来の偽の委譲」とみなし、`CommitXxx`を呼ばずに`Focus()`を奪い返すデバウンス処理を検討する。Issue自体がMicrosoft側で修正されない前提のため、アプリ側での「奪い返し」対応が現実的な落とし所になる可能性が高い。
3. `Canvas`/`FrameLabelBox`側の`PointerReleased`で`e.Handled = true`を設定することでフレームワークの既定処理が抑制されるか試す余地はあるが、他機能への副作用リスクが読めないため優先度は低い。

**要判断事項**: 案1は変更が1属性のみで低リスクなため、まず実機確認してよいか。効果が無ければ案2（デバウンス）に進む、という段階的対応を提案する。

---

**【2026-07-01 殿判断・確定】本件（枠ラベル早期確定バグ、WinUI3既知バグ Issue #6179 由来）は今回保留とし、既知の制限事項として記録するに留める。** 案1（`FocusSinkButton.IsHitTestVisible`変更）以降の追加対応（案2のデバウンス処理等）は実施しない。実用上の影響（枠をダブルクリック編集した直後に別操作を挟んだ場合にまれに空ラベルのまま確定される）は限定的と判断し、修正コストと発生頻度のバランスから見送りとした。既知の制限事項として`docs/todo.md`にも記録する。
