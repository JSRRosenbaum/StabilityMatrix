using System.Text.Json;
using System.Text.Json.Serialization;

namespace StabilityMatrix.Core.Inference;

internal sealed class ManagedSubmissionJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string path;
    private readonly object sync = new();

    public ManagedSubmissionJournal(string path)
    {
        this.path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
    }

    public Entry? Load()
    {
        lock (sync)
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Managed submission journal '{path}' is empty or invalid.");
        }
    }

    public void Save(Entry entry)
    {
        lock (sync)
        {
            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, entry, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
    }

    internal sealed record Entry(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("prompt_id")] string PromptId,
        [property: JsonPropertyName("state")] string State
    );
}
