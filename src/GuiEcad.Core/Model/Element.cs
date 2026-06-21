namespace GuiEcad.Model;

public enum LineStyle { Solid, Dashed, Dotted }

public enum ElementKind
{
    ContactNO, ContactNC, Coil, Lamp,
    PushButtonNO, PushButtonNC, SelectSwitch, Terminal, Timer, Counter,
    // サンプル凡例の追加パーツ（2端子）。タイマ接点・OL は初期は手動入力（docs/simulation.md）。
    TimerContactNO, TimerContactNC, EmergencyStop, ThermalOverload,
    // 三相モータ（多端子）。三相動力回路は制御回路と別系統のため初期は記号のみ・非シミュレート。
    Motor
}

/// <summary>グリッド座標（行・列）。Row はデータ上の内部座標（ステップ番号ではない）。
/// 画面/PDF には視覚ガイドとして 1 始まりの行番号を表示する（DiagramRenderer.DrawRowNumbers）。</summary>
public readonly record struct GridPos(int Row, int Column);

/// <summary>
/// 接続点（ポート／端子）の定義。種別固定でカタログが宣言する（docs/data-model.md「接続点（Port）モデル」）。
/// 実ノード座標 = (要素 Pos.Row + RowOffset, 列境界 Pos.Column + BoundaryOffset)。
/// 列境界は左母線=0、右母線=Columns。同一ノード座標に載るポート同士が電気的に同一ネット。
/// </summary>
public readonly record struct PortDef(string Name, int RowOffset, int BoundaryOffset);

/// <summary>グリッドに配置された1要素。記号（見た目）は描画側カタログが Kind で引く。</summary>
public sealed class ElementInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ElementKind Kind { get; set; }
    public GridPos Pos { get; set; }
    /// <summary>占有セル数（既定は ElementCatalog.DefaultCellWidth）。SelectSwitch=3 等。</summary>
    public int CellWidth { get; set; } = 1;
    /// <summary>機器名参照（CR11 等）。Device と紐付くキー。</summary>
    public string? DeviceName { get; set; }
    /// <summary>自作パーツ参照（PartLibrary の Id）。null なら組込み種別（<see cref="Kind"/>）。</summary>
    public string? PartId { get; set; }
    /// <summary>色(G/R)・SW位置(閉/開)・ラベル等の付加情報。</summary>
    public Dictionary<string, string> Params { get; set; } = new();
    /// <summary>注記テキスト。機器名ラベルの下に小フォントで表示される。</summary>
    public string? Comment { get; set; }

    public ElementInstance DeepClone() => new()
    {
        Id = Guid.NewGuid(),
        Kind = Kind,
        Pos = Pos,
        CellWidth = CellWidth,
        DeviceName = DeviceName,
        PartId = PartId,
        Params = new Dictionary<string, string>(Params),
        Comment = Comment,
    };
}

/// <summary>同一列で複数行をつなぐ縦渡り（分岐）。接点に黒ドット● を描画して明示。</summary>
public sealed class VerticalConnector
{
    /// <summary>分岐の水平位置（列境界）。0.5 刻みでセル中央にも置ける（線番が出る空きセル中央など）。</summary>
    public double Column { get; set; }
    public int TopRow { get; set; }
    public int BottomRow { get; set; }
}

/// <summary>設置場所のグルーピング枠（点線）。中継ボックス・MR盤 等。</summary>
public sealed class GroupFrame
{
    public string Label { get; set; } = "";
    public GridPos TopLeft { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>枠の線種。null = テーマ既定（Dashed）。旧ファイルとの互換性のため nullable。</summary>
    public LineStyle? BorderStyle { get; set; }
    /// <summary>
    /// ドラッグ移動後の mm 座標。null = TopLeft グリッドから計算した位置を使う。
    /// 旧ファイルとの互換性のため nullable（null = グリッド追従）。
    /// </summary>
    public double? VisualXMm { get; set; }
    public double? VisualYMm { get; set; }
    /// <summary>
    /// 自由作成時の mm サイズ。null = Width/Height（グリッド単位）×セル幅を使う。
    /// 旧ファイル互換のため nullable。
    /// </summary>
    public double? VisualWidthMm { get; set; }
    public double? VisualHeightMm { get; set; }
}
