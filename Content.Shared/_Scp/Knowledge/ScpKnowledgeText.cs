#pragma warning disable IDE0130 // Namespace does not match folder structure
using System.Text;
using Robust.Shared.Utility;

namespace Content.Shared._Scp.Knowledge;

public static class ScpKnowledgeText
{
    private static readonly string[] RussianObjectForms =
    [
        "объект",
        "объекта",
        "объекту",
        "объектом",
        "объекте",
    ];

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
        var variants = new List<string>();
        AddPhraseVariant(variants, phrase);
        AddPhraseVariant(variants, BuildSeparatorVariant(phrase));
        AddRussianObjectVariants(variants);
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

    private static void AddRussianObjectVariants(List<string> variants)
    {
        var snapshotCount = variants.Count;

        for (var index = 0; index < snapshotCount; index++)
        {
            var variant = variants[index];
            var separatorIndex = GetFirstSeparatorIndex(variant);
            if (separatorIndex <= 0)
                continue;

            var head = NormalizeRecognitionText(variant[..separatorIndex]);
            if (!string.Equals(head, RussianObjectForms[0], StringComparison.Ordinal))
                continue;

            var tail = variant[separatorIndex..];
            foreach (var form in RussianObjectForms)
            {
                AddPhraseVariant(variants, $"{form}{tail}");
            }
        }
    }

    private static int GetFirstSeparatorIndex(string phrase)
    {
        for (var i = 0; i < phrase.Length; i++)
        {
            if (!char.IsLetterOrDigit(phrase[i]))
                return i;
        }

        return -1;
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
