using GuiEcad.Model;
using GuiEcad.Persistence;
using Xunit;

namespace GuiEcad.Tests;

public class PersistenceTests
{
    private static LadderDocument MakeSampleDocument()
    {
        var doc = new LadderDocument
        {
            Info = new DocumentInfo
            {
                CompanyName = "サンプル電機株式会社",
                Title = "コンプレッサー遠方運転盤",
                DrawingNo = "E-001",
                Designer = "赤松",
                Date = "2022-08-25",
            },
            Settings = new DocumentSettings
            {
                DefaultBus = new BusConfig { LeftName = "N24", RightName = "P24" },
                PaperSize = PaperSize.A3,
            },
        };

        doc.Devices.ByName["CR11"] = new Device { Name = "CR11", Class = DeviceClass.Relay, Maker = "オムロン", Model = "MY2N", Quantity = 2 };
        doc.Devices.ByName["PB"] = new Device { Name = "PB", Class = DeviceClass.PushButton };

        var sheet = new Sheet
        {
            PageNumber = 1,
            Name = "制御回路図",
            Grid = new GridSpec { Rows = 10, Columns = 6 },
            Bus = new BusConfig { LeftName = "N24", RightName = "P24", PowerLabel = "DC24V" },
        };
        sheet.Elements.Add(new ElementInstance
        {
            Kind = ElementKind.PushButtonNO,
            Pos = new GridPos(0, 0),
            DeviceName = "PB",
            Params = { ["Color"] = "G" },
        });
        sheet.Elements.Add(new ElementInstance
        {
            Kind = ElementKind.ContactNO,
            Pos = new GridPos(0, 1),
            DeviceName = "CR11",
        });
        sheet.Elements.Add(new ElementInstance
        {
            Kind = ElementKind.Coil,
            Pos = new GridPos(0, 5),
            DeviceName = "CR11",
        });
        sheet.Connectors.Add(new VerticalConnector { Column = 1, TopRow = 0, BottomRow = 1 });
        sheet.Frames.Add(new GroupFrame { Label = "中継ボックス", TopLeft = new GridPos(0, 0), Width = 3, Height = 2 });
        sheet.Lines.Add(new CircuitLine { Row = 0, CircuitNumber = 1 });
        doc.Sheets.Add(sheet);

        return doc;
    }

    [Fact]
    public void Document_JsonRoundTrips()
    {
        var doc = MakeSampleDocument();
        var json = GcadSerializer.Serialize(doc);
        var back = GcadSerializer.Deserialize(json);

        Assert.Equal(1, back.SchemaVersion);
        Assert.Equal("サンプル電機株式会社", back.Info.CompanyName);
        Assert.Equal("コンプレッサー遠方運転盤", back.Info.Title);
        Assert.Equal("赤松", back.Info.Designer);
        Assert.Equal("N24", back.Settings.DefaultBus.LeftName);
        Assert.Equal(PaperSize.A3, back.Settings.PaperSize);
        Assert.True(back.Devices.ByName.ContainsKey("CR11"));
        Assert.Equal(DeviceClass.Relay, back.Devices.ByName["CR11"].Class);
        Assert.Equal("MY2N", back.Devices.ByName["CR11"].Model);
        Assert.Equal(2, back.Devices.ByName["CR11"].Quantity);
    }

    [Fact]
    public void Sheet_ElementsRoundTrip()
    {
        var doc = MakeSampleDocument();
        var json = GcadSerializer.Serialize(doc);
        var back = GcadSerializer.Deserialize(json);

        var sheet = back.Sheets[0];
        Assert.Equal("制御回路図", sheet.Name);
        Assert.Equal(10, sheet.Grid.Rows);
        Assert.Equal(6, sheet.Grid.Columns);
        Assert.Equal("DC24V", sheet.Bus.PowerLabel);
        Assert.Equal(3, sheet.Elements.Count);

        var pb = sheet.Elements[0];
        Assert.Equal(ElementKind.PushButtonNO, pb.Kind);
        Assert.Equal("PB", pb.DeviceName);
        Assert.Equal("G", pb.Params["Color"]);
        Assert.Equal(0, pb.Pos.Row);
        Assert.Equal(0, pb.Pos.Column);

        var coil = sheet.Elements[2];
        Assert.Equal(ElementKind.Coil, coil.Kind);
        Assert.Equal(5, coil.Pos.Column);
    }

