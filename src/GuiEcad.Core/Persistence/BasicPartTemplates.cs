using GuiEcad.Model;

namespace GuiEcad.Persistence;

/// <summary>
/// 同梱する基本図形テンプレート（接点・コイル・端子台・セレクトSW 等）。
/// 初回起動時に <see cref="PartFolderStore"/> が「図形/」へ展開する元データ。
/// 座標は PartDefinition の規約に一致: 原点 = 最左ポート境界・行中心線、+x 右 / +y 下、単位 = セル。
/// （組込み記号 <c>SymbolGlyphs</c> の中心基準座標に +0.5 して左端基準へ移したもの。）
/// Id は固定値とし、再シードや埋め込みで重複・齟齬が出ないようにする。
/// </summary>
public static class BasicPartTemplates
{
    /// <summary>2端子（左=NetA / 右=NetB）の標準ポート。1セル幅の図形で共通。</summary>
    private static List<PortDef> TwoPorts() => new()
    {
        new PortDef("L", 0, 0),
        new PortDef("R", 0, 1),
    };

    /// <summary>同梱テンプレート一式（基本図形）。</summary>
    public static IReadOnlyList<PartDefinition> All() => new[]
    {
        ContactNO(),
        ContactNC(),
        Coil(),
        Terminal(),
        SelectSwitch(),
    };

    // a接点(NO): 2ブレード＋左右リード
    private static PartDefinition ContactNO() => new()
    {
        Id = "basic-contact-no",
        Name = "a接点",
        WidthCells = 1,
        HeightCells = 1,
        Role = PartRole.ContactNO,
        Ports = TwoPorts(),
        Primitives =
        {
            new PartLine(0, 0, 0.342, 0),
            new PartLine(0.658, 0, 1.0, 0),
            new PartLine(0.342, -0.317, 0.342, 0.317),
            new PartLine(0.658, -0.317, 0.658, 0.317),
        },
    };

    // b接点(NC): 斜線＋左右リード＋2ブレード（b接点NEW.gcadpart 由来）
    private static PartDefinition ContactNC() => new()
    {
        Id = "basic-contact-nc",
        Name = "b接点",
        WidthCells = 1,
        HeightCells = 1,
        Role = PartRole.ContactNC,
        Ports = TwoPorts(),
        Primitives =
        {
            new PartLine(0.92, -0.42, 0.08, 0.42),       // 斜線
            new PartLine(0.3125, 0, 0, 0),               // 左リード
            new PartLine(0.6875, 0, 1, 0),               // 右リード
            new PartLine(0.6875, 0, 0.6875, -0.3125),    // 右ブレード上
            new PartLine(0.6875, 0, 0.6875, 0.3125),     // 右ブレード下
            new PartLine(0.3125, 0, 0.3125, -0.3125),    // 左ブレード上
            new PartLine(0.3125, 0, 0.3125, 0.3125),     // 左ブレード下
        },
    };

    // コイル: 円＋左右リード
    private static PartDefinition Coil() => new()
    {
        Id = "basic-coil",
        Name = "コイル",
        WidthCells = 1,
        HeightCells = 1,
        Role = PartRole.Coil,
        Ports = TwoPorts(),
        Primitives =
        {
            new PartLine(0, 0, 0.08, 0),
            new PartLine(0.92, 0, 1.0, 0),
            new PartCircle(0.5, 0, 0.420),
        },
    };

    // 端子台: 小円＋斜線＋左右リード
    private static PartDefinition Terminal() => new()
    {
        Id = "basic-terminal",
        Name = "端子台",
        WidthCells = 1,
        HeightCells = 1,
        Role = PartRole.Terminal,
        Ports = TwoPorts(),
        Primitives =
        {
            new PartLine(0, 0, 0.35, 0),
            new PartLine(0.65, 0, 1.0, 0),
            new PartCircle(0.5, 0, 0.15),
            new PartLine(0.33, 0.17, 0.67, -0.17),
        },
    };

    // セレクトSW: 2端子円＋切替バー（接点として扱う・セレクトSW-NEW.gcadpart 由来）
    private static PartDefinition SelectSwitch() => new()
    {
        Id = "basic-select-switch",
        Name = "セレクトSW",
        WidthCells = 1,
        HeightCells = 1,
        Role = PartRole.ContactNO,
        Ports = TwoPorts(),
        Primitives =
        {
            new PartCircle(0.875, 0, 0.125),             // 右端子円
            new PartCircle(0.125, 0, 0.125),             // 左端子円
            new PartLine(0.25, 0, 0.375, 0),             // 左リード
            new PartLine(0.5625, 0, 0.5625, -0.1875),    // 右接片（縦）
            new PartLine(0.375, 0, 0.4375, 0),           // 中央リード
            new PartLine(0.4375, 0, 0.4375, -0.1875),    // 左接片（縦）
            new PartLine(0.5625, 0, 0.75, 0),            // 右リード
        },
    };
}
