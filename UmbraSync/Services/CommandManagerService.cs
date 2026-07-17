using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using System.Globalization;
using UmbraSync.FileCache;
using UmbraSync.MareConfiguration;
using UmbraSync.MareConfiguration.Models;
using UmbraSync.Services.Mediator;
using UmbraSync.Services.ServerConfiguration;
using UmbraSync.UI;

namespace UmbraSync.Services;

public sealed class CommandManagerService : IDisposable
{
    private const string _commandName = "/usync";
    private const string _commandAlias = "/umbrasync";

    private readonly ApiController _apiController;
    private readonly ICommandManager _commandManager;
    private readonly MareMediator _mediator;
    private readonly MareConfigService _mareConfigService;
    private readonly PerformanceCollectorService _performanceCollectorService;
    private readonly CacheMonitor _cacheMonitor;
    private readonly ServerConfigurationManager _serverConfigurationManager;

    public CommandManagerService(ICommandManager commandManager, PerformanceCollectorService performanceCollectorService,
        ServerConfigurationManager serverConfigurationManager, CacheMonitor periodicFileScanner,
        ApiController apiController, MareMediator mediator, MareConfigService mareConfigService)
    {
        _commandManager = commandManager;
        _performanceCollectorService = performanceCollectorService;
        _serverConfigurationManager = serverConfigurationManager;
        _cacheMonitor = periodicFileScanner;
        _apiController = apiController;
        _mediator = mediator;
        _mareConfigService = mareConfigService;
        _commandManager.AddHandler(_commandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Ouvre l'interface UmbraSync. Sous-commandes : toggle [on|off], gpose, rescan, perf, analyze"
        });
        _commandManager.AddHandler(_commandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = $"Alias de {_commandName}."
        });
    }

    public void Dispose()
    {
        _commandManager.RemoveHandler(_commandName);
        _commandManager.RemoveHandler(_commandAlias);
    }


    private void OnCommand(string command, string args)
    {
        var splitArgs = args.ToLowerInvariant().Trim().Split(" ", StringSplitOptions.RemoveEmptyEntries);

        if (splitArgs.Length == 0)
        {
            // Interpret this as toggling the UI
            if (_mareConfigService.Current.HasValidSetup())
                _mediator.Publish(new UiToggleMessage(typeof(CompactUi)));
            else
                _mediator.Publish(new UiToggleMessage(typeof(IntroUi)));
            return;
        }

        if (string.Equals(splitArgs[0], "toggle", StringComparison.OrdinalIgnoreCase))
        {
            if (_apiController.ServerState == WebAPI.SignalR.Utils.ServerState.Disconnecting)
            {
                _mediator.Publish(new NotificationMessage("Umbra disconnecting", "Cannot use /toggle while Umbra is still disconnecting",
                    NotificationType.Error));
            }

            if (!_serverConfigurationManager.HasServers) return;
            var fullPause = splitArgs.Length > 1 ? splitArgs[1] switch
            {
                "on" => false,
                "off" => true,
                _ => !_serverConfigurationManager.CurrentServer.FullPause,
            } : !_serverConfigurationManager.CurrentServer.FullPause;

            if (fullPause != _serverConfigurationManager.CurrentServer.FullPause)
            {
                _serverConfigurationManager.CurrentServer.FullPause = fullPause;
                _serverConfigurationManager.Save();
                _ = _apiController.CreateConnections();
            }
        }
        else if (string.Equals(splitArgs[0], "gpose", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new UiToggleMessage(typeof(CharaDataHubUi)));
        }
        else if (string.Equals(splitArgs[0], "rescan", StringComparison.OrdinalIgnoreCase))
        {
            _cacheMonitor.InvokeScan();
        }
        else if (string.Equals(splitArgs[0], "perf", StringComparison.OrdinalIgnoreCase))
        {
            if (splitArgs.Length > 1 && int.TryParse(splitArgs[1], CultureInfo.InvariantCulture, out var limitBySeconds))
            {
                _performanceCollectorService.PrintPerformanceStats(limitBySeconds);
            }
            else
            {
                _performanceCollectorService.PrintPerformanceStats();
            }
        }
        else if (string.Equals(splitArgs[0], "medi", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.PrintSubscriberInfo();
        }
        else if (string.Equals(splitArgs[0], "analyze", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new UiToggleMessage(typeof(DataAnalysisUi)));
        }
        else if (string.Equals(splitArgs[0], "npcadd", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new HousingNpcAddRequestMessage());
        }
        else if (string.Equals(splitArgs[0], "npcwipe", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new HousingNpcWipeMessage());
        }
        else if (string.Equals(splitArgs[0], "npcedit", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new UiToggleMessage(typeof(UI.HousingNpcSceneEditorUi)));
        }
        else if (string.Equals(splitArgs[0], "npcsharetest", StringComparison.OrdinalIgnoreCase))
        {
            _mediator.Publish(new HousingNpcShareTestMessage());
        }
    }
}