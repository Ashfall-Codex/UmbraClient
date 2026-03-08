using Dalamud.Interface.Utility.Raii;
using System.Globalization;
using System.Text;
using UmbraSync.API.Dto.CharaData;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.CharaData.Models;

namespace UmbraSync.UI;

public sealed partial class CharaDataHubUi
{
    private static string GetAccessTypeString(AccessTypeDto dto) => dto switch
    {
        AccessTypeDto.AllPairs => Loc.Get("CharaDataHub.AccessType.AllPairs"),
        AccessTypeDto.ClosePairs => Loc.Get("CharaDataHub.AccessType.DirectPairs"),
        AccessTypeDto.Individuals => Loc.Get("CharaDataHub.AccessType.Specified"),
        AccessTypeDto.Public => Loc.Get("CharaDataHub.AccessType.Everyone"),
        _ => ((int)dto).ToString(CultureInfo.InvariantCulture)
    };

    private static string GetShareTypeString(ShareTypeDto dto) => dto switch
    {
        ShareTypeDto.Private => Loc.Get("CharaDataHub.ShareType.CodeOnly"),
        ShareTypeDto.Shared => Loc.Get("CharaDataHub.ShareType.Shared"),
        _ => ((int)dto).ToString(CultureInfo.InvariantCulture)
    };

    private static string GetWorldDataTooltipText(PoseEntryExtended poseEntry)
    {
        if (!poseEntry.HasWorldData) return Loc.Get("CharaDataHub.WorldDataTooltip.None");
        return poseEntry.WorldDataDescriptor;
    }


    private void GposeMetaInfoAction(Action<CharaDataMetaInfoExtendedDto?> gposeActionDraw, string actionDescription, CharaDataMetaInfoExtendedDto? dto, bool hasValidGposeTarget, bool isSpawning)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine(actionDescription);
        bool isDisabled = false;

        void AddErrorStart()
        {
            sb.Append(UiSharedService.TooltipSeparator);
            sb.AppendLine(Loc.Get("CharaDataHub.Gpose.ActionCannotExecute"));
        }

        if (dto == null)
        {
            if (!isDisabled) AddErrorStart();
            sb.AppendLine(Loc.Get("CharaDataHub.Gpose.Error.NoMetaInfo"));
            isDisabled = true;
        }
        if (!dto?.CanBeDownloaded ?? false)
        {
            if (!isDisabled) AddErrorStart();
            sb.AppendLine(Loc.Get("CharaDataHub.Gpose.Error.NotDownloadable"));
            isDisabled = true;
        }
        if (!_uiSharedService.IsInGpose)
        {
            if (!isDisabled) AddErrorStart();
            sb.AppendLine(Loc.Get("CharaDataHub.Gpose.Error.RequiresGpose"));
            isDisabled = true;
        }
        if (!hasValidGposeTarget && !isSpawning)
        {
            if (!isDisabled) AddErrorStart();
            sb.AppendLine(Loc.Get("CharaDataHub.Gpose.Error.RequiresTarget"));
            isDisabled = true;
        }
        if (isSpawning && !_charaDataManager.BrioAvailable)
        {
            if (!isDisabled) AddErrorStart();
            sb.AppendLine(Loc.Get("CharaDataHub.Gpose.Error.RequiresBrio"));
            isDisabled = true;
        }

