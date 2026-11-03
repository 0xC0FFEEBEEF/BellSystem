using System.Text.Json;

namespace SchoolBell.Client.Core;

/// Tiny durable JSON file store with atomic writes (temp file + rename/replace).
public sealed class JsonStore<T>
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; }

    public JsonStore(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
    }

    public T? Load()
    {
        if (!File.Exists(Path)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(Path), Opts);
        }
        catch (JsonException)
        {
            return default; // a corrupt store file is treated as absent (callers log)
        }
    }

    public void Save(T value)
    {
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opts));
        File.Move(tmp, Path, overwrite: true);
    }
}
