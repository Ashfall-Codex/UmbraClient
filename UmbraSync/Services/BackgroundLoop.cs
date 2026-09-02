namespace UmbraSync.Services;
internal static class BackgroundLoop
{
    public static async Task StopAsync(CancellationTokenSource cts, Task? loopTask)
    {
        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
            if (loopTask != null)
            {
                await loopTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // sortie attendue de la boucle
        }
        finally
        {
            cts.Dispose();
        }
    }
}
