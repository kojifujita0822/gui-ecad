using GuiEcad.Model;

namespace GuiEcad.Simulation;

/// <summary>図面上のある位置（ページ番号＋回路番号）への参照。</summary>
public readonly record struct CircuitRef(int PageNumber, int CircuitNumber)
{
    public override string ToString() => $"{PageNumber}-{CircuitNumber}";
}

/// <summary>1機器のクロスリファレンス情報。コイル所在と接点所在の一覧を保持する。</summary>
public sealed class CrossRefEntry
{
    public string DeviceName { get; init; } = "";
    /// <summary>コイル（負荷）が現れる箇所。</summary>
    public List<CircuitRef> Coils { get; } = new();
    /// <summary>接点が現れる箇所。</summary>
    public List<CircuitRef> Contacts { get; } = new();
}

/// <summary>
/// ドキュメント全体のクロスリファレンス。
/// <see cref="CircuitNumberer.Number"/> でシートが採番済みであることが前提。
/// </summary>
public sealed class CrossReference
{
    private readonly Dictionary<string, CrossRefEntry> _entries = new();

    /// <summary>全エントリを機器名昇順で返す。</summary>
    public IEnumerable<CrossRefEntry> Entries =>
        _entries.Values.OrderBy(e => e.DeviceName, StringComparer.OrdinalIgnoreCase);

    /// <summary>コイルが存在するエントリのみ（一覧表の対象）を機器名昇順で返す。</summary>
    public IEnumerable<CrossRefEntry> CoilEntries =>
        Entries.Where(e => e.Coils.Count > 0);

    public bool TryGet(string deviceName, out CrossRefEntry entry) =>
        _entries.TryGetValue(deviceName, out entry!);

    internal CrossRefEntry GetOrAdd(string deviceName)
    {
        if (!_entries.TryGetValue(deviceName, out var entry))
            _entries[deviceName] = entry = new CrossRefEntry { DeviceName = deviceName };
        return entry;
    }
}

/// <summary>
/// LadderDocument からクロスリファレンスを生成する。
/// 各要素の回路番号は <see cref="Sheet.Lines"/> を参照する（<see cref="CircuitNumberer"/> 呼び出し後に使用）。
/// 回路番号未割り当ての行（Lines に存在しない）は CircuitNumber=0 で扱う。
/// </summary>
public static class CrossReferenceBuilder
{
    public static CrossReference Build(LadderDocument doc, PartLibrary? lib = null)
    {
        var xref = new CrossReference();

        foreach (var sheet in doc.Sheets.OrderBy(s => s.PageNumber))
        {
            // Row → CircuitNumber のルックアップ
            var circuitByRow = sheet.Lines.ToDictionary(l => l.Row, l => l.CircuitNumber);

            foreach (var elem in sheet.Elements)
            {
                if (string.IsNullOrEmpty(elem.DeviceName)) continue;

                var effectiveKind = PartResolver.ComponentKind(elem, lib);
                bool isLoad = ElementCatalog.IsLoad(effectiveKind);
                bool isContact = ElementCatalog.IsContact(effectiveKind);
                if (!isLoad && !isContact) continue;

                int circuitNo = circuitByRow.GetValueOrDefault(elem.Pos.Row, 0);
                var cref = new CircuitRef(sheet.PageNumber, circuitNo);
                var entry = xref.GetOrAdd(elem.DeviceName);

                if (isLoad)
                    entry.Coils.Add(cref);
                else
                    entry.Contacts.Add(cref);
            }
        }

        return xref;
    }
}
