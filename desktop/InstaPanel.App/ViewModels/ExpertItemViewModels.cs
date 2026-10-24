using InstaPanel.App.Services;
using InstaPanel.Core.Expert;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace InstaPanel.App.ViewModels;

// Itens das listas da tela Especialista (o relatório vem de InstaPanel.Core.Expert).

public sealed class ExpertMetricViewModel(Metric metric)
{
    public string Label => metric.Label;

    public string Value => metric.Value;

    public string Note => metric.Note ?? "";

    public bool HasNote => metric.Note is not null;
}

public sealed class ExpertFindingViewModel(Finding finding)
{
    public string Title => finding.Title;

    public string Detail => finding.Detail;

    public string Action => finding.Action ?? "";

    public bool HasAction => finding.Action is not null;

    public string Glyph => finding.Level switch
    {
        FindingLevel.Good => "",
        FindingLevel.Warning => "",
        FindingLevel.Problem => "",
        _ => "",
    };

    public Brush Brush => Resource(finding.Level switch
    {
        FindingLevel.Good => "SystemFillColorSuccessBrush",
        FindingLevel.Warning => "SystemFillColorCautionBrush",
        FindingLevel.Problem => "SystemFillColorCriticalBrush",
        _ => "TextFillColorSecondaryBrush",
    });

    public string LevelText => finding.Level switch
    {
        FindingLevel.Good => "Ok",
        FindingLevel.Warning => "Atenção",
        FindingLevel.Problem => "Problema",
        _ => "Info",
    };

    internal static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}

public sealed class ExpertSectionViewModel(ExpertSection section)
{
    public string Title => section.Title;

    public string Skill => section.Skill;

    public string Summary => section.Summary;

    public string ScoreText => section.Score is { } s ? $"{s}" : "—";

    public Brush ScoreBrush => ExpertFindingViewModel.Resource(section.Score switch
    {
        null => "TextFillColorSecondaryBrush",
        >= 80 => "SystemFillColorSuccessBrush",
        >= 50 => "SystemFillColorCautionBrush",
        _ => "SystemFillColorCriticalBrush",
    });

    public string CountsText
    {
        get
        {
            var problems = section.Findings.Count(f => f.Level == FindingLevel.Problem);
            var warnings = section.Findings.Count(f => f.Level == FindingLevel.Warning);
            if (problems + warnings == 0) return "Sem pendências";
            var parts = new List<string>();
            if (problems > 0) parts.Add(problems == 1 ? "1 problema" : $"{problems} problemas");
            if (warnings > 0) parts.Add(warnings == 1 ? "1 ponto de atenção" : $"{warnings} pontos de atenção");
            return string.Join(" · ", parts);
        }
    }

    public string Glyph => section.Key switch
    {
        "profile" => "",
        "audience" => "",
        "captions" => "",
        "hooks" => "",
        "hashtags" => "",
        "humanizer" => "",
        "carousel" => Formatting.PostTypeGlyph("CAROUSEL"),
        "planner" => "",
        _ => "",
    };

    /// <summary>Abre já expandido o que tem problema.</summary>
    public bool IsExpanded => section.Findings.Any(f => f.Level == FindingLevel.Problem);

    public IReadOnlyList<ExpertMetricViewModel> Metrics { get; } = section.Metrics.Select(m => new ExpertMetricViewModel(m)).ToList();

    public IReadOnlyList<ExpertFindingViewModel> Findings { get; } = section.Findings.Select(f => new ExpertFindingViewModel(f)).ToList();
}

public sealed class ExpertTopPostViewModel(RankedPost ranked)
{
    private ImageSource? _thumbnail;

    public string TypeText => ProfileExpert.FormatName(ranked.Post.Format);

    public string TypeGlyph => ranked.Post.Format switch
    {
        MediaFormat.Reel => Formatting.PostTypeGlyph("REELS"),
        MediaFormat.Carousel => Formatting.PostTypeGlyph("CAROUSEL"),
        _ => Formatting.PostTypeGlyph("IMAGE"),
    };

    public string DateText => ranked.Post.PublishedAt.ToLocalTime().ToString("dd/MM/yyyy", Formatting.PtBr);

    public string Caption => CaptionAnalysis.Snippet(ranked.Post.Caption, 120);

    public string StatsText
    {
        get
        {
            var text = $"{ranked.Post.Interactions?.ToString("N0", Formatting.PtBr)} interações";
            if (ranked.VsMedian is { } v) text += $" · {v.ToString("0.0", Formatting.PtBr)}× a mediana";
            if (ranked.EngagementRate is { } r) text += $" · {r.ToString("0.0", Formatting.PtBr)}% dos seguidores";
            return text;
        }
    }

    public string FormulaText => ranked.Formula.Code.Length == 0 ? ranked.Formula.Name : $"{ranked.Formula.Label} · busca {ranked.Formula.Goal}";

    public Uri? Permalink => Uri.TryCreate(ranked.Post.Permalink, UriKind.Absolute, out var u) ? u : null;

    public bool HasPermalink => Permalink is not null;

    public ImageSource? Thumbnail => _thumbnail ??=
        Uri.TryCreate(ranked.Post.ThumbnailUrl, UriKind.Absolute, out var u) ? new BitmapImage(u) { DecodePixelWidth = 112 } : null;
}

public sealed class ExpertPlanSlotViewModel(PlanSlot slot)
{
    public string DayText => slot.Date.ToDateTime(TimeOnly.MinValue).ToString("ddd, dd/MM", Formatting.PtBr);

    public string Time => slot.Time;

    public string FormatText => ProfileExpert.FormatName(slot.Format);

    public string FormatGlyph => slot.Format switch
    {
        MediaFormat.Reel => Formatting.PostTypeGlyph("REELS"),
        MediaFormat.Carousel => Formatting.PostTypeGlyph("CAROUSEL"),
        MediaFormat.Story => Formatting.PostTypeGlyph("STORIES"),
        _ => Formatting.PostTypeGlyph("IMAGE"),
    };

    public string Details => $"{slot.Pillar} · {slot.Formula.Label}" + (slot.Goal.Length > 0 ? $" · busca {slot.Goal}" : "");

    public string Idea => slot.Idea;

    public string AlreadyPlanned => slot.AlreadyPlanned ?? "";

    public bool HasAlreadyPlanned => slot.AlreadyPlanned is not null;
}
