using System.Text.RegularExpressions;

namespace InstaPanel.Core;

/// <summary>Contagens da legenda com as mesmas regras do Worker (worker/src/posts.ts).</summary>
public sealed partial record CaptionStats(int Characters, int Hashtags, int Mentions)
{
    public const int MaxCharacters = 2200;
    public const int MaxHashtags = 30;
    public const int MaxMentions = 20;

    public bool IsValid => Characters <= MaxCharacters && Hashtags <= MaxHashtags && Mentions <= MaxMentions;

    public static CaptionStats From(string? caption)
    {
        var text = caption?.Trim() ?? "";
        return new CaptionStats(
            text.EnumerateRunes().Count(),
            HashtagRegex().Count(text),
            MentionRegex().Count(text));
    }

    [GeneratedRegex(@"#[\p{L}\p{N}_]+")]
    private static partial Regex HashtagRegex();

    [GeneratedRegex(@"@[\p{L}\p{N}._]+")]
    private static partial Regex MentionRegex();
}
