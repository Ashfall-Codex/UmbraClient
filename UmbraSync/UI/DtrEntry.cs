using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Runtime.InteropServices;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.PlayerData.Pairs;
using UmbraSync.Services;
using UmbraSync.Services.AutoDetect;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI;

public sealed class DtrEntry : IDisposable, IHostedService
{
    public const string DefaultGlyph = "\u25CB";
    private enum DtrStyle
    {
        Style1,
        Style2,
        Style3,
        Style4,
        Style5,
        Style6,
        Style7,
        Style8,
        Style9
    }

    public const int NumStyles = 10;

    private readonly ApiController _apiController;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly MareConfigService _configService;
    private readonly IDtrBar _dtrBar;
    private readonly Lazy<IDtrBarEntry> _entry;
    private readonly ILogger<DtrEntry> _logger;
    private readonly MareMediator _mareMediator;
    private readonly PairManager _pairManager;
    private readonly NearbyPendingService _nearbyPendingService;
    private readonly SlotService _slotService;
    private Task? _runTask;
    private string? _text;
    private string? _tooltip;
    private Colors _colors;

    public DtrEntry(ILogger<DtrEntry> logger, IDtrBar dtrBar, MareConfigService configService, MareMediator mareMediator, PairManager pairManager, ApiController apiController, NearbyPendingService nearbyPendingService, SlotService slotService)
    {
        _logger = logger;
        _dtrBar = dtrBar;
        _entry = new(CreateEntry);
        _configService = configService;
        _mareMediator = mareMediator;
        _pairManager = pairManager;
        _apiController = apiController;
        _nearbyPendingService = nearbyPendingService;
        _slotService = slotService;
    }

    public void Dispose()
    {
        if (_entry.IsValueCreated)
        {
            _logger.LogDebug("Disposing DtrEntry");
            Clear();
            _entry.Value.Remove();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting DtrEntry");
        _runTask = Task.Run(RunAsync, _cancellationTokenSource.Token);
        _logger.LogInformation("Started DtrEntry");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        try
        {
            await _runTask!.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore cancelled
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }

    private void Clear()
    {
        if (!_entry.IsValueCreated) return;
        _logger.LogInformation("Clearing entry");
        _text = null;
        _tooltip = null;
        _colors = default;

        _entry.Value.Shown = false;
    }

    private IDtrBarEntry CreateEntry()
    {
        _logger.LogTrace("Creating new DtrBar entry");
        var entry = _dtrBar.Get("Umbra");
        entry.OnClick = _ => _mareMediator.Publish(new UiToggleMessage(typeof(CompactUi)));

        return entry;
    }

    private async Task RunAsync()
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            await Task.Delay(1000, _cancellationTokenSource.Token).ConfigureAwait(false);

            Update();
        }
    }

