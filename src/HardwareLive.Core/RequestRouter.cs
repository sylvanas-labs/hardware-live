using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace HardwareLive.Core;

internal sealed class RequestRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly byte[] _token;
    private readonly string _tokenText;
    private readonly InMemoryLayoutStore _layouts;
    private readonly ITelemetrySource _telemetry;

    public RequestRouter(
        byte[] token,
        string tokenText,
        InMemoryLayoutStore layouts,
        ITelemetrySource telemetry)
    {
        _token = token;
        _tokenText = tokenText;
        _layouts = layouts;
        _telemetry = telemetry;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is null)
        {
            await NotFound(context);
            return;
        }

        if (path == "/")
        {
            await HandleRoot(context);
            return;
        }

        if (path is "/api/snapshot" or "/api/meta" or "/api/health")
        {
            await HandleTelemetryRead(context, path);
            return;
        }

        if (path == "/api/layouts")
        {
            await HandleLayouts(context);
            return;
        }

        if (path == "/api/layouts/import")
        {
            await HandleImport(context);
            return;
        }

        const string layoutPrefix = "/api/layouts/";
        if (path.StartsWith(layoutPrefix, StringComparison.Ordinal) &&
            path.Length > layoutPrefix.Length &&
            path.IndexOf('/', layoutPrefix.Length) < 0)
        {
            await HandleLayout(context, path[layoutPrefix.Length..]);
            return;
        }

        await NotFound(context);
    }

    private async Task HandleRoot(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await MethodNotAllowed(context);
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        var encodedToken = HtmlEncoder.Default.Encode(_tokenText);
        await context.Response.WriteAsync(
            $"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"hl-token\" content=\"{encodedToken}\"><title>Hardware Live</title></head><body><h1>Hardware Live</h1></body></html>");
    }

    private async Task HandleTelemetryRead(HttpContext context, string path)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await MethodNotAllowed(context);
            return;
        }

        if (path == "/api/snapshot")
        {
            await HandleSnapshot(context);
            return;
        }

        if (path == "/api/meta")
        {
            var frame = _telemetry.LatestFrame;
            await WriteJson(context, new
            {
                hardware = frame?.Hardware ?? [],
                sensors = frame?.Sensors.Select(sensor => new
                {
                    sensor.Id,
                    sensor.HardwareId,
                    sensor.Name,
                    sensor.Type,
                }) ?? [],
                pawnIoInstalled = frame?.PawnIoInstalled ?? false,
                elevated = frame?.Elevated ?? false,
                lhmVersion = frame?.LhmVersion ?? string.Empty,
            });
            return;
        }

        var reason = _telemetry.HasSamplerIdentityMismatch
            ? "sampler identity mismatch"
            : _telemetry.LatestFrame is null
                ? "sampler not running"
                : _telemetry.IsStale
                    ? "sampler stale"
                    : "classifier not implemented";
        await WriteJson(context, new { status = "UNKNOWN", reason });
    }

    private async Task HandleSnapshot(HttpContext context)
    {
        IReadOnlyCollection<string>? requestedIds = null;
        if (context.Request.Query.ContainsKey("ids"))
        {
            var ids = context.Request.Query["ids"]
                .ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (ids.Length > 200)
            {
                await EmptyStatus(context, StatusCodes.Status400BadRequest);
                return;
            }

            requestedIds = ids;
        }

        var snapshot = _telemetry.GetSnapshot(requestedIds);
        var frame = snapshot.LatestFrame;
        await WriteJson(context, new
        {
            timestampUnixMs = frame?.TimestampUnixMs,
            sequence = frame?.Sequence,
            stale = snapshot.Stale,
            sensors = frame?.Sensors.Select(sensor => new { sensor.Id, sensor.Value }) ?? [],
            history = snapshot.History,
        });
    }

    private async Task HandleLayouts(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await WriteJson(context, _layouts.List());
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await MethodNotAllowed(context);
            return;
        }

        if (!Authorize(context))
        {
            await EmptyStatus(context, StatusCodes.Status403Forbidden);
            return;
        }

        var body = await ReadJsonBody(context);
        if (body.StatusCode is not null)
        {
            await EmptyStatus(context, body.StatusCode.Value);
            return;
        }

        if (!LayoutJson.TryParseLayout(body.Content, out var layout))
        {
            await EmptyStatus(context, StatusCodes.Status400BadRequest);
            return;
        }

        if (!_layouts.Create(layout!))
        {
            await EmptyStatus(context, StatusCodes.Status409Conflict);
            return;
        }

        await EmptyStatus(context, StatusCodes.Status201Created);
    }

    private async Task HandleLayout(HttpContext context, string id)
    {
        if (HttpMethods.IsPut(context.Request.Method))
        {
            if (!Authorize(context))
            {
                await EmptyStatus(context, StatusCodes.Status403Forbidden);
                return;
            }

            var body = await ReadJsonBody(context);
            if (body.StatusCode is not null)
            {
                await EmptyStatus(context, body.StatusCode.Value);
                return;
            }

            if (!LayoutJson.TryParseLayout(body.Content, out var layout) ||
                !string.Equals(layout!.Id, id, StringComparison.Ordinal))
            {
                await EmptyStatus(context, StatusCodes.Status400BadRequest);
                return;
            }

            _layouts.Replace(layout);
            await EmptyStatus(context, StatusCodes.Status200OK);
            return;
        }

        if (HttpMethods.IsDelete(context.Request.Method))
        {
            if (!Authorize(context))
            {
                await EmptyStatus(context, StatusCodes.Status403Forbidden);
                return;
            }

            _layouts.Delete(id);
            await EmptyStatus(context, StatusCodes.Status204NoContent);
            return;
        }

        await MethodNotAllowed(context);
    }

    private async Task HandleImport(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await MethodNotAllowed(context);
            return;
        }

        if (!Authorize(context))
        {
            await EmptyStatus(context, StatusCodes.Status403Forbidden);
            return;
        }

        var body = await ReadJsonBody(context);
        if (body.StatusCode is not null)
        {
            await EmptyStatus(context, body.StatusCode.Value);
            return;
        }

        if (!LayoutJson.TryParseImport(body.Content, out var layouts))
        {
            await EmptyStatus(context, StatusCodes.Status400BadRequest);
            return;
        }

        _layouts.Import(layouts!);
        await WriteJson(context, new { imported = layouts!.Count });
    }

    private bool Authorize(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("X-HL-Token", out var values) || values.Count != 1)
        {
            return false;
        }

        try
        {
            var supplied = WebEncoders.Base64UrlDecode(values[0]!);
            return CryptographicOperations.FixedTimeEquals(_token, supplied);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<RequestBody> ReadJsonBody(HttpContext context)
    {
        var mediaType = context.Request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return new RequestBody(ReadOnlyMemory<byte>.Empty, StatusCodes.Status415UnsupportedMediaType);
        }

        if (context.Request.ContentLength > HardwareLiveServer.MaximumRequestBodySize)
        {
            context.Response.Headers.Connection = "close";
            return new RequestBody(ReadOnlyMemory<byte>.Empty, StatusCodes.Status413PayloadTooLarge);
        }

        try
        {
            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                if (body.Length + read > HardwareLiveServer.MaximumRequestBodySize)
                {
                    context.Response.Headers.Connection = "close";
                    return new RequestBody(ReadOnlyMemory<byte>.Empty, StatusCodes.Status413PayloadTooLarge);
                }

                await body.WriteAsync(buffer.AsMemory(0, read));
            }

            return new RequestBody(body.ToArray(), null);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            context.Response.Headers.Connection = "close";
            return new RequestBody(ReadOnlyMemory<byte>.Empty, StatusCodes.Status413PayloadTooLarge);
        }
    }

    private static Task WriteJson<T>(HttpContext context, T value)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(context.Response.Body, value, JsonOptions);
    }

    private static Task MethodNotAllowed(HttpContext context) =>
        EmptyStatus(context, StatusCodes.Status405MethodNotAllowed);

    private static Task NotFound(HttpContext context) =>
        EmptyStatus(context, StatusCodes.Status404NotFound);

    private static Task EmptyStatus(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    private sealed record RequestBody(ReadOnlyMemory<byte> Content, int? StatusCode);
}
