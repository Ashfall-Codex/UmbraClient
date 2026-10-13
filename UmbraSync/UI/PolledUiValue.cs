using Microsoft.Extensions.Logging;

namespace UmbraSync.UI;

public sealed class PolledUiValue<T>
{
    private readonly Func<Task<T>> _refresh;
    private readonly TimeSpan _interval;
    private Task<T>? _pending;
    private DateTime _lastRefresh = DateTime.MinValue;

    public PolledUiValue(Func<Task<T>> refresh, TimeSpan interval, T initialValue)
    {
        ArgumentNullException.ThrowIfNull(refresh);

        _refresh = refresh;
        _interval = interval;
        Value = initialValue;
    }
    public T Value { get; private set; }
    
    public T Poll(ILogger? logger = null)
    {
        if (_pending is { IsCompleted: true })
        {
            var finished = _pending;
            _pending = null;

            if (finished.IsCompletedSuccessfully)
            {
                Value = finished.Result;
            }
            else
            {

                logger?.LogWarning(finished.Exception, "Rafraîchissement d'une valeur d'interface en échec");
            }
        }

        if (_pending is null && DateTime.UtcNow - _lastRefresh >= _interval)
        {
            _lastRefresh = DateTime.UtcNow;
            try
            {
                _pending = _refresh();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Lancement du rafraîchissement d'une valeur d'interface en échec");
            }
        }

        return Value;
    }
}
