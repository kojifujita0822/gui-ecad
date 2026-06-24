using Xunit;

// UI オートメーションはデスクトップを占有するため、テスト同士を並列実行しない。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
