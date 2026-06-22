using GuiEcad.Model;
using GuiEcad.Rendering;
using GuiEcad.Simulation;
using Xunit;
using static GuiEcad.Tests.TestHelper;

namespace GuiEcad.Tests;

/// <summary>サンプル凡例の追加2端子パーツ（タイマ接点 a/b・非常停止押釦・サーマルOL）の導通挙動。</summary>
public class NewPartsTests
{
    private static bool Energized(ElementKind contact, string dev, bool input)
    {
        // L —[contact dev]— (Coil C) — R
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(El(contact, 0, 0, dev));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 2, "C"));
        var nl = NetlistBuilder.Build(sheet);
        var res = new Evaluator(nl).Evaluate(new SimState { Inputs = { [dev] = input } });
        return res.State.Energized["C"];
    }

    // タイマ回路: Row0 = L—[PB "PB1"]—(Timer "TLR1" setpoint秒)—R
    //             Row1 = L—[timerContact "TLR1"]—(Coil "C")—R
    // Columns=2: 境界0=左母線/境界2=右母線。各行の要素が直列に並ぶ。
    private static Sheet MakeTimerSheet(ElementKind contact, double setpoint)
    {
        var timerEl = new ElementInstance
        {
            Kind = ElementKind.Timer, Pos = new GridPos(0, 1), DeviceName = "TLR1",
        };
        timerEl.Params["Setpoint"] = setpoint.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var sheet = new Sheet { Grid = new GridSpec { Columns = 2 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "PB1"));
        sheet.Elements.Add(timerEl);
        sheet.Elements.Add(El(contact, 1, 0, "TLR1"));
        sheet.Elements.Add(El(ElementKind.Coil, 1, 1, "C"));
        return sheet;
    }

    [Fact]
    public void TimerContactNO_ConductsOnlyWhenCoilOnAndTimedOut()
    {
        var session = new TestSession(MakeTimerSheet(ElementKind.TimerContactNO, setpoint: 5.0));
        session.Evaluate();

        // PB未押下 → TLR1消磁 → 接点開放
        Assert.False(session.IsEnergized("C"));

        // PB押下 → TLR1励磁 → 時間未達 → 接点開放
        session.SetInput("PB1", true);
        Assert.False(session.IsEnergized("C"));

        // Tick 3s → まだ未達 → 開放
        session.Tick(3.0);
        Assert.False(session.IsEnergized("C"));

        // Tick 2s → 累計5s到達 → 閉路
        session.Tick(2.0);
        Assert.True(session.IsEnergized("C"));

        // PB離す → TLR1消磁・タイマリセット → 接点開放
        session.SetInput("PB1", false);
        Assert.False(session.IsEnergized("C"));
    }

    [Fact]
    public void TimerContactNC_OpenOnlyWhenCoilOnAndTimedOut()
    {
        var session = new TestSession(MakeTimerSheet(ElementKind.TimerContactNC, setpoint: 5.0));
        session.Evaluate();

        // PB未押下 → TLR1消磁 → b接点閉路 → C通電
        Assert.True(session.IsEnergized("C"));

        // PB押下 → TLR1励磁 → 時間未達 → b接点閉路
        session.SetInput("PB1", true);
        Assert.True(session.IsEnergized("C"));

        // Tick 5s → 限時到達 → b接点開放 → C消灯
        session.Tick(5.0);
        Assert.False(session.IsEnergized("C"));

        // PB離す → TLR1消磁・リセット → b接点閉路に戻る
        session.SetInput("PB1", false);
        Assert.True(session.IsEnergized("C"));
    }

    [Fact]
    public void EmergencyStop_IsNormallyClosed()
    {
        Assert.True(Energized(ElementKind.EmergencyStop, "ES", input: false));   // 未押下=導通
        Assert.False(Energized(ElementKind.EmergencyStop, "ES", input: true));   // 押下=遮断
    }

    [Fact]
    public void ThermalOverload_IsNormallyClosed()
    {
        Assert.True(Energized(ElementKind.ThermalOverload, "OL", input: false)); // 正常=導通
        Assert.False(Energized(ElementKind.ThermalOverload, "OL", input: true)); // トリップ=遮断
    }

    [Fact]
    public void SelectSwitch_ConductsOnlyAtMatchingPosition()
    {
        // 行0: L —[SS pos1]— (Coil A)   行1: L —[SS pos2]— (Coil B)（同一機器 "SS"・別ノッチ）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        var ss1 = new ElementInstance { Kind = ElementKind.SelectSwitch, Pos = new GridPos(0, 0), DeviceName = "SS" };
        ss1.Params["Position"] = "1";
        var ss2 = new ElementInstance { Kind = ElementKind.SelectSwitch, Pos = new GridPos(1, 0), DeviceName = "SS" };
        ss2.Params["Position"] = "2";
        sheet.Elements.Add(ss1);
        sheet.Elements.Add(new ElementInstance { Kind = ElementKind.Coil, Pos = new GridPos(0, 2), DeviceName = "A" });
        sheet.Elements.Add(ss2);
        sheet.Elements.Add(new ElementInstance { Kind = ElementKind.Coil, Pos = new GridPos(1, 2), DeviceName = "B" });

        var nl = NetlistBuilder.Build(sheet);

        var atPos1 = new Evaluator(nl).Evaluate(new SimState { Positions = { ["SS"] = 1 } }).State;
        Assert.True(atPos1.Energized["A"]);
        Assert.False(atPos1.Energized["B"]);

        var atPos2 = new Evaluator(nl).Evaluate(new SimState { Positions = { ["SS"] = 2 } }).State;
        Assert.False(atPos2.Energized["A"]);
        Assert.True(atPos2.Energized["B"]);
    }

    [Fact]
    public void Motor_IsNonSimulated_NoComponent()
    {
        // 三相モータは記号のみ＝Component を生成しない（制御ロジック評価対象外）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 6 } };
        sheet.Elements.Add(new ElementInstance
        { Kind = ElementKind.Motor, Pos = new GridPos(0, 1), CellWidth = 3, DeviceName = "M1" });

        var nl = NetlistBuilder.Build(sheet);
        Assert.DoesNotContain(nl.Components, c => c.DeviceName == "M1");
        Assert.False(ElementCatalog.CreatesComponent(ElementKind.Motor));
        Assert.Equal(3, ElementCatalog.Ports(ElementKind.Motor, 3).Count); // U/V/W の3端子
    }

    // ===== 主回路（三相動力）用 3極記号 =====

    public static IEnumerable<object[]> MainCircuitKinds() => new[]
    {
        new object[] { ElementKind.Breaker3P },
        new object[] { ElementKind.ContactorMain3P },
        new object[] { ElementKind.ThermalOverload3P },
    };

    [Theory]
    [MemberData(nameof(MainCircuitKinds))]
    public void MainCircuitSymbol_IsNonSimulated_NoComponentNoPorts(ElementKind kind)
    {
        // 主回路3極記号は記号のみ＝非シミュレート・接続点なし・2セル幅（2×2）。
        Assert.False(ElementCatalog.CreatesComponent(kind));
        Assert.Empty(ElementCatalog.Ports(kind, 2));
        Assert.Equal(2, ElementCatalog.DefaultCellWidth(kind));

        var sheet = new Sheet { MainCircuit = true, Grid = new GridSpec { Columns = 6 } };
        sheet.Elements.Add(new ElementInstance
        { Kind = kind, Pos = new GridPos(0, 1), CellWidth = 3, DeviceName = "X1" });

        var nl = NetlistBuilder.Build(sheet);
        Assert.DoesNotContain(nl.Components, c => c.DeviceName == "X1");
    }

    [Theory]
    [MemberData(nameof(MainCircuitKinds))]
    public void MainCircuitSymbol_BoundarySpan_UsesCellWidth(ElementKind kind)
    {
        // 接続点が無くても占有列は CellWidth で決まる（選択・ヒットテストが破綻しない）。
        var e = new ElementInstance { Kind = kind, Pos = new GridPos(0, 2), CellWidth = 3 };
        var (left, right) = PartResolver.BoundarySpan(e, null);
        Assert.Equal(2, left);
        Assert.Equal(5, right);
    }

    [Theory]
    [MemberData(nameof(MainCircuitKinds))]
    public void MainCircuitSymbol_Renders_BothOrientations_WithoutException(ElementKind kind)
    {
        foreach (var orient in new[] { "V", "H" })
        {
            var sheet = new Sheet { MainCircuit = true, Grid = new GridSpec { Columns = 9 } };
            var e = new ElementInstance { Kind = kind, Pos = new GridPos(1, 2), CellWidth = 2, DeviceName = "X1" };
            e.Params["Orient"] = orient;
            if (kind == ElementKind.Breaker3P) e.Params["Type"] = "ELB";   // 漏電遮断器の付加印も通す
            sheet.Elements.Add(e);

            var ex = Record.Exception(() => new DiagramRenderer().Render(new NullRenderer(), sheet));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void MainCircuit_MultiPage_FreeLinesRender_ClipBalanced()
    {
        // 主回路シートを2ページに跨らせ、自由直線（母線）描画でクリップが釣り合うこと（はみ出し対策）。
        int rows = DiagramRenderer.RowsPerPage + 5;   // 2ページ分
        var sheet = new Sheet { MainCircuit = true, Grid = new GridSpec { Columns = 6, Rows = rows } };
        // 全行に跨る縦母線R/S/Tを自由直線で引く。
        for (int i = 0; i < 3; i++)
            sheet.FreeLines.Add(new FreeLine { X1Mm = 30 + i * 9, Y1Mm = 20, X2Mm = 30 + i * 9, Y2Mm = 20 + rows * 9 });
        sheet.Elements.Add(new ElementInstance
        { Kind = ElementKind.Breaker3P, Pos = new GridPos(2, 1), CellWidth = 3 });

        var dr = new DiagramRenderer();
        var rec = new NullRenderer();
        var ex = Record.Exception(() =>
        {
            for (int page = 0; page < DiagramRenderer.PageCount(sheet); page++)
                dr.Render(rec, sheet, enableBorder: true,
                          pageRowStart: page * DiagramRenderer.RowsPerPage,
                          pageRowCount: DiagramRenderer.RowsPerPage);
        });
        Assert.Null(ex);
        Assert.Equal(0, rec.ClipDepth);   // PushClip/PopClip が全ページで釣り合う
    }

    // 何も記録しないテスト用 IRenderer（描画呼び出しが例外を投げないことの確認用）。
    private sealed class NullRenderer : IRenderer
    {
        public int ClipDepth { get; private set; }
        public void PushTransform(double tx, double ty, double scale = 1.0) { }
        public void PopTransform() { }
        public void PushClip(Rect2D r) => ClipDepth++;
        public void PopClip() => ClipDepth--;
        public void DrawLine(Point2D a, Point2D b, StrokeStyle s) { }
        public void DrawPolyline(ReadOnlySpan<Point2D> p, StrokeStyle s) { }
        public void DrawRectangle(Rect2D r, StrokeStyle s) { }
        public void FillRectangle(Rect2D r, Color c) { }
        public void DrawCircle(Point2D c, double r, StrokeStyle s) { }
        public void FillCircle(Point2D c, double r, Color col) { }
        public void DrawEllipse(Point2D c, double rx, double ry, StrokeStyle s) { }
        public void DrawArc(Point2D c, double r, double sd, double sw, StrokeStyle s) { }
        public void DrawText(string t, Point2D p, TextStyle s) { }
        public Size2D MeasureText(string t, TextStyle s) => new(t.Length * s.FontSizeMm * 0.5, s.FontSizeMm);
    }
}
