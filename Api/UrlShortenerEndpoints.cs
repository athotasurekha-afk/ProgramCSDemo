using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ProgramCSDemo.Services;

namespace ProgramCSDemo.Api;

internal static class UrlShortenerEndpoints
{
    public static void MapUrlShortenerEndpoints(this WebApplication app)
    {
        app.MapPost("/api/links", async Task<Results<
                Created<ShortLinkResponse>,
                ProblemHttpResult>> (
                CreateShortLinkRequest request,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                IUrlShortenerService service,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                var result = await service.CreateAsync(request, idempotencyKey, cancellationToken);
                if (result.Link is not null)
                {
                    var link = result.Link;
                    var response = new ShortLinkResponse(
                        link.Code,
                        $"/{link.Code}",
                        link.CreatedAt,
                        link.ExpiresAt);
                    if (result.IdempotentReplay)
                    {
                        context.Response.Headers["Idempotency-Replayed"] = "true";
                    }

                    return TypedResults.Created($"/{link.Code}", response);
                }

                var (status, title, detail) = result.Error switch
                {
                    CreateLinkError.InvalidUrl =>
                        (400, "Invalid URL", "Provide a non-empty absolute HTTP or HTTPS URL."),
                    CreateLinkError.UnsafeUrl =>
                        (400, "Unsafe URL", "Credentials, local hosts, and non-public IP addresses cannot be shortened."),
                    CreateLinkError.InvalidCode =>
                        (400, "Invalid custom code", "Custom codes must be 4-32 letters, digits, hyphens, or underscores."),
                    CreateLinkError.ReservedCode =>
                        (400, "Reserved custom code", "That code conflicts with a reserved service route."),
                    CreateLinkError.InvalidIdempotencyKey =>
                        (400, "Invalid idempotency key", "Use 1-128 ASCII letters, digits, dots, colons, hyphens, or underscores."),
                    CreateLinkError.ExpirationInPast =>
                        (400, "Invalid expiration", "The expiration time must be in the future."),
                    CreateLinkError.CodeConflict =>
                        (409, "Code already exists", "Choose a different custom code."),
                    CreateLinkError.IdempotencyConflict =>
                        (409, "Idempotency key conflict", "This idempotency key was already used with a different request."),
                    _ => throw new InvalidOperationException("The create operation returned no link and no error.")
                };

                return TypedResults.Problem(statusCode: status, title: title, detail: detail);
            })
            .WithName("CreateShortLink")
            .WithSummary("Create a short link")
            .WithDescription("Creates a random code or reserves a requested custom code. Supply a stable Idempotency-Key to safely retry a create request.")
            .Produces<ShortLinkResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting("link-creation");

        app.MapGet("/{code}", async Task<Results<
                RedirectHttpResult,
                NotFound,
                ProblemHttpResult>> (
                string code,
                IUrlShortenerService service,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                var result = await service.ResolveAsync(code, cancellationToken);
                if (result.Status == LinkResolutionStatus.NotFound)
                {
                    return TypedResults.NotFound();
                }

                if (result.Status == LinkResolutionStatus.Expired)
                {
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status410Gone,
                        title: "Link expired",
                        detail: "This short link has expired.");
                }

                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Redirect(result.Link!.OriginalUrl);
            })
            .WithName("RedirectShortLink")
            .WithSummary("Redirect to the original URL")
            .WithDescription("Returns a temporary redirect and records a click. Expired links return 410 Gone.")
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone)
            .RequireRateLimiting("short-link-redirect");

        app.MapGet("/api/links/{code}/analytics", async Task<Results<
                Ok<LinkAnalyticsResponse>,
                NotFound>> (
                string code,
                IUrlShortenerService service,
                CancellationToken cancellationToken) =>
            {
                var link = await service.GetAnalyticsAsync(code, cancellationToken);
                return link is null
                    ? TypedResults.NotFound()
                    : TypedResults.Ok(new LinkAnalyticsResponse(
                        link.Code,
                        link.ClickCount,
                        link.CreatedAt,
                        link.ExpiresAt,
                        link.LastClickedAt));
            })
            .WithName("GetLinkAnalytics")
            .WithSummary("Get click analytics for a short link")
            .WithDescription("Returns aggregate click metrics without exposing the destination URL.")
            .Produces<LinkAnalyticsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
    }
}
