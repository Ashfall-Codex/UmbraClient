namespace UmbraSync.MareConfiguration.Configurations;

[Serializable]
public class RpJournalConfig : IMareConfiguration
{
    public Dictionary<string, CharacterJournal> Journals { get; set; } = new(StringComparer.Ordinal);
    public int Version { get; set; } = 1;
}

[Serializable]
public class CharacterJournal
{
    public Dictionary<string, JournalSubject> Subjects { get; set; } = new(StringComparer.Ordinal);
}

public enum JournalTrust
{
    Unknown = 0,
    Hostile = 1,
    Wary = 2,
    Neutral = 3,
    Trusting = 4,
    Devoted = 5,
}

[Serializable]
public class JournalSubject
{
    public string TargetName { get; set; } = string.Empty;
    public uint TargetWorldId { get; set; }
    public string? LastKnownUid { get; set; }
    public string RpDisplayName { get; set; } = string.Empty;
    public JournalTrust Trust { get; set; } = JournalTrust.Unknown;
    public string Relation { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<JournalField> CustomFields { get; set; } = [];
    public List<JournalEntry> Entries { get; set; } = [];
    public bool Pinned { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenUtc { get; set; }
    public string? LastSeenPlace { get; set; }
}

[Serializable]
public class JournalField
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

[Serializable]
public class JournalEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Text { get; set; } = string.Empty;
    public string? Place { get; set; }
}
