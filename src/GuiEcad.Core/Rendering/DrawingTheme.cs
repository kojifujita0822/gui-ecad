using GuiEcad.Model;

namespace GuiEcad.Rendering;

/// <summary>線の役割。実際の線スタイルは <see cref="DrawingTheme"/> から引く（1か所変更で全体反映）。</summary>
public enum StrokeRole { Wire, BusRail, SymbolOutline, GroupFrame, Grid }

/// <summary>文字の役割。</summary>
public enum TextRole { DeviceName, LineNumber, CrossRef, Title }

/// <summary>
/// 役割ごとの線・文字プリセット（画面/PDF共通）。太さ・サイズは mm 固定。
///
/// カラー: 色を「テーマ非依存の素材定数（static）」と「テーマで切り替わるパレット（インスタンス）」に分離する。
/// - パレット（<see cref="Foreground"/>/<see cref="Background"/>/<see cref="GridColor"/>/<see cref="TableHeaderFill"/>）は
///   テーマごとに差し替える。線・記号・文字は <see cref="Foreground"/> を使う。
/// - <see cref="Powered"/>（通電）・<see cref="Blue"/>（接続済み）・<see cref="ManualForced"/>（手動強制）は
///   状態を表す「意味色」なのでテーマ間で固定（static のまま）。
///
/// 画面の作図色は <see cref="Default"/>（白地・黒線）と <see cref="Dark"/>（暗地・明線）の2種。
/// 画面はメニューの「ダークモード(作図色)」で切替、PDF は常に <see cref="Default"/> を使う。
/// </summary>
public sealed class DrawingTheme
{
    // ===== テーマ非依存の素材色（パレットの既定値・意味色）=====
    public static readonly Color Black = new(255, 0, 0, 0);
    public static readonly Color White = new(255, 255, 255, 255);
    public static readonly Color GridGray = new(255, 210, 210, 210);
    // 状態を表す意味色（テーマ間で固定）。
    public static readonly Color Blue = new(255, 0, 80, 220);          // 接続検査: 接続済み
    public static readonly Color Powered = new(255, 230, 60, 0);       // テストモード: 通電/励磁
    public static readonly Color ManualForced = new(110, 0, 80, 220);  // テストモード: 接点手動強制（半透明青）

    // 表（機器表・クロスリファレンス・表題欄）の罫線幅と、テスト通電配線の強調線幅(mm)。
    public const double TableLineWidth = 0.18;
    public const double PoweredWireWidth = 0.45;

    /// <summary>線幅の最小クランプ(mm)。画面(Win2D)とPDFで同一に保ち、極細線がどちらでも消えないようにする。</summary>
    public const double MinStrokeWidthMm = 0.05;

    // 破線の ON,OFF 長（線幅の倍数）。全バックエンドで同一比率にして見た目を揃える。
    public const double DashOn = 4.0, DashOff = 2.0;   // Dashed
    public const double DotOn = 1.0, DotOff = 2.0;     // Dotted

    // ===== テーマで切り替わるパレット（インスタンス）=====
    /// <summary>UI に出すテーマ名（カラーテーマ一覧の表示・永続化キー）。</summary>
    public string Name { get; init; } = "ライト";
    public string FontFamily { get; init; } = "Yu Gothic UI";
    /// <summary>線・記号・文字の前景色。</summary>
    public Color Foreground { get; init; } = Black;
    /// <summary>キャンバス背景色（画面のみ。PDFは Default を使う運用）。</summary>
    public Color Background { get; init; } = White;
    /// <summary>作図ガイドの薄い格子色。</summary>
    public Color GridColor { get; init; } = GridGray;
    /// <summary>機器表・各種表のヘッダ背景色。</summary>
    public Color TableHeaderFill { get; init; } = new(255, 230, 230, 230);

    public StrokeStyle Get(StrokeRole role) => role switch
    {
        StrokeRole.BusRail => new(Foreground, 0.35),
        StrokeRole.GroupFrame => new(Foreground, 0.18, LineStyle.Dashed),
        StrokeRole.Grid => new(GridColor, 0.10),
        _ => new(Foreground, 0.25),   // Wire / SymbolOutline
    };

    public TextStyle Text(TextRole role) => role switch
    {
        TextRole.LineNumber => new(FontFamily, 2.2, Foreground, HAlign: HAlign.Center, VAlign: VAlign.Bottom),
        TextRole.DeviceName => new(FontFamily, 2.0, Foreground, HAlign: HAlign.Center, VAlign: VAlign.Bottom),
        TextRole.Title => new(FontFamily, 4.0, Foreground, Bold: true, HAlign: HAlign.Left, VAlign: VAlign.Baseline),
        _ => new(FontFamily, 2.5, Foreground, HAlign: HAlign.Left, VAlign: VAlign.Middle),
    };

    // ===== 既定テーマ＋カラーテーマのひな形 =====
    /// <summary>標準（白地・黒線）。PDF 出力は常にこれを使う（提出図面は白地・黒線が基本）。</summary>
    public static DrawingTheme Default { get; } = new();

    /// <summary>ダーク（暗い背景・明色の線）。作図エリア用のダークモード。</summary>
    public static DrawingTheme Dark { get; } = new()
    {
        Name = "ダーク",
        Foreground = new(255, 225, 225, 225),
        Background = new(255, 32, 34, 38),
        GridColor = new(255, 70, 74, 80),
        TableHeaderFill = new(255, 55, 58, 64),
    };
}
