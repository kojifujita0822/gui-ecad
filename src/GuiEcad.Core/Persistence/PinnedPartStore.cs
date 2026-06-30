using System.Text.Json;

namespace GuiEcad.Persistence;

public sealed class PinnedPartStore
{
    private readonly string _path;

    public PinnedPartStore(string path) => _path = path;

    public static PinnedPartStore CreateDefault()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return new PinnedPartStore(Path.Combine(docs, "GuiEcad", "pinned-parts.json"));
    }

    public HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new HashSet<string>();
            var json = File.ReadAllText(_path, System.Text.Encoding.UTF8);
            var ids = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            return new HashSet<string>(ids);
        }
        catch { return new HashSet<string>(); }
    }

    public void Save(IEnumerable<string> ids)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path,
                JsonSerializer.Serialize(ids.ToList()),
                System.Text.Encoding.UTF8);
        }
        catch { }
    }
}
