using GuiEcad.Model;
using GuiEcad.Rendering;
using Xunit;
using static GuiEcad.Tests.TestHelper;

namespace GuiEcad.Tests;

/// <summary>
/// 長い図面の複数ページ分割（枠あり出力）のページ数算出を検証する。
/// データ上は母線連続のまま、描画だけ RowsPerPage 行ごとに分割する想定。
/// </summary>
public class PageSplitTests
{
    [Fact]
    public void TotalRows_UsesMaxOfGridRowsAndElementExtent()
    {
        // グリッド8行・要素は行14（=15行目）にある → 総行数は 15。
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4, Rows = 8 } };
        sheet.Elements.Add(El(ElementKind.Coil, 14, 3, "CR1"));

        Assert.Equal(15, DiagramRenderer.TotalRows(sheet));
    }

    [Fact]
    public void TotalRows_EmptySheet_UsesGridRows()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4, Rows = 8 } };
        Assert.Equal(8, DiagramRenderer.TotalRows(sheet));
    }

    [Theory]
    [InlineData(1, 1)]    // 1行 → 1ページ
    [InlineData(28, 1)]   // ちょうど 28 行 → 1ページ
    [InlineData(29, 2)]   // 29 行 → 2ページ
    [InlineData(56, 2)]   // 56 行 → 2ページ
    [InlineData(57, 3)]   // 57 行 → 3ページ
    public void PageCount_SplitsByRowsPerPage(int contentRows, int expectedPages)
    {
        // RowsPerPage は 28 を前提（定数が変わってもロジックを検証）。
        Assert.Equal(28, DiagramRenderer.RowsPerPage);

        // contentRows 行ぶんを占有する要素を 1 行目と最終行に置く（Grid.Rows は小さく）。
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4, Rows = 1 } };
        sheet.Elements.Add(El(ElementKind.Coil, contentRows - 1, 3, "OUT"));

        Assert.Equal(contentRows, DiagramRenderer.TotalRows(sheet));
        Assert.Equal(expectedPages, DiagramRenderer.PageCount(sheet));
    }
}
