using GuiEcad.Model;
using GuiEcad.Persistence;
using Xunit;

namespace GuiEcad.Tests;

/// <summary>図形フォルダ管理（<see cref="PartFolderStore"/>）と単体 .gcadpart I/O のテスト。</summary>
public class PartFolderStoreTests : IDisposable
{
    private readonly string _root;
    private readonly PartFolderStore _store;

    public PartFolderStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GuiEcadTest_" + Guid.NewGuid().ToString("N"), "図形");
        _store = new PartFolderStore(_root);
    }

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_root);
        if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }

    private static PartDefinition SamplePart(string id = "p1", string name = "テスト接点") => new()
    {
        Id = id,
        Name = name,
        WidthCells = 1,
        HeightCells = 1,
        Role = PartRole.ContactNO,
        Ports = { new PortDef("L", 0, 0), new PortDef("R", 0, 1) },
        Primitives = { new PartLine(0, 0, 1, 0), new PartRect(0.3, -0.2, 0.4, 0.4) },
    };

    [Fact]
    public void SaveCustom_ThenEnumerate_RoundTrips()
    {
        var part = SamplePart();
        _store.SaveCustom(part);

        var entries = _store.Enumerate();
        var e = Assert.Single(entries);
        Assert.Equal("自作", e.Category);
        Assert.Equal(part.Id, e.Definition.Id);
        Assert.Equal(part.Name, e.Definition.Name);
        Assert.Equal(2, e.Definition.Ports.Count);
        Assert.IsType<PartRect>(e.Definition.Primitives[1]);   // 多態が保たれている
    }

    [Fact]
    public void SeedBasics_IsIdempotent()
    {
        int first = _store.SeedBasics();
        Assert.True(first > 0);

        // 2回目は重複展開しない
        int second = _store.SeedBasics();
        Assert.Equal(0, second);

        // 走査件数は基本図形の点数と一致（直下＝カテゴリ空）
        var basics = _store.Enumerate().Where(e => e.Category == "").ToList();
        Assert.Equal(BasicPartTemplates.All().Count, basics.Count);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        _store.SaveCustom(SamplePart());
        var path = Assert.Single(_store.Enumerate()).FilePath;

        _store.Delete(path);
        Assert.Empty(_store.Enumerate());
    }

    [Fact]
    public void EnsureFolders_CreatesRootAndCustom()
    {
        _store.EnsureFolders();
        Assert.True(Directory.Exists(_store.RootDir));
        Assert.True(Directory.Exists(_store.CustomDir));
    }

    [Fact]
    public void SerializeOne_RoundTrips()
    {
        var part = SamplePart();
        string json = PartLibrarySerializer.SerializeOne(part);
        var back = PartLibrarySerializer.DeserializeOne(json);

        Assert.Equal(part.Id, back.Id);
        Assert.Equal(part.Role, back.Role);
        Assert.Equal(2, back.Primitives.Count);
        Assert.IsType<PartRect>(back.Primitives[1]);
    }
}
