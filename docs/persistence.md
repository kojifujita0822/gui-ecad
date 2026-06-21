# 永続化フォーマット

- **形式: JSON（`System.Text.Json`・標準ライブラリのみ）／単一ファイル**（確定）。外部依存なし・人間可読・差分容易。
- **ファイル拡張子: `.GCAD`**（確定）。
- **保存対象**: `DocumentInfo` / `Settings` / `DeviceTable`（BOM=型式/メーカー/数量を含む） / `Library` / `Sheets`（Elements・Connectors・Frames・Lines・GridSpec・BusConfig）。
- **保存しない（派生・再計算）**: `Netlist` / `Net` / `SimState`。開く度に幾何から再構築。
- **設計上の備え**:
  - 先頭に **`schemaVersion`** を持ちマイグレーション対応。
  - 安定ID（GUID）で要素識別。`ElementKind` は enum、`Params` は辞書（ポリモーフィズム不要＝シリアライズ容易）。
  - 将来 多シート・サムネ等が増えたら **zip パッケージ化へ拡張**できる構造（ルートに `DocumentInfo` を manifest 相当で配置）。

```jsonc
{
  "schemaVersion": 1,
  "info": { "title": "コンプレッサー遠方運転盤", "drawingNo": "...", "designer": "赤松", "date": "2022-08-25" },
  "settings": { "defaultBus": { "left": "N24", "right": "P24" } },
  "devices": [ { "name": "CR11", "class": "Relay", "partId": "p-001" } ],
  "parts":   [ { "id": "p-001", "name": "リレー", "maker": "オムロン", "model": "MY2N", "rating": "DC24V", "quantity": 5 } ],
  "sheets":  [ { "id": "...", "pageNumber": 7, "name": "制御回路図",
                 "grid": { "rows": 22, "columns": 40 },
                 "bus": { "left": "N24", "right": "P24", "powerLabel": "DC24V" },
                 "elements": [ { "id":"...", "kind":"ContactNO", "pos":{"row":6,"col":3}, "cellWidth":1, "deviceName":"CR11", "params":{} } ],
                 "connectors": [ { "column": 5, "topRow": 3, "bottomRow": 5 } ],
                 "frames": [ { "label":"中継ボックス", "topLeft":{"row":2,"col":2}, "width":10, "height":6 } ],
                 "lines": [ { "row": 6, "circuitNumber": 14 } ] } ]
}
```
