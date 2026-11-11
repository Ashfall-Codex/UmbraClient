using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using System.Globalization;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Configurations;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;

namespace UmbraSync.Modules.Rp.Services;

public sealed class RpJournalService : DisposableMediatorSubscriberBase, IRuntimeScopedService
{
    private static readonly TimeSpan SeenThrottle = TimeSpan.FromMinutes(10);

    private readonly RpJournalConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly ITargetManager _targetManager;
    private readonly IDataManager _gameData;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private string? _ownerKey;
    private string _ownerName = string.Empty;

    public RpJournalService(ILogger<RpJournalService> logger, MareMediator mediator, RpJournalConfigService configService,
        DalamudUtilService dalamudUtil, ITargetManager targetManager, IDataManager gameData, PairManager pairManager,
        ServerConfigurationManager serverConfigurationManager)
        : base(logger, mediator)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _configService = configService;
        _dalamudUtil = dalamudUtil;
        _targetManager = targetManager;
        _gameData = gameData;
        _pairManager = pairManager;

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, _ => RefreshOwner());
        Mediator.Subscribe<DalamudLogoutMessage>(this, _ =>
        {
            _ownerKey = null;
            _ownerName = string.Empty;
        });
    }

    public bool HasOwner => _ownerKey != null;
    public string OwnerName => _ownerName;

    public CharacterJournal? CurrentJournal
        => _ownerKey != null && _configService.Current.Journals.TryGetValue(_ownerKey, out var journal) ? journal : null;

    public int SubjectCount => CurrentJournal?.Subjects.Count ?? 0;

    private void RefreshOwner()
    {
        try
        {
            if (!_dalamudUtil.IsLoggedIn)
            {
                _ownerKey = null;
                _ownerName = string.Empty;
                return;
            }

            var name = _dalamudUtil.GetPlayerName();
            var world = _dalamudUtil.GetHomeWorldId();
            if (string.IsNullOrEmpty(name) || string.Equals(name, "--", StringComparison.Ordinal) || world == 0) return;

            var key = RpJournalQuery.SubjectKey(name, world);
            if (string.Equals(key, _ownerKey, StringComparison.Ordinal)) return;
            _ownerKey = key;
            _ownerName = name;
            ImportCharacterNotes(key);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Journal: lecture du personnage joué impossible");
        }
    }

    private void ImportCharacterNotes(string ownerKey)
    {
        try
        {
            if (_configService.Current.Journals.TryGetValue(ownerKey, out var existing) && existing.CharacterNotesImported) return;

            var notes = _serverConfigurationManager.GetAllCharacterNotes();
            var journal = existing ?? new CharacterJournal();
            int imported = RpJournalQuery.ImportCharacterNotes(journal, notes, DateTime.UtcNow);
            _configService.Current.Journals[ownerKey] = journal;
            _configService.Save();
            if (imported > 0)
                Logger.LogInformation("Journal: {count} note(s) privée(s) reprise(s) dans le journal", imported);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Journal: reprise des notes privées impossible");
        }
    }

    public IReadOnlyList<JournalSubject> Subjects(string? search, JournalTrust? trust)
    {
        var journal = CurrentJournal;
        return journal == null ? [] : RpJournalQuery.Filter(journal.Subjects.Values, search, trust);
    }

    public JournalSubject? TryGetSubject(string? name, uint worldId)
    {
        if (string.IsNullOrEmpty(name) || worldId == 0) return null;
        var journal = CurrentJournal;
        return journal != null && journal.Subjects.TryGetValue(RpJournalQuery.SubjectKey(name, worldId), out var subject) ? subject : null;
    }

    public JournalSubject? GetOrCreateSubject(string name, uint worldId, string? uid = null, string? rpName = null)
    {
        if (_ownerKey == null || string.IsNullOrEmpty(name) || worldId == 0) return null;

        if (!_configService.Current.Journals.TryGetValue(_ownerKey, out var journal))
        {
            journal = new CharacterJournal();
            _configService.Current.Journals[_ownerKey] = journal;
        }

        var key = RpJournalQuery.SubjectKey(name, worldId);
        if (!journal.Subjects.TryGetValue(key, out var subject))
        {
            subject = new JournalSubject { TargetName = name, TargetWorldId = worldId };
            journal.Subjects[key] = subject;
            Logger.LogDebug("Journal: nouveau personnage suivi ({count} au total)", journal.Subjects.Count);
        }

        ApplyIdentity(subject, uid, rpName);
        subject.UpdatedUtc = DateTime.UtcNow;
        _configService.Save();
        return subject;
    }

    public void Touch(JournalSubject subject)
    {
        subject.UpdatedUtc = DateTime.UtcNow;
        _configService.Save();
    }

    public void RefreshIdentity(JournalSubject subject, string? uid, string? rpName)
    {
        bool uidChanged = !string.IsNullOrEmpty(uid) && !string.Equals(subject.LastKnownUid, uid, StringComparison.Ordinal);
        bool nameChanged = !string.IsNullOrWhiteSpace(rpName) && !string.Equals(subject.RpDisplayName, rpName.Trim(), StringComparison.Ordinal);
        if (!uidChanged && !nameChanged) return;
        ApplyIdentity(subject, uid, rpName);
        _configService.Save();
    }

    public void DeleteSubject(JournalSubject subject)
    {
        var journal = CurrentJournal;
        if (journal == null) return;
        journal.Subjects.Remove(RpJournalQuery.SubjectKey(subject.TargetName, subject.TargetWorldId));
        _configService.Save();
    }

    public void DeleteEntry(JournalSubject subject, Guid entryId)
    {
        if (subject.Entries.RemoveAll(e => e.Id == entryId) > 0) Touch(subject);
    }

    public async Task AddEntryAsync(JournalSubject subject, string text)
    {
        var clipped = RpJournalQuery.Clip(text.Trim(), RpJournalQuery.MaxEntryLength);
        if (clipped.Length == 0) return;

        string? place = null;
        try
        {
            place = await _dalamudUtil.RunOnFrameworkThread(DescribeCurrentPlace).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Journal: lieu courant indisponible");
        }

        await _dalamudUtil.RunOnFrameworkThread(() =>
        {
            if (subject.Entries.Count >= RpJournalQuery.MaxEntries)
                subject.Entries.RemoveAt(0);
            subject.Entries.Add(new JournalEntry { Text = clipped, Place = place });
            Touch(subject);
        }).ConfigureAwait(false);
    }

    public void NoteSeen(string name, uint worldId, string? uid, string? rpName)
    {
        var subject = TryGetSubject(name, worldId);
        if (subject == null) return;
        RefreshIdentity(subject, uid, rpName);
        var now = DateTime.UtcNow;
        if (subject.LastSeenUtc is { } last && now - last < SeenThrottle) return;

        subject.LastSeenUtc = now;
        try
        {
            subject.LastSeenPlace = DescribeCurrentPlace();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Journal: lieu courant indisponible");
        }
        _configService.Save();
    }

    public async Task<(string Name, uint WorldId, string? Uid)?> GetTargetPlayerAsync()
    {
        return await _dalamudUtil.RunOnFrameworkThread<(string, uint, string?)?>(() =>
        {
            if (_targetManager.Target is not IPlayerCharacter pc) return null;
            var name = pc.Name.TextValue;
            var world = pc.HomeWorld.RowId;
            if (string.IsNullOrEmpty(name) || world == 0) return null;
            var uid = _pairManager.GetVisiblePairByObjectId(pc.EntityId)?.UserData.UID;
            return (name, world, uid);
        }).ConfigureAwait(false);
    }

    public string WorldName(uint worldId)
        => _dalamudUtil.WorldData.Value.TryGetValue((ushort)worldId, out var world) ? world : worldId.ToString(CultureInfo.InvariantCulture);

    private static void ApplyIdentity(JournalSubject subject, string? uid, string? rpName)
    {
        if (!string.IsNullOrEmpty(uid)) subject.LastKnownUid = uid;
        if (!string.IsNullOrWhiteSpace(rpName)) subject.RpDisplayName = rpName.Trim();
    }

    private string? DescribeCurrentPlace()
    {
        var map = _dalamudUtil.GetMapData();
        if (map.TerritoryId == 0) return null;

        var territory = _gameData.GetExcelSheet<TerritoryType>().GetRowOrDefault(map.TerritoryId);
        var placeName = territory?.PlaceName.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(placeName)) return null;

        if (map.HouseId == 0) return placeName;

        var world = WorldName(map.ServerId);
        var detail = map.HouseId >= 100
            ? string.Format(CultureInfo.CurrentCulture, Loc.Get("Establishment.Detail.Apartment"), map.WardId, map.RoomId)
            : string.Format(CultureInfo.CurrentCulture, Loc.Get("Establishment.Detail.Ward"), map.WardId, map.HouseId);
        return $"{placeName} • {world} • {detail}";
    }
}
