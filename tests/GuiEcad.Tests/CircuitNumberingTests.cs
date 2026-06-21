using GuiEcad.Model;
using GuiEcad.Simulation;
using Xunit;

namespace GuiEcad.Tests;

public class CircuitNumberingTests
{
    private static ElementInstance El(ElementKind kind, int row, int col, string? device = null)
        => new() { Kind = kind, Pos = new GridPos(row, col), DeviceName = device };

    // ---- 単シート ----

    [Fact]
    public void SingleSheet_RowsInOrder_AreNumberedFrom1()
    {
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "C1"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "B"));
        sheet.Elements.Add(El(ElementKind.Coil, 1, 3, "C2"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 2, 0, "D"));
        sheet.Elements.Add(El(ElementKind.Coil, 2, 3, "C3"));

        var doc = new LadderDocument();
        doc.Sheets.Add(sheet);
        CircuitNumberer.Number(doc);

        Assert.Equal(3, sheet.Lines.Count);
        Assert.Equal(1, sheet.Lines[0].CircuitNumber); Assert.Equal(0, sheet.Lines[0].Row);
        Assert.Equal(2, sheet.Lines[1].CircuitNumber); Assert.Equal(1, sheet.Lines[1].Row);
        Assert.Equal(3, sheet.Lines[2].CircuitNumber); Assert.Equal(2, sheet.Lines[2].Row);
    }

    [Fact]
    public void ParallelBranch_EachRowGetsOwnNumber()
    {
        // 行0: 主線  行1: 並列枝（縦コネクタで合流）
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "C"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "B"));  // 並列枝
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 1 });

        var doc = new LadderDocument();
        doc.Sheets.Add(sheet);
        CircuitNumberer.Number(doc);

        Assert.Equal(2, sheet.Lines.Count);
        Assert.Equal(1, sheet.Lines[0].CircuitNumber);
        Assert.Equal(2, sheet.Lines[1].CircuitNumber);
    }

    [Fact]
    public void SameRowMultipleElements_CountsOnce()
    {
        // 同一行に複数要素があっても回路番号は1つ
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 6 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 1, "B"));
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 2, "C"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 5, "CR"));

        var doc = new LadderDocument();
        doc.Sheets.Add(sheet);
        CircuitNumberer.Number(doc);

        Assert.Single(sheet.Lines);
        Assert.Equal(1, sheet.Lines[0].CircuitNumber);
    }

    [Fact]
    public void EmptySheet_ProducesNoLines()
    {
        var sheet = new Sheet { PageNumber = 1 };
        var doc = new LadderDocument();
        doc.Sheets.Add(sheet);
        CircuitNumberer.Number(doc);

        Assert.Empty(sheet.Lines);
    }

    // ---- 複数シート ----

    [Fact]
    public void MultiSheet_CircuitNumbersContinueAcrossSheets()
    {
        var sheet1 = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet1.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet1.Elements.Add(El(ElementKind.Coil, 0, 3, "C1"));
        sheet1.Elements.Add(El(ElementKind.ContactNO, 1, 0, "B"));
        sheet1.Elements.Add(El(ElementKind.Coil, 1, 3, "C2"));

        var sheet2 = new Sheet { PageNumber = 2, Grid = new GridSpec { Columns = 4 } };
        sheet2.Elements.Add(El(ElementKind.ContactNO, 0, 0, "X"));
        sheet2.Elements.Add(El(ElementKind.Coil, 0, 3, "C3"));

        var doc = new LadderDocument();
        doc.Sheets.Add(sheet1);
        doc.Sheets.Add(sheet2);
        CircuitNumberer.Number(doc);

        Assert.Equal(2, sheet1.Lines.Count);
        Assert.Equal(1, sheet1.Lines[0].CircuitNumber);
        Assert.Equal(2, sheet1.Lines[1].CircuitNumber);

        Assert.Single(sheet2.Lines);
        Assert.Equal(3, sheet2.Lines[0].CircuitNumber);  // 継続連番
    }

    [Fact]
    public void MultiSheet_ProcessedInPageNumberOrder()
    {
        // シートを逆順で追加しても PageNumber 順で採番される
        var sheet1 = new Sheet { PageNumber = 2, Grid = new GridSpec { Columns = 4 } };
        sheet1.Elements.Add(El(ElementKind.ContactNO, 0, 0, "Late"));
        sheet1.Elements.Add(El(ElementKind.Coil, 0, 3, "L"));

        var sheet2 = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet2.Elements.Add(El(ElementKind.ContactNO, 0, 0, "Early"));
        sheet2.Elements.Add(El(ElementKind.Coil, 0, 3, "E"));

        var doc = new LadderDocument();
        doc.Sheets.Add(sheet1);  // PageNumber=2 を先に追加
        doc.Sheets.Add(sheet2);  // PageNumber=1 を後に追加
        CircuitNumberer.Number(doc);

        // PageNumber=1 のシート（sheet2）が先に採番される
        Assert.Equal(1, sheet2.Lines[0].CircuitNumber);
        Assert.Equal(2, sheet1.Lines[0].CircuitNumber);
    }

    [Fact]
    public void RenumberAfterChange_UpdatesLines()
    {
        // 要素を追加→再採番で Lines が更新されることを確認
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "C1"));

        var doc = new LadderDocument();
        doc.Sheets.Add(sheet);
        CircuitNumberer.Number(doc);
        Assert.Single(sheet.Lines);

        // 新しい行を追加して再採番
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "B"));
        sheet.Elements.Add(El(ElementKind.Coil, 1, 3, "C2"));
        CircuitNumberer.Number(doc);

        Assert.Equal(2, sheet.Lines.Count);
        Assert.Equal(1, sheet.Lines[0].CircuitNumber);
        Assert.Equal(2, sheet.Lines[1].CircuitNumber);
    }
}
