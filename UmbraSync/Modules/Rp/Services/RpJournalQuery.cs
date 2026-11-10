using System.Globalization;
using UmbraSync.MareConfiguration.Configurations;

namespace UmbraSync.Modules.Rp.Services;

public static class RpJournalQuery
{
    public const int MaxSummaryLength = 50_000;
    public const int MaxEntryLength = 8_000;
    public const int MaxRelationLength = 80;
    public const int MaxFieldLabelLength = 60;
    public const int MaxFieldValueLength = 500;
    public const int MaxCustomFields = 30;
    public const int MaxEntries = 500;

    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions SearchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public static string SubjectKey(string name, uint worldId) => $"{name}@{worldId}";

    public static string[] SplitTerms(string? search)
        => string.IsNullOrWhiteSpace(search)
            ? []
            : search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool Matches(JournalSubject subject, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
        {
            if (!MatchesTerm(subject, term)) return false;
        }
        return true;
    }

    private static bool MatchesTerm(JournalSubject subject, string term)
    {
        if (Contains(subject.TargetName, term) || Contains(subject.RpDisplayName, term)
            || Contains(subject.Relation, term) || Contains(subject.Summary, term))
            return true;

        foreach (var field in subject.CustomFields)
        {
            if (Contains(field.Label, term) || Contains(field.Value, term)) return true;
        }

        foreach (var entry in subject.Entries)
        {
            if (Contains(entry.Text, term) || Contains(entry.Place, term)) return true;
        }

        return false;
    }

    private static bool Contains(string? source, string term)
        => !string.IsNullOrEmpty(source) && Compare.IndexOf(source, term, SearchOptions) >= 0;

    public static List<JournalSubject> Filter(IEnumerable<JournalSubject> subjects, string? search, JournalTrust? trust)
    {
        var terms = SplitTerms(search);
        return subjects
            .Where(s => trust == null || s.Trust == trust)
            .Where(s => terms.Length == 0 || Matches(s, terms))
            .OrderByDescending(s => s.Pinned)
            .ThenByDescending(s => s.UpdatedUtc)
            .ToList();
    }

    public static string Clip(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    public static string DisplayName(JournalSubject subject)
        => string.IsNullOrWhiteSpace(subject.RpDisplayName) ? subject.TargetName : subject.RpDisplayName;
}
