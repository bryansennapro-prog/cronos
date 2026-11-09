using CommunityToolkit.Mvvm.ComponentModel;
using InstaPanel.App.Services;
using InstaPanel.Core.Expert;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace InstaPanel.App.ViewModels;

// Itens da tela Especialista. O relatório vem de InstaPanel.Core.Expert; aqui só viram textos, ícones e cores.

internal static class ExpertLook
{
    public static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    public static string AreaGlyph(string key) => key switch
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

    public static string FormatGlyph(MediaFormat format) => format switch
    {
        MediaFormat.Reel => Formatting.PostTypeGlyph("REELS"),
        MediaFormat.Carousel => Formatting.PostTypeGlyph("CAROUSEL"),
        MediaFormat.Story => Formatting.PostTypeGlyph("STORIES"),
        _ => Formatting.PostTypeGlyph("IMAGE"),
    };

    public static string ToneGlyph(FindingLevel level) => level switch
    {
        FindingLevel.Good => "",
        FindingLevel.Warning => "",
        FindingLevel.Problem => "",
        _ => "",
    };

    public static Brush ToneBrush(FindingLevel level) => Resource(level switch
    {
        FindingLevel.Good => "SystemFillColorSuccessBrush",
        FindingLevel.Warning => "SystemFillColorCautionBrush",
        FindingLevel.Problem => "SystemFillColorCriticalBrush",
        _ => "TextFillColorSecondaryBrush",
    });

    public static Brush ToneBackground(FindingLevel level) => Resource(level switch
    {
        FindingLevel.Good => "SystemFillColorSuccessBackgroundBrush",
        FindingLevel.Warning => "SystemFillColorCautionBackgroundBrush",
        FindingLevel.Problem => "SystemFillColorCriticalBackgroundBrush",
        _ => "SystemFillColorNeutralBackgroundBrush",
    });

    /// <summary>Nota → tom: 80+ ótimo, 60+ bom, 40+ atenção, abaixo disso cuidado.</summary>
    public static FindingLevel ScoreTone(int? score) => score switch
    {
        null => FindingLevel.Info,
        >= 60 => FindingLevel.Good,
        >= 40 => FindingLevel.Warning,
        _ => FindingLevel.Problem,
    };
}

/// <summary>Um dos 4 números do resumo, com o que ele significa.</summary>
public sealed class ExpertStatViewModel(Highlight highlight)
{
    public string Label => highlight.Label;

    public string Value => highlight.Value;

    public string Meaning => highlight.Meaning;

    public string Glyph => highlight.Key switch
    {
        "followers" => "",
        "engagement" => "",
        "cadence" => "",
        _ => "",
    };

    public string ToneGlyph => ExpertLook.ToneGlyph(highlight.Tone);

    public Brush ToneBrush => ExpertLook.ToneBrush(highlight.Tone);
}

/// <summary>Um passo de "Comece por aqui".</summary>
public sealed class ExpertStepViewModel(int number, Priority priority)
{
    public string Number => number.ToString(Formatting.PtBr);

    public string Area => priority.Area;

    public string AreaGlyph => ExpertLook.AreaGlyph(priority.AreaKey);

    public string Title => priority.Finding.Title;

    public string Action => priority.Finding.Action ?? "";

    public string Detail => priority.Finding.Detail;

    public string LevelText => priority.Finding.Level == FindingLevel.Problem ? "Importante" : "Recomendado";

    public Brush LevelBrush => ExpertLook.ToneBrush(priority.Finding.Level);

    public Brush LevelBackground => ExpertLook.ToneBackground(priority.Finding.Level);
}

/// <summary>Bloco de uma das 9 áreas no "Raio-X".</summary>
public sealed partial class ExpertAreaViewModel(ExpertSection section) : ObservableObject
{
    public ExpertSection Section { get; } = section;

    public string Title => Section.Title;

    public string Glyph => ExpertLook.AreaGlyph(Section.Key);

    public string GradeText => Section.Score is { } s ? ProfileExpert.Grade(s) : "Sem dados";

    public double Score => Section.Score ?? 0;

    public string ScoreText => Section.Score is { } s ? s.ToString(Formatting.PtBr) : "—";

    public Brush GradeBrush => ExpertLook.ToneBrush(ExpertLook.ScoreTone(Section.Score));

    public Brush GradeBackground => ExpertLook.ToneBackground(ExpertLook.ScoreTone(Section.Score));

    public string StatusText
    {
        get
        {
            var fix = Section.Findings.Count(f => f.Level is FindingLevel.Problem or FindingLevel.Warning);
            var good = Section.Findings.Count(f => f.Level == FindingLevel.Good);
            if (fix == 0) return good > 0 ? "Tudo certo por aqui" : "Nada para corrigir";
            return fix == 1 ? "1 coisa para melhorar" : $"{fix} coisas para melhorar";
        }
    }

