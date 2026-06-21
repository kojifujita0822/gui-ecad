using System.Text.Json;
using GuiEcad.Model;
using GuiEcad.Pdf;
using GuiEcad.Persistence;
using GuiEcad.Rendering;
using GuiEcad.Simulation;
using Xunit;

namespace GuiEcad.Tests;

public class CustomPartTests
{
    // 自作の a接点（基準範囲1×1セル・2端子・独自グリフ）
    private static (PartLibrary Lib, string Id) MakeCustomNO()
    {
        var part = new PartDefinition
        {
            Id = "myNO",
            Name = "自作a接点",
            WidthCells = 1,
            HeightCells = 1,
            Role = PartRole.ContactNO,
            Ports = { new PortDef("L", 0, 0), new PortDef("R", 0, 1) },
            Primitives =
            {
                new PartLine(0, 0, 0.35, 0),
                new PartLine(0.65, 0, 1.0, 0),
                new PartRect(0.35, -0.2, 0.3, 0.4),
                new PartLine(0.35, 0.2, 0.65, -0.2),   // 独自の斜線
            },
        };
        var lib = new PartLibrary();
        lib.ById[part.Id] = part;
        return (lib, part.Id);
    }

    [Fact]
    public void CustomContact_BehavesAsContactNO()
    {
        var (lib, id) = MakeCustomNO();
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(new ElementInstance { PartId = id, Pos = new GridPos(0, 0), DeviceName = "MY" });
        sheet.Elements.Add(new ElementInstance { Kind = ElementKind.Coil, Pos = new GridPos(0, 2), DeviceName = "C" });

        var nl = NetlistBuilder.Build(sheet, lib);

        // 自作NO接点: 機器ON で導通 → コイル励磁
        var off = new Evaluator(nl).Evaluate(new SimState { Energized = { ["MY"] = false } });
        var on = new Evaluator(nl).Evaluate(new SimState { Energized = { ["MY"] = true } });
        Assert.False(off.State.Energized["C"]);
        Assert.True(on.State.Energized["C"]);
    }

    [Fact]
    public void CustomPart_PortsDefineConnectivity()
    {
        var (lib, id) = MakeCustomNO();
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(new ElementInstance { PartId = id, Pos = new GridPos(0, 0), DeviceName = "MY" });
        sheet.Elements.Add(new ElementInstance { Kind = ElementKind.Coil, Pos = new GridPos(0, 2), DeviceName = "C" });

        var nl = NetlistBuilder.Build(sheet, lib);
        var contact = nl.Components.Find(c => c.DeviceName == "MY")!;
        // 左端子は母線(境界0)、右端子はコイル左ネットと連続
        Assert.Equal(nl.LeftRailNet, contact.NetA);
        var coil = nl.Components.Find(c => c.DeviceName == "C")!;
        Assert.Equal(contact.NetB, coil.NetA);
    }

    [Fact]
    public void PartLibrary_JsonRoundTrips()
    {
        var (lib, id) = MakeCustomNO();
        string json = JsonSerializer.Serialize(lib);
        var back = JsonSerializer.Deserialize<PartLibrary>(json)!;

        var p = back.Get(id)!;
        Assert.Equal(PartRole.ContactNO, p.Role);
        Assert.Equal(2, p.Ports.Count);
        Assert.Equal(4, p.Primitives.Count);
        Assert.IsType<PartRect>(p.Primitives[2]);   // 多態が保たれている
    }

    [Fact]
    public void PartLibraryFile_RoundTrips_ExternalFormat()
    {
        var (lib, id) = MakeCustomNO();
        string json = PartLibrarySerializer.Serialize(lib);
        var parts = PartLibrarySerializer.Deserialize(json);

        var p = Assert.Single(parts);
        Assert.Equal(id, p.Id);
        Assert.Equal(PartRole.ContactNO, p.Role);
        Assert.Equal(4, p.Primitives.Count);
        Assert.IsType<PartRect>(p.Primitives[2]);   // 多態が保たれている
    }

    [Fact]
    public void PartLibraryFile_RejectsUnknownSchemaVersion()
    {
        string json = """{ "schemaVersion": 99, "parts": [] }""";
        Assert.Throws<NotSupportedException>(() => PartLibrarySerializer.Deserialize(json));
    }

    [Fact]
    public void Generates_CustomPartPdf()
    {
        var (lib, id) = MakeCustomNO();
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(new ElementInstance { PartId = id, Pos = new GridPos(0, 0), DeviceName = "X1" });
        sheet.Elements.Add(new ElementInstance { Kind = ElementKind.Coil, Pos = new GridPos(0, 3), DeviceName = "C" });

        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        var dr = new DiagramRenderer();
        using var surface = new PdfRenderSurface(Path.Combine(dir, "custom_part.pdf"));
        var r = surface.BeginPage(dr.PageSize(sheet));
        dr.Render(r, sheet, lib);
        surface.EndPage();

        Assert.True(File.Exists(Path.Combine(dir, "custom_part.pdf")));
    }
}
