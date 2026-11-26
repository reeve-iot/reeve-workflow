using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReeveWorkflow.Http;

internal static class JsonRequestBody
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Strict = new();

    internal static async Task<(T? Body, IResult? Refusal)> ReadAsync<T>(
        HttpContext context, bool optional, Func<string, IResult> refuse, CancellationToken cancellationToken)
        where T : class
    {
        var request = context.Request;
        var canHaveBody = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? request.ContentLength != 0;
        if (!canHaveBody || (optional && request.ContentLength == 0))
            return optional ? (null, null) : (null, refuse("A request body is required."));
        if (!request.HasJsonContentType())
            return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));

        try
        {
            var body = await request.ReadFromJsonAsync<T>(Refusing(context), cancellationToken).ConfigureAwait(false);
            return body is null && !optional ? (null, refuse("A request body is required.")) : (body, null);
        }
        catch (JsonException exception)
        {
            return (null, refuse(Detail(exception)));
        }
        catch (BadHttpRequestException exception)
        {
            return (null, exception.StatusCode == StatusCodes.Status400BadRequest
                ? refuse("The request body could not be read.")
                : Results.StatusCode(exception.StatusCode));
        }
        catch (IOException exception)
        {
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ReeveWorkflow.Http.JsonRequestBody")
                .LogDebug(exception, "Reading the request body failed.");
            return (null, refuse("The request body could not be read."));
        }
    }

    private static JsonSerializerOptions Refusing(HttpContext context) =>
        Strict.GetValue(
            context.RequestServices.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions
                ?? JsonSerializerOptions.Web,
            source => new JsonSerializerOptions(source) { AllowDuplicateProperties = false });

    private static string Detail(JsonException exception)
    {
        var path = exception.Path is { Length: > 0 } p ? (p.Length > 200 ? p[..200] + "..." : p) : null;
        var position = exception.LineNumber is { } line && exception.BytePositionInLine is { } at
            ? $"line {line + 1}, byte {at + 1}" : null;
        var stopped = (path, position) switch
        {
            (null, null) => string.Empty,
            (null, _) => $": reading stopped at {position}",
            (_, null) => $": reading stopped at {path}",
            _ => $": reading stopped at {path} ({position})",
        };
        return $"The request body could not be read{stopped}.";
    }
}
