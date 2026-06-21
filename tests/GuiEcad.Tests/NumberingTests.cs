using GuiEcad.Model;
using GuiEcad.Simulation;
using Xunit;
using static GuiEcad.Tests.TestHelper;

namespace GuiEcad.Tests;

public class NumberingTests
{
    // 指定デバイスの要素の左右ネットを返す
    private static (Net A, Net B) NetsOf(Netlist nl, string device)
    {
        var c = nl.Components.Find(x => x.DeviceName == device)!;
        return (nl.Nets[c.NetA], nl.Nets[c.NetB]);
    }

    [Fact]
    public void Rails_AreNamedNotNumbered()
    {
        // L —[NO A]—[NO B]— (Coil C) — R
        var sheet = new Sheet
        {
            Grid = new GridSpec { Columns = 4 },
            Bus = new BusConfig { LeftName = "R200", RightName = "S200" },
        };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 1, "B"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "C"));
        var nl = NetlistBuilder.Build(sheet);

        Assert.Equal("R200", nl.Nets[nl.LeftRailNet].Name);
        Assert.Equal("S200", nl.Nets[nl.RightRailNet].Name);
        Assert.Equal(0, nl.Nets[nl.LeftRailNet].WireNumber);   // 母線は番号を持たない
        Assert.Equal(0, nl.Nets[nl.RightRailNet].WireNumber);
    }

    [Fact]
    public void InternalNets_NumberedInReadingOrder()
    {
        // L —[NO A]—(1)—[NO B]—(2)— (Coil C) — R
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 1, "B"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "C"));
        var nl = NetlistBuilder.Build(sheet);

        var a = NetsOf(nl, "A");
        var b = NetsOf(nl, "B");
        var c = NetsOf(nl, "C");

        Assert.Equal(1, a.B.WireNumber);          // A と B の間 = 1
        Assert.Equal(1, b.A.WireNumber);          // 同一ネット
        Assert.Equal(2, b.B.WireNumber);          // B と Coil の間 = 2（空セルを跨ぐ）
        Assert.Equal(2, c.A.WireNumber);
        Assert.True(a.A.IsRail && c.B.IsRail);     // 母線直結端は番号対象外
    }

    [Fact]
    public void ReadingOrder_MainLineBeforeBranch()
    {
        // 行0(主線): L —[A]—(1)—[B]— (Coil)…  行1(分岐): L —[D]—(?)—[E]→ 行0へ合流
        // 主線が上の行 → 主線の番号が分岐枝より先になることを確認
        var sheet = new Sheet { Grid = new GridSpec { Columns = 6 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 1, "B"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 5, "C"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "D"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 1, "E"));
        // 分岐枝(行1)の出力を行0の B の右（主線）へ縦コネクタで合流
        sheet.Connectors.Add(new VerticalConnector { Column = 2, TopRow = 0, BottomRow = 1 });

        var nl = NetlistBuilder.Build(sheet);
        int mainAB = NetsOf(nl, "A").B.WireNumber;   // 行0 主線 A-B 間
        int branchDE = NetsOf(nl, "D").B.WireNumber;  // 行1 分岐 D-E 間

        Assert.True(mainAB >= 1 && branchDE >= 1);
        Assert.True(mainAB < branchDE);               // 主線が分岐枝より先
    }

    [Fact]
    public void MergingBranch_SharesNetNumber()
    {
        // 並列OR: 行0 [A]→Coil、行1 [B] を列1で合流。A右・B右は同一ネット＝同番号。
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "C"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "B"));
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 1 });

        var nl = NetlistBuilder.Build(sheet);
        Assert.Equal(NetsOf(nl, "A").B.Id, NetsOf(nl, "B").B.Id);            // 同一ネット
        Assert.Equal(NetsOf(nl, "A").B.WireNumber, NetsOf(nl, "B").B.WireNumber); // 番号共有
    }

    [Fact]
    public void PassthroughTerminal_EntranceAndExit_ShareWireNumber()
    {
        // L —[NO CR1]— TB1 — (Coil SOL) — TB2 — R
        // 端子台は通過接続: 入口/出口は同一ネット＝同一線番。末尾 TB2 は右母線と同一ネット。
        var sheet = new Sheet { Grid = new GridSpec { Columns = 6 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "CR1"));
        sheet.Elements.Add(El(ElementKind.Terminal,  0, 2, "TB1"));
        sheet.Elements.Add(El(ElementKind.Coil,      0, 3, "SOL"));
        sheet.Elements.Add(El(ElementKind.Terminal,  0, 4, "TB2"));
        var nl = NetlistBuilder.Build(sheet);

        var cr1 = NetsOf(nl, "CR1");
        var tb1 = NetsOf(nl, "TB1");
        var sol = NetsOf(nl, "SOL");
        var tb2 = NetsOf(nl, "TB2");

        // TB1: 入口=出口（同一ネット・同一線番）
        Assert.Equal(tb1.A.Id, tb1.B.Id);
        Assert.Equal(tb1.A.WireNumber, tb1.B.WireNumber);
        // CR1 右 — TB1 — SOL 左 は連続した同一ネット
        Assert.Equal(cr1.B.Id, tb1.A.Id);
        Assert.Equal(tb1.B.Id, sol.A.Id);
        // 末尾 TB2 は右母線ネットと同一（SOL 右側も右母線へ連続）
        Assert.Equal(nl.RightRailNet, tb2.A.Id);
        Assert.Equal(nl.RightRailNet, tb2.B.Id);
        Assert.Equal(nl.RightRailNet, sol.B.Id);
    }

    // D-1: 同一回路を複数回 Build しても線番が安定（冪等性）
    [Fact]
    public void WireNumbers_Stable_AcrossMultipleBuilds()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 1, "B"));
        sheet.Elements.Add(El(ElementKind.Coil,      0, 3, "C"));

        var nl1 = NetlistBuilder.Build(sheet);
        var nl2 = NetlistBuilder.Build(sheet);

        var a1 = nl1.Components.Find(x => x.DeviceName == "A")!;
        var a2 = nl2.Components.Find(x => x.DeviceName == "A")!;
        Assert.Equal(nl1.Nets[a1.NetB].WireNumber, nl2.Nets[a2.NetB].WireNumber);
    }

    // D-2: 空行（要素なし）を挟む場合、空行には回路番号が付かない
    [Fact]
    public void EmptyRow_Between_Elements_IsNotNumbered()
    {
        // Row0 と Row2 に要素。Row1 は空。
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil,      0, 3, "C1"));
        // Row1: 空（要素なし）
        sheet.Elements.Add(El(ElementKind.ContactNO, 2, 0, "B"));
        sheet.Elements.Add(El(ElementKind.Coil,      2, 3, "C2"));

        var doc = new LadderDocument();
        doc.Sheets.Add(sheet);
        CircuitNumberer.Number(doc);

        Assert.Equal(2, sheet.Lines.Count);
        Assert.Equal(0, sheet.Lines[0].Row);
        Assert.Equal(1, sheet.Lines[0].CircuitNumber);
        Assert.Equal(2, sheet.Lines[1].Row);
        Assert.Equal(2, sheet.Lines[1].CircuitNumber);
        Assert.DoesNotContain(sheet.Lines, l => l.Row == 1); // Row1 は採番されない
    }

    // D-3: 再採番で Lines が前回分をクリアして再生成（旧エントリ残留なし）
    [Fact]
    public void Renumber_ClearsPreviousLines_BeforeRebuilding()
    {
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil,      0, 3, "C1"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "B"));
        sheet.Elements.Add(El(ElementKind.Coil,      1, 3, "C2"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 2, 0, "D"));
        sheet.Elements.Add(El(ElementKind.Coil,      2, 3, "C3"));

        var doc = new LadderDocument();
        doc.Sheets.Add(sheet);
        CircuitNumberer.Number(doc); // 初回: 3回路
        Assert.Equal(3, sheet.Lines.Count);

        // Row1,2 の要素を削除して再採番
        sheet.Elements.RemoveAll(e => e.Pos.Row >= 1);
        CircuitNumberer.Number(doc);

        Assert.Single(sheet.Lines); // 旧Linesは完全にクリアされ1件のみ
        Assert.Equal(1, sheet.Lines[0].CircuitNumber);
    }
}
