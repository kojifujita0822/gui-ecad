using GuiEcad.Model;
using GuiEcad.Simulation;
using Xunit;
using static GuiEcad.Tests.TestHelper;

namespace GuiEcad.Tests;

public class DeviceRenamerTests
{
    private static LadderDocument MakeDoc()
    {
        var doc = new LadderDocument();
        doc.Devices.ByName["CR11"] = new Device { Name = "CR11", Class = DeviceClass.Relay };

        var sheet1 = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet1.Elements.Add(El(ElementKind.ContactNO, 0, 0, "CR11"));
        sheet1.Elements.Add(El(ElementKind.ContactNO, 1, 0, "CR11"));
        sheet1.Elements.Add(El(ElementKind.Coil,      0, 3, "CR11"));
        sheet1.Elements.Add(El(ElementKind.ContactNO, 2, 0, "CR12"));  // 別機器

        var sheet2 = new Sheet { PageNumber = 2, Grid = new GridSpec { Columns = 4 } };
        sheet2.Elements.Add(El(ElementKind.ContactNO, 0, 0, "CR11"));

        doc.Sheets.Add(sheet1);
        doc.Sheets.Add(sheet2);
        return doc;
    }

    // ---- Rename ----

    [Fact]
    public void Rename_ReplacesAllOccurrences()
    {
        var doc = MakeDoc();
        int count = DeviceRenamer.Rename(doc, "CR11", "CR21");

        Assert.Equal(4, count);  // sheet1×3 + sheet2×1
        Assert.All(doc.Sheets.SelectMany(s => s.Elements)
            .Where(e => e.DeviceName == "CR11"), _ => Assert.Fail("CR11 が残っている"));
        Assert.Equal(4, doc.Sheets.SelectMany(s => s.Elements)
            .Count(e => e.DeviceName == "CR21"));
    }

    [Fact]
    public void Rename_DoesNotTouchOtherDevices()
    {
        var doc = MakeDoc();
        DeviceRenamer.Rename(doc, "CR11", "CR21");

        Assert.Single(doc.Sheets[0].Elements.Where(e => e.DeviceName == "CR12"));
    }

    [Fact]
    public void Rename_IsCaseInsensitive()
    {
        var doc = MakeDoc();
        int count = DeviceRenamer.Rename(doc, "cr11", "CR21");  // 小文字で検索

        Assert.Equal(4, count);
    }

    [Fact]
    public void Rename_UpdatesDeviceTable()
    {
        var doc = MakeDoc();
        DeviceRenamer.Rename(doc, "CR11", "CR21");

        Assert.False(doc.Devices.ByName.ContainsKey("CR11"));
        Assert.True(doc.Devices.ByName.ContainsKey("CR21"));
        Assert.Equal("CR21", doc.Devices.ByName["CR21"].Name);
    }

    [Fact]
    public void Rename_SameFromTo_ReturnsZero()
    {
        var doc = MakeDoc();
        int count = DeviceRenamer.Rename(doc, "CR11", "CR11");
        Assert.Equal(0, count);
    }

    [Fact]
    public void Rename_EmptyFrom_ReturnsZero()
    {
        var doc = MakeDoc();
        int count = DeviceRenamer.Rename(doc, "", "CR21");
        Assert.Equal(0, count);
    }

    [Fact]
    public void Rename_NotFound_ReturnsZero()
    {
        var doc = MakeDoc();
        int count = DeviceRenamer.Rename(doc, "CR99", "CR00");
        Assert.Equal(0, count);
    }

    // ---- Find ----

    [Fact]
    public void Find_ReturnsAllMatches_InPageOrder()
    {
        var doc = MakeDoc();
        var hits = DeviceRenamer.Find(doc, "CR11");

        Assert.Equal(4, hits.Count);
        // sheet1 が先（PageNumber 順）
        Assert.All(hits.Take(3), h => Assert.Equal(1, h.Sheet.PageNumber));
        Assert.Equal(2, hits[3].Sheet.PageNumber);
    }

    [Fact]
    public void Find_EmptyName_ReturnsEmpty()
    {
        var doc = MakeDoc();
        Assert.Empty(DeviceRenamer.Find(doc, ""));
    }

    [Fact]
    public void Find_NotFound_ReturnsEmpty()
    {
        var doc = MakeDoc();
        Assert.Empty(DeviceRenamer.Find(doc, "CR99"));
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        var doc = MakeDoc();
        Assert.Equal(4, DeviceRenamer.Find(doc, "cr11").Count);
    }
}
