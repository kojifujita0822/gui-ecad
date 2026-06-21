using GuiEcad.Model;
using GuiEcad.Pdf;
using GuiEcad.Persistence;
using GuiEcad.Rendering;
using GuiEcad.Simulation;
using Xunit;
using static GuiEcad.Tests.TestHelper;

namespace GuiEcad.Tests;

/// <summary>図面作成 → 保存/読込 → シミュレーション → DRC → PDF の統合テスト。</summary>
public class IntegrationTests
{


    private static string TempDir()
    {
        var dir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // コンプレッサー遠方運転盤ミニ版（自己保持 + OL + ランプ）
    private static LadderDocument MakeCompressorDoc()
    {
        var doc = new LadderDocument
        {
            Info = new DocumentInfo
            {
                Title = "コンプレッサー遠方運転盤（テスト）",
                DrawingNo = "T-001",
                Designer = "テスト",
                Date = "2026-06-20",
            },
        };

        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 6 } };

        // Row0: L-[PB_START(NO)]-[PB_STOP(NC)]-[OL1(NC)]-(CR11 coil)-R
        sheet.Elements.Add(El(ElementKind.PushButtonNO,    0, 0, "PB_START"));
        sheet.Elements.Add(El(ElementKind.PushButtonNC,    0, 2, "PB_STOP"));
        sheet.Elements.Add(El(ElementKind.ThermalOverload, 0, 3, "OL1"));
        sheet.Elements.Add(El(ElementKind.Coil,            0, 5, "CR11"));

        // Row1: L-[CR11(NO)] 自己保持 → VC col=1 で PB_START右へ合流
        sheet.Elements.Add(El(ElementKind.ContactNO, 1, 0, "CR11"));
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 1 });

        // Row2: L-[CR11(NO)]-(PL1 lamp)-R
        sheet.Elements.Add(El(ElementKind.ContactNO, 2, 0, "CR11"));
        sheet.Elements.Add(El(ElementKind.Lamp,      2, 5, "PL1"));

        doc.Sheets.Add(sheet);
        return doc;
    }

    // E-1: フルパイプライン — 作成→JSON保存→読込→シミュレーション→DRC→PDF
    [Fact]
    public void FullPipeline_SaveLoadSimulateDrcPdf()
    {
        var doc = MakeCompressorDoc();
        var savePath = Path.Combine(TempDir(), "integration_e1.gcad");

        // 保存・読込
        GcadSerializer.Save(doc, savePath);
        var loaded = GcadSerializer.Load(savePath);
        Assert.Equal("コンプレッサー遠方運転盤（テスト）", loaded.Info.Title);

        var sheet = loaded.Sheets[0];

        // シミュレーション: PB_START押下 → CR11励磁・ランプ点灯
        var ts = new TestSession(sheet);
        ts.SetInput("PB_START", true);
        Assert.True(ts.IsEnergized("CR11"));
        Assert.True(ts.IsEnergized("PL1"));

        // 自己保持確認
        ts.SetInput("PB_START", false);
        Assert.True(ts.IsEnergized("CR11"));

        // 停止
        ts.SetInput("PB_STOP", true);
        Assert.False(ts.IsEnergized("CR11"));

        // DRC: CR11のコイル↔接点対応に問題なし
        CircuitNumberer.Number(loaded);
        var nl = NetlistBuilder.Build(sheet);
        var drcAll = new List<Diagnostic>();
        drcAll.AddRange(DesignRuleCheck.CheckCrossReference(loaded));
        drcAll.AddRange(DesignRuleCheck.CheckDeviceTypeConsistency(loaded));
        drcAll.AddRange(DesignRuleCheck.CheckVerticalCrossings(sheet, nl));
        drcAll.AddRange(DesignRuleCheck.CheckLoadReachability(sheet, nl));
        Assert.DoesNotContain(drcAll, d => d.DeviceName == "CR11");

        // PDF出力（Dispose で Save されるため using ブロックを閉じてから確認）
        var pdfPath = Path.Combine(TempDir(), "integration_e1.pdf");
        var dr = new DiagramRenderer();
        using (var surface = new PdfRenderSurface(pdfPath))
        {
            var r = surface.BeginPage(dr.PageSize(sheet));
            dr.Render(r, sheet);
            surface.EndPage();
        }
        Assert.True(File.Exists(pdfPath));
    }

    // E-2: 複数シートでのDRCクロスリファレンス — コイルとその接点が別シートでもマッチ
    [Fact]
    public void MultiSheet_DrcCrossRef_CrossSheetPairFindsNoIssue()
    {
        var sheet1 = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        sheet1.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "PB1"));
        sheet1.Elements.Add(El(ElementKind.Coil,         0, 3, "CR11"));

        var sheet2 = new Sheet { PageNumber = 2, Grid = new GridSpec { Columns = 4 } };
        sheet2.Elements.Add(El(ElementKind.ContactNO, 0, 0, "CR11")); // シート1コイルに対応
        sheet2.Elements.Add(El(ElementKind.Coil,      0, 3, "OUT1"));

        var doc = new LadderDocument();
        doc.Sheets.Add(sheet1);
        doc.Sheets.Add(sheet2);
        CircuitNumberer.Number(doc);

        var diags = DesignRuleCheck.CheckCrossReference(doc);

        // CR11: コイル(シート1) + 接点(シート2) → 問題なし
        Assert.DoesNotContain(diags, d => d.DeviceName == "CR11");
    }

    // E-3: JSON保存→読込後のシミュレーション結果が元と一致
    [Fact]
    public void SaveLoad_ThenSimulate_ConsistentResult()
    {
        var doc = MakeCompressorDoc();
        var json = GcadSerializer.Serialize(doc);
        var loaded = GcadSerializer.Deserialize(json);

        // 元ドキュメントでシミュレーション
        var ts1 = new TestSession(doc.Sheets[0]);
        ts1.SetInput("PB_START", true);
        bool originalEnergized = ts1.IsEnergized("CR11");

        // 読込後でシミュレーション
        var ts2 = new TestSession(loaded.Sheets[0]);
        ts2.SetInput("PB_START", true);
        bool loadedEnergized = ts2.IsEnergized("CR11");

        Assert.Equal(originalEnergized, loadedEnergized);
    }

    // E-4: 3シートそれぞれのPDFを出力 → 全ファイル存在
    [Fact]
    public void MultiPagePdf_ThreeSheets_AllGenerated()
    {
        var doc = new LadderDocument
        {
            Info = new DocumentInfo { Title = "マルチシートテスト" }
        };
        for (int p = 1; p <= 3; p++)
        {
            var sheet = new Sheet { PageNumber = p, Name = $"シート{p}", Grid = new GridSpec { Columns = 4 } };
            sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, $"PB{p}"));
            sheet.Elements.Add(El(ElementKind.Coil,         0, 3, $"CR{p}"));
            doc.Sheets.Add(sheet);
        }
        CircuitNumberer.Number(doc);
        var xref = CrossReferenceBuilder.Build(doc);

        var dir = TempDir();
        var dr = new DiagramRenderer();
        foreach (var sheet in doc.Sheets)
        {
            var pdfPath = Path.Combine(dir, $"integration_e4_sheet{sheet.PageNumber}.pdf");
            using (var surface = new PdfRenderSurface(pdfPath))
            {
                var page = surface.BeginPage(dr.PageSize(sheet, xref, doc.Info));
                dr.Render(page, sheet, null, null, xref, doc.Info);
                surface.EndPage();
            }
            Assert.True(File.Exists(pdfPath));
        }
    }
}
