using GuiEcad.Model;
using GuiEcad.Simulation;
using Xunit;
using static GuiEcad.Tests.TestHelper;

namespace GuiEcad.Tests;

public class CrossReferenceTests
{
    private static LadderDocument MakeDoc(params Sheet[] sheets)
    {
        var doc = new LadderDocument();
        doc.Sheets.AddRange(sheets);
        CircuitNumberer.Number(doc);
        return doc;
    }

    // ---- 基本動作 ----

    [Fact]
    public void Coil_And_Contact_AreSeparated()
    {
        // CR11 コイル(行0)・CR11 NO接点(行1)
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "PB"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "CR11"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "CR11"));
        sheet.Elements.Add(El(ElementKind.Coil, 1, 3, "CR12"));
        var doc = MakeDoc(sheet);

        var xref = CrossReferenceBuilder.Build(doc);

        xref.TryGet("CR11", out var cr11);
        Assert.Single(cr11.Coils);    Assert.Equal(1, cr11.Coils[0].CircuitNumber);
        Assert.Single(cr11.Contacts); Assert.Equal(2, cr11.Contacts[0].CircuitNumber);
    }

    [Fact]
    public void Contact_WithoutCoil_IsTracked()
    {
        // PB は接点のみ（コイルなし）
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "PB"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "CR11"));
        var doc = MakeDoc(sheet);

        var xref = CrossReferenceBuilder.Build(doc);

        xref.TryGet("PB", out var pb);
        Assert.Single(pb.Contacts);
        Assert.Empty(pb.Coils);
    }

    [Fact]
    public void MultipleContacts_SameDevice_AllTracked()
    {
        // CR11 接点が3箇所
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "CR11"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "C1"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "CR11"));
        sheet.Elements.Add(El(ElementKind.Coil, 1, 3, "C2"));
        sheet.Elements.Add(El(ElementKind.ContactNC, 2, 0, "CR11"));  // b接点
        sheet.Elements.Add(El(ElementKind.Coil, 2, 3, "C3"));
        sheet.Elements.Add(El(ElementKind.Coil, 3, 3, "CR11"));
        var doc = MakeDoc(sheet);

        var xref = CrossReferenceBuilder.Build(doc);

        xref.TryGet("CR11", out var cr11);
        Assert.Single(cr11.Coils);
        Assert.Equal(3, cr11.Contacts.Count);
        // 回路番号 1, 2, 3 が接点位置
        var cnums = cr11.Contacts.Select(c => c.CircuitNumber).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, cnums);
    }

    [Fact]
    public void Ref_UsesVisualRowNumber_NotSkippingEmptyRows()
    {
        // 行0・1は空、要素は行2と行4にある。CR番号は図面左の行番号（行+1=3, 5）に一致すべき。
        // 旧仕様（要素のある行だけの連番）だと 1, 2 になりずれていた。
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.Coil, 2, 3, "CR1"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 4, 0, "CR1"));
        var doc = MakeDoc(sheet);

        var xref = CrossReferenceBuilder.Build(doc);

        xref.TryGet("CR1", out var cr1);
        Assert.Equal(3, cr1.Coils[0].CircuitNumber);     // 行2 → 行番号3
        Assert.Equal(5, cr1.Contacts[0].CircuitNumber);  // 行4 → 行番号5
    }

    [Fact]
    public void NoDeviceName_ElementIsIgnored()
    {
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, null));   // DeviceName なし
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, null));
        var doc = MakeDoc(sheet);

        var xref = CrossReferenceBuilder.Build(doc);
        Assert.Empty(xref.Entries);
    }

    // ---- 複数シート ----

    [Fact]
    public void MultiSheet_RefIncludesPageNumber()
    {
        var sheet1 = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet1.Elements.Add(El(ElementKind.ContactNO, 0, 0, "CR11"));
        sheet1.Elements.Add(El(ElementKind.Coil, 0, 3, "CR12"));

        var sheet2 = new Sheet { PageNumber = 2, Grid = new GridSpec { Columns = 4 } };
        sheet2.Elements.Add(El(ElementKind.Coil, 0, 3, "CR11"));
        sheet2.Elements.Add(El(ElementKind.ContactNO, 1, 0, "CR12"));  // 別機器の接点

        var doc = MakeDoc(sheet1, sheet2);
        var xref = CrossReferenceBuilder.Build(doc);

        xref.TryGet("CR11", out var cr11);
        // 接点: シート1 行1（行番号は図面左ガイド＝行インデックス+1・各シート1始まり）
        Assert.Single(cr11.Contacts);
        Assert.Equal(1, cr11.Contacts[0].PageNumber);
        Assert.Equal(1, cr11.Contacts[0].CircuitNumber);

        // コイル: シート2 行1（各シートで行番号は1始まり。ページ番号で区別）
        Assert.Single(cr11.Coils);
        Assert.Equal(2, cr11.Coils[0].PageNumber);
        Assert.Equal(1, cr11.Coils[0].CircuitNumber);
    }

    // ---- CoilEntries / 一覧表 ----

    [Fact]
    public void CoilEntries_OnlyDevicesWithCoils()
    {
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "PB"));   // 接点のみ
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "CR11"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "CR11"));
        sheet.Elements.Add(El(ElementKind.Coil, 1, 3, "CR12"));
        var doc = MakeDoc(sheet);

        var xref = CrossReferenceBuilder.Build(doc);
        var coilDevices = xref.CoilEntries.Select(e => e.DeviceName).ToList();

        Assert.Contains("CR11", coilDevices);
        Assert.Contains("CR12", coilDevices);
        Assert.DoesNotContain("PB", coilDevices);  // PB はコイルなし
    }

    [Fact]
    public void Entries_OrderedByDeviceName()
    {
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "CR12"));
        sheet.Elements.Add(El(ElementKind.Coil, 1, 3, "CR10"));
        sheet.Elements.Add(El(ElementKind.Coil, 2, 3, "CR11"));
        var doc = MakeDoc(sheet);

        var xref = CrossReferenceBuilder.Build(doc);
        var names = xref.Entries.Select(e => e.DeviceName).ToList();

        Assert.Equal(new[] { "CR10", "CR11", "CR12" }, names);
    }

    // ---- 自作パーツ ----

    [Fact]
    public void CustomPart_RoleIsRespected()
    {
        var lib = new PartLibrary();
        lib.ById["myCoil"] = new PartDefinition
        {
            Id = "myCoil", Role = PartRole.Coil,
            Ports = { new PortDef("L", 0, 0), new PortDef("R", 0, 1) },
        };

        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(new ElementInstance
        {
            PartId = "myCoil", Pos = new GridPos(0, 1), DeviceName = "CR99"
        });
        var doc = MakeDoc(sheet);

        var xref = CrossReferenceBuilder.Build(doc, lib);

        xref.TryGet("CR99", out var cr99);
        Assert.Single(cr99.Coils);
        Assert.Empty(cr99.Contacts);
    }
}
