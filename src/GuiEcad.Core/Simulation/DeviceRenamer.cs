using GuiEcad.Model;

namespace GuiEcad.Simulation;

/// <summary>
/// ドキュメント全体の DeviceName を一括置換するユーティリティ。
/// 検索は大文字小文字を区別しない（実機器名は慣習的に大文字だが入力ゆれに対応）。
/// DeviceTable も同時に更新する。
/// </summary>
public static class DeviceRenamer
{
    /// <summary>
    /// <paramref name="doc"/> 内の全シート・全要素の DeviceName が
    /// <paramref name="from"/> に一致するものを <paramref name="to"/> に置換する。
    /// DeviceTable のキーも移行する。
    /// </summary>
    /// <returns>置換した要素数（DeviceTable の移行は含まない）。</returns>
    public static int Rename(LadderDocument doc, string from, string to)
    {
        if (string.IsNullOrEmpty(from) || from == to) return 0;

        int count = 0;
        foreach (var sheet in doc.Sheets)
        {
            foreach (var elem in sheet.Elements)
            {
                if (string.Equals(elem.DeviceName, from, StringComparison.OrdinalIgnoreCase))
                {
                    elem.DeviceName = to;
                    count++;
                }
            }
        }

        // DeviceTable のキー移行（from が登録されていれば to へ移す。大文字小文字を区別しない）
        var key = doc.Devices.ByName.Keys
            .FirstOrDefault(k => string.Equals(k, from, StringComparison.OrdinalIgnoreCase));
        if (key is not null && doc.Devices.ByName.TryGetValue(key, out var device))
        {
            doc.Devices.ByName.Remove(key);
            device.Name = to;
            doc.Devices.ByName[to] = device;
        }

        return count;
    }

    /// <summary>
    /// <paramref name="doc"/> 内で <paramref name="name"/> に一致する要素をすべて返す。
    /// 検索バーのハイライト用。
    /// </summary>
    public static IReadOnlyList<(Sheet Sheet, ElementInstance Element)> Find(
        LadderDocument doc, string name)
    {
        if (string.IsNullOrEmpty(name)) return Array.Empty<(Sheet, ElementInstance)>();

        var results = new List<(Sheet, ElementInstance)>();
        foreach (var sheet in doc.Sheets.OrderBy(s => s.PageNumber))
            foreach (var elem in sheet.Elements)
                if (string.Equals(elem.DeviceName, name, StringComparison.OrdinalIgnoreCase))
                    results.Add((sheet, elem));
        return results;
    }
}
