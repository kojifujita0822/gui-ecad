using GuiEcad.Model;
using GuiEcad.Simulation;
using Xunit;

namespace GuiEcad.Tests;

public class TestSessionTests
{
    private static ElementInstance El(ElementKind k, int row, int col, string? dev = null)
        => new() { Kind = k, Pos = new GridPos(row, col), DeviceName = dev };

    // L —[ST]—[SP(NC)]— (Coil R1) — R ＋ R1自己保持接点（列1で合流）
    private static Sheet SelfHold()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 5 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "ST"));
        sheet.Elements.Add(El(ElementKind.PushButtonNC, 0, 2, "SP"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 4, "R1"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "R1"));
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 1 });
        return sheet;
    }

    [Fact]
    public void TestSession_SelfHold_LatchesAcrossInputChanges()
    {
        var ts = new TestSession(SelfHold());

        ts.SetInput("ST", true);
        Assert.True(ts.IsEnergized("R1"));          // 起動押下 → 励磁

        ts.SetInput("ST", false);
        Assert.True(ts.IsEnergized("R1"));          // 離しても自己保持で継続

        ts.SetInput("SP", true);
        Assert.False(ts.IsEnergized("R1"));         // 停止(NC)押下 → 消磁
    }

    [Fact]
    public void TestSession_PoweredNets_AvailableForHighlight()
    {
        var ts = new TestSession(SelfHold());
        ts.SetInput("ST", true);
        Assert.NotNull(ts.Result);
        Assert.NotEmpty(ts.Result!.PoweredNets);    // 通電ネット（ハイライト用）が得られる
    }
}