    [Fact]
    public void Sheet_ConnectorsFramesLinesRoundTrip()
    {
        var doc = MakeSampleDocument();
        var json = GcadSerializer.Serialize(doc);
        var back = GcadSerializer.Deserialize(json);

        var sheet = back.Sheets[0];
        Assert.Single(sheet.Connectors);
        Assert.Equal(1, sheet.Connectors[0].Column);
        Assert.Equal(0, sheet.Connectors[0].TopRow);
        Assert.Equal(1, sheet.Connectors[0].BottomRow);

        Assert.Single(sheet.Frames);
        Assert.Equal("中継ボックス", sheet.Frames[0].Label);
        Assert.Equal(3, sheet.Frames[0].Width);

        Assert.Single(sheet.Lines);
        Assert.Equal(1, sheet.Lines[0].CircuitNumber);
    }

    [Fact]
    public void Document_WithEmbeddedPartLibrary_RoundTrips()
    {
        var doc = MakeSampleDocument();
        doc.Library = new PartLibrary();
        var part = new PartDefinition
        {
            Id = "myNC",
            Name = "自作b接点",
            WidthCells = 1,
            HeightCells = 1,
            Role = PartRole.ContactNC,
            Ports = { new PortDef("L", 0, 0), new PortDef("R", 0, 1) },
            Primitives = { new PartLine(0, 0, 1, 0), new PartRect(0.3, -0.2, 0.4, 0.4) },
        };
        doc.Library.ById[part.Id] = part;

        var json = GcadSerializer.Serialize(doc);
        var back = GcadSerializer.Deserialize(json);

        Assert.NotNull(back.Library);
        var p = back.Library!.Get("myNC")!;
        Assert.Equal(PartRole.ContactNC, p.Role);
        Assert.Equal(2, p.Ports.Count);
        Assert.IsType<PartRect>(p.Primitives[1]);
    }

    [Fact]
    public void SaveAndLoad_ViaFile_RoundTrips()
    {
        var doc = MakeSampleDocument();
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "temp"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "test_save.gcad");

        GcadSerializer.Save(doc, path);
        Assert.True(File.Exists(path));

