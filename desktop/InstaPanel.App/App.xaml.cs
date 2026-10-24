using Microsoft.UI.Xaml;

namespace InstaPanel.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        Services.WindowsToast.Register(); // antes da janela: define o AppUserModelID do processo
        // Login do Instagram (WebView2): cookies e cache na pasta de dados, não ao lado do .exe
        // (que é trocada a cada atualização).
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
            Path.Combine(Core.AppPaths.DataDirectory, "WebView2"));
    }

    public static MainWindow? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        if (Environment.GetEnvironmentVariable("INSTAPANEL_BACKGROUND") == "1")
        {
            // Testes automatizados: abre atrás das outras janelas, sem roubar o foco de quem está usando o PC.
            MainWindow.AppWindow.Show(activateWindow: false);
            MainWindow.AppWindow.MoveInZOrderAtBottom();
        }
        else
        {
            MainWindow.Activate();
        }

        Services.PostSync.NotificationClicked += (_, _) =>
        {
            MainWindow.Activate();
            MainWindow.NavigateTo("history");
        };
        Services.PostSync.Start(MainWindow.DispatcherQueue);
        Services.AppUpdates.StartBackgroundChecks(MainWindow.DispatcherQueue);
        MainWindow.Closed += (_, _) => Services.PostSync.Stop();

        // "InstaPanel.exe arquivo1 arquivo2" (ou "Abrir com") importa os arquivos para a Biblioteca.
        var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();
        if (files.Count > 0)
        {
            Pages.LibraryPage.PendingImport = files;
            MainWindow.NavigateTo("library");
        }
    }
}
