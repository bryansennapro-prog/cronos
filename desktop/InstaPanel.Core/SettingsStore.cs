using System.Text.Json;

namespace InstaPanel.Core;

public sealed record PanelSettings
{
    public const string DefaultWorkerUrl = "https://instapanel.instapanel-worker.workers.dev";

    public string WorkerUrl { get; init; } = DefaultWorkerUrl;

    /// <summary>Objetivo escolhido na tela Especialista.</summary>
    public Expert.ProfileGoal ExpertGoal { get; init; } = Expert.ProfileGoal.Grow;
}

/// <summary>Configurações não sensíveis em %LOCALAPPDATA%\InstaPanel\settings.json.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public SettingsStore(string? directory = null)
    {
        FilePath = Path.Combine(directory ?? AppPaths.DataDirectory, "settings.json");
    }

    public string FilePath { get; }

    public PanelSettings Load()
    {
        if (!File.Exists(FilePath)) return new PanelSettings();
        try
        {
            return JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(FilePath), Json) ?? new PanelSettings();
        }
        catch (JsonException)
        {
            return new PanelSettings();
        }
    }

    public void Save(PanelSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Valida e normaliza a URL do Worker (https obrigatório, exceto localhost para testes).</summary>
    public static bool TryNormalizeWorkerUrl(string? input, out Uri url)
    {
        url = null!;
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var parsed)) return false;
        var isLocal = parsed.IsLoopback;
        if (parsed.Scheme != Uri.UriSchemeHttps && !(isLocal && parsed.Scheme == Uri.UriSchemeHttp)) return false;
        url = new Uri(parsed.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        return true;
    }
}
