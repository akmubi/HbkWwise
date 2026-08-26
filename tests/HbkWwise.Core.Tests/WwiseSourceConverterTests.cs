namespace HbkWwise.Core.Tests;

public sealed class WwiseSourceConverterTests
{
    [Fact]
    public async Task ConvertAsync_PassesReadyWemThroughWithoutWwise()
    {
        var wem = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wem");
        try
        {
            await File.WriteAllBytesAsync(wem, [1, 2, 3]);
            var result = await WwiseSourceConverter.ConvertAsync(
                [new WwiseSourceInput(42, wem)],
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

            Assert.Equal(Path.GetFullPath(wem), result[42]);
        }
        finally
        {
            File.Delete(wem);
        }
    }

    [Fact]
    public async Task ConvertAsync_RejectsUnsupportedConsumerFormats()
    {
        var aac = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.aac");
        try
        {
            await File.WriteAllBytesAsync(aac, [1, 2, 3]);
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                WwiseSourceConverter.ConvertAsync(
                    [new WwiseSourceInput(42, aac)],
                    Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

            Assert.Contains("MP3", exception.Message);
        }
        finally
        {
            File.Delete(aac);
        }
    }
}
