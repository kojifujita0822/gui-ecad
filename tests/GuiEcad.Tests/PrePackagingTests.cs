using System.Text.Json.Nodes;
using GuiEcad.Model;
using GuiEcad.Persistence;
using GuiEcad.Rendering;
using GuiEcad.Simulation;
using Xunit;
using static GuiEcad.Tests.TestHelper;

namespace GuiEcad.Tests;

/// <summary>
/// パッケージング前の穴埋めテスト（docs/test-plan.md 1.2）。
/// 主回路まわりの新フィールド（接続点・自由直線・主回路記号 Type）の永続化、旧ファイル後方互換、
/// 大規模図面の処理性能を担保する。
/// </summary>
public sealed class PrePackagingTests
{
    // --- 1.2: 接続点 ConnectionDot・自由直線 FreeLine の永続化往復 ---
    [Fact]
    public void ConnectionDots_And_FreeLines_RoundTrip()
    {
        var doc = new LadderDocument();
        var sheet = new Sheet { PageNumber = 1, MainCircuit = true, Grid = new GridSpec { Columns = 6 } };
        sheet.FreeLines.Add(new FreeLine { X1Mm = 30, Y1Mm = 20, X2Mm = 30, Y2Mm = 200, Style = LineStyle.Solid });
        sheet.FreeLines.Add(new FreeLine { X1Mm = 39, Y1Mm = 20, X2Mm = 39, Y2Mm = 200, Style = LineStyle.Dashed });
        sheet.ConnectionDots.Add(new ConnectionDot { XMm = 30, YMm = 60 });
        sheet.ConnectionDots.Add(new ConnectionDot { XMm = 39, YMm = 120 });
        doc.Sheets.Add(sheet);

        var back = GcadSerializer.Deserialize(GcadSerializer.Serialize(doc));
        var s = back.Sheets[0];

        Assert.True(s.MainCircuit);
        Assert.Equal(2, s.FreeLines.Count);
        Assert.Equal(30, s.FreeLines[0].X1Mm);
        Assert.Equal(200, s.FreeLines[0].Y2Mm);
        Assert.Equal(LineStyle.Dashed, s.FreeLines[1].Style);
        Assert.Equal(2, s.ConnectionDots.Count);
        Assert.Equal(39, s.ConnectionDots[1].XMm);
        Assert.Equal(120, s.ConnectionDots[1].YMm);
    }

    // --- 1.2: 主回路ブレーカの Params["Type"] (NFB/MCCB/ELB) 往復永続化 ---
    [Theory]
    [InlineData("NFB")]
    [InlineData("MCCB")]
    [InlineData("ELB")]
    public void MainCircuitBreakerType_RoundTrips(string type)
    {
        var doc = new LadderDocument();
        var sheet = new Sheet { PageNumber = 1, MainCircuit = true, Grid = new GridSpec { Columns = 6 } };
        var brk = new ElementInstance { Kind = ElementKind.Breaker3P, Pos = new GridPos(0, 1), CellWidth = 2, DeviceName = "Q1" };
        brk.Params["Type"] = type;
        brk.Params["Orient"] = "V";
        sheet.Elements.Add(brk);
        doc.Sheets.Add(sheet);

        var back = GcadSerializer.Deserialize(GcadSerializer.Serialize(doc));
        var e = back.Sheets[0].Elements[0];

        Assert.Equal(ElementKind.Breaker3P, e.Kind);
        Assert.Equal(type, e.Params["Type"]);
        Assert.Equal("V", e.Params["Orient"]);
        Assert.Equal("Q1", e.DeviceName);
    }

    // --- 1.2: 接続点・自由直線・主回路記号を含むシートの描画で例外が出ない（スモーク） ---
    [Fact]
    public void SheetWith_FreeLinesAndDots_Renders_WithoutException()
    {
        var sheet = new Sheet { MainCircuit = true, Grid = new GridSpec { Columns = 9, Rows = 10 } };
        for (int i = 0; i < 3; i++)
            sheet.FreeLines.Add(new FreeLine { X1Mm = 30 + i * 9, Y1Mm = 20, X2Mm = 30 + i * 9, Y2Mm = 110 });
        sheet.ConnectionDots.Add(new ConnectionDot { XMm = 30, YMm = 50 });
        sheet.ConnectionDots.Add(new ConnectionDot { XMm = 39, YMm = 80 });
        sheet.Elements.Add(new ElementInstance
        { Kind = ElementKind.ThermalOverload3P, Pos = new GridPos(3, 1), CellWidth = 2, DeviceName = "OL1" });

        var rec = new NullRenderer();
        var ex = Record.Exception(() => new DiagramRenderer().Render(rec, sheet));
        Assert.Null(ex);
        Assert.Equal(0, rec.ClipDepth);   // PushClip/PopClip が釣り合う
    }

    // --- 1.2: 後方互換。新フィールド（freeLines/connectionDots/mainCircuit）が無い旧 JSON を既定で開ける ---
    [Fact]
    public void LegacyJson_WithoutNewFields_LoadsWithDefaults()
    {
        var doc = new LadderDocument();
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet.Elements.Add(El(ElementKind.ContactNO, 0, 0, "A"));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 3, "A"));
        doc.Sheets.Add(sheet);

        // シリアライズ後、主回路まわりの新フィールドをキーごと取り除いて旧ファイルを模擬する。
        var node = JsonNode.Parse(GcadSerializer.Serialize(doc))!;
        var s0 = node["sheets"]!.AsArray()[0]!.AsObject();
        s0.Remove("freeLines");
        s0.Remove("connectionDots");
        s0.Remove("mainCircuit");
        var legacyJson = node.ToJsonString();

        // 念のため、本当にキーが消えていることを確認（テスト自体の妥当性）。
        Assert.DoesNotContain("freeLines", legacyJson);
        Assert.DoesNotContain("connectionDots", legacyJson);

        var back = GcadSerializer.Deserialize(legacyJson);
        var s = back.Sheets[0];
        Assert.Empty(s.FreeLines);
        Assert.Empty(s.ConnectionDots);
        Assert.False(s.MainCircuit);          // 旧ファイルは制御回路扱い
        Assert.Equal(2, s.Elements.Count);    // 既存要素は失われない
    }

    // --- 1.2: 大規模図面の性能番兵。ネットリスト構築＋採番＋DRC が現実的時間で完了する ---
    [Fact]
    public void LargeDocument_NetlistNumberingDrc_CompletesInTime()
    {
        var doc = new LadderDocument();
        for (int p = 1; p <= 20; p++)
        {
            var sheet = new Sheet { PageNumber = p, Grid = new GridSpec { Rows = 60, Columns = 6 } };
            for (int row = 0; row < 60; row++)
            {
                sheet.Elements.Add(El(ElementKind.ContactNO, row, 0, $"CR{p}_{row}"));
                sheet.Elements.Add(El(ElementKind.Coil, row, 5, $"CR{p}_{row}"));
            }
            doc.Sheets.Add(sheet);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var sheet in doc.Sheets)
            _ = NetlistBuilder.Build(sheet);
        CircuitNumberer.Number(doc);
        var diags = DesignRuleCheck.CheckCrossReference(doc);
        sw.Stop();

        Assert.NotNull(diags);   // 各 CR はコイル＋接点対なので未解決診断は出ない想定だが、件数より時間が要点
        Assert.True(sw.Elapsed.TotalSeconds < 5.0,
            $"大規模図面のネットリスト構築＋採番＋DRC は5秒以内に完了すべき。実際: {sw.Elapsed.TotalSeconds:F2}s");
    }
}
