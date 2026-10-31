using System.Globalization;
using System.Text;

namespace MyFront.Core.Import;

/// <summary>
/// Matches file headers to table columns ignoring case, spaces/underscores/punctuation and Turkish letters:
/// "İSTPOZ ADI", "istpoz_adi", "IstPozAdı" and "Istpoz-Adi" are all the same column.
/// </summary>
public static class HeaderMatcher
{
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            char c = ch switch
            {
                'İ' or 'I' or 'ı' or 'i' => 'i',
                'Ş' or 'ş' => 's',
                'Ğ' or 'ğ' => 'g',
                'Ü' or 'ü' => 'u',
                'Ö' or 'ö' => 'o',
                'Ç' or 'ç' => 'c',
                _ => char.ToLowerInvariant(ch),
            };
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        // other accents (é, â …) → base letter
        var decomposed = sb.ToString().Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) result.Append(c);
        return result.ToString();
    }

    /// <summary>For each source header the matching target column, or null. Exact names win, then normalized ones; each target is used once.</summary>
    public static string?[] Match(IReadOnlyList<string> sourceHeaders, IReadOnlyList<string> targetColumns)
    {
        var result = new string?[sourceHeaders.Count];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < sourceHeaders.Count; i++)
        {
            var exact = targetColumns.FirstOrDefault(t => t == sourceHeaders[i].Trim());
            if (exact is not null && used.Add(exact)) result[i] = exact;
        }
        var byKey = targetColumns.GroupBy(Normalize).ToDictionary(g => g.Key, g => g.ToList());
        for (int i = 0; i < sourceHeaders.Count; i++)
        {
            if (result[i] is not null) continue;
            var key = Normalize(sourceHeaders[i]);
            if (key.Length > 0 && byKey.TryGetValue(key, out var candidates))
            {
                var free = candidates.FirstOrDefault(c => !used.Contains(c));
                if (free is not null) { used.Add(free); result[i] = free; }
            }
        }
        return result;
    }
}