        var back = GcadSerializer.Load(path);
        Assert.Equal("コンプレッサー遠方運転盤", back.Info.Title);
        Assert.Single(back.Sheets);
        Assert.Equal(3, back.Sheets[0].Elements.Count);
    }

    [Fact]
    public void UnknownSchemaVersion_Throws()
    {
        var doc = MakeSampleDocument();
        var json = GcadSerializer.Serialize(doc).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
        Assert.Throws<NotSupportedException>(() => GcadSerializer.Deserialize(json));
    }

    // C-1: null の Comment は WhenWritingNull で JSON 非出力 → 読込でも null
    [Fact]
    public void Deserialize_NullComment_RemainsNull()
    {
        var doc = MakeSampleDocument();
        Assert.Null(doc.Sheets[0].Elements[0].Comment); // デフォルトnull

        var json = GcadSerializer.Serialize(doc);
        Assert.DoesNotContain("\"comment\"", json); // WhenWritingNull で出力なし

        var back = GcadSerializer.Deserialize(json);
        Assert.Null(back.Sheets[0].Elements[0].Comment);
    }

    // C-2: sheets が空のドキュメント → 例外なく 0 シートで復元
    [Fact]
    public void Deserialize_EmptySheets_ReturnsEmptyDocument()
    {
        var doc = new LadderDocument();
        var json = GcadSerializer.Serialize(doc);
        var back = GcadSerializer.Deserialize(json);

        Assert.Empty(back.Sheets);
        Assert.NotNull(back.Info);
        Assert.NotNull(back.Settings);
    }

    // C-3: 全オプションフィールド（RungComment・RevisionEntry）のラウンドトリップ
    [Fact]
    public void AllOptionalFields_FullRoundTrip()
    {
        var doc = MakeSampleDocument();
        doc.Info.Revisions.Add(new RevisionEntry
        {
            Rev = "A", Date = "2026-01-01", Description = "初版", By = "太郎"
        });
        doc.Sheets[0].RungComments.Add(new RungComment { Row = 0, Text = "起動回路" });

        var json = GcadSerializer.Serialize(doc);
        var back = GcadSerializer.Deserialize(json);

        Assert.Single(back.Info.Revisions);
        Assert.Equal("A", back.Info.Revisions[0].Rev);
        Assert.Equal("初版", back.Info.Revisions[0].Description);
        Assert.Single(back.Sheets[0].RungComments);
        Assert.Equal("起動回路", back.Sheets[0].RungComments[0].Text);
    }

    // C-4: 日本語を含む機器名・コメント・Params → UTF-8 で正しくラウンドトリップ
    [Fact]
    public void JapaneseDeviceNameAndLabel_RoundTrip()
    {
        var doc = new LadderDocument();
        doc.Info.Title = "制御回路図（テスト）";
        var sheet = new Sheet { PageNumber = 1, Grid = new GridSpec { Columns = 4 } };
        var elem = new ElementInstance
        {
            Kind = ElementKind.Coil,
            Pos = new GridPos(0, 3),
            DeviceName = "電磁弁１",
            Comment = "起動用SOL",
        };
        elem.Params["Label"] = "起動（運転中）";
        sheet.Elements.Add(elem);
        doc.Sheets.Add(sheet);

        var json = GcadSerializer.Serialize(doc);
        var back = GcadSerializer.Deserialize(json);

        Assert.Equal("制御回路図（テスト）", back.Info.Title);
        var e = back.Sheets[0].Elements[0];
        Assert.Equal("電磁弁１", e.DeviceName);
        Assert.Equal("起動用SOL", e.Comment);
        Assert.Equal("起動（運転中）", e.Params["Label"]);
    }

    // C-6: 挿入画像（ImageInsert）のラウンドトリップ。旧ファイル（Images なし）は空配列で互換。
    [Fact]
    public void ImageInsert_RoundTrips()
    {
        var doc = MakeSampleDocument();
        doc.Sheets[0].Images.Add(new ImageInsert
        {
            FilePath = @"C:\drawings\background.png",
            XMm = 10.5, YMm = 20.0, WidthMm = 80.0, HeightMm = 60.0, IsTracingOnly = false,
        });

        var json = GcadSerializer.Serialize(doc);
        var back = GcadSerializer.Deserialize(json);

        var img = Assert.Single(back.Sheets[0].Images);
        Assert.Equal(@"C:\drawings\background.png", img.FilePath);
        Assert.Equal(10.5, img.XMm);
        Assert.Equal(80.0, img.WidthMm);
        Assert.False(img.IsTracingOnly);
    }

    [Fact]
    public void ImageInsert_DefaultIsTracingOnly()
    {
        var img = new ImageInsert();
        Assert.True(img.IsTracingOnly);
    }

    [Fact]
    public void Deserialize_NoImagesField_ReturnsEmptyList()
    {
        // 旧ファイル互換: images フィールドが無い JSON（FreeLines 追加前の形式を模す）でも
        // 例外なく空リストで復元される（Sheet.Images の既定値 = new()）。
        var doc = MakeSampleDocument();
        var json = GcadSerializer.Serialize(doc);
        string jsonWithoutImages = System.Text.RegularExpressions.Regex.Replace(
            json, "\"images\"\\s*:\\s*\\[\\s*\\]\\s*,?", "");

        var back = GcadSerializer.Deserialize(jsonWithoutImages);
        Assert.Empty(back.Sheets[0].Images);
    }

    // C-5: 大量ドキュメント（20シート×100要素）→ 3 秒以内にシリアライズ・デシリアライズ完了
    [Fact]
    public void LargeDocument_SerializesInReasonableTime()
    {
        var doc = new LadderDocument();
        for (int p = 1; p <= 20; p++)
        {
            var sheet = new Sheet { PageNumber = p, Grid = new GridSpec { Rows = 50, Columns = 6 } };
            for (int row = 0; row < 50; row++)
            {
                sheet.Elements.Add(new ElementInstance
                {
                    Kind = ElementKind.ContactNO,
                    Pos = new GridPos(row, 0),
                    DeviceName = $"CR{p}_{row}",
                });
                sheet.Elements.Add(new ElementInstance
                {
                    Kind = ElementKind.Coil,
                    Pos = new GridPos(row, 5),
                    DeviceName = $"CR{p}_{row}",
                });
            }
            doc.Sheets.Add(sheet);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var json = GcadSerializer.Serialize(doc);
        var back = GcadSerializer.Deserialize(json);
        sw.Stop();

        Assert.Equal(20, back.Sheets.Count);
        Assert.Equal(100, back.Sheets[0].Elements.Count); // 50行×2要素
        Assert.True(sw.Elapsed.TotalSeconds < 3.0,
            $"シリアライズ・デシリアライズが3秒以内に完了すべき。実際: {sw.Elapsed.TotalSeconds:F2}s");
    }
}
