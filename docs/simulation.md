# テストモード評価アルゴリズム

有接点リレーシーケンスを **connectivity ＋ 不動点反復**で評価する（PLCスキャンではない＝評価順序非依存・電気的同時動作に忠実）。

## 電気モデル
- 母線の一方が **+側**、他方が **−側/コモン**（`BusConfig` の極性で保持）。
- **負荷（コイル・ランプ）は片端が+側・他端が−側へ閉路でつながると励磁**。
- **接点＝導通/非導通の切替**（a接点=機器ONで導通、b接点=機器OFFで導通）。**給電フラッドでは接点・配線は通すが負荷は通さない**（負荷は電位を落とすため）。

## アルゴリズム（2階層）
```
外側ループ（時間ステップ／タイマ対応・任意）: 各 tick でタイマ経過を進める
  内側ループ（不動点・同一時刻の安定化） repeat:
    1. 機器状態 → 各接点の導通/非導通を決定
    2. +側母線から閉接点・配線のみ辿るフラッドで powered(+) 集合
    3. −側母線から同様に return(−) 集合
    4. 各負荷: 片端∈powered かつ 他端∈return なら励磁ON
    5. 機器状態（リレー励磁・ランプ）を更新
    until 変化なし（収束） | 上限回数超過（発振検出）
```

```csharp
namespace GuiEcad.Simulation;

public sealed class Evaluator {
    private readonly Netlist _net;            // 幾何から構築・キャッシュ
    public const int MaxIterations = 100;

    public EvalResult Evaluate(SimState s) {
        for (int it = 0; it < MaxIterations; it++) {
            var conduct = ContactConduction(s);                 // 1
            var powered = Flood(_net.PlusRail, conduct);        // 2 （負荷は通さない）
            var ret     = Flood(_net.MinusRail, conduct);       // 3
            var next    = s.Clone();
            foreach (var load in _net.Loads)                    // 4
                next.Energized[load.DeviceName] =
                    powered.Contains(load.NetA) && ret.Contains(load.NetB)
                 || powered.Contains(load.NetB) && ret.Contains(load.NetA);
            if (next.Energized.SequenceEqualTo(s.Energized))    // 5: 収束
                return EvalResult.Converged(next, powered);
            s = next;
        }
        return EvalResult.Oscillating(s);                       // 発振
    }
}
```

## ポイント
- **評価順序に依存しない**（実リレーの電気的同時動作に忠実）。自己保持は通常2〜3反復で収束。
- **発振検出**: 反復上限（例 `MaxIterations=100`）or 状態サイクル検出で発振と判定し通知。
- **ネットリストはキャッシュ**（トポロジ変更時のみ再構築）。各反復は O(ネット数+要素数)。線番号採番の Union-Find と共用。
- `powered` 集合は**通電配線のUIハイライト**にそのまま使える。

## NetlistBuilder 接続ルール（2026-06-20 確定）
- **最左要素の左ポート → 左母線**: 各行の最左要素は左母線との間に横配線があるとみなし、常に左母線へ結合。
- **末尾負荷・末尾 Passthrough → 右母線自動接続**: 行末の要素が `IsLoad`（コイル等）または `IsPassthrough`（端子台等）で右母線手前に配置されていても、描画上は右母線まで横線が続くため電気的にも右母線へ結合する。
  - `IsPassthrough` 追加（2026-06-20）: 修正前は `IsLoad` のみが対象で、行末が端子台の回路（例: `L—[NO]—TB—(Coil)—TB—R`）でコイルが通電されないバグがあった。テスト: `TrailingTerminal_CoilBeforeIt_Energizes`。

## 入力の扱い
- 押ボタン=モーメンタリ（押下中ON）、セレクトSW=維持（位置トグル）、アクチュエータ端信号（開端/閉端補助）=**初期は手動入力**、将来アクチュエータ動作モデルへ拡張。
- **タイマ／アクチュエータ動作は初期スコープでは簡易**（手動）とし、本格対応は後続。
