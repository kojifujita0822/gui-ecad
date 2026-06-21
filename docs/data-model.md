# データモデル設計

**永続化するドキュメントモデル**と、**シミュレーション用に幾何から導出するネットリスト**を分離する。

## 全体構成
```
LadderDocument（プロジェクト全体）
├─ DocumentInfo   … タイトルブロック（図番・客先・設計/製図/検図・日付）
├─ Settings       … 既定母線名・既定グリッド等
├─ DeviceTable    … 機器（CR11, PB1…）を名前で一元管理 ← 状態・相互参照のキー
├─ PartsList      … 部品リスト（名称・メーカー・型式・容量・数量）
└─ Sheets[]       … 各ページ
     ├─ GridSpec     … 行数・列数（セルmmは DrawingTheme 側）
     ├─ BusConfig    … 左右母線名（固定でなく設定可）+ 電源ラベル
     ├─ Elements[]   … 配置要素（接点/コイル/PB/SS/ランプ/端子台…）
     ├─ Connectors[] … 縦渡り（分岐）= 黒ドットで明示
     ├─ Frames[]     … 設置場所枠（点線）
     └─ Lines[]      … 横の回路線（回路番号を保持。図面全体で連番）
```

## 要点
1. **機器（Device）を状態・相互参照の中心に置く**: `CR11` のコイルと各所の `CR11` 接点は同じ `DeviceName` を参照。これで (a) クロスリファレンス自動生成、(b) テストモードの状態を Device 名キーで一元保持、が成立。
2. **記号（見た目）と種別（データ）の分離**: `ElementInstance` は `Kind`＋`DeviceName`＋`Params` を持つ。記号グリフは描画側カタログが `Kind` で引く（端子台の記号差し替えに対応）。占有セル数 `CellWidth` は種別カタログ既定（三相モータ=3、接点/コイル/セレクトSW=1）。セレクトSWは**ノッチ毎の2端子接点**として表現し、選択位置は `Params["Position"]`（同一 `DeviceName` を共有し、現在位置が一致する接点のみ導通）。
3. **配線モデル＝接続点（Port）一致（確定）**: 部品の接続は**幾何の推測ではなくカタログ定義のポート（接続点）と、グリッドノード座標の一致**で決定する。横隣接の暗黙接続も「両要素のポートが同一境界ノードを共有する」結果として自然に成立する（旧 v1 の「横隣接＝接続」「`NodeAtColumn` で分岐先を近接走査推測」は廃止＝データ上の判断が不安定だったため）。詳細は下記「接続点（Port）モデル」。
4. **線番号＝ネット（電気的連続）単位で自動採番**:
   - **同一ノードに集まるポート＝同一ネット（同一番号）**（空セル・分岐ドット・縦コネクタを跨いでも連続なら同番号）。
   - **要素（接点・コイル等）はその左右ポート間でネットを分断**＝先は別番号。
   - 線番号 = ネットID。**connectivity（Union-Find）が「線番号採番」と「シミュレーションのノード生成」を兼ねる**。
   - **母線ネットは番号でなく母線名**（`R200`/`S200` 等）。連番 1,2,3… は母線非接続の内部ネットにのみ、**読み順**で付与（採番ルーチンは [drawing-spec.md](drawing-spec.md)「線番採番ルーチン」が正典）。
   - 番号追加・削除時は読み順で自動再採番。
5. **回路番号（線番号とは別）**: 横の回路線ごとに付与する**図面全体通しの連番**（`CircuitLine`）。1出力が中間に2〜3本の並列線を持つことがあるため「横の回路線」単位で振る。クロスリファレンスは機器名＋回路番号で構成。**行番号（ステップ番号）は不採用**（`GridPos.Row` は内部座標のみ）。番号追加時は自動順送り。
6. **ネットリストは幾何から導出（永続化しない別物）**: ドキュメント（幾何）→ Union-Find でセル端点を統合 → 接点/コイルをノード間エッジ化 → 不動点評価へ（[simulation.md](simulation.md)）。

## 接続点（Port）モデル（確定）
部品ごとの**接続点（ポート／端子）をカタログで定義**し、**同一グリッドノードに載るポート同士を電気的に同一**とみなす。これにより多端子部品・分岐・母線接続を**幾何推測なしで安定**に判定する。

- **グリッドノード座標**: 配線はセル境界の**ノード**でつながる。ノード座標 = `(Row, Boundary)`。`Boundary` は列境界インデックス（境界 `b` はセル `b-1` と `b` の間。左母線＝境界 `0`、右母線＝最終境界＝`Columns`）。複数行にまたがる端子のため `Row` も座標に含む。
- **ポート定義（カタログ・種別固定）**: 各 `ElementKind` が `Ports[]` を宣言する。ポート = `(Name, RowOffset, BoundaryOffset)`。要素インスタンス（`Pos.Row=r`, `Pos.Column=c`, 幅 `w`）に対し、ポートの実ノードは `(r + RowOffset, c + BoundaryOffset)`。
  - `ContactNO/NC`・`Coil`・`Lamp`（幅1, 2端子）: `L(row+0, 境界 c+0)` / `R(row+0, 境界 c+1)`。
  - `SelectSwitch`（幅1, 2端子・ノッチ毎の接点）: `L(c+0)` / `R(c+1)`。位置は `Params["Position"]`。
  - `Terminal`（端子台・passthrough・2端子）: `L(c+0)` / `R(c+1)`、常時導通。
  - **多端子の例**（実装済 / 予定）:
    - 三相モータ（実装済・非シミュレート）: 同一行に3ポート `U(c+0)` `V(c+1)` `W(c+2)`（幅3）。制御回路と別系統の電源回路ページに配置（`sample/sample_ex2.pdf`）。
    - タイマ接点 a/b・非常停止押釦・サーマル(OL)（実装済・2端子）。
- **接続判定**: 同一ノード座標 `(Row, Boundary)` に載る**全ポートを Union**（同一ネット）。
  - **横隣接**: 左要素の `R` ポートと右要素の `L` ポートが同一境界ノードを共有 → 接続（暗黙接続はこの帰結）。
  - **分岐（黒ドット●）**: あるノードに集まるポート＋縦コネクタ端点を**全結合**する明示ノード。
  - **縦コネクタ**: 端点を `(Row, Boundary)` ノードとして持ち、上下端のノードを Union（旧 `NodeAtColumn` の近接推測は不要）。
  - **母線接続**: 境界 `0` のポート → 左母線ネット、最終境界のポート → 右母線ネット。
- **編集時の制約**: ポートは種別固定（ユーザーが自由配置はしない）。配置・移動時にポートのノード座標を算出し、衝突・未接続（宙ぶらりんポート）を検出できる。

> 実装影響: 現行 `NetlistBuilder`（横隣接＋`NodeAtColumn` 推測）はこの Port モデルへ作り替える（[todo.md](todo.md)）。

## モデル スケッチ（C# / 抜粋）
```csharp
namespace GuiEcad.Model;

public sealed class LadderDocument {
    public DocumentInfo Info; public DocumentSettings Settings;
    public DeviceTable Devices = new(); public PartsList Parts = new();
    public List<Sheet> Sheets = new();
}
public sealed class Sheet {
    public Guid Id; public int PageNumber; public string Name;   // 項名称
    public GridSpec Grid; public BusConfig Bus;
    public List<ElementInstance> Elements = new();
    public List<VerticalConnector> Connectors = new();
    public List<GroupFrame> Frames = new();
    public List<CircuitLine> Lines = new();          // 横の回路線（回路番号）
}
public sealed class GridSpec { public int Rows; public int Columns; }   // Row は内部座標のみ（ステップ番号は表示しない）
// 回路番号: 図面全体通しの連番。横1本の回路線ごと（1出力=2,3本の並列線になる場合あり）。自動順送り。
public sealed class CircuitLine { public int Row; public int CircuitNumber; }
public sealed class BusConfig { public string LeftName; public string RightName; public string? PowerLabel; }

public enum ElementKind { ContactNO, ContactNC, Coil, CoilNC, Lamp,
                          PushButtonNO, PushButtonNC, SelectSwitch, Terminal, Timer, Counter }

// 接続点（種別固定・カタログ定義）。実ノード = (Pos.Row + RowOffset, Pos.Column + BoundaryOffset)
public readonly record struct PortDef(string Name, int RowOffset, int BoundaryOffset);
// ElementCatalog.Ports(kind) が種別ごとの PortDef[] を返す（例 ContactNO => [("L",0,0),("R",0,1)]）
// 接続判定: 同一 (Row, Boundary) ノードに載るポートを Union（縦コネクタ端点・分岐●も同ノードで結合）
public sealed class ElementInstance {
    public Guid Id; public ElementKind Kind;
    public GridPos Pos; public int CellWidth;        // 占有セル数（カタログ既定）
    public string? DeviceName;                       // CR11 等（Device と紐付け）
    public Dictionary<string,string> Params = new(); // 色(G/R)・SW位置(閉/開)・ラベル 等
}
public readonly record struct GridPos(int Row, int Column);
public sealed class VerticalConnector { public int Column; public int TopRow; public int BottomRow; }
public sealed class GroupFrame { public string Label; public GridPos TopLeft; public int Width; public int Height; }

public enum DeviceClass { Relay, PushButton, SelectSwitch, Lamp, Timer, Counter, Terminal, Other }
public sealed class Device { public string Name; public DeviceClass Class; public string? PartId; }
public sealed class DeviceTable { public Dictionary<string,Device> ByName = new(); }
public sealed class Part { public string Id, Name, Maker, Model; public string? Rating; public int Quantity; }
public sealed class PartsList { public List<Part> Items = new(); }
```
```csharp
namespace GuiEcad.Simulation;   // 幾何から導出（永続化しない）

public sealed class Net {        // 線番号（ワイヤ番号）= 電気的に連続した配線に1番号
    public int Id; public int WireNumber; public bool IsRail; public List<GridPos> Cells;
}
public sealed class Netlist { public List<Net> Nets; public List<Component> Components; }
public sealed class Component { public ElementKind Kind; public string? DeviceName; public int NetA, NetB; }
public sealed class SimState {
    public Dictionary<string,bool> Inputs = new();    // PB・SS 等の操作状態
    public Dictionary<string,bool> Energized = new(); // リレーコイル等 ON/OFF（Device名キー）
}
```
