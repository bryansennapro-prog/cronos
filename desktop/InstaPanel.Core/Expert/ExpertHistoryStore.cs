using System.Text.Json;

namespace InstaPanel.Core.Expert;

/// <summary>Última análise salva em %LOCALAPPDATA%\InstaPanel\especialista.json (para comparar seguidores).</summary>
public sealed class ExpertHistoryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ExpertHistoryStore(string? directory = null)
    {
        FilePath = Path.Combine(directory ?? AppPaths.DataDirectory, "especialista.json");
    }

    public string FilePath { get; }

    public ExpertSnapshot? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            return JsonSerializer.Deserialize<ExpertSnapshot>(File.ReadAllText(FilePath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(ExpertSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
