using ProgramCSDemo.Storage;

namespace ProgramCSDemo.Services;

internal sealed class AnalyticsCheckpointService(
    IUrlShortenerStore store,
    ILogger<AnalyticsCheckpointService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(2);
    private static readonly Action<ILogger, Exception?> CheckpointStopped =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, "CheckpointStopped"),
            "Analytics checkpoint service stopped after flushing pending clicks");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckpointInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await store.FlushAnalyticsAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await store.FlushAnalyticsAsync(CancellationToken.None);
            CheckpointStopped(logger, null);
        }
    }
}
