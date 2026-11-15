namespace UmbraSync.MareConfiguration;

public static class ConfigurationDiagnostics
{
    private const int MaxPending = 32;
    private static readonly List<(string Message, Exception? Error)> Pending = [];
    private static Action<string, Exception?>? _sink;

    public static void Report(string message, Exception? error)
    {
        Action<string, Exception?>? sink;
        lock (Pending)
        {
            sink = _sink;
            if (sink == null)
            {
                if (Pending.Count < MaxPending) Pending.Add((message, error));
                return;
            }
        }
        sink(message, error);
    }

    public static void Attach(Action<string, Exception?> sink)
    {
        List<(string Message, Exception? Error)> flush;
        lock (Pending)
        {
            _sink = sink;
            flush = [.. Pending];
            Pending.Clear();
        }
        foreach (var (message, error) in flush)
            sink(message, error);
    }
}
