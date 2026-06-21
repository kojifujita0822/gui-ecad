using GuiEcad.Model;

namespace GuiEcad.Rendering;

/// <summary>
/// 種別ごとの記号グリフ（ローカル座標）。原点=要素の最左ポート点、行中心線=y0、+x右・+y下。
/// 形状はユーザー提供の Jw_cad 図（temp の個別DXF）に準拠し、1セルに収まるよう正規化済み。
/// 各記号は中央 cx を基準に描き、リード線（横配線）は <see cref="Leads"/> で母線側へ伸ばす。
/// 座標値は「中心基準・セル単位」の実測値（DXFを cell=150 で割り、1セルへフィットしたもの）。
/// </summary>
internal static class SymbolGlyphs
{
    public static void Draw(IRenderer r, StrokeStyle s, ElementKind kind, double width, double cell,
                            Color? manualFill = null)
    {
        double cx = width / 2;

        switch (kind)
        {
            case ElementKind.ContactNO: ContactNO(r, s, cx, width, cell, manualFill); break;
            case ElementKind.ContactNC: ContactNC(r, s, cx, width, cell, manualFill); break;

            case ElementKind.PushButtonNO: PushButtonNO(r, s, cx, width, cell); break;
            case ElementKind.PushButtonNC: PushButtonNC(r, s, cx, width, cell); break;
            case ElementKind.EmergencyStop: EmergencyStop(r, s, cx, width, cell); break;

            case ElementKind.SelectSwitch: SelectSwitch(r, s, cx, width, cell); break;

            case ElementKind.TimerContactNO: TimerContactNO(r, s, cx, width, cell); break;
            case ElementKind.TimerContactNC: TimerContactNC(r, s, cx, width, cell); break;

            case ElementKind.ThermalOverload: Thermal(r, s, cx, width, cell); break;

            case ElementKind.Coil: Coil(r, s, cx, width, cell); break;
            case ElementKind.Timer: TimerCoil(r, s, cx, width, cell); break;
            case ElementKind.Lamp: Lamp(r, s, cx, width, cell); break;
            case ElementKind.Terminal: Terminal(r, s, cx, width, cell); break;
            case ElementKind.Motor: Motor(r, s, width, cell); break;

            default:
                Leads(r, s, cx, width, cell, 0.3);
                r.DrawRectangle(new(cx - cell * 0.3, -cell * 0.2, cell * 0.6, cell * 0.4), s);
                break;
        }
    }

    // ===== 描画ヘルパ（中心基準・セル単位 → ローカル mm）=====

    private static void L(IRenderer r, StrokeStyle s, double cx, double cell,
        double x1, double y1, double x2, double y2)
        => r.DrawLine(new(cx + x1 * cell, y1 * cell), new(cx + x2 * cell, y2 * cell), s);

    private static void C(IRenderer r, StrokeStyle s, double cx, double cell, double x, double y, double rad)
        => r.DrawCircle(new(cx + x * cell, y * cell), rad * cell, s);

    /// <summary>左母線側(0)と右母線側(width)から本体端 ±half まで横配線（リード線）を引く。</summary>
    private static void Leads(IRenderer r, StrokeStyle s, double cx, double width, double cell, double half)
    {
        r.DrawLine(new(0, 0), new(cx - half * cell, 0), s);
        r.DrawLine(new(cx + half * cell, 0), new(width, 0), s);
    }

    // ===== 各記号（座標は temp/appstyle.py の正規化出力に一致）=====

    // a接点(NO): 2本の縦ブレード。manualFill 指定時はブレード間を塗りつぶす（手動強制マーク）。
    private static void ContactNO(IRenderer r, StrokeStyle s, double cx, double width, double cell,
                                  Color? manualFill = null)
    {
        Leads(r, s, cx, width, cell, 0.158);
        if (manualFill is Color fc)
            r.FillRectangle(new(cx - 0.158 * cell, -0.317 * cell, 0.316 * cell, 0.634 * cell), fc);
        L(r, s, cx, cell, -0.158, -0.317, -0.158, 0.317);
        L(r, s, cx, cell, 0.158, -0.317, 0.158, 0.317);
    }

