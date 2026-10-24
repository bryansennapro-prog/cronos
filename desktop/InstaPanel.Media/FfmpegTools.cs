using System.Diagnostics;
using InstaPanel.Core;

namespace InstaPanel.Media;

/// <summary>Localiza e executa ffmpeg/ffprobe.</summary>
public sealed class FfmpegTools
{
    private FfmpegTools(string directory)
    {
        FfmpegPath = Path.Combine(directory, "ffmpeg.exe");
        FfprobePath = Path.Combine(directory, "ffprobe.exe");
    }

    public string FfmpegPath { get; }

    public string FfprobePath { get; }

    /// <summary>
    /// Procura, nesta ordem: pasta do app (ffmpeg\), %LOCALAPPDATA%\InstaPanel\ffmpeg\bin, PATH,
    /// e a pasta onde o winget instala o pacote Gyan.FFmpeg.
    /// </summary>
    public static FfmpegTools? TryLocate()
    {
        foreach (var dir in CandidateDirectories())
        {
            if (File.Exists(Path.Combine(dir, "ffmpeg.exe")) && File.Exists(Path.Combine(dir, "ffprobe.exe")))
                return new FfmpegTools(dir);
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        yield return Path.Combine(AppPaths.DataDirectory, "ffmpeg", "bin");

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return dir;

        // O PATH do processo pode estar desatualizado se o FFmpeg foi instalado depois de abrir o terminal/Explorer.
        var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        foreach (var dir in userPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Environment.ExpandEnvironmentVariables(dir);

        var winget = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            foreach (var pkg in Directory.EnumerateDirectories(winget, "Gyan.FFmpeg*"))
            foreach (var build in Directory.EnumerateDirectories(pkg, "ffmpeg-*").OrderDescending())
                yield return Path.Combine(build, "bin");
        }
    }

    internal Task<ProcessResult> RunFfmpegAsync(IEnumerable<string> args, Action<string>? onStdoutLine, CancellationToken ct) =>
        RunAsync(FfmpegPath, args, onStdoutLine, ct);

    internal Task<ProcessResult> RunFfprobeAsync(IEnumerable<string> args, CancellationToken ct) =>
        RunAsync(FfprobePath, args, null, ct);

    private static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, Action<string>? onStdoutLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new System.Text.StringBuilder();
        var stderrTail = new Queue<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (onStdoutLine is not null) onStdoutLine(e.Data);
            else stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderrTail)
            {
                stderrTail.Enqueue(e.Data);
                if (stderrTail.Count > 20) stderrTail.Dequeue();
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        string tail;
        lock (stderrTail) tail = string.Join(Environment.NewLine, stderrTail);
        return new ProcessResult(process.ExitCode, stdout.ToString(), tail);
    }
}

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErrTail);

public sealed class MediaToolException(string message) : Exception(message);
