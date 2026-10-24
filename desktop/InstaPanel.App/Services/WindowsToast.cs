using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace InstaPanel.App.Services;

/// <summary>
/// Notificações do Windows para app sem pacote (MSIX). O AppNotificationManager do Windows App SDK
/// não registra em app desempacotado (COMException 0x8007007E), então usamos a API clássica com
/// um AppUserModelID próprio registrado em HKCU — o mesmo que o ToastNotificationManagerCompat faz.
/// </summary>
public static class WindowsToast
{
    public const string AppId = "InstaPanel.Desktop";

    private static bool _registered;
    private static string? _problem;

    /// <summary>Chamado no clique de uma notificação (enquanto o app está aberto).</summary>
    public static event EventHandler? Clicked;

    /// <summary>
    /// Registra o app (uma vez). Chamar antes de criar a janela, pois define o AppUserModelID do processo.
    /// Devolve o problema, ou null se deu certo.
    /// </summary>
    public static string? Register()
    {
        if (_registered || _problem is not null) return _problem;
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppId);
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
            key.SetValue("DisplayName", "Cronos");
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "NotificationIcon.png");
            if (File.Exists(icon)) key.SetValue("IconUri", icon);
            _registered = true;
            return null;
        }
        catch (Exception ex)
        {
            return _problem = $"Não foi possível registrar as notificações: {ex.Message}";
        }
    }

    /// <summary>Mostra a notificação. Devolve o problema, ou null se foi entregue ao Windows.</summary>
    public static string? Show(string title, string body)
    {
        if (!_registered) return "Notificações não registradas.";
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml($"""
                <toast launch="open=history">
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{Escape(title)}</text>
                      <text>{Escape(body)}</text>
                    </binding>
                  </visual>
                </toast>
                """);
            var toast = new ToastNotification(xml);
            toast.Activated += (_, _) => Clicked?.Invoke(null, EventArgs.Empty);
            var notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            if (BlockedSetting(notifier) is { } blocked)
                return $"O Windows está bloqueando as notificações do Cronos ({blocked}). Ative em Configurações do Windows › Sistema › Notificações.";
            notifier.Show(toast);
            return null;
        }
        catch (Exception ex)
        {
            return $"Falha ao mostrar a notificação ({ex.GetType().Name} 0x{ex.HResult:X8}): {ex.Message}".Trim();
        }
    }

    /// <summary>
    /// Bloqueio pelo usuário/política, ou null. Na primeira notificação o Windows ainda não conhece o app
    /// e a leitura falha (0x80070490); nesse caso segue e deixa o Windows decidir.
    /// </summary>
    private static NotificationSetting? BlockedSetting(ToastNotifier notifier)
    {
        try
        {
            var setting = notifier.Setting;
            return setting == NotificationSetting.Enabled ? null : setting;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);
}
