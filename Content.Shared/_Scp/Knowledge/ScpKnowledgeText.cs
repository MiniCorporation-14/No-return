using System.Text;
using Robust.Shared.Utility;

namespace Content.Shared._Scp.Knowledge;

public static class ScpKnowledgeText
{
    public static string NormalizeRecognitionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = FormattedMessage.RemoveMarkupPermissive(text);

        var builder = new StringBuilder(text.Length);
        var hasSeparator = true;

        foreach (var value in text)
        {
            var character = char.ToLowerInvariant(value);

            if (character == 'ё')
                character = 'е';

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                hasSeparator = false;
                continue;
            }

            if (hasSeparator)
                continue;

            builder.Append(' ');
            hasSeparator = true;
        }

        return builder.ToString().Trim();
    }

    public static string WrapForPhraseSearch(string normalizedText)
    {
        if (normalizedText.Length == 0)
            return string.Empty;

        return $" {normalizedText} ";
    }

    public static List<string> GetRecognitionPhraseVariants(string phrase)
    {
        var expandedPhrases = new List<string>();
        AddExpandedPhraseVariants(expandedPhrases, phrase);

        var variants = new List<string>();
        foreach (var expandedPhrase in expandedPhrases)
        {
            AddPhraseVariant(variants, expandedPhrase);
            AddPhraseVariant(variants, BuildSeparatorVariant(expandedPhrase));
        }

        return variants;
    }

    public static string BuildSeparatorVariant(string phrase)
    {
        if (phrase.Length == 0)
            return phrase;

        var builder = new StringBuilder(phrase.Length);
        var hasSeparator = true;

        foreach (var character in phrase)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                hasSeparator = false;
                continue;
            }

            if (hasSeparator)
                continue;

            builder.Append(' ');
            hasSeparator = true;
        }

        return builder.ToString().Trim();
    }

    private static void AddPhraseVariant(List<string> variants, string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return;

        var normalizedWhitespace = NormalizeVariantWhitespace(phrase);
        if (normalizedWhitespace.Length == 0 || ContainsVariant(variants, normalizedWhitespace))
            return;

        variants.Add(normalizedWhitespace);
    }

    private static string NormalizeVariantWhitespace(string phrase)
    {
        var builder = new StringBuilder(phrase.Length);
        var hasWhitespace = true;

        foreach (var character in phrase)
        {
            if (char.IsWhiteSpace(character))
            {
                if (hasWhitespace)
                    continue;

                builder.Append(' ');
                hasWhitespace = true;
                continue;
            }

            builder.Append(character);
            hasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static void AddExpandedPhraseVariants(List<string> variants, string phrase)
    {
        var groupStart = FindAlternativeGroup(phrase, out var groupEnd, out var options);
        if (groupStart == -1 || options == null)
        {
            AddPhraseVariant(variants, phrase);
            return;
        }

        var prefix = phrase[..groupStart];
        var suffix = phrase[(groupEnd + 1)..];
        for (var i = 0; i < options.Count; i++)
        {
            AddExpandedPhraseVariants(variants, $"{prefix}{options[i]}{suffix}");
        }
    }

    private static int FindAlternativeGroup(string phrase, out int groupEnd, out List<string>? options)
    {
        groupEnd = -1;
        options = null;

        for (var i = 0; i < phrase.Length; i++)
        {
            if (phrase[i] != '(')
                continue;

            if (!TryParseAlternativeGroup(phrase, i, out groupEnd, out options))
                continue;

            return i;
        }

        return -1;
    }

    private static bool TryParseAlternativeGroup(string phrase, int groupStart, out int groupEnd, out List<string>? options)
    {
        groupEnd = -1;
        options = null;

        var depth = 0;
        var optionStart = groupStart + 1;
        var hasSeparator = false;
        var parsedOptions = new List<string>();

        for (var i = groupStart; i < phrase.Length; i++)
        {
            switch (phrase[i])
            {
                case '(':
                    depth++;
                    break;
                case ')' when depth == 1:
                    parsedOptions.Add(phrase[optionStart..i]);
                    groupEnd = i;
                    options = hasSeparator ? parsedOptions : null;
                    return hasSeparator;
                case ')':
                    depth--;
                    break;
                case '|' when depth == 1:
                    parsedOptions.Add(phrase[optionStart..i]);
                    optionStart = i + 1;
                    hasSeparator = true;
                    break;
            }
        }

        return false;
    }

    private static bool ContainsVariant(List<string> variants, string phrase)
    {
        for (var i = 0; i < variants.Count; i++)
        {
            if (string.Equals(variants[i], phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
