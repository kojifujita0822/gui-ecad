using GuiEcad.Model;

namespace GuiEcad.Rendering;

/// <summary>線の役割。実際の線スタイルは <see cref="DrawingTheme"/> から引く（1か所変更で全体反映）。</summary>
public enum StrokeRole { Wire, BusRail, SymbolOutline, GroupFrame, Grid }

/// <summary>文字の役割。</summary>
public enum TextRole { DeviceName, LineNumber, CrossRef, Title }

/// <summary>
/// 役割ごとの線・文字プリセット（画面/PDF共通）。太さ・サイズは mm 固定。
/// 当面ユーザー設定は持たない（固定プリセット。docs/rendering.md）。
/// </summary>
public sealed class DrawingTheme
{
    public static readonly Color Black = new(255, 0, 0, 0);
    public static readonly Color Blue = new(255, 0, 80, 220);          // 接続検査: 接続済み
    public static readonly Color Powered = new(255, 230, 60, 0);       // テストモード: 通電/励磁
    public static readonly Color ManualForced = new(110, 0, 80, 220);  // テストモード: 接点手動強制（半透明青）
    public static readonly Color GridGray = new(255, 210, 210, 210);

    /// <summary>線幅の最小クランプ(mm)。画面(Win2D)とPDFで同一に保ち、極細線がどちらでも消えないようにする。</summary>
    public const double MinStrokeWidthMm = 0.05;

    // 破線の ON,OFF 長（線幅の倍数）。全バックエンドで同一比率にして見た目を揃える。
    public const double DashOn = 4.0, DashOff = 2.0;   // Dashed
    public const double DotOn = 1.0, DotOff = 2.0;     // Dotted

    public string FontFamily { get; init; } = "Yu Gothic UI";

    public StrokeStyle Get(StrokeRole role) => role switch
    {
        StrokeRole.BusRail => new(Black, 0.35),
        StrokeRole.GroupFrame => new(Black, 0.18, LineStyle.Dashed),
        StrokeRole.Grid => new(GridGray, 0.10),
        _ => new(Black, 0.25),   // Wire / SymbolOutline
    };

    public TextStyle Text(TextRole role) => role switch
    {
        TextRole.LineNumber => new(FontFamily, 2.2, Black, HAlign: HAlign.Center, VAlign: VAlign.Bottom),
        TextRole.DeviceName => new(FontFamily, 2.0, Black, HAlign: HAlign.Center, VAlign: VAlign.Bottom),
        TextRole.Title => new(FontFamily, 4.0, Black, Bold: true, HAlign: HAlign.Left, VAlign: VAlign.Baseline),
        _ => new(FontFamily, 2.5, Black, HAlign: HAlign.Left, VAlign: VAlign.Middle),
    };

    public static DrawingTheme Default { get; } = new();
}