    // b接点(NC): 2ブレード＋斜線。manualFill 指定時はブレード間を塗りつぶす（手動強制マーク）。
    private static void ContactNC(IRenderer r, StrokeStyle s, double cx, double width, double cell,
                                  Color? manualFill = null)
    {
        Leads(r, s, cx, width, cell, 0.1875);
        if (manualFill is Color fc)
            r.FillRectangle(new(cx - 0.1875 * cell, -0.3125 * cell, 0.375 * cell, 0.625 * cell), fc);
        L(r, s, cx, cell, -0.1875, -0.3125, -0.1875, 0.3125);
        L(r, s, cx, cell, 0.1875, -0.3125, 0.1875, 0.3125);
        L(r, s, cx, cell, 0.42, -0.42, -0.42, 0.42);
    }

    // タイマ接点(NO): 端子円(中心線上)＋上の限時バー＋上向き△
    private static void TimerContactNO(IRenderer r, StrokeStyle s, double cx, double width, double cell)
    {
        Leads(r, s, cx, width, cell, 0.460);
        C(r, s, cx, cell, -0.307, 0, 0.153);
        C(r, s, cx, cell, 0.307, 0, 0.153);
        L(r, s, cx, cell, -0.460, -0.245, 0.460, -0.245);
        L(r, s, cx, cell, 0, -0.399, -0.089, -0.245);
        L(r, s, cx, cell, 0, -0.399, 0.089, -0.245);
    }

    // タイマ接点(NC): 端子円(中心線上)＋下の限時バー＋下向き△
    private static void TimerContactNC(IRenderer r, StrokeStyle s, double cx, double width, double cell)
    {
        Leads(r, s, cx, width, cell, 0.460);
        C(r, s, cx, cell, -0.307, 0, 0.153);
        C(r, s, cx, cell, 0.307, 0, 0.153);
        L(r, s, cx, cell, -0.460, 0.153, 0.460, 0.153);
        L(r, s, cx, cell, 0, 0, -0.089, 0.153);
        L(r, s, cx, cell, 0, 0, 0.089, 0.153);
    }

    // 押釦(NO): 端子円(中心線上)＋上の可動バー＋ステム
    private static void PushButtonNO(IRenderer r, StrokeStyle s, double cx, double width, double cell)
    {
        Leads(r, s, cx, width, cell, 0.420);
        C(r, s, cx, cell, -0.280, 0, 0.140);
        C(r, s, cx, cell, 0.280, 0, 0.140);
        L(r, s, cx, cell, -0.420, -0.280, 0.420, -0.280);
        L(r, s, cx, cell, 0, -0.280, 0, -0.420);
    }

    // 押釦(NC): 端子円(中心線上)＋下の橋絡バー＋ステム
    private static void PushButtonNC(IRenderer r, StrokeStyle s, double cx, double width, double cell)
    {
        Leads(r, s, cx, width, cell, 0.460);
        C(r, s, cx, cell, -0.307, 0, 0.153);
        C(r, s, cx, cell, 0.307, 0, 0.153);
        L(r, s, cx, cell, -0.460, 0.153, 0.460, 0.153);
        L(r, s, cx, cell, 0, 0.153, 0, -0.307);
    }

    // 非常停止: 押釦(NC形)＋ドーム(キノコ頭)
    private static void EmergencyStop(IRenderer r, StrokeStyle s, double cx, double width, double cell)
    {
        Leads(r, s, cx, width, cell, 0.460);
        C(r, s, cx, cell, -0.307, 0, 0.153);
        C(r, s, cx, cell, 0.307, 0, 0.153);
        L(r, s, cx, cell, -0.460, 0.153, 0.460, 0.153);
        L(r, s, cx, cell, 0, 0.153, 0, -0.380);
        Span<Point2D> dome = stackalloc Point2D[]
        {
            new(cx - 0.373 * cell, -0.239 * cell), new(cx - 0.311 * cell, -0.289 * cell),
            new(cx - 0.239 * cell, -0.329 * cell), new(cx - 0.160 * cell, -0.358 * cell),
            new(cx - 0.076 * cell, -0.375 * cell), new(cx + 0.010 * cell, -0.380 * cell),
            new(cx + 0.096 * cell, -0.372 * cell), new(cx + 0.179 * cell, -0.352 * cell),
            new(cx + 0.256 * cell, -0.321 * cell), new(cx + 0.326 * cell, -0.278 * cell),
            new(cx + 0.373 * cell, -0.239 * cell),
        };
        r.DrawPolyline(dome, s);
    }

