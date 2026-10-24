using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace InstaPanel.App;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Precisa ser a primeira coisa: durante instalação, atualização e desinstalação o Velopack
        // chama o .exe com argumentos especiais e encerra o processo aqui mesmo.
        VelopackApp.Build().Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
