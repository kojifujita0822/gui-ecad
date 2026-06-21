using GuiEcad.Model;
using GuiEcad.Simulation;
using Xunit;

namespace GuiEcad.Tests;

/// <summary>電気設計の実務回路パターン特化テスト。インターロック・短絡・タイマシーケンス等。</summary>
public class ElectricalPatternTests
{
    private static ElementInstance El(ElementKind kind, int row, int col, string? device = null, int width = 1)
        => new() { Kind = kind, Pos = new GridPos(row, col), CellWidth = width, DeviceName = device };

    private static EvalResult Run(Sheet sheet, SimState state)
        => new Evaluator(NetlistBuilder.Build(sheet)).Evaluate(state);

    // Row0: L-[FOR_PB(NO)]-[REV_NC]-(FOR coil)-R
    // Row1: L-[REV_PB(NO)]-[FOR_NC]-(REV coil)-R
    private static Sheet MakeInterlockSheet()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "FOR_PB"));
        sheet.Elements.Add(El(ElementKind.ContactNC,    0, 1, "REV"));
        sheet.Elements.Add(El(ElementKind.Coil,         0, 3, "FOR"));
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 1, 0, "REV_PB"));
        sheet.Elements.Add(El(ElementKind.ContactNC,    1, 1, "FOR"));
        sheet.Elements.Add(El(ElementKind.Coil,         1, 3, "REV"));
        return sheet;
    }

    // A-1: 正転先行インターロック — FOR励磁中にREV投入しても不励磁
    [Fact]
    public void Interlock_Forward_Blocks_Reverse()
    {
        var ts = new TestSession(MakeInterlockSheet());
        ts.SetInput("FOR_PB", true);
        Assert.True(ts.IsEnergized("FOR"));
        Assert.False(ts.IsEnergized("REV"));

        ts.SetInput("REV_PB", true); // FOR励磁中にREV投入
        Assert.True(ts.IsEnergized("FOR"));   // FOR継続
        Assert.False(ts.IsEnergized("REV")); // REVはb接点で阻止
    }

    // A-1b: 逆転先行インターロック — 同様に対称動作
    [Fact]
    public void Interlock_Reverse_Blocks_Forward()
    {
        var ts = new TestSession(MakeInterlockSheet());
        ts.SetInput("REV_PB", true);
        Assert.True(ts.IsEnergized("REV"));
        Assert.False(ts.IsEnergized("FOR"));

        ts.SetInput("FOR_PB", true);
        Assert.True(ts.IsEnergized("REV"));
        Assert.False(ts.IsEnergized("FOR"));
    }

    // A-2: 同時投入 → 互いのb接点が阻止しあい不動点なし → Cyclic発振
    [Fact]
    public void Interlock_Simultaneous_Press_Causes_Cyclic_Oscillation()
    {
        var sheet = MakeInterlockSheet();
        var state = new SimState { Inputs = { ["FOR_PB"] = true, ["REV_PB"] = true } };
        var result = new Evaluator(NetlistBuilder.Build(sheet)).Evaluate(state);
        Assert.Equal(EvalStatus.Cyclic, result.Status);
    }

    // A-3: 3段並列 — 3行中いずれか1つONで出力
    [Fact]
    public void ThreeLevelParallel_AnyBranchEnergizesOutput()
    {
        // Row0: L-[A]-+--- (Coil OUT)--R
        // Row1: L-[B]-|  VC(col=1, 0..1)
        // Row2: L-[C]-+  VC(col=1, 0..2)
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil,      0, 3, "OUT"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "B"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 2, 0, "C"));
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 1 });
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 2 });

        Assert.True(Run(sheet, new SimState { Energized = { ["A"] = true } }).State.Energized["OUT"]);
        Assert.True(Run(sheet, new SimState { Energized = { ["B"] = true } }).State.Energized["OUT"]);
        Assert.True(Run(sheet, new SimState { Energized = { ["C"] = true } }).State.Energized["OUT"]);
        Assert.False(Run(sheet, new SimState()).State.Energized.TryGetValue("OUT", out var all) && all);
    }

    // A-4: 短絡検出 — 負荷を介さず両母線を接点でブリッジ → ShortCircuitNets非空
    [Fact]
    public void ShortCircuit_ContactsWithoutLoad_Detected()
    {
        // L-[A(NO,col0)]-[B(NO,col1)]-R  (columns=2, コイルなし=短絡)
        var sheet = new Sheet { Grid = new GridSpec { Columns = 2 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 1, "B"));

        var nl = NetlistBuilder.Build(sheet);
        var result = new Evaluator(nl).Evaluate(new SimState { Energized = { ["A"] = true, ["B"] = true } });

        Assert.Equal(EvalStatus.Converged, result.Status);
        Assert.NotEmpty(result.ShortCircuitNets);
    }

    // A-4b: 正常回路（接点+コイル）では短絡なし
    [Fact]
    public void NormalCircuit_NoShortCircuit()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil,      0, 2, "C"));

        var result = Run(sheet, new SimState { Energized = { ["A"] = true } });

        Assert.Equal(EvalStatus.Converged, result.Status);
        Assert.Empty(result.ShortCircuitNets);
    }

    // A-5: タイマシーケンス — 起動→タイマ励磁→限時後のみ出力
    [Fact]
    public void TimerSequence_OutputActivatesOnlyAfterTimeout()
    {
        // Row0: L-[PB(NO)]-(MC coil)-R
        // Row1: L-[MC(NO)]-(TLR1 timer setpoint=3s)-R
        // Row2: L-[TLR1(TimerContactNO)]-(OUT coil)-R
        var timer = new ElementInstance
        {
            Kind = ElementKind.Timer,
            Pos = new GridPos(1, 1),
            DeviceName = "TLR1",
        };
        timer.Params["Setpoint"] = "3";

        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO,   0, 0, "PB"));
        sheet.Elements.Add(El(ElementKind.Coil,           0, 2, "MC"));
        sheet.Elements.Add(El(ElementKind.ContactNO,      1, 0, "MC"));
        sheet.Elements.Add(timer);
        sheet.Elements.Add(El(ElementKind.TimerContactNO, 2, 0, "TLR1"));
        sheet.Elements.Add(El(ElementKind.Coil,           2, 2, "OUT"));

        var ts = new TestSession(sheet);
        ts.SetInput("PB", true);

        Assert.True(ts.IsEnergized("MC"));
        Assert.True(ts.IsEnergized("TLR1")); // タイマコイル励磁
        Assert.False(ts.IsEnergized("OUT")); // 限時前

        ts.Tick(2.0);
        Assert.False(ts.IsEnergized("OUT")); // 2s未達

        ts.Tick(1.0);
        Assert.True(ts.IsEnergized("OUT")); // 3s到達

        ts.SetInput("PB", false);
        Assert.False(ts.IsEnergized("MC"));
        Assert.False(ts.IsEnergized("OUT")); // リセット
    }

    // A-6: 同名コイル2個 — いずれかの回路が通電すれば励磁（OR集約）
    [Fact]
    public void DualCoilSameName_OrAggregation()
    {
        // Row0: L-[A(NO)]-(CR1 coil)-R
        // Row1: L-[B(NO)]-(CR1 coil)-R  同一機器名
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil,      0, 2, "CR1"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "B"));
        sheet.Elements.Add(El(ElementKind.Coil,      1, 2, "CR1"));

        var nl = NetlistBuilder.Build(sheet);

        var onlyA = new Evaluator(nl).Evaluate(new SimState { Energized = { ["A"] = true } }).State;
        Assert.True(onlyA.Energized["CR1"]);

        var onlyB = new Evaluator(nl).Evaluate(new SimState { Energized = { ["B"] = true } }).State;
        Assert.True(onlyB.Energized["CR1"]);

        var neither = new Evaluator(nl).Evaluate(new SimState()).State;
        Assert.False(neither.Energized.TryGetValue("CR1", out var v) && v);
    }

    // A-7: 10直列接点 — 全ON→励磁、1つでもOFF→非励磁。内部ネット10個。
    [Fact]
    public void DeepSeries_TenContacts_AllRequiredForEnergization()
    {
        // L-[C0]-[C1]-...-[C9]-(Coil OUT)-R  (columns=11)
        var sheet = new Sheet { Grid = new GridSpec { Columns = 11 } };
        for (int i = 0; i < 10; i++)
            sheet.Elements.Add(El(ElementKind.ContactNO, 0, i, $"C{i}"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 10, "OUT"));

        var nl = NetlistBuilder.Build(sheet);

        var allOn = new SimState();
        for (int i = 0; i < 10; i++) allOn.Energized[$"C{i}"] = true;
        Assert.True(new Evaluator(nl).Evaluate(allOn).State.Energized["OUT"]);

        // C5のみOFF → 非励磁
        var c5Off = new SimState();
        for (int i = 0; i < 10; i++) c5Off.Energized[$"C{i}"] = (i != 5);
        Assert.False(new Evaluator(nl).Evaluate(c5Off).State.Energized.TryGetValue("OUT", out var v) && v);

        // 内部ネットは10個（境界 1..10）
        Assert.Equal(10, nl.Nets.Count(n => !n.IsRail));
    }

    // A-8: Counter種別 — Componentを生成しない（記号のみ・非シミュレート）
    [Fact]
    public void Counter_IsNonSimulated_NoComponent()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(new ElementInstance
        {
            Kind = ElementKind.Counter,
            Pos = new GridPos(0, 1),
            DeviceName = "CTR1",
        });

        var nl = NetlistBuilder.Build(sheet);

        Assert.False(ElementCatalog.CreatesComponent(ElementKind.Counter));
        Assert.DoesNotContain(nl.Components, c => c.DeviceName == "CTR1");
    }
}
