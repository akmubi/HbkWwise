using HbkWwise.Core;

namespace HbkWwise.Core.Tests;

public sealed class IndexStoreTests
{
    [Fact]
    public async Task RoundTrip_PreservesPakSourceAndFingerprint()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hbkwwise-{Guid.NewGuid():N}.json");
        var index = new WwiseIndex(
            DateTimeOffset.UtcNow,
            [],
            [],
            [],
            [],
            [],
            [new PakSource("game.pak", "Hibiki/Content/WwiseAudio/Windows")],
            "ABC123");

        try
        {
            await IndexStore.SaveAsync(index, path);
            var loaded = await IndexStore.LoadAsync(path);

            Assert.Equal(index.Paks, loaded.Paks);
            Assert.Equal(index.SourceFingerprint, loaded.SourceFingerprint);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_CancellationPreservesExistingIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hbkwwise-{Guid.NewGuid():N}.json");
        var original = "existing index";
        var index = new WwiseIndex(DateTimeOffset.UtcNow, [], [], [], [], []);
        using var cancellation = new CancellationTokenSource();

        try
        {
            await File.WriteAllTextAsync(path, original);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => IndexStore.SaveAsync(index, path, cancellation.Token));

            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
