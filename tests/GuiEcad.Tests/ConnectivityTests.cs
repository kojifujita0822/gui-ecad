using GuiEcad.Model;
using GuiEcad.Simulation;
using Xunit;

namespace GuiEcad.Tests;

public class ConnectivityTests
{
    private static ElementInstance El(ElementKind kind, int row, int col, string? device = null, int width = 1)
        => new() { Kind = kind, Pos = new GridPos(row, col), CellWidth = width, DeviceName = device };

    private static (Net A, Net B) NetsOf(Netlist nl, string device)
    {
        var c = nl.Components.Find(x => x.DeviceName == device)!;
        return (nl.Nets[c.NetA], nl.Nets[c.NetB]);
    }

    [Fact]
    public void CompleteCircuit_HasNoDanglingNets()
    {
        // L —[NO A]—[NO B]— (Coil C) — R （全ネットが両側で結線）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 1, "B"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "C"));

        var nl = NetlistBuilder.Build(sheet);
        var report = ConnectivityChecker.Check(nl);

        Assert.Empty(report.DanglingNets);
        Assert.Equal(WireStatus.Connected, report.Of(NetsOf(nl, "A").B.Id)); // A-B 間 = 青
        Assert.Equal(WireStatus.Connected, report.Of(NetsOf(nl, "B").B.Id)); // B-Coil 間 = 青
    }

    [Fact]
    public void TrailingElement_BeforeRightRail_StillEnergizes()
    {
        // 末尾コイルが右母線(境界5)手前(col2・右境界3)。描画は母線まで横線が伸びる＝電気的にも母線へ繋がるべき。
        var sheet = new Sheet { Grid = new GridSpec { Columns = 5 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 2, "C"));

        var nl = NetlistBuilder.Build(sheet);
        var on = new Evaluator(nl).Evaluate(new SimState { Energized = { ["A"] = true } });
        Assert.True(on.State.Energized["C"]);   // A 導通でコイル C が励磁すべき
    }

    [Fact]
    public void HalfCellConnector_BranchesFromWireSegment_SelfHold()
    {
        // 0.5 セル位置（空きセル中央）の縦コネクタが横線セグメントから分岐し自己保持を成す。
        // row0: L —[ST]—(空き col1)—(CR coil)— R / row1: L —[CR 接点]→ col1.5 の縦コネクタで ST 右の横線へ合流
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "ST"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 2, "CR"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "CR"));
        sheet.Connectors.Add(new VerticalConnector { Column = 1.5, TopRow = 0, BottomRow = 1 });

        var nl = NetlistBuilder.Build(sheet);

        // ST 押下で CR 励磁（横線セグメント経由で導通）。PushButtonNO は Inputs で制御。
        var on = new Evaluator(nl).Evaluate(new SimState { Inputs = { ["ST"] = true } });
        Assert.True(on.State.Energized["CR"]);
        // ST を離しても CR 接点（0.5 位置の分岐）で自己保持
        var hold = new Evaluator(nl).Evaluate(new SimState { Energized = { ["CR"] = true } });
        Assert.True(hold.State.Energized["CR"]);
    }

    [Fact]
    public void DanglingStub_IsFlaggedBlack()
    {
        // L —[NO A]—(net1: 行き止まり)   …  別に L … (Coil C)—R（左側は未結線）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 6 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));   // A.R = どこにもつながらない
        sheet.Elements.Add(El(ElementKind.Coil, 2, 5, "C"));        // C.L = どこにもつながらない

        var nl = NetlistBuilder.Build(sheet);
        var report = ConnectivityChecker.Check(nl);

        Assert.Equal(WireStatus.Dangling, report.Of(NetsOf(nl, "A").B.Id)); // A 右の宙ぶらり = 黒（行内に右隣要素なし）
        Assert.Equal(WireStatus.Connected, report.Of(NetsOf(nl, "C").A.Id)); // C 左は左母線直結（行内最左要素は常に左母線に接続）
    }

    [Fact]
    public void Rails_AreAlwaysConnected()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 2, "C"));

        var nl = NetlistBuilder.Build(sheet);
        var report = ConnectivityChecker.Check(nl);

        Assert.Equal(WireStatus.Connected, report.Of(nl.LeftRailNet));
        Assert.Equal(WireStatus.Connected, report.Of(nl.RightRailNet));
    }
}
