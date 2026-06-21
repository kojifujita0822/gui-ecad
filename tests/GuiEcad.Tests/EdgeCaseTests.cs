using GuiEcad.Model;
using GuiEcad.Simulation;
using Xunit;

namespace GuiEcad.Tests;

/// <summary>境界値・退化ケース・エッジ入力でのロバスト性テスト。</summary>
public class EdgeCaseTests
{
    private static ElementInstance El(ElementKind kind, int row, int col, string? device = null, int width = 1)
        => new() { Kind = kind, Pos = new GridPos(row, col), CellWidth = width, DeviceName = device };

    // F-1: 1列グリッドにコイルのみ → 左右母線が直結 → コイル励磁（接点不要）
    [Fact]
    public void SingleCellGrid_CoilDirectConnected_IsEnergized()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 1 } };
        sheet.Elements.Add(El(ElementKind.Coil, 0, 0, "C"));

        var nl = NetlistBuilder.Build(sheet);
        var result = new Evaluator(nl).Evaluate(new SimState());

        Assert.Equal(EvalStatus.Converged, result.Status);
        Assert.True(result.State.Energized.TryGetValue("C", out var v) && v);
        // コイルはLoadなのでfloodが貫通せず短絡にはならない
        Assert.Empty(result.ShortCircuitNets);
    }

    // F-2: 要素がグリッド末尾列に配置 → 右母線へ正しく接続
    [Fact]
    public void ElementAtLastColumn_ConnectedToRightRail()
    {
        // 6列グリッド: Contact(col4) + Coil(col5=最終列)
        var sheet = new Sheet { Grid = new GridSpec { Columns = 6 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 4, "A"));
        sheet.Elements.Add(El(ElementKind.Coil,      0, 5, "C"));

        var nl = NetlistBuilder.Build(sheet);

        // A=ON → C励磁（末尾列のコイルが右母線に正しく接続されている）
        var result = new Evaluator(nl).Evaluate(new SimState { Energized = { ["A"] = true } });
        Assert.True(result.State.Energized["C"]);

        // 接続検査：DRC-LOAD エラーなし
        var drcIssues = DesignRuleCheck.CheckLoadReachability(sheet, nl);
        Assert.Empty(drcIssues);
    }

    // F-3: TopRow==BottomRow の縦コネクタ（ゼロ長）→ 例外なくビルド・評価
    [Fact]
    public void VerticalConnector_ZeroLength_NoException()
    {
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 2, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil,      2, 3, "C"));
        // TopRow == BottomRow: 自己ループ（no-op）
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 2, BottomRow = 2 });

        var nl = NetlistBuilder.Build(sheet); // 例外なし
        var result = new Evaluator(nl).Evaluate(new SimState { Energized = { ["A"] = true } });

        Assert.Equal(EvalStatus.Converged, result.Status);
        Assert.True(result.State.Energized["C"]); // 通常動作に影響しない
    }

    // F-4: DeviceName=null の要素でDRC実行 → 例外なし・診断0件
    [Fact]
    public void DeviceName_Null_IgnoredByDrc()
    {
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(new ElementInstance
        {
            Kind = ElementKind.Coil,
            Pos = new GridPos(0, 3),
            DeviceName = null,
        });
        sheet.Elements.Add(new ElementInstance
        {
            Kind = ElementKind.ContactNO,
            Pos = new GridPos(1, 0),
            DeviceName = null,
        });
        var doc = new LadderDocument();
        doc.Sheets.Add(sheet);
        CircuitNumberer.Number(doc);

        Assert.Empty(DesignRuleCheck.CheckCrossReference(doc));
        Assert.Empty(DesignRuleCheck.CheckDeviceTypeConsistency(doc));
    }

    // F-5: 10段並列分岐 — スタックオーバーフローなく評価、各枝がONで出力
    [Fact]
    public void ManyParallelBranches_TenRows_EvaluatesWithoutStackOverflow()
    {
        // Row0: L-[B0(NO)]-+---(Coil OUT)---R
        // Rows 1..9: L-[Bi(NO)]-| (VC col=1 → Row0)
        var sheet = new Sheet { Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "B0"));
        sheet.Elements.Add(El(ElementKind.Coil,      0, 3, "OUT"));
        for (int i = 1; i < 10; i++)
        {
            sheet.Elements.Add(El(ElementKind.ContactNO, i, 0, $"B{i}"));
            sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = i });
        }

        var nl = NetlistBuilder.Build(sheet);

        for (int i = 0; i < 10; i++)
        {
            var state = new SimState();
            state.Energized[$"B{i}"] = true;
            var result = new Evaluator(nl).Evaluate(state);
            Assert.Equal(EvalStatus.Converged, result.Status);
            Assert.True(result.State.Energized["OUT"], $"B{i}=ON のとき OUT が励磁されるべき");
        }
    }

    // F-6: 0.5列位置の縦コネクタを使った自己保持回路が正しく動作する
    [Fact]
    public void HalfCellConnector_SelfHold_WorksCorrectly()
    {
        // Row0: L-[ST(NO)]-(CR1 coil)-R
        // Row1: L-[CR1(NO)] → VC@1.5 → ST右の横線へ合流（自己保持）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "ST"));
        sheet.Elements.Add(El(ElementKind.Coil,         0, 2, "CR1"));
        sheet.Elements.Add(El(ElementKind.ContactNO,    1, 0, "CR1"));
        sheet.Connectors.Add(new VerticalConnector { Column = 1.5, TopRow = 0, BottomRow = 1 });

        var nl = NetlistBuilder.Build(sheet); // 例外なし

        var ts = new TestSession(sheet);
        ts.SetInput("ST", true);
        Assert.True(ts.IsEnergized("CR1"));

        ts.SetInput("ST", false);
        Assert.True(ts.IsEnergized("CR1")); // 自己保持継続
    }
}
