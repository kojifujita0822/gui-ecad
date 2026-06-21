// PDF 生成テストは PDFsharp のグローバル状態（FontResolver）とファイルIOを共有するため、
// テストクラスの並列実行を無効化して競合を防ぐ。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
