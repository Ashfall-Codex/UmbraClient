namespace UmbraSync.Services;

public sealed record LastSeenLiveData(string UID, string CharName, uint WorldId, string? HonorificData, string? MoodlesData, DateTime SeenAtUtc);
