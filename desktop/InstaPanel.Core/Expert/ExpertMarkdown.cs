using System.Text;

namespace InstaPanel.Core.Expert;

/// <summary>Relatório do Especialista em Markdown (botão "Copiar relatório").</summary>
public static class ExpertMarkdown
{
    public static string From(ExpertReport report, TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Local;
        var pt = ProfileExpert.Pt;
        var sb = new StringBuilder();
        var when = TimeZoneInfo.ConvertTime(report.GeneratedAt, tz);

        sb.AppendLine($"# Especialista do perfil · @{report.Username}");
        sb.AppendLine();
        sb.AppendLine($"{when.ToString("dd/MM/yyyy 'às' HH:mm", pt)} · {report.PostsAnalyzed} posts analisados");
        sb.AppendLine();
        sb.AppendLine($"**Nota geral: {report.Score}/100.** {report.Headline}");
        sb.AppendLine();
        foreach (var h in report.Highlights) sb.AppendLine($"- {h.Label}: **{h.Value}** ({h.Meaning})");

        if (report.Priorities.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Prioridades");
            sb.AppendLine();
            var n = 1;
            foreach (var p in report.Priorities) sb.AppendLine($"{n++}. **{p.Area}: {p.Finding.Title}.** {p.Finding.Action}");
        }

        foreach (var s in report.Sections)
        {
            sb.AppendLine();
            sb.AppendLine($"## {s.Title}{(s.Score is { } sc ? $" ({sc}/100)" : "")}");
            sb.AppendLine();
            sb.AppendLine($"_{s.Why}_");
            sb.AppendLine();
            sb.AppendLine($"{s.Summary} (skill `{s.Skill}`)");
            if (s.Metrics.Count > 0)
            {
                sb.AppendLine();
                foreach (var m in s.Metrics) sb.AppendLine($"- {m.Label}: {m.Value}{Note(m.Note)}");
            }
            if (s.Findings.Count > 0)
            {
                sb.AppendLine();
                foreach (var f in s.Findings)
                {
                    sb.AppendLine($"- {Mark(f.Level)} **{f.Title}.** {f.Detail}");
                    if (f.Action is { } a) sb.AppendLine($"  - O que fazer: {a}");
                }
            }
        }

        if (report.TopPosts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Melhores posts");
            sb.AppendLine();
            foreach (var r in report.TopPosts)
            {
                var date = TimeZoneInfo.ConvertTime(r.Post.PublishedAt, tz).ToString("dd/MM/yyyy", pt);
                var vs = r.VsMedian is { } v ? $" · {ProfileExpert.Times(v)} a mediana" : "";
                sb.AppendLine($"- {ProfileExpert.FormatName(r.Post.Format)} de {date}: {ProfileExpert.Num(r.Post.Interactions ?? 0)} interações{vs} · {r.Formula.Label} · \"{CaptionAnalysis.Snippet(r.Post.Caption)}\"{(r.Post.Permalink is { } l ? $" ({l})" : "")}");
            }
        }

        if (report.WeekPlan.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Plano da semana");
            sb.AppendLine();
            sb.AppendLine("| Dia | Formato | Pilar | Fórmula | Objetivo | Horário | Ideia |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var p in report.WeekPlan)
            {
                var day = p.Date.ToDateTime(TimeOnly.MinValue).ToString("ddd dd/MM", pt);
                var idea = p.Idea + (p.Inspiration is { } i ? $" {i}" : "") + (p.AlreadyPlanned is { } a ? $" ({a})" : "");
                sb.AppendLine($"| {day} | {ProfileExpert.FormatName(p.Format)} | {p.Pillar} | {p.Formula.Label} | {p.Goal} | {p.Time} | {idea.Replace("|", "/")} |");
            }
        }

        return sb.ToString();
    }

    private static string Note(string? note) => note is null ? "" : $" ({note})";

    private static string Mark(FindingLevel level) => level switch
    {
        FindingLevel.Problem => "🔴",
        FindingLevel.Warning => "🟡",
        FindingLevel.Good => "🟢",
        _ => "ℹ️",
    };
}
