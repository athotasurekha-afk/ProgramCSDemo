using Microsoft.Extensions.Logging;

namespace ProgramCSDemo.Api;

internal static class ReadinessLog
{
    private static readonly Action<ILogger, Exception?> ProbeFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1, "ReadinessProbeFailed"),
            "Storage readiness probe failed");

    public static void LogFailure(ILogger logger, Exception exception) =>
        ProbeFailed(logger, exception);
}
