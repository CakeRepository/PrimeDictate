using System.Text;

namespace PrimeDictate;

internal readonly record struct VoiceCommandMatch(
    string CleanedText,
    bool StopRequested,
    bool HistoryRequested);

internal sealed record VoiceCommandOptions(
    bool Enabled,
    string StopPhrase,
    string HistoryPhrase);

internal static class VoiceCommandMatcher
{
    public static VoiceCommandMatch Apply(string transcript, VoiceCommandOptions options)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(transcript))
        {
            return new VoiceCommandMatch(transcript, StopRequested: false, HistoryRequested: false);
        }

        var cleaned = transcript;
        var stopRequested = TryRemovePhrase(cleaned, options.StopPhrase, out cleaned);
        var historyRequested = TryRemovePhrase(cleaned, options.HistoryPhrase, out cleaned);

        return new VoiceCommandMatch(
            CollapseWhitespace(cleaned).Trim(),
            stopRequested,
            historyRequested);
    }

    private static bool TryRemovePhrase(string text, string phrase, out string cleaned)
    {
        cleaned = text;
        var phraseTokens = Tokenize(phrase);
        if (phraseTokens.Count == 0)
        {
            return false;
        }

        var textTokens = Tokenize(text);
        if (textTokens.Count < phraseTokens.Count)
        {
            return false;
        }

        for (var startIndex = 0; startIndex <= textTokens.Count - phraseTokens.Count; startIndex++)
        {
            var matched = true;
            for (var phraseIndex = 0; phraseIndex < phraseTokens.Count; phraseIndex++)
            {
                if (!string.Equals(
                        textTokens[startIndex + phraseIndex].Value,
                        phraseTokens[phraseIndex].Value,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
            {
                continue;
            }

            var removeStart = textTokens[startIndex].Start;
            var removeEnd = textTokens[startIndex + phraseTokens.Count - 1].End;
            while (removeStart > 0 && char.IsWhiteSpace(text[removeStart - 1]))
            {
                removeStart--;
            }

            while (removeEnd < text.Length && (char.IsWhiteSpace(text[removeEnd]) || char.IsPunctuation(text[removeEnd])))
            {
                removeEnd++;
            }

            cleaned = text[..removeStart] + " " + text[removeEnd..];
            return true;
        }

        return false;
    }

    private static List<TokenSpan> Tokenize(string text)
    {
        var tokens = new List<TokenSpan>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && !char.IsLetterOrDigit(text[index]))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            var start = index;
            while (index < text.Length && char.IsLetterOrDigit(text[index]))
            {
                index++;
            }

            tokens.Add(new TokenSpan(text[start..index], start, index));
        }

        return tokens;
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var lastWasWhitespace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasWhitespace)
                {
                    builder.Append(' ');
                    lastWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasWhitespace = false;
        }

        return builder.ToString();
    }

    private readonly record struct TokenSpan(string Value, int Start, int End);
}
