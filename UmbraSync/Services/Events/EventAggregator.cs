using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UmbraSync.MareConfiguration;
using UmbraSync.Services.Mediator;
using UmbraSync.Utils;

namespace UmbraSync.Services.Events;

public class EventAggregator : MediatorSubscriberBase, IHostedService
{
    private readonly RollingList<Event> _events = new(500);
    private readonly SemaphoreSlim _lock = new(1);
    private Task _writeChain = Task.CompletedTask;
    private readonly string _configDirectory;
    private readonly ILogger<EventAggregator> _logger;

    public Lazy<List<Event>> EventList { get; private set; }
    public bool NewEventsAvailable => !EventList.IsValueCreated;
    public string EventLogFolder => Path.Combine(_configDirectory, "eventlog");
    private static string BuildLogFileName(DateTime time) => $"{time:yyyy-MM-dd}-events.log";
    private DateTime _currentTime;

    public EventAggregator(MareConfigService configService, ILogger<EventAggregator> logger, MareMediator mareMediator) : base(logger, mareMediator)
    {
        Mediator.Subscribe<EventMessage>(this, (msg) =>
        {
            _lock.Wait();
            try
            {
                Logger.LogTrace("Received Event: {evt}", msg.Event.ToString());
                _events.Add(msg.Event);
            }
            finally
            {
                _lock.Release();
            }

            // L'écriture disque quitte le thread du jeu ; la chaîne garde l'ordre des événements.
            if (configService.Current.LogEvents)
            {
                var received = msg.Event;
                _writeChain = _writeChain.ContinueWith(_ => WriteToFile(received), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            RecreateLazy();
        });

        EventList = CreateEventLazy();
        _configDirectory = configService.ConfigurationDirectory;
        _logger = logger;
        _currentTime = DateTime.Now - TimeSpan.FromDays(1);
    }

    private void RecreateLazy()
    {
        if (!EventList.IsValueCreated) return;

        EventList = CreateEventLazy();
    }

    private Lazy<List<Event>> CreateEventLazy()
    {
        return new Lazy<List<Event>>(() =>
        {
            _lock.Wait();
            try
            {
                return [.. _events];
            }
            finally
            {
                _lock.Release();
            }
        });
    }

    private void WriteToFile(Event receivedEvent)
    {
        if (DateTime.Now.Day != _currentTime.Day)
        {
            try
            {
                _currentTime = DateTime.Now;
                var filesInDirectory = Directory.EnumerateFiles(EventLogFolder, "*.log")
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();
                if (filesInDirectory.Count > 10)
                {
                    filesInDirectory[0].Delete();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete last events");
            }
        }

        var eventLogFile = Path.Combine(EventLogFolder, BuildLogFileName(DateTime.Now));
        try
        {
            if (!Directory.Exists(EventLogFolder)) Directory.CreateDirectory(EventLogFolder);
            File.AppendAllLines(eventLogFile, [receivedEvent.ToString()]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible d'écrire dans le fichier d'événements {fichier}", eventLogFile);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting EventAggregatorService");
        Logger.LogInformation("Started EventAggregatorService");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}