namespace InstaPanel.Core;

public static class AppPaths
{
    /// <summary>
    /// %LOCALAPPDATA%\InstaPanel — mesma pasta usada por worker/scripts/configurar.ps1.
    /// A variável INSTAPANEL_DATA_DIR troca a pasta (útil para testar sem mexer na biblioteca real).
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("INSTAPANEL_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InstaPanel");
}
