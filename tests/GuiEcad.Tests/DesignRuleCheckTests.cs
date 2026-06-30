using GuiEcad.Model;
using GuiEcad.Simulation;
using Xunit;
using static GuiEcad.Tests.TestHelper;

namespace GuiEcad.Tests;

public sealed class DesignRuleCheckTests
{
    private static LadderDocument MakeDoc(params Sheet[] sheets)
    {
        var doc = new LadderDocument();
        doc.Sheets.AddRange(sheets);
        CircuitNumberer.Number(doc);
        return doc;
    }

    private static Sheet SheetWith(params ElementInstance[] elems)
    {
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.AddRange(elems);
        return sheet;
    }

    [Fact]
    public void Relay_With_Coil_And_Contact_HasNoDiagnostic()
    {
        var doc = MakeDoc(SheetWith(
            El(ElementKind.Coil, 0, 3, "CR1"),
            El(ElementKind.ContactNO, 1, 0, "CR1")));

        var diags = DesignRuleCheck.CheckCrossReference(doc);

        Assert.Empty(diags);
    }

    [Fact]
    public void Contact_WithoutCoil_IsFlaggedAsDriverUnknown()
    {
        var doc = MakeDoc(SheetWith(
            El(ElementKind.ContactNO, 0, 0, "CR9"),
            El(ElementKind.ContactNC, 1, 0, "CR9")));

        var diags = DesignRuleCheck.CheckCrossReference(doc);

        var d = Assert.Single(diags);
        Assert.Equal(DesignRuleCheck.ContactWithoutCoil, d.Code);
        Assert.Equal("CR9", d.DeviceName);
        Assert.Equal(2, d.Locations.Count); // 両接点の所在を報告
    }

    [Fact]
    public void Coil_WithoutContact_IsFlaggedAsDeadRelay()
    {
        var doc = MakeDoc(SheetWith(El(ElementKind.Coil, 0, 3, "CR5")));

        var diags = DesignRuleCheck.CheckCrossReference(doc);

        var d = Assert.Single(diags);
        Assert.Equal(DesignRuleCheck.CoilWithoutContact, d.Code);
        Assert.Equal("CR5", d.DeviceName);
    }

    [Fact]
    public void Coil_With_FiveContacts_IsFlaggedAsTooManyContacts()
    {
        // リレーは構造上 4 接点まで。コイル1個に対し接点5個は機器選定ミスの可能性 → 警告。
        var doc = MakeDoc(SheetWith(
            El(ElementKind.Coil, 0, 3, "CR2"),
            El(ElementKind.ContactNO, 0, 0, "CR2"),
            El(ElementKind.ContactNO, 1, 0, "CR2"),
            El(ElementKind.ContactNO, 2, 0, "CR2"),
            El(ElementKind.ContactNO, 3, 0, "CR2"),
            El(ElementKind.ContactNO, 4, 0, "CR2")));

        var diags = DesignRuleCheck.CheckCrossReference(doc);

        var d = Assert.Single(diags);
        Assert.Equal(DesignRuleCheck.TooManyRelayContacts, d.Code);
        Assert.Equal("CR2", d.DeviceName);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(5, d.Locations.Count);
    }

    [Fact]
    public void Coil_With_FourContacts_IsNotFlagged()
    {
        // 上限ちょうど（4接点）は正常。
        var doc = MakeDoc(SheetWith(
            El(ElementKind.Coil, 0, 3, "CR3"),
            El(ElementKind.ContactNO, 0, 0, "CR3"),
            El(ElementKind.ContactNO, 1, 0, "CR3"),
            El(ElementKind.ContactNO, 2, 0, "CR3"),
            El(ElementKind.ContactNO, 3, 0, "CR3")));

        var diags = DesignRuleCheck.CheckCrossReference(doc);

        Assert.Empty(diags);
    }

    [Fact]
    public void InputControlledContact_WithoutCoil_IsNotFlagged()
    {
        // 押釦・非常停止・OL は外部入力駆動。同名コイルが無くて正常。
        // タイマ接点(TimerContactNO/NC)はタイマコイル(Timer)で駆動するため対象外。
        var doc = MakeDoc(SheetWith(
            El(ElementKind.PushButtonNO, 0, 0, "PB1"),
            El(ElementKind.EmergencyStop, 1, 0, "ES1"),
            El(ElementKind.ThermalOverload, 2, 0, "THR1")));

        var diags = DesignRuleCheck.CheckCrossReference(doc);

        Assert.Empty(diags);
    }

    [Fact]
    public void TimerContactWithTimerCoil_IsNotFlagged()
    {
        // タイマ接点 + タイマコイル（Timer）がセットで揃っていれば警告なし
        var doc = MakeDoc(SheetWith(
            El(ElementKind.TimerContactNO, 0, 0, "TLR1"),
            El(ElementKind.Timer, 1, 2, "TLR1")));

        var diags = DesignRuleCheck.CheckCrossReference(doc);

        Assert.Empty(diags);
    }

    [Fact]
    public void TimerContact_WithoutTimerCoil_IsFlaggedAsDriverUnknown()
    {
        // タイマ接点だけでタイマコイルが無い → 駆動元不明
        var doc = MakeDoc(SheetWith(
            El(ElementKind.TimerContactNO, 0, 0, "TLR1")));

        var diags = DesignRuleCheck.CheckCrossReference(doc);

        var d = Assert.Single(diags);
        Assert.Equal(DesignRuleCheck.ContactWithoutCoil, d.Code);
        Assert.Equal("TLR1", d.DeviceName);
    }

    [Fact]
    public void LampAlone_IsNotFlaggedAsDeadRelay()
    {
        // ランプは接点を持たない表示負荷。死にリレー扱いしない。
        var doc = MakeDoc(SheetWith(El(ElementKind.Lamp, 0, 3, "PL1")));

        var diags = DesignRuleCheck.CheckCrossReference(doc);

        Assert.Empty(diags);
    }

    [Fact]
    public void CoilAndContact_AcrossPages_AreMatched()
    {
        var sheet1 = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet1.Elements.Add(El(ElementKind.Coil, 0, 3, "CR2"));
        var sheet2 = new Sheet { PageNumber = 2, Grid = new GridSpec { Columns = 4 } };
        sheet2.Elements.Add(El(ElementKind.ContactNO, 0, 0, "CR2"));

        var doc = MakeDoc(sheet1, sheet2);

        var diags = DesignRuleCheck.CheckCrossReference(doc);

        Assert.Empty(diags); // ページをまたいでも対応がとれていれば警告なし
    }

    // ===== CheckDeviceTypeConsistency (P2/P6) =====

    [Fact]
    public void NormalRelay_HasNoTypeConflict()
    {
        // コイル + ContactNO は正常パターン（励磁系で統一）
        var doc = MakeDoc(SheetWith(
            El(ElementKind.Coil, 0, 3, "CR1"),
            El(ElementKind.ContactNO, 1, 0, "CR1"),
            El(ElementKind.ContactNC, 2, 0, "CR1")));

        var diags = DesignRuleCheck.CheckDeviceTypeConsistency(doc);

        Assert.Empty(diags);
    }

    [Fact]
    public void InputDevice_WithoutCoil_HasNoTypeConflict()
    {
        // 押釦のみ（コイル無し）は正常
        var doc = MakeDoc(SheetWith(
            El(ElementKind.PushButtonNO, 0, 0, "PB1"),
            El(ElementKind.PushButtonNC, 1, 0, "ES1"),
            El(ElementKind.TimerContactNO, 2, 0, "TLR1")));

        var diags = DesignRuleCheck.CheckDeviceTypeConsistency(doc);

        Assert.Empty(diags);
    }

    [Fact]
    public void SameDevice_EnergizedAndInput_IsFlaggedAsTypeConflict()
    {
        // P6: 同一名で ContactNO（励磁系）と PushButtonNO（入力系）が混在
        var doc = MakeDoc(SheetWith(
            El(ElementKind.ContactNO, 0, 0, "X1"),
            El(ElementKind.PushButtonNO, 1, 0, "X1")));

        var diags = DesignRuleCheck.CheckDeviceTypeConsistency(doc);

        var d = Assert.Single(diags);
        Assert.Equal(DesignRuleCheck.TypeConflictEnergizedVsInput, d.Code);
        Assert.Equal("X1", d.DeviceName);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
    }

    [Fact]
    public void CoilDevice_WithInputKindContact_IsFlaggedAsKindMismatch()
    {
        // P2: コイル CR1 があるのに接点が PushButtonNO（入力系）で名前を流用している
        var doc = MakeDoc(SheetWith(
            El(ElementKind.Coil, 0, 3, "CR1"),
            El(ElementKind.PushButtonNO, 1, 0, "CR1")));

        var diags = DesignRuleCheck.CheckDeviceTypeConsistency(doc);

        var d = Assert.Single(diags);
        Assert.Equal(DesignRuleCheck.CoilContactKindMismatch, d.Code);
        Assert.Equal("CR1", d.DeviceName);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
    }

    [Fact]
    public void CoilDevice_WithBothContactKinds_ReportsBothConflicts()
    {
        // コイルあり + ContactNO（励磁系）+ PushButtonNO（入力系）の混在 → P6+P2 両方
        var doc = MakeDoc(SheetWith(
            El(ElementKind.Coil, 0, 3, "CR1"),
            El(ElementKind.ContactNO, 1, 0, "CR1"),
            El(ElementKind.PushButtonNO, 2, 0, "CR1")));

        var diags = DesignRuleCheck.CheckDeviceTypeConsistency(doc);

        // P6（励磁+入力混在）だけが出る（P2の条件「励磁系なし」が満たされないため）
        var d = Assert.Single(diags);
        Assert.Equal(DesignRuleCheck.TypeConflictEnergizedVsInput, d.Code);
    }

    // ===== CheckVerticalCrossings (P7) =====

