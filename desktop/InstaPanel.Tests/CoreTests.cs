using System.Diagnostics;
using InstaPanel.Core;

namespace InstaPanel.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "instapanel-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void PanelKey_roundtrips_and_file_is_not_plaintext()
    {
        var store = new PanelKeyStore(_dir);
        store.Write("chave-super-secreta-123");

        Assert.Equal("chave-super-secreta-123", store.Read());
        Assert.DoesNotContain("chave-super-secreta", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void PanelKey_missing_or_corrupt_returns_null()
    {
        var store = new PanelKeyStore(_dir);
        Assert.Null(store.Read());

        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, "nao-e-hex");
        Assert.Null(store.Read());
    }

    /// <summary>A chave salva pelo worker/scripts/configurar.ps1 (ConvertFrom-SecureString) precisa ser lida pelo app, e vice-versa.</summary>
    [Fact]
    public void PanelKey_is_compatible_with_PowerShell_SecureString()
    {
        Directory.CreateDirectory(_dir);
        var psFile = Path.Combine(_dir, "panel-key.dat");
        RunPowerShell($"ConvertTo-SecureString 'chave-do-powershell' -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -LiteralPath '{psFile.Replace("'", "''")}'");

        var store = new PanelKeyStore(_dir);
        Assert.Equal("chave-do-powershell", store.Read());

        store.Write("chave-do-app");
        var fromPs = RunPowerShell(
            $"$s = Get-Content -LiteralPath '{psFile.Replace("'", "''")}' | ConvertTo-SecureString; " +
            "[Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s))");
        Assert.Equal("chave-do-app", fromPs.Trim());
    }

    [Theory]
    [InlineData("https://instapanel.x.workers.dev", "https://instapanel.x.workers.dev/")]
    [InlineData("  https://instapanel.x.workers.dev/  ", "https://instapanel.x.workers.dev/")]
    [InlineData("https://instapanel.x.workers.dev?x=1#frag", "https://instapanel.x.workers.dev/")]
    [InlineData("http://127.0.0.1:8787", "http://127.0.0.1:8787/")]
    public void WorkerUrl_is_normalized(string input, string expected)
    {
        Assert.True(SettingsStore.TryNormalizeWorkerUrl(input, out var url));
        Assert.Equal(expected, url.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("instapanel.workers.dev")]
    [InlineData("http://instapanel.x.workers.dev")]
    [InlineData("ftp://example.com")]
    public void WorkerUrl_rejects_invalid_or_insecure(string input)
    {
        Assert.False(SettingsStore.TryNormalizeWorkerUrl(input, out _));
    }

    [Fact]
    public void Settings_default_then_roundtrip()
    {
        var store = new SettingsStore(_dir);
        Assert.Equal(PanelSettings.DefaultWorkerUrl, store.Load().WorkerUrl);

        store.Save(new PanelSettings { WorkerUrl = "https://outro.workers.dev" });
        Assert.Equal("https://outro.workers.dev", new SettingsStore(_dir).Load().WorkerUrl);
    }

    private static string RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"PowerShell falhou: {stderr}");
        return stdout;
    }
}
