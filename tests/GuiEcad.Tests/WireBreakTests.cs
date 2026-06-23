using GuiEcad.Model;
using GuiEcad.Persistence;
using GuiEcad.Simulation;
using Xunit;
using static GuiEcad.Tests.TestHelper;

namespace GuiEcad.Tests;

/// <summary>
/// 配線分断（WireBreak）の電気的効果。
/// 問題2 の短絡（直列接点を挟む2本の縦コネクタが下行の連続ネットに着地して接点を飛び越す）が、
/// 下行に分断を1つ置くと解消し、左右が別ネット・別線番になることを検証する。
/// 回路（Columns=5）:
///   Row0: L—[A]— ●1.5 —[M(直列接点)]— ●3.5 —(C1 コイル)—R
///   Row1: L—[S]——————(空セル連続)——————(X ランプ)—R
///   縦コネクタ: 1.5(M左) と 3.5(M右) を Row0↔Row1 で接続。
/// 分断なし: 両コネクタが Row1 連続ネットに着地 → M の左右が同一ネット（短絡）。
/// Row1 の 2.5 に分断: Row1 が左右に割れ、M の左右が別ネットになる。
/// </summary>
public class WireBreakTests
{
    private static Sheet MakeShortCircuitSheet(bool withBreak)
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 5 } };
        // Row0: 直列接点 M を挟んでコイル C1 へ
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.ContactNC, 0, 2, "M"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 4, "C1"));
        // Row1: 接点 S と負荷ランプ X（間は空セル＝自動横配線で連続）
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "S"));
        sheet.Elements.Add(El(ElementKind.Lamp, 1, 4, "X"));
        // M の左右を Row1 へ分岐する2本の縦コネクタ（セル中央 1.5 / 3.5）
        sheet.Connectors.Add(new VerticalConnector { Column = 1.5, TopRow = 0, BottomRow = 1 });
        sheet.Connectors.Add(new VerticalConnector { Column = 3.5, TopRow = 0, BottomRow = 1 });
        if (withBreak)
            sheet.WireBreaks.Add(new WireBreak { Row = 1, Boundary = 2.5 });
        return sheet;
    }

    private static (int A, int B) MidNets(Netlist nl)
    {
        var c = nl.Components.First(x => x.DeviceName == "M");
        return (c.NetA, c.NetB);
    }

    [Fact]
    public void WithoutBreak_SeriesContactIsShorted()
    {
        var nl = NetlistBuilder.Build(MakeShortCircuitSheet(withBreak: false));
        var (a, b) = MidNets(nl);
        Assert.Equal(a, b);   // 直列接点 M の左右が同一ネット＝短絡（飛び越し）
    }

    [Fact]
    public void WithBreak_SeriesContactSeparatesIntoTwoNets()
    {
        var nl = NetlistBuilder.Build(MakeShortCircuitSheet(withBreak: true));
        var (a, b) = MidNets(nl);
        Assert.NotEqual(a, b);   // 左右が別ネット＝短絡解消
    }

    [Fact]
    public void WithBreak_AssignsDistinctWireNumbersToEachSide()
    {
        var nl = NetlistBuilder.Build(MakeShortCircuitSheet(withBreak: true));
        var (a, b) = MidNets(nl);
        int wa = nl.Nets[a].WireNumber, wb = nl.Nets[b].WireNumber;
        Assert.True(wa > 0 && wb > 0);   // どちらも内部ネット（母線でない）
        Assert.NotEqual(wa, wb);         // 自動採番が左右で 1 / 2 に分かれる
    }

    // 行の先頭要素が母線から離れ、その左に縦コネクタ(分岐源)がある場合、母線へ繋いではいけない
    // （描画 LeftTerminator と一致）。繋ぐと母線短絡で「テストONだけで全通電」になる回帰を防ぐ。
    [Fact]
    public void BranchFedCoilOnOwnRow_NotShortedToLeftRail()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 5 } };
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "K"));        // 行0: コイルのみ（col3＝母線から離れる）
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "X"));   // 行1: 接点（母線から）
        sheet.Connectors.Add(new VerticalConnector { Column = 2, TopRow = 0, BottomRow = 1 }); // X.R → K.L

        var nl = NetlistBuilder.Build(sheet);

        // X開 → K非通電（母線間が短絡していない）
        Assert.False(new Evaluator(nl).Evaluate(new SimState()).State.Energized.GetValueOrDefault("K"));
        // X閉(強制ON) → 分岐経由で K 通電
        Assert.True(new Evaluator(nl).Evaluate(
            new SimState { Inputs = { ["X"] = true } }).State.Energized.GetValueOrDefault("K"));
    }

    [Fact]
    public void WireBreak_RoundTripsThroughPersistence()
    {
        var doc = new LadderDocument();
        doc.Sheets.Add(MakeShortCircuitSheet(withBreak: true));

        var back = GcadSerializer.Deserialize(GcadSerializer.Serialize(doc));

        var breaks = back.Sheets[0].WireBreaks;
        Assert.Single(breaks);
        Assert.Equal(1, breaks[0].Row);
        Assert.Equal(2.5, breaks[0].Boundary);
    }
}