    private static Sheet MakeSheetWithVc(int vcTop, int vcBottom, int vcCol, int cols = 4)
    {
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = cols } };
        sheet.Connectors.Add(new VerticalConnector { TopRow = vcTop, BottomRow = vcBottom, Column = vcCol });
        return sheet;
    }

    [Fact]
    public void VerticalConnector_DirectAdjacentRows_IsNotFlagged()
    {
        // vc(col=1, top=0, bottom=1): 中間行なし → 警告なし
        var sheet = MakeSheetWithVc(vcTop: 0, vcBottom: 1, vcCol: 1);
        // 各行に要素を追加（境界1にポート形成）
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "CR1")); // boundary 0..1
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "CR2")); // boundary 0..1
        var net = NetlistBuilder.Build(sheet);
        var diags = DesignRuleCheck.CheckVerticalCrossings(sheet, net);
        Assert.Empty(diags);
    }

    [Fact]
    public void VerticalConnector_ThroughIntermediateRow_IsFlagged()
    {
        // vc(col=1, top=0, bottom=2): 中間行1が境界1に別ネット → DRC-CONN-001
        var sheet = MakeSheetWithVc(vcTop: 0, vcBottom: 2, vcCol: 1);
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));  // row0: boundary 0..1
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "B"));  // row1: boundary 0..1 (交差)
        sheet.Elements.Add(El(ElementKind.ContactNO, 2, 0, "C"));  // row2: boundary 0..1
        var net = NetlistBuilder.Build(sheet);
        var diags = DesignRuleCheck.CheckVerticalCrossings(sheet, net);
        var d = Assert.Single(diags);
        Assert.Equal(DesignRuleCheck.VerticalCrossing, d.Code);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
    }

    [Fact]
    public void VerticalConnector_NoElementInIntermediateRow_IsNotFlagged()
    {
        // vc(col=1, top=0, bottom=2): 中間行1に要素なし → 交差なし → 警告なし
        var sheet = MakeSheetWithVc(vcTop: 0, vcBottom: 2, vcCol: 1);
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));  // row0
        sheet.Elements.Add(El(ElementKind.ContactNO, 2, 0, "C"));  // row2（row1 に要素なし）
        var net = NetlistBuilder.Build(sheet);
        var diags = DesignRuleCheck.CheckVerticalCrossings(sheet, net);
        Assert.Empty(diags);
    }

    // ===== CheckLoadReachability (P8) =====

    private static Sheet MakeLoadSheet(bool connectLeft, bool connectRight)
    {
        // 列数4: 境界0=左母線, 境界4=右母線
        // 通常: L—[ContactNO col0]—(Coil col2)—R
        // connectLeft=false: ContactNO を外す（左母線への経路なし）
        // connectRight=false: Coil を右母線に繋がない（Coil は境界2..3にある=境界4に到達しない）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 }, PageNumber = 1 };
        if (connectLeft)
            sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "CR1"));  // 境界0..1
        // Coil: connectRight=true なら col3（境界3..4=右母線接続）、false なら col2（境界2..3=浮き）
        int coilCol = connectRight ? 3 : 2;
        sheet.Elements.Add(El(ElementKind.Coil, 0, coilCol, "OUT1"));
        return sheet;
    }

    [Fact]
    public void Load_FullyConnectedToRails_IsNotFlagged()
    {
        // 正常: L—[CR1]—(OUT1)—R
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 }, PageNumber = 1 };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "CR1")); // 境界0..1
        sheet.Elements.Add(El(ElementKind.Coil, 0, 2, "OUT1"));     // 境界2..3=右母線
        var net = NetlistBuilder.Build(sheet);
        var diags = DesignRuleCheck.CheckLoadReachability(sheet, net);
        Assert.Empty(diags);
    }

    [Fact]
    public void Load_NotConnectedToRightRail_FlagsLoadNotReachableFromRight()
    {
        // Coil が行末ではなく（右側に ContactNO がある）、右側の ContactNO が右母線へ自動接続される。
        // OUT1右 → CR2左（horizontal union）→ CR2右 → 右母線 という経路で OUT1 は右母線に到達可能。
        // よって LoadNotReachableFromRight DRC フラグは立たない（フラグ0件が正しい新動作）。
        var sheet = new Sheet { Grid = new GridSpec { Columns = 5 }, PageNumber = 1 };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "CR1")); // 境界0..1
        sheet.Elements.Add(El(ElementKind.Coil, 0, 1, "OUT1"));     // 境界1..2（行末ではない）
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 3, "CR2")); // 境界3..4（行末、右母線へ自動接続）
        var net = NetlistBuilder.Build(sheet);
        var diags = DesignRuleCheck.CheckLoadReachability(sheet, net);
        Assert.Empty(diags);
    }

    [Fact]
    public void Load_BehindAnotherLoad_FlagsLoadNotReachableFromLeft()
    {
        // L — (Coil OUT1, col0) — (Coil OUT2, col2) — R
        // DRC洪水はLoadを通過しないため、OUT1右側のネットはleftRailから到達不可→OUT2が未到達と診断。
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 }, PageNumber = 1 };
        sheet.Elements.Add(El(ElementKind.Coil, 0, 0, "OUT1"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 2, "OUT2"));
        var net = NetlistBuilder.Build(sheet);
        var diags = DesignRuleCheck.CheckLoadReachability(sheet, net);
        Assert.Contains(diags, d => d.Code == DesignRuleCheck.LoadNotReachableFromLeft && d.DeviceName == "OUT2");
    }

    // ===== CheckSeriesCoils（二重コイル） =====

    [Fact]
    public void TwoCoilsInSeries_FlagsSeriesCoils()
    {
        // L —(Coil OUT1, col0)—(Coil OUT2, col1)— … 2つのコイルが節点を共有して直列。
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 }, PageNumber = 1 };
        sheet.Elements.Add(El(ElementKind.Coil, 0, 0, "OUT1"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 1, "OUT2"));
        var net = NetlistBuilder.Build(sheet);

        var diags = DesignRuleCheck.CheckSeriesCoils(sheet, net);

        var d = Assert.Single(diags);
        Assert.Equal(DesignRuleCheck.SeriesCoils, d.Code);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("OUT1", d.Message);
        Assert.Contains("OUT2", d.Message);
    }

    [Fact]
    public void NormalSingleLoad_IsNotFlaggedAsSeriesCoils()
    {
        // 正常: L—[CR1]—(OUT1)—R は直列コイルではない。
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 }, PageNumber = 1 };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "CR1"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 2, "OUT1"));
        var net = NetlistBuilder.Build(sheet);

        var diags = DesignRuleCheck.CheckSeriesCoils(sheet, net);

        Assert.Empty(diags);
    }
}
