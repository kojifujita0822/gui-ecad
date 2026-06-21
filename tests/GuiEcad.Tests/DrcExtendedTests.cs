using GuiEcad.Model;
using GuiEcad.Simulation;
using Xunit;
using static GuiEcad.Tests.TestHelper;

namespace GuiEcad.Tests;

/// <summary>DRC（設計ルール検査）拡張テスト。全メソッド複合・エッジケース・カスタムパーツ対応。</summary>
public class DrcExtendedTests
{
    private static LadderDocument MakeDoc(params Sheet[] sheets)
    {
        var doc = new LadderDocument();
        doc.Sheets.AddRange(sheets);
        CircuitNumberer.Number(doc);
        return doc;
    }

    // B-1: 有効回路で全4DRCメソッドを実行 → 合算ゼロ件
    [Fact]
    public void AllFourDrc_OnValidCircuit_ProduceNoDiagnostics()
    {
        // Row0: L-[PB1(NO,input)]-(CR1 coil)-R
        // Row1: L-[CR1(NO,energized)]-(PL1 lamp)-R
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "PB1"));
        sheet.Elements.Add(El(ElementKind.Coil,         0, 3, "CR1"));
        sheet.Elements.Add(El(ElementKind.ContactNO,    1, 0, "CR1"));
        sheet.Elements.Add(El(ElementKind.Lamp,         1, 3, "PL1"));
        var doc = MakeDoc(sheet);
        var nl = NetlistBuilder.Build(sheet);

        var allDiags = new List<Diagnostic>();
        allDiags.AddRange(DesignRuleCheck.CheckCrossReference(doc));
        allDiags.AddRange(DesignRuleCheck.CheckDeviceTypeConsistency(doc));
        allDiags.AddRange(DesignRuleCheck.CheckVerticalCrossings(sheet, nl));
        allDiags.AddRange(DesignRuleCheck.CheckLoadReachability(sheet, nl));

        Assert.Empty(allDiags);
    }

    // B-2: 同名コイルが2行 → CoilWithoutContact の Locations に2エントリ
    [Fact]
    public void DualCoilSameName_BothCoilLocationsReported()
    {
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "CR1")); // 回路番号1
        sheet.Elements.Add(El(ElementKind.Coil, 1, 3, "CR1")); // 回路番号2
        var doc = MakeDoc(sheet);

        var diags = DesignRuleCheck.CheckCrossReference(doc);

        var d = Assert.Single(diags);
        Assert.Equal(DesignRuleCheck.CoilWithoutContact, d.Code);
        Assert.Equal("CR1", d.DeviceName);
        Assert.Equal(2, d.Locations.Count); // 両コイルの所在を報告
    }

    // B-3: 空ドキュメントで全DRCメソッドを実行 → 例外なし・診断0件
    [Fact]
    public void AllFourDrc_OnEmptyDocument_NoExceptionsAndNoDiagnostics()
    {
        var emptySheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        var doc = MakeDoc(emptySheet);
        var nl = NetlistBuilder.Build(emptySheet);

        Assert.Empty(DesignRuleCheck.CheckCrossReference(doc));
        Assert.Empty(DesignRuleCheck.CheckDeviceTypeConsistency(doc));
        Assert.Empty(DesignRuleCheck.CheckVerticalCrossings(emptySheet, nl));
        Assert.Empty(DesignRuleCheck.CheckLoadReachability(emptySheet, nl));
    }

    // B-4: 0.5列位置の縦コネクタが中間行を通過しても交差検出しない（設計上の意図）
    [Fact]
    public void HalfIntegerColumnConnector_CrossingNotDetectedByDesign()
    {
        // VC@col=1.5, top=0, bottom=2 が中間行1の要素を通過する。
        // NetlistBuilder は非整数列のVCについて交差チェックを意図的にスキップする。
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 1, "A")); // row0 boundary 1..2
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 1, "B")); // row1 boundary 1..2 (VC通過)
        sheet.Elements.Add(El(ElementKind.ContactNO, 2, 1, "C")); // row2 boundary 1..2
        sheet.Connectors.Add(new VerticalConnector { Column = 1.5, TopRow = 0, BottomRow = 2 });

        var nl = NetlistBuilder.Build(sheet);

        Assert.Empty(nl.VerticalCrossings);
        Assert.Empty(DesignRuleCheck.CheckVerticalCrossings(sheet, nl));
    }

    // B-5: カスタムパーツ（Role=Coil）がDRCでコイルとして正しく扱われる
    [Fact]
    public void CustomPartCoil_InDrc_TreatedAsRelayCoil()
    {
        var lib = new PartLibrary();
        lib.ById["myCoil"] = new PartDefinition
        {
            Id = "myCoil",
            Role = PartRole.Coil,
            Ports = { new PortDef("L", 0, 0), new PortDef("R", 0, 1) },
        };

        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        // カスタムコイル CR99
        sheet.Elements.Add(new ElementInstance
        {
            PartId = "myCoil", Pos = new GridPos(0, 3), DeviceName = "CR99"
        });
        // 対応する組込みContactNO
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "CR99"));
        var doc = new LadderDocument();
        doc.Sheets.Add(sheet);
        CircuitNumberer.Number(doc);

        var diags = DesignRuleCheck.CheckCrossReference(doc, lib);

        // コイルと接点が対応 → 警告なし
        Assert.Empty(diags);
    }

    // B-6: Counter要素（非シミュレート）はDRCで無視される
    [Fact]
    public void CounterElement_InDrc_ProducesNoDiagnostic()
    {
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(new ElementInstance
        {
            Kind = ElementKind.Counter,
            Pos = new GridPos(0, 1),
            DeviceName = "CTR1",
        });
        var doc = MakeDoc(sheet);

        Assert.Empty(DesignRuleCheck.CheckCrossReference(doc));
        Assert.Empty(DesignRuleCheck.CheckDeviceTypeConsistency(doc));
    }
}
