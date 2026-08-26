using System.Text.Json;

namespace HbkWwise.Core;

public static class IndexStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static async Task SaveAsync(
        WwiseIndex index,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var output = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var temporary = $"{output}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, index, Options, cancellationToken);
            }

            File.Move(temporary, output, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static async Task<WwiseIndex> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WwiseIndex>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException($"Index is empty: {path}");
    }
}
