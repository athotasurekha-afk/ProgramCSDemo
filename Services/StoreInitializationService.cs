using Microsoft.Extensions.Hosting;
using ProgramCSDemo.Api;
using ProgramCSDemo.Storage;

namespace ProgramCSDemo.Services;

internal sealed class StoreInitializationService(
    IUrlShortenerStore store,
    IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        RateLimitConfiguration.ReadPositiveInt(
            configuration,
            "RateLimiting:Create:PermitLimit",
            30);
        RateLimitConfiguration.ReadPositiveInt(
            configuration,
            "RateLimiting:Create:WindowSeconds",
            60);
        RateLimitConfiguration.ReadPositiveInt(
            configuration,
            "RateLimiting:Redirect:PermitLimit",
            300);
        RateLimitConfiguration.ReadPositiveInt(
            configuration,
            "RateLimiting:Redirect:WindowSeconds",
            60);
        await store.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
