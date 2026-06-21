namespace GuiEcad.Model;

/// <summary>要素種別ごとの既定値・分類。</summary>
public static class ElementCatalog
{
    /// <summary>既定の占有セル数。</summary>
    public static int DefaultCellWidth(ElementKind kind) => kind switch
    {
        // セレクトSWはノッチ毎の2端子接点（1セル）。位置は ElementInstance.Params["Position"]。
        ElementKind.Motor => 3,   // 三相 U/V/W の3端子を横並びに確保（暫定レイアウト）
        _ => 1,
    };

    /// <summary>
    /// 種別ごとの接続点（ポート）定義。実効幅 <paramref name="width"/> から端子境界を算出する。
    /// 2端子種別: 左端子=境界0／右端子=境界 width（先頭=NetA・末尾=NetB）。
    /// 三相モータ: U/V/W の3端子（境界 0/1/2）。非シミュレート（接続のみ生成）。
    /// ポートは境界オフセット昇順で返す（先頭=最左・末尾=最右）。
    /// </summary>
    public static IReadOnlyList<PortDef> Ports(ElementKind kind, int width) => kind switch
    {
        ElementKind.Motor => new[]
        {
            new PortDef("U", 0, 0),
            new PortDef("V", 0, 1),
            new PortDef("W", 0, 2),
        },
        _ => new[]
        {
            new PortDef("L", 0, 0),
            new PortDef("R", 0, width),
        },
    };

    /// <summary>
    /// 機器名ラベルの既定高さオフセット(mm、正で上)。要素に Params["LabelDy"] が無い場合に使う。
    /// 密集時の重なり回避の標準位置（sample/kitei.gcad の調整値が由来）。
    /// </summary>
    public static double DefaultLabelDy(ElementKind k) => k switch
    {
        ElementKind.ContactNO or ElementKind.ContactNC => -1.5,
        ElementKind.PushButtonNC => -1.5,
        ElementKind.PushButtonNO => -0.5,
        ElementKind.Coil => -5.5,   // コイルの丸の中あたりに表示（重なり回避）
        ElementKind.Lamp => -1.5,
        ElementKind.Terminal => -2.0,
        _ => 0.0,
    };

    /// <summary>ネットリストの電気要素（Component）を生成する種別か。false は記号のみ（非シミュレート）。</summary>
    public static bool CreatesComponent(ElementKind k) => IsContact(k) || IsLoad(k) || IsPassthrough(k);

    /// <summary>接点（導通/非導通を切り替える要素）か。</summary>
    public static bool IsContact(ElementKind k) => k is
        ElementKind.ContactNO or ElementKind.ContactNC or
        ElementKind.PushButtonNO or ElementKind.PushButtonNC or
        ElementKind.SelectSwitch or
        ElementKind.TimerContactNO or ElementKind.TimerContactNC or
        ElementKind.EmergencyStop or ElementKind.ThermalOverload;

    /// <summary>負荷（コイル・ランプ・タイマコイル・カウンタコイル）か。</summary>
    public static bool IsLoad(ElementKind k) => k is
        ElementKind.Coil or ElementKind.Lamp or ElementKind.Timer or ElementKind.Counter;

    /// <summary>常時導通の通過要素（端子台）か。</summary>
    public static bool IsPassthrough(ElementKind k) => k is ElementKind.Terminal;

    /// <summary>
    /// 接点の制御元が外部入力（押ボタン・セレクトSW・OL手動）か。false ならリレーコイル状態で制御。
    /// タイマ接点（TimerContactNO/NC）はタイマコイル励磁＋経過時間で制御するため含まない。
    /// </summary>
    public static bool IsInputControlled(ElementKind k) => k is
        ElementKind.PushButtonNO or ElementKind.PushButtonNC or ElementKind.SelectSwitch or
        ElementKind.EmergencyStop or ElementKind.ThermalOverload;
}
