using GuiEcad.Model;
using GuiEcad.Simulation;
using Xunit;

namespace GuiEcad.Tests;

public class SimulationTests
{
    private static ElementInstance El(ElementKind kind, int row, int col, string? device = null, int width = 1)
        => new() { Kind = kind, Pos = new GridPos(row, col), CellWidth = width, DeviceName = device };

    private static EvalResult Run(Sheet sheet, SimState state)
        => new Evaluator(NetlistBuilder.Build(sheet)).Evaluate(state);

    [Fact]
    public void SeriesAnd_EnergizesOnlyWhenBothClosed()
    {
        // L —[NO A]—[NO B]— (Coil C) — R   （列0..3）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 1, "B"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "C"));

        Assert.False(Run(sheet, new SimState { Inputs = { ["A"] = true, ["B"] = false } }).State.Energized["C"]);
        Assert.False(Run(sheet, new SimState { Inputs = { ["A"] = false, ["B"] = true } }).State.Energized["C"]);
        Assert.True(Run(sheet, new SimState { Inputs = { ["A"] = true, ["B"] = true } }).State.Energized["C"]);
    }

    [Fact]
    public void ParallelOr_EnergizesWhenEitherClosed()
    {
        // 行0: L —[NO A]— (Coil C) — R    行1: L —[NO B]—（列1でコネクタにより A の右へ合流）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "C"));
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 1, 0, "B"));
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 1 });

        Assert.False(Run(sheet, new SimState { Inputs = { ["A"] = false, ["B"] = false } }).State.Energized["C"]);
        Assert.True(Run(sheet, new SimState { Inputs = { ["A"] = true, ["B"] = false } }).State.Energized["C"]);
        Assert.True(Run(sheet, new SimState { Inputs = { ["A"] = false, ["B"] = true } }).State.Energized["C"]);
    }

    [Fact]
    public void NormallyClosed_ConductsWhenNotPressed()
    {
        // L —[NC A]— (Coil C) — R
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNC, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 2, "C"));

        Assert.True(Run(sheet, new SimState()).State.Energized["C"]);                       // 未押下→導通
        Assert.False(Run(sheet, new SimState { Inputs = { ["A"] = true } }).State.Energized["C"]); // 押下→遮断
    }

    [Fact]
    public void SelfHold_LatchesAndStops()
    {
        // L —[ST]—[SP(NC)]— (Coil R1) — R    ＋ R1 NO接点を ST に並列（自己保持）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 5 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "ST"));   // 起動
        sheet.Elements.Add(El(ElementKind.PushButtonNC, 0, 2, "SP"));   // 停止（NC）
        sheet.Elements.Add(El(ElementKind.Coil, 0, 4, "R1"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "R1"));      // 自己保持接点
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 1 });

        // 起動押下 → 励磁
        var afterStart = Run(sheet, new SimState { Inputs = { ["ST"] = true } });
        Assert.True(afterStart.State.Energized["R1"]);

        // 起動を離す → 自己保持で励磁継続
        var held = Run(sheet, new SimState
        {
            Inputs = { ["ST"] = false },
            Energized = new(afterStart.State.Energized),
        });
        Assert.True(held.State.Energized["R1"]);

        // 停止押下 → 消磁
        var stopped = Run(sheet, new SimState
        {
            Inputs = { ["SP"] = true },
            Energized = new(held.State.Energized),
        });
        Assert.False(stopped.State.Energized["R1"]);
    }

    [Fact]
    public void Oscillation_IsDetected()
    {
        // L —[NC R1]— (Coil R1) — R   （自身のNC接点で自身を駆動 → 発振）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(El(ElementKind.ContactNC, 0, 0, "R1"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 2, "R1"));

        var result = Run(sheet, new SimState());
        // NC自己フィードバックは周期2の周期的振動（Cyclic）。不定発振（Diverging）ではない。
        Assert.Equal(EvalStatus.Cyclic, result.Status);
        Assert.Equal(2, result.CycleLength);
    }

    [Fact]
    public void NormalSelfHold_IsConverged_NotCyclic()
    {
        // 自己保持回路が正常収束し、Cyclic にならないことを確認（P5 の誤検知防止）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 5 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "ST"));
        sheet.Elements.Add(El(ElementKind.PushButtonNC, 0, 2, "SP"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 4, "R1"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "R1"));
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 1 });

        // 起動後の励磁状態で収束するか確認
        var result = Run(sheet, new SimState
        {
            Inputs = { ["ST"] = false },
            Energized = { ["R1"] = true },
        });
        Assert.Equal(EvalStatus.Converged, result.Status);
    }

    [Fact]
    public void CycleLength_ReflectsOscillationPeriod()
    {
        // 周期2の発振（NC自己フィードバック）と別回路が混在しても周期長を正確に返す
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(El(ElementKind.ContactNC, 0, 0, "R1"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 2, "R1"));

        var result = new Evaluator(NetlistBuilder.Build(sheet)) { MaxIterations = 50 }.Evaluate(new SimState());
        Assert.Equal(EvalStatus.Cyclic, result.Status);
        Assert.True(result.CycleLength >= 2);
    }

    [Fact]
    public void TrailingTerminal_CoilBeforeIt_Energizes()
    {
        // L —[NO A]— TB1 — (Coil C) — TB2 — R   末尾が端子台のとき右母線へ自動接続される
        var sheet = new Sheet { Grid = new GridSpec { Columns = 6 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Terminal,    0, 1, "TB1"));
        sheet.Elements.Add(El(ElementKind.Coil,        0, 3, "C"));
        sheet.Elements.Add(El(ElementKind.Terminal,    0, 4, "TB2"));

        Assert.True(Run(sheet, new SimState { Inputs = { ["A"] = true } }).State.Energized["C"]);
        Assert.False(Run(sheet, new SimState { Inputs = { ["A"] = false } }).State.Energized.TryGetValue("C", out var v) && v);
    }

    [Fact]
    public void CoilWithNoContacts_IsEnergized()
    {
        // L ——————————— (Coil C) — R （接点なし・コイル直結）
        // 左母線→最左要素の左ポートは常に接続されるべき。
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "C"));

        var result = Run(sheet, new SimState());
        Assert.True(result.State.Energized.TryGetValue("C", out var v) && v);
    }

    [Fact]
    public void CoilNotAtColumn0_LeftBusStillConnects()
    {
        // L —[NO A(col2)]— (Coil C(col5)) — R   （接点が列0以外から始まる行）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 8 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 2, "A"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 7, "C"));

        // A=ON → C励磁。A=OFF → C非励磁。
        Assert.True(Run(sheet, new SimState { Inputs = { ["A"] = true } }).State.Energized["C"]);
        Assert.False(Run(sheet, new SimState { Inputs = { ["A"] = false } }).State.Energized.TryGetValue("C", out var v) && v);
    }

    [Fact]
    public void ShortCircuit_DetectedWhenContactBridgesBothRails()
    {
        // L —[NO A]— R （列0..1、接点が負荷を介さず両母線を直結＝短絡）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 1 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));

        // A=ON → 負荷非経由で左右母線が導通＝短絡を検出
        Assert.NotEmpty(Run(sheet, new SimState { Inputs = { ["A"] = true } }).ShortCircuitNets);
        // A=OFF → 導通せず短絡なし
        Assert.Empty(Run(sheet, new SimState { Inputs = { ["A"] = false } }).ShortCircuitNets);
    }

    [Fact]
    public void NormalLoadCircuit_IsNotFlaggedAsShort()
    {
        // 負荷(コイル)を介して通電する正常回路。保持回路の戻り線(自己保持接点)があっても短絡誤検出しない。
        var sheet = new Sheet { Grid = new GridSpec { Columns = 5 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "ST"));
        sheet.Elements.Add(El(ElementKind.PushButtonNC, 0, 2, "SP"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 4, "R1"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "R1"));   // 自己保持接点（戻り線）
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 1 });

        // 自己保持で励磁中でも、負荷を介す正常回路なので短絡ではない
        var held = Run(sheet, new SimState { Inputs = { ["ST"] = false }, Energized = { ["R1"] = true } });
        Assert.Equal(EvalStatus.Converged, held.Status);
        Assert.Empty(held.ShortCircuitNets);
    }

    [Fact]
    public void PortlessPart_DoesNotCrashNetlistBuild()
    {
        // ポート0個の自作パーツ（接続点なし図形）が混在しても NetlistBuilder が落ちず、他要素は正常評価される。
        var parts = new PartLibrary();
        parts.ById["deco"] = new PartDefinition { Id = "deco", Name = "装飾", Ports = new() };
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(new ElementInstance { Kind = ElementKind.ContactNO, Pos = new GridPos(0, 1), PartId = "deco" });
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 1, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil, 1, 3, "C"));

        var net = NetlistBuilder.Build(sheet, parts);
        var result = new Evaluator(net).Evaluate(new SimState { Inputs = { ["A"] = true } });
        Assert.True(result.State.Energized["C"]);
    }
}
