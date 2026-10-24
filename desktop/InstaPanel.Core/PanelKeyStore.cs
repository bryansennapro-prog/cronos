using System.Security.Cryptography;
using System.Text;

namespace InstaPanel.Core;

/// <summary>
/// Guarda a chave do painel (PANEL_API_KEY) protegida por DPAPI, só legível pelo seu usuário do Windows.
/// O formato é o mesmo de <c>ConvertFrom-SecureString</c> do PowerShell (blob DPAPI em hexadecimal,
/// texto em UTF-16), então a chave salva pelo configurar.ps1 é lida aqui sem passo extra.
/// </summary>
public sealed class PanelKeyStore
{
    public PanelKeyStore(string? directory = null)
    {
        FilePath = Path.Combine(directory ?? AppPaths.DataDirectory, "panel-key.dat");
    }

    public string FilePath { get; }

    public bool Exists => File.Exists(FilePath);

    /// <summary>Retorna a chave, ou null se o arquivo não existir ou não puder ser lido por este usuário.</summary>
    public string? Read()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var hex = File.ReadAllText(FilePath).Trim();
            var plain = ProtectedData.Unprotect(Convert.FromHexString(hex), null, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.Unicode.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public void Write(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var plain = Encoding.Unicode.GetBytes(key.Trim());
        try
        {
            var blob = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, Convert.ToHexStringLower(blob));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