    private void Update()
    {
        if (!_configService.Current.EnableDtrEntry || !_configService.Current.HasValidSetup())
        {
            if (_entry.IsValueCreated && _entry.Value.Shown)
            {
                _logger.LogInformation("Disabling entry");

                Clear();
            }
            return;
        }

        if (!_entry.Value.Shown)
        {
            _logger.LogInformation("Showing entry");
            _entry.Value.Shown = true;
        }

        string text;
        string tooltip;
        Colors colors;
        if (_apiController.IsConnected)
        {
            var pairCount = _pairManager.GetVisibleUserCount();
            var baseText = RenderDtrStyle(_configService.Current.DtrStyle, pairCount.ToString(CultureInfo.InvariantCulture));
            var pendingNearby = _nearbyPendingService.Pending.Count;
            text = pendingNearby > 0 ? $"{baseText} ({pendingNearby})" : baseText;
            if (pairCount > 0)
            {
                IEnumerable<string> visiblePairs;
                if (_configService.Current.ShowUidInDtrTooltip)
                {
                    visiblePairs = _pairManager.GetOnlineUserPairs()
                        .Where(x => x.IsVisible)
                        .Select(x => string.Format(CultureInfo.CurrentCulture, "{0} ({1})", _configService.Current.PreferNoteInDtrTooltip ? x.GetNoteOrName() : x.PlayerName, x.UserData.AliasOrUID));
                }
                else
                {
                    visiblePairs = _pairManager.GetOnlineUserPairs()
                        .Where(x => x.IsVisible)
                        .Select(x => string.Format(CultureInfo.CurrentCulture, "{0}", _configService.Current.PreferNoteInDtrTooltip ? x.GetNoteOrName() : x.PlayerName));
                }

                tooltip = Loc.Get("DtrEntry.Tooltip.Connected");
                if (pendingNearby > 0)
                {
                    tooltip += $"{Environment.NewLine}{string.Format(CultureInfo.CurrentCulture, Loc.Get("DtrEntry.Tooltip.PendingInvites"), pendingNearby)}";
                }
                tooltip += $"{Environment.NewLine}----------{Environment.NewLine}{string.Join(Environment.NewLine, visiblePairs)}";
                colors = _configService.Current.DtrColorsPairsInRange;
            }
            else
            {
                tooltip = Loc.Get("DtrEntry.Tooltip.Connected");
                if (pendingNearby > 0)
                {
                    tooltip += $"{Environment.NewLine}{string.Format(CultureInfo.CurrentCulture, Loc.Get("DtrEntry.Tooltip.PendingInvites"), pendingNearby)}";
                }
                colors = _configService.Current.DtrColorsDefault;
            }
        }
        else
        {
            text = RenderDtrStyle(_configService.Current.DtrStyle, "\uE04C");
            tooltip = Loc.Get("DtrEntry.Tooltip.Disconnected");
            colors = _configService.Current.DtrColorsNotConnected;
        }

        var activeSlotName = _slotService.ActiveSlotName;
        if (_apiController.IsConnected && activeSlotName != null)
        {
            var remaining = _slotService.LeaveTimeRemaining;
            if (remaining.HasValue)
            {
                colors = _configService.Current.DtrColorsLeavingSlot;
                var countdown = $"{(int)remaining.Value.TotalMinutes}:{remaining.Value.Seconds:D2}";
                tooltip += $"{Environment.NewLine}----------{Environment.NewLine}{string.Format(CultureInfo.CurrentCulture, Loc.Get("DtrEntry.Tooltip.LeavingSlotZone"), activeSlotName, countdown)}";
            }
            else
            {
                colors = _configService.Current.DtrColorsInSlot;
                tooltip += $"{Environment.NewLine}----------{Environment.NewLine}{string.Format(CultureInfo.CurrentCulture, Loc.Get("DtrEntry.Tooltip.InSlotZone"), activeSlotName)}";
            }
        }

        if (!_configService.Current.UseColorsInDtr)
            colors = default;

        if (!string.Equals(text, _text, StringComparison.Ordinal) || !string.Equals(tooltip, _tooltip, StringComparison.Ordinal) || colors != _colors)
        {
            _text = text;
            _tooltip = tooltip;
            _colors = colors;
            _entry.Value.Text = BuildColoredSeString(text, colors);
            _entry.Value.Tooltip = tooltip;
        }
    }

    public static string RenderDtrStyle(int styleNum, string text)
    {
        if (styleNum == 0)
            return DefaultGlyph + " " + text;

        var style = (DtrStyle)(styleNum - 1);

        return style switch
        {
            DtrStyle.Style1 => $"\xE039 {text}",
            DtrStyle.Style2 => $"\xE0BC {text}",
            DtrStyle.Style3 => $"\xE0BD {text}",
            DtrStyle.Style4 => $"\xE03A {text}",
            DtrStyle.Style5 => $"\xE033 {text}",
            DtrStyle.Style6 => $"\xE038 {text}",
            DtrStyle.Style7 => $"\xE05D {text}",
            DtrStyle.Style8 => $"\xE03C{text}",
            DtrStyle.Style9 => $"\xE040 {text} \xE041",
            _ => DefaultGlyph + " " + text
        };
    }

    #region Colored SeString
    private const byte _colorTypeForeground = 0x13;
    private const byte _colorTypeGlow = 0x14;

    private static SeString BuildColoredSeString(string text, Colors colors)
    {
        var ssb = new SeStringBuilder();
        if (colors.Foreground != default)
            ssb.Add(BuildColorStartPayload(_colorTypeForeground, colors.Foreground));
        if (colors.Glow != default)
            ssb.Add(BuildColorStartPayload(_colorTypeGlow, colors.Glow));
        ssb.AddText(text);
        if (colors.Glow != default)
            ssb.Add(BuildColorEndPayload(_colorTypeGlow));
        if (colors.Foreground != default)
            ssb.Add(BuildColorEndPayload(_colorTypeForeground));
        return ssb.Build();
    }

    private static RawPayload BuildColorStartPayload(byte colorType, uint color)
        => new(unchecked([0x02, colorType, 0x05, 0xF6, byte.Max((byte)color, 0x01), byte.Max((byte)(color >> 8), 0x01), byte.Max((byte)(color >> 16), 0x01), 0x03]));

    private static RawPayload BuildColorEndPayload(byte colorType)
        => new([0x02, colorType, 0x02, 0xEC, 0x03]);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct Colors(uint Foreground = default, uint Glow = default);
    #endregion
}