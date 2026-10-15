using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace ProgramCSDemo.Api;

internal static class RateLimitProblemWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests",
                Detail = "The request rate limit was exceeded."
            },
            JsonOptions);
        var response = context.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.ContentType = "application/problem+json";
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, cancellationToken);
    }
}
