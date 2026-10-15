using Microsoft.Extensions.Configuration;

namespace ProgramCSDemo.Api;

internal static class RateLimitConfiguration
{
    public static int ReadPositiveInt(IConfiguration configuration, string key, int defaultValue)
    {
        var value = configuration.GetValue<int?>(key) ?? defaultValue;
        if (value <= 0)
        {
            throw new InvalidOperationException($"Configuration value '{key}' must be a positive integer.");
        }

        return value;
    }
}