    // セレクトSW: 端子円(中心線上)＋段付きハンドル
    private static void SelectSwitch(IRenderer r, StrokeStyle s, double cx, double width, double cell)
    {
        Leads(r, s, cx, width, cell, 0.5);
        C(r, s, cx, cell, -0.375, 0, 0.125);
        C(r, s, cx, cell, 0.375, 0, 0.125);
        L(r, s, cx, cell, -0.25, 0, -0.0625, 0);     // 左リード（円→左接片）
        L(r, s, cx, cell, 0.0625, 0, 0.25, 0);       // 右リード（右接片→円）
        L(r, s, cx, cell, -0.0625, 0, -0.0625, -0.1875);   // 左接片（縦）
        L(r, s, cx, cell, 0.0625, 0, 0.0625, -0.1875);     // 右接片（縦）
    }

    // サーマル(OL): 端子円＋斜線（暫定: 端子台に準ずる）。専用DXF未提供のため従来形を維持。
    private static void Thermal(IRenderer r, StrokeStyle s, double cx, double width, double cell)
    {
        double w = cell * 0.26, top = -cell * 0.30;
        Leads(r, s, cx, width, cell, 0.26);
        L(r, s, cx, cell, -0.26, 0, -0.26, top / cell);
        L(r, s, cx, cell, -0.26, top / cell, 0.26, top / cell);
        L(r, s, cx, cell, 0.26, top / cell, 0.26, 0);
    }

    // コイル: 円(r=0.420)
    private static void Coil(IRenderer r, StrokeStyle s, double cx, double width, double cell)
    {
        Leads(r, s, cx, width, cell, 0.420);
        C(r, s, cx, cell, 0, 0, 0.420);
    }

    // タイマコイル: コイル円＋上向き△（限時要素マーク）
    private static void TimerCoil(IRenderer r, StrokeStyle s, double cx, double width, double cell)
    {
        Leads(r, s, cx, width, cell, 0.420);
        C(r, s, cx, cell, 0, 0, 0.420);
        L(r, s, cx, cell, 0, -0.550, -0.089, -0.420);
        L(r, s, cx, cell, 0, -0.550,  0.089, -0.420);
        L(r, s, cx, cell, -0.089, -0.420, 0.089, -0.420);
    }

    // 表示灯: 円(r=0.323)＋外向き4放射線
    private static void Lamp(IRenderer r, StrokeStyle s, double cx, double width, double cell)
    {
        Leads(r, s, cx, width, cell, 0.323);
        C(r, s, cx, cell, 0, 0, 0.323);
        L(r, s, cx, cell, 0.275, 0.275, 0.420, 0.420);
        L(r, s, cx, cell, -0.275, 0.275, -0.420, 0.420);
        L(r, s, cx, cell, -0.275, -0.275, -0.420, -0.420);
        L(r, s, cx, cell, 0.275, -0.275, 0.420, -0.420);
    }

    // 端子台: 円(r=0.15)＋斜線貫通（直径0.3セル）
    private static void Terminal(IRenderer r, StrokeStyle s, double cx, double width, double cell)
    {
        Leads(r, s, cx, width, cell, 0.15);
        C(r, s, cx, cell, 0, 0, 0.15);
        L(r, s, cx, cell, -0.17, 0.17, 0.17, -0.17);
    }

    // 三相モータ: 大円＋左に縦3端子(∅)＋リード。ユーザー提供DXFを3セル幅に正規化。
    // 座標は「3セル幅基準・原点=最左ポート・+y下」。width に比例スケール（k = width/3）。
    private static void Motor(IRenderer r, StrokeStyle s, double width, double cell)
    {
        double k = width / 3.0;
        void M(double x1, double y1, double x2, double y2) => r.DrawLine(new(x1 * k, y1 * k), new(x2 * k, y2 * k), s);
        void O(double x, double y, double rad) => r.DrawCircle(new(x * k, y * k), rad * k, s);

        O(2.009, 0, 0.991);                 // 本体大円
        O(0.224, -1.189, 0.198);            // 端子 U
        O(0.224, 0, 0.198);                 // 端子 V
        O(0.224, 1.190, 0.198);             // 端子 W
        M(0, -0.965, 0.449, -1.414);        // ∅斜線 U
        M(0, 0.224, 0.449, -0.224);         // ∅斜線 V
        M(0, 1.414, 0.449, 0.965);          // ∅斜線 W
        M(0.423, 0, 1.017, 0);              // リード V→本体
        M(0.423, -1.189, 0.819, -1.189);    // リード U（水平）
        M(1.308, -0.701, 0.819, -1.189);    // リード U（斜め）→本体
        M(0.423, 1.190, 0.819, 1.190);      // リード W（水平）
        M(1.308, 0.701, 0.819, 1.190);      // リード W（斜め）→本体
    }
}