    public string AutomationName => $"{Title}: {GradeText}, nota {ScoreText}. {StatusText}";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TileBorder))]
    public partial bool IsSelected { get; set; }

    public Brush TileBorder => ExpertLook.Resource(IsSelected ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
}

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

    public string Glyph => ExpertLook.ToneGlyph(finding.Level);

    public Brush Brush => ExpertLook.ToneBrush(finding.Level);

    public string LevelText => finding.Level switch
    {
        FindingLevel.Good => "Está bom",
        FindingLevel.Warning => "Para melhorar",
        FindingLevel.Problem => "Importante",
        _ => "Bom saber",
    };
}

/// <summary>Detalhe da área escolhida: por que importa, números e achados separados por tipo.</summary>
public sealed class ExpertAreaDetailViewModel(ExpertSection section)
{
    public string Title => section.Title;

    public string Glyph => ExpertLook.AreaGlyph(section.Key);

    public string Why => section.Why;

    public string Summary => section.Summary;

    public string GradeText => section.Score is { } s ? $"{ProfileExpert.Grade(s)} · {s}/100" : "Sem dados suficientes";

    public Brush GradeBrush => ExpertLook.ToneBrush(ExpertLook.ScoreTone(section.Score));

    public Brush GradeBackground => ExpertLook.ToneBackground(ExpertLook.ScoreTone(section.Score));

    public string SkillText => $"Regras da skill {section.Skill}";

    public IReadOnlyList<ExpertMetricViewModel> Metrics { get; } = section.Metrics.Select(m => new ExpertMetricViewModel(m)).ToList();

    public IReadOnlyList<ExpertFindingViewModel> Improve { get; } = section.Findings
        .Where(f => f.Level is FindingLevel.Problem or FindingLevel.Warning).Select(f => new ExpertFindingViewModel(f)).ToList();

    public IReadOnlyList<ExpertFindingViewModel> Good { get; } = section.Findings
        .Where(f => f.Level == FindingLevel.Good).Select(f => new ExpertFindingViewModel(f)).ToList();

    public IReadOnlyList<ExpertFindingViewModel> Know { get; } = section.Findings
        .Where(f => f.Level == FindingLevel.Info).Select(f => new ExpertFindingViewModel(f)).ToList();

    public bool HasMetrics => Metrics.Count > 0;

    public bool HasImprove => Improve.Count > 0;

    public bool HasGood => Good.Count > 0;

    public bool HasKnow => Know.Count > 0;

    public bool NothingToImprove => Improve.Count == 0;
}

public sealed class ExpertTopPostViewModel(RankedPost ranked)
{
    private ImageSource? _thumbnail;

    public string TypeText => ProfileExpert.FormatName(ranked.Post.Format);

    public string TypeGlyph => ExpertLook.FormatGlyph(ranked.Post.Format);

    public string DateText => ranked.Post.PublishedAt.ToLocalTime().ToString("dd/MM/yyyy", Formatting.PtBr);

    public string Caption => CaptionAnalysis.Snippet(ranked.Post.Caption, 90);

    public string InteractionsText => $"{ranked.Post.Interactions?.ToString("N0", Formatting.PtBr)} interações";

    public string VsMedianText => ranked.VsMedian is { } v ? $"{v.ToString("0.0", Formatting.PtBr)}× um post típico" : "";

    public string FormulaText => ranked.Formula.Code.Length == 0 ? "Sem fórmula clara" : ranked.Formula.Label;

    public Uri? Permalink => Uri.TryCreate(ranked.Post.Permalink, UriKind.Absolute, out var u) ? u : null;

    public bool HasPermalink => Permalink is not null;

    public ImageSource? Thumbnail => _thumbnail ??=
        Uri.TryCreate(ranked.Post.ThumbnailUrl, UriKind.Absolute, out var u) ? new BitmapImage(u) { DecodePixelWidth = 360 } : null;
}

/// <summary>Um dia do plano da semana.</summary>
public sealed class ExpertPlanDayViewModel(PlanSlot slot)
{
    private DateTime Day => slot.Date.ToDateTime(TimeOnly.MinValue);

    public string DayName => Day.ToString("dddd", Formatting.PtBr).Split('-')[0].ToUpperInvariant();

    public string DateText => Day.ToString("dd/MM", Formatting.PtBr);

    public string Time => slot.Time;

    public string FormatText => ProfileExpert.FormatName(slot.Format);

    public string FormatGlyph => ExpertLook.FormatGlyph(slot.Format);

    public string Pillar => slot.Pillar;

    public string FormulaText => slot.Formula.Label;

    public string GoalText => slot.Goal.Length == 0 ? "" : $"Busca {slot.Goal}";

    public bool HasGoal => slot.Goal.Length > 0;

    public string Idea => slot.Idea;

    public string Inspiration => slot.Inspiration ?? "";

    public bool HasInspiration => slot.Inspiration is not null;

    public string FormatAndTime => $"{FormatText} às {Time}";

    public string AlreadyPlanned => slot.AlreadyPlanned ?? "";

    public bool HasAlreadyPlanned => slot.AlreadyPlanned is not null;

    public string AutomationName => $"{DayName} {DateText}, {FormatText} às {Time}. {Idea}";
}
