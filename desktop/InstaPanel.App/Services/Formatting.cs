using System.Globalization;
using InstaPanel.Cloud;
using InstaPanel.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace InstaPanel.App.Services;

public static class Formatting
{
    public static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / 1024d / 1024 / 1024).ToString("0.0", PtBr) + " GB",
        >= 1024 * 1024 => (bytes / 1024d / 1024).ToString("0.0", PtBr) + " MB",
        _ => Math.Max(1, bytes / 1024) + " KB",
    };

    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", PtBr) : d.ToString(@"m\:ss", PtBr);

    public static string Dimensions(MediaInfo info) => $"{info.Width}×{info.Height}";

    public static string RelativeTime(DateTimeOffset when)
    {
        var diff = DateTimeOffset.Now - when;
        if (diff < TimeSpan.FromMinutes(1)) return "agora";
        if (diff < TimeSpan.FromHours(1)) return $"há {(int)diff.TotalMinutes} min";
        if (diff < TimeSpan.FromDays(1)) return $"há {(int)diff.TotalHours} h";
        return DateTime(when);
    }

    /// <summary>"22/10 às 18:00" (com o ano se não for o atual).</summary>
    public static string DateTime(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var format = local.Year == System.DateTime.Now.Year ? "dd/MM 'às' HH:mm" : "dd/MM/yyyy 'às' HH:mm";
        return local.ToString(format, PtBr);
    }

    /// <summary>"hoje às 18:00", "amanhã às 09:00", "sex, 24/10 às 18:00".</summary>
    public static string Friendly(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var days = (local.Date - System.DateTime.Today).Days;
        var time = local.ToString("HH:mm", PtBr);
        return days switch
        {
            0 => $"hoje às {time}",
            1 => $"amanhã às {time}",
            -1 => $"ontem às {time}",
            > 1 and < 7 => local.ToString("ddd, dd/MM", PtBr) + $" às {time}",
            _ => DateTime(when),
        };
    }

    public static string PostState(string state) => state switch
    {
        PostStates.Scheduled => "Agendado",
        PostStates.Preparing => "Enviando para a Meta",
        PostStates.Processing => "Meta processando a mídia",
        PostStates.Ready => "Pronto para publicar",
        PostStates.Publishing => "Publicando",
        PostStates.Published => "Publicado",
        PostStates.Failed => "Falhou",
        PostStates.Canceled => "Cancelado",
        _ => state,
    };

    public static string PostStateGlyph(string state) => state switch
    {
        PostStates.Published => "",   // check
        PostStates.Failed => "",      // erro
        PostStates.Canceled => "",    // x
        PostStates.Scheduled => "",   // relógio
        _ => "",                      // sincronizando
    };

    public static Brush PostStateBrush(string state) => (Brush)Application.Current.Resources[state switch
    {
        PostStates.Published => "SystemFillColorSuccessBrush",
        PostStates.Failed => "SystemFillColorCriticalBrush",
        PostStates.Canceled => "SystemFillColorNeutralBrush",
        PostStates.Scheduled => "AccentFillColorDefaultBrush",
        _ => "SystemFillColorCautionBrush",
    }];

    public static string PostType(string type) => type switch
    {
        "REELS" => "Reel",
        "CAROUSEL" => "Carrossel",
        "STORIES" => "Story",
        _ => "Imagem",
    };

    public static string PostTypeGlyph(string type) => type switch
    {
        "REELS" => "",
        "CAROUSEL" => "",
        "STORIES" => "",
        _ => "",
    };

    /// <summary>Linha de horário para listas: "Agendado para amanhã às 18:00", "Publicado hoje às 14:03"…</summary>
    public static string PostWhen(PostInfo post) => post.State switch
    {
        PostStates.Published => $"Publicado {Friendly(post.UpdatedAt)}",
        PostStates.Canceled => $"Cancelado {Friendly(post.UpdatedAt)} (era para {Friendly(post.ScheduledAt)})",
        PostStates.Failed => $"Falhou {Friendly(post.UpdatedAt)}",
        _ when post.ScheduledAt > DateTimeOffset.Now.AddMinutes(1) => $"Agendado para {Friendly(post.ScheduledAt)}",
        _ => $"{PostState(post.State)}…",
    };
}
