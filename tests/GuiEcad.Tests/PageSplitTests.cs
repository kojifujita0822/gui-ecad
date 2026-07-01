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

    // ---- 主回路（MainCircuit）シートの mm ベース分割（RenderPageCount） ----

    [Fact]
    public void RenderPageCount_NonMainCircuit_MatchesStaticPageCount()
    {
        // 制御回路シートは従来どおり静的 PageCount と同じ結果を返す（互換性の確認）。
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4, Rows = 40 } };
        var dr = new DiagramRenderer();

        Assert.Equal(DiagramRenderer.PageCount(sheet), dr.RenderPageCount(sheet));
    }

    [Fact]
    public void RenderPageCount_MainCircuit_GridRowsSmall_UsesFreeLineExtent()
    {
        // Grid.Rows は小さいが、自由直線（母線）が RowsPerPage を大きく超えて伸びるケース。
        // 既定 CellMm=9.0 / MarginMm=20.0 前提: Y = 20 + 40*9 = 380mm ぶんの母線 → 40行相当 → 2ページ。
        var sheet = new Sheet { MainCircuit = true, Grid = new GridSpec { Columns = 6, Rows = 3 } };
        sheet.FreeLines.Add(new FreeLine { X1Mm = 30, Y1Mm = 20, X2Mm = 30, Y2Mm = 20 + 40 * 9 });
        var dr = new DiagramRenderer();

        // 要素グリッド行だけを見る静的 PageCount では1ページ扱いになってしまう（旧挙動）。
        Assert.Equal(1, DiagramRenderer.PageCount(sheet));
        // mm 座標の内容を加味した RenderPageCount は 2ページと判定する。
        Assert.Equal(2, dr.RenderPageCount(sheet));
    }

    [Fact]
    public void RenderPageCount_MainCircuit_ConnectionDotExtent_Counted()
    {
        // 接続点（●）だけが下方に伸びているケースも仮想行数に加味される。
        var sheet = new Sheet { MainCircuit = true, Grid = new GridSpec { Columns = 6, Rows = 2 } };
        sheet.ConnectionDots.Add(new ConnectionDot { XMm = 30, YMm = 20 + 35 * 9 });
        var dr = new DiagramRenderer();

        Assert.Equal(2, dr.RenderPageCount(sheet));
    }

    [Fact]
    public void RenderPageCount_MainCircuit_FrameVisualExtent_Counted()
    {
        // 枠（GroupFrame）の VisualYMm/VisualHeightMm による広がりも仮想行数に加味される。
        var sheet = new Sheet { MainCircuit = true, Grid = new GridSpec { Columns = 6, Rows = 2 } };
        sheet.Frames.Add(new GroupFrame
        {
            TopLeft = new GridPos(0, 0), Width = 2, Height = 2,
            VisualYMm = 20, VisualHeightMm = 35 * 9,
        });
        var dr = new DiagramRenderer();

        Assert.Equal(2, dr.RenderPageCount(sheet));
    }

    [Fact]
    public void MainCircuit_MultiPage_FreeLineExtentOnly_RendersWithoutException()
    {
        // Grid.Rows は分割を意識しない小さい値のまま、自由直線の mm 座標だけで複数ページに
        // またがる主回路シートを、RenderPageCount が返すページ数ぶん描画してもクリップが釣り合うこと。
        var sheet = new Sheet { MainCircuit = true, Grid = new GridSpec { Columns = 6, Rows = 3 } };
        for (int i = 0; i < 3; i++)
            sheet.FreeLines.Add(new FreeLine { X1Mm = 30 + i * 9, Y1Mm = 20, X2Mm = 30 + i * 9, Y2Mm = 20 + 40 * 9 });

        var dr = new DiagramRenderer();
        var rec = new NullRenderer();
        int pages = dr.RenderPageCount(sheet);
        Assert.Equal(2, pages);

        var ex = Record.Exception(() =>
        {
            for (int page = 0; page < pages; page++)
                dr.Render(rec, sheet, enableBorder: true,
                          pageRowStart: page * DiagramRenderer.RowsPerPage,
                          pageRowCount: DiagramRenderer.RowsPerPage);
        });
        Assert.Null(ex);
        Assert.Equal(0, rec.ClipDepth);
    }
}