        using (ImRaii.Group())
        {
            using var dis = ImRaii.Disabled(isDisabled);
            gposeActionDraw.Invoke(dto);
        }
        if (sb.Length > 0)
        {
            UiSharedService.AttachToolTip(sb.ToString());
        }
    }

    private float GposePoseAction(Func<float, float> poseActionDraw, string poseDescription, bool hasValidGposeTarget, float start)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine(poseDescription);
        bool isDisabled = false;

        void AddErrorStart()
        {
            sb.Append(UiSharedService.TooltipSeparator);
            sb.AppendLine(Loc.Get("CharaDataHub.Gpose.ActionCannotExecute"));
        }

        if (!_uiSharedService.IsInGpose)
        {
            if (!isDisabled) AddErrorStart();
            sb.AppendLine(Loc.Get("CharaDataHub.Gpose.Error.RequiresGpose"));
            isDisabled = true;
        }
        if (!hasValidGposeTarget)
        {
            if (!isDisabled) AddErrorStart();
            sb.AppendLine(Loc.Get("CharaDataHub.Gpose.Error.RequiresTarget"));
            isDisabled = true;
        }
        if (!_charaDataManager.BrioAvailable)
        {
            if (!isDisabled) AddErrorStart();
            sb.AppendLine(Loc.Get("CharaDataHub.Gpose.Error.RequiresBrio"));
            isDisabled = true;
        }

        float result;
        using (ImRaii.Group())
        {
            using var dis = ImRaii.Disabled(isDisabled);
            result = poseActionDraw.Invoke(start);
        }
        if (sb.Length > 0)
        {
            UiSharedService.AttachToolTip(sb.ToString());
        }

        return result;
    }

    private void SetWindowSizeConstraints(bool? inGposeTab = null)
    {
        SizeConstraints = new()
        {
            MinimumSize = new((inGposeTab ?? false) ? 400 : 1000, 500),
            MaximumSize = new((inGposeTab ?? false) ? 400 : 1000, 2000)
        };
    }

    private void UpdateFilteredFavorites()
    {
        _ = Task.Run(async () =>
        {
            if (_charaDataManager.DownloadMetaInfoTask != null)
            {
                await _charaDataManager.DownloadMetaInfoTask.ConfigureAwait(false);
            }
            Dictionary<string, (CharaDataFavorite, CharaDataMetaInfoExtendedDto?, bool)> newFiltered = [];
            foreach (var favorite in _configService.Current.FavoriteCodes)
            {
                var uid = favorite.Key.Split(":")[0];
                var note = _serverConfigurationManager.GetNoteForUid(uid) ?? string.Empty;
                bool hasMetaInfo = _charaDataManager.TryGetMetaInfo(favorite.Key, out var metaInfo);
                bool addFavorite =
                    (string.IsNullOrEmpty(_filterCodeNote)
                        || (note.Contains(_filterCodeNote, StringComparison.OrdinalIgnoreCase)
                        || uid.Contains(_filterCodeNote, StringComparison.OrdinalIgnoreCase)))
                    && (string.IsNullOrEmpty(_filterDescription)
                        || (favorite.Value.CustomDescription.Contains(_filterDescription, StringComparison.OrdinalIgnoreCase)
                        || (metaInfo != null && metaInfo.Description.Contains(_filterDescription, StringComparison.OrdinalIgnoreCase))))
                    && (!_filterPoseOnly
                        || (metaInfo != null && metaInfo.HasPoses))
                    && (!_filterWorldOnly
                        || (metaInfo != null && metaInfo.HasWorldData));
                if (addFavorite)
                {
                    newFiltered[favorite.Key] = (favorite.Value, metaInfo, hasMetaInfo);
                }
            }

            _filteredFavorites = newFiltered;
        });
    }

    private void UpdateFilteredItems()
    {
        if (_charaDataManager.GetSharedWithYouTask == null)
        {
            _filteredDict = _charaDataManager.SharedWithYouData
                .SelectMany(k => k.Value)
                .Where(k =>
                    (!_sharedWithYouDownloadableFilter || k.CanBeDownloaded)
                    && (string.IsNullOrEmpty(_sharedWithYouDescriptionFilter) || k.Description.Contains(_sharedWithYouDescriptionFilter, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(k => k.Uploader)
                    .ToDictionary(k =>
                    {
                        var note = _serverConfigurationManager.GetNoteForUid(k.Key.UID);
                        return string.IsNullOrEmpty(note) ? k.Key.AliasOrUID : $"{note} ({k.Key.AliasOrUID})";
                    }, k => k.ToList(), StringComparer.OrdinalIgnoreCase)
                    .Where(k => string.IsNullOrEmpty(_sharedWithYouOwnerFilter) || k.Key.Contains(_sharedWithYouOwnerFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);
        }
    }
}