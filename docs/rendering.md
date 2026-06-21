# 描画設計（IRenderer / 線スタイル）

## 設計方針
- **座標・寸法はすべて mm（double）のワールド座標で統一**。図面は寸法図でありPDF/印刷は物理サイズのため。
  - PDFsharp は `XGraphicsUnit.Millimeter` でそのまま描画。Win2D は mm→DIP 変換（ズーム・パン・DPI反映）のみ。
- **`IRenderer` は描画専用**。ヒットテスト・選択・状態管理は持たない（グリッド座標上でデータモデルに対して行う）。
- **変換スタック（Push/PopTransform）**で、各記号はローカル座標で描いてセル原点へ平行移動して配置（記号描画コードを再利用）。
- 上位の **`DiagramRenderer`** がモデルを走査して `IRenderer` を呼ぶ。これを画面/PDFで**共用**し二重保守を防ぐ。

## IRenderer API（ドラフト）
```csharp
namespace GuiEcad.Rendering;

// すべてワールド座標 = mm(double)。各バックエンドが mm→デバイス単位へ変換する。
public readonly record struct Point2D(double X, double Y);
public readonly record struct Size2D(double Width, double Height);
public readonly record struct Rect2D(double X, double Y, double Width, double Height);
public readonly record struct Color(byte A, byte R, byte G, byte B);

public enum LineStyle { Solid, Dashed, Dotted }   // 設置場所枠は Dashed/Dotted
public enum LineCap   { Butt, Round, Square }
public enum HAlign    { Left, Center, Right }
public enum VAlign    { Top, Middle, Baseline, Bottom }

public readonly record struct StrokeStyle(
    Color Color, double Width /*mm*/, LineStyle Style = LineStyle.Solid, LineCap Cap = LineCap.Butt);
public readonly record struct TextStyle(
    string FontFamily, double FontSizeMm, Color Color,
    bool Bold = false, bool Italic = false,
    HAlign HAlign = HAlign.Left, VAlign VAlign = VAlign.Baseline);

public interface IRenderer
{
    void PushTransform(double translateX, double translateY, double scale = 1.0);
    void PopTransform();
    void PushClip(Rect2D rect);
    void PopClip();

    void DrawLine(Point2D a, Point2D b, StrokeStyle stroke);
    void DrawPolyline(ReadOnlySpan<Point2D> points, StrokeStyle stroke);
    void DrawRectangle(Rect2D rect, StrokeStyle stroke);
    void FillRectangle(Rect2D rect, Color color);
    void DrawCircle(Point2D center, double radius, StrokeStyle stroke);   // コイル・ランプ外形
    void FillCircle(Point2D center, double radius, Color color);          // 分岐ドット ●
    void DrawEllipse(Point2D center, double radiusX, double radiusY, StrokeStyle stroke);
    void DrawArc(Point2D center, double radius, double startDeg, double sweepDeg, StrokeStyle stroke);
    void DrawText(string text, Point2D position, TextStyle style);        // 配置は HAlign/VAlign 基準
    Size2D MeasureText(string text, TextStyle style);                    // レイアウト用計測
}

// 複数ページ出力（主にPDF）用の上位抽象。画面は CanvasControl 側で IRenderer を直接生成する。
public interface IRenderSurface : IDisposable
{
    IRenderer BeginPage(Size2D pageSizeMm);
    void EndPage();
}
```

## バックエンド対応
| 抽象 | Win2D（画面） | PDFsharp（PDF） |
|---|---|---|
| 座標単位 | mm→DIP（ズーム/パン/DPI反映） | `XGraphics` を mm 単位で生成 |
| 線/塗り | `CanvasDrawingSession.DrawLine/FillCircle…` | `XGraphics`＋`XPen/XBrush` |
| 破線 | `CanvasStrokeStyle.CustomDashStyle` | `XPen.DashPattern` |
| 文字 | `CanvasTextFormat`（mm→DIP） | `XFont`（mm→pt, 1mm≒2.8346pt） |
| 変換 | `session.Transform = Matrix3x2` | `XGraphics.Translate/ScaleTransform`＋Save/Restore |

## 線スタイル（固定プリセット）
線は直接指定せず、**役割ごとのプリセット（`DrawingTheme`）から引く**（1か所変更で全体反映・画面/PDF一致）。当面**ユーザー設定は持たない（固定）**。

```csharp
public enum StrokeRole { Wire, BusRail, SymbolOutline, GroupFrame, Grid }
public enum TextRole   { DeviceName, LineNumber, CrossRef, /* … */ }
public sealed class DrawingTheme
{
    public StrokeStyle Get(StrokeRole role);
    public TextStyle   Text(TextRole role);
}
```

- **太さは mm 固定（ズーム非依存）**。画面は最低 1 デバイスピクセルを保証（ヘアライン消失防止）。PDFは 0幅ヘアライン禁止（必ず実 mm）。
- **破線パターンも mm で定義**し両バックエンドへ同じ mm 値を渡す: `Dashed`=線2.0/空1.0mm、`Dotted`=線0.3/空0.8mm（丸キャップ）。

初期プリセット値（案）:

| ロール | 線種 | 太さ(mm) | 備考 |
|---|---|---|---|
| Wire（配線・接点） | 実線 | 0.25 | 標準線 |
| BusRail（母線） | 実線 | 0.25〜0.35 | 標準 or やや太め |
| SymbolOutline（記号外形） | 実線 | 0.25 | |
| GroupFrame（設置場所枠） | 点線/破線 | 0.18 | 中継ボックス等 |
| Grid（補助線） | 実線/点線 | 0.10 | 画面のみ・PDF出力は任意 |

## 接続検査モード（線色 青/黒）（確定）
作画時の配線ミス検出を目的とする**画面のみの検証オーバーレイ**。トグルで ON/OFF。**通常表示・PDF出力は従来通り黒固定**（色は付けない）。

- **青**: ポートが同一グリッドノードに乗り、**実データで同一ネット**として結線されている配線。
- **黒（警告）**: 「見た目はつながって見えるが、データ上は接続されていない」箇所。判定対象は次の2つ:
  1. **ノード不一致の隣接**: 隣り合う要素／線が視覚的に接して見えるが、両者のポートが同一ノードに乗らず別ネットになっている（位置ずれ・1セルの隙間など）。
  2. **宙ぶらりポート（未結線）**: どこにもつながらない孤立ポート・片端だけのスタブ。
- **対象外**: ドット無しの交差（縦横の線が交差するが分岐●が無い）は**正しい非接続**なので警告しない（通常表示のまま）。
- **実装**: `DiagramRenderer` が Port モデルのネットリスト（Union-Find）を参照し、配線セグメント／ポートごとに色を決定。検証モード時のみ `StrokeRole.Wire` の色を **per-segment で上書き**（青＝接続済み、黒＝警告）。判定ソースは [data-model.md](data-model.md)「接続点（Port）モデル」と同一（採番・シミュレーションと共用）。PDF出力・通常画面には反映しない。

## 注意点
- **文字メトリクス差**: `MeasureText` は Win2D（DirectWrite）と PDFsharp で僅差が出うる。厳密な揃えは片方の計測に依存せず**グリッド座標で算術的に決める**。
- 画面とPDFで**同一フォント**を使用する前提（差異・埋め込みは別途検討）。
- `DrawArc` は押ボタン等の曲線記号用。直線・円で代替可能なら最小化してよい。
