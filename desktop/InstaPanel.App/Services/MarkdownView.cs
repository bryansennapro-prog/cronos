using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace InstaPanel.App.Services;

/// <summary>
/// Mostra o Markdown simples da análise com IA num RichTextBlock: títulos (#, ##, ###), listas (-, *, 1.),
/// **negrito** e `código`. O resto aparece como texto.
/// </summary>
public static partial class MarkdownView
{
    public static void Render(RichTextBlock target, string? markdown)
    {
        target.Blocks.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                var p = new Paragraph { Margin = new Thickness(0, 14, 0, 4) };
                AddInlines(p, heading.Groups[2].Value, bold: true, size: level <= 2 ? 17 : 15);
                target.Blocks.Add(p);
                continue;
            }

            var item = ListRegex().Match(line);
            if (item.Success)
            {
                var indent = Math.Min(3, item.Groups[1].Value.Length / 2);
                var marker = item.Groups[2].Value is "-" or "*" ? "•" : item.Groups[2].Value;
                var p = new Paragraph { Margin = new Thickness(12 + 16 * indent, 2, 0, 2), TextIndent = -12 };
                p.Inlines.Add(new Run { Text = marker + " " });
                AddInlines(p, item.Groups[3].Value);
                target.Blocks.Add(p);
                continue;
            }

            var para = new Paragraph { Margin = new Thickness(0, 2, 0, 6) };
            AddInlines(para, line);
            target.Blocks.Add(para);
        }
    }

    private static void AddInlines(Paragraph p, string text, bool bold = false, double? size = null)
    {
        foreach (var part in InlineRegex().Split(text))
        {
            if (part.Length == 0) continue;
            Run run;
            if (part.Length > 4 && part.StartsWith("**", StringComparison.Ordinal) && part.EndsWith("**", StringComparison.Ordinal))
                run = new Run { Text = part[2..^2], FontWeight = FontWeights.SemiBold };
            else if (part.Length > 2 && part[0] == '`' && part[^1] == '`')
                run = new Run { Text = part[1..^1], FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
            else
                run = new Run { Text = part };
            if (bold) run.FontWeight = FontWeights.SemiBold;
            if (size is { } s) run.FontSize = s;
            p.Inlines.Add(run);
        }
    }

    [GeneratedRegex(@"^(#{1,4})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\s*)([-*]|\d+[.)])\s+(.+)$")]
    private static partial Regex ListRegex();

    // Separa mantendo os trechos **negrito** e `código`.
    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`)")]
    private static partial Regex InlineRegex();
}
