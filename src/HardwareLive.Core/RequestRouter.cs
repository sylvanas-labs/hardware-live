using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using HardwareLive.Core.Analysis;
using HardwareLive.Core.Classification;
using HardwareLive.Core.Profiles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace HardwareLive.Core;

internal sealed class RequestRouter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly byte[] _token;
    private readonly string _tokenText;
    private readonly FileLayoutStore _layouts;
    private readonly ITelemetrySource _telemetry;
    private readonly UserThresholdConfig _thresholdConfig;
    private readonly ClassificationCache _classification = new();
    private readonly TimeProvider _clock;
    private readonly DateTimeOffset _createdAt;
    private readonly string _notesPath;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public RequestRouter(
        byte[] token,
        string tokenText,
        FileLayoutStore layouts,
        ITelemetrySource telemetry,
        UserThresholdConfig? thresholdConfig = null,
        TimeProvider? clock = null,
        DateTimeOffset? createdAt = null,
        string? notesPath = null)
    {
        _token = token;
        _tokenText = tokenText;
        _layouts = layouts;
        _telemetry = telemetry;
        _thresholdConfig = thresholdConfig ?? UserThresholdConfig.Empty;
        _clock = clock ?? TimeProvider.System;
        _createdAt = createdAt ?? _clock.GetUtcNow();
        _notesPath = notesPath ?? ConfigPaths.ResolveNotesPath();
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

        if (path == "/api/notes")
        {
            await HandleNotes(context);
            return;
        }

        if (path == "/api/settings")
        {
            await HandleSettings(context);
            return;
        }

        if (StaticFiles.TryGetResource(path, out _, out _))
        {
            await HandleStaticFile(context, path);
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

    // Every directive the CSP needs (docs/SPEC.md step5): no inline script/style, no
    // cross-origin fetch/img/frame targets, no forms. Identical on every response that could
    // ever render HTML (today, only "/").
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private const string TokenPlaceholder = "__HL_TOKEN__";

    private static readonly Lazy<string> IndexHtmlTemplate = new(() => ReadEmbeddedText(StaticFiles.IndexHtmlResourceName));

    private async Task HandleRoot(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await MethodNotAllowed(context);
            return;
        }

        var encodedToken = HtmlEncoder.Default.Encode(_tokenText);
        var html = IndexHtmlTemplate.Value.Replace(TokenPlaceholder, encodedToken, StringComparison.Ordinal);

        context.Response.ContentType = "text/html; charset=utf-8";
        ApplyStaticHeaders(context);
        context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        await context.Response.WriteAsync(html);
    }

    private async Task HandleStaticFile(HttpContext context, string path)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await MethodNotAllowed(context);
            return;
        }

        if (!StaticFiles.TryGetResource(path, out var resourceName, out var contentType))
        {
            await NotFound(context);
            return;
        }

        await using var stream = typeof(HardwareLiveServer).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            await NotFound(context);
            return;
        }

        context.Response.ContentType = contentType;
        ApplyStaticHeaders(context);
        await stream.CopyToAsync(context.Response.Body);
    }

    private static void ApplyStaticHeaders(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.CacheControl = "no-store";
    }

    private static string ReadEmbeddedText(string resourceName)
    {
        using var stream = typeof(HardwareLiveServer).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private async Task HandleNotes(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await MethodNotAllowed(context);
            return;
        }

        var notes = NotesReader.Read(_notesPath);
        if (notes is null)
        {
            await EmptyStatus(context, StatusCodes.Status204NoContent);
            return;
        }

        var ageMinutes = (_clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(notes.Ts)).TotalMinutes;
        await WriteJson(context, new
        {
            at = notes.At,
            source = notes.Source,
            lines = notes.Lines,
            ageMinutes,
        });
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
            var classification = _classification.Classify(frame);
            var thresholds = ThresholdResolver.Resolve(frame, classification, _thresholdConfig);
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
                roles = classification.Roles,
                limits = classification.Limits,
                primaryCpuId = classification.PrimaryCpuId,
                primaryGpuId = classification.PrimaryGpuId,
                missingMandatory = classification.MissingMandatory,
                thresholds = thresholds.Thresholds,
                profile = new { cpu = thresholds.CpuProfile, gpu = thresholds.GpuProfile },
                labels = SensorLabelBuilder.Build(frame, classification),
            });
            return;
        }

        var uptimeSeconds = Math.Max(0, (_clock.GetUtcNow() - _createdAt).TotalSeconds);

        string reason;
        if (_telemetry.HasSamplerIdentityMismatch)
        {
            reason = "sampler identity mismatch";
        }
        else if (_telemetry.LatestFrame is null)
        {
            // LHM's first hardware scan can take 10+ seconds (docs/SPEC.md step5 feature 0):
            // no frame yet within the startup window just means "still starting", not "not
            // running". Once the window elapses with still nothing, it really isn't running.
            reason = uptimeSeconds < HardwareLiveServer.StartupWindow.TotalSeconds
                ? "starting"
                : "sampler not running";
        }
        else
        {
            var frame = _telemetry.LatestFrame;
            var classification = _classification.Classify(frame);
            if (_telemetry.IsStale)
            {
                reason = "sampler stale";
            }
            else if (classification.MissingMandatory.Count > 0)
            {
                reason = $"unmapped: {string.Join(", ", classification.MissingMandatory)}";
            }
            else
            {
                var sensorIds = classification.Roles.Select(r => r.SensorId).Distinct(StringComparer.Ordinal).ToArray();
                var snapshot = _telemetry.GetSnapshot(sensorIds);
                var thresholds = ThresholdResolver.Resolve(frame, classification, _thresholdConfig);
                var result = HealthAnalyzer.Analyze(snapshot, classification, thresholds, _thresholdConfig.Invalid);
                var unit = _layouts.GetSettings().TemperatureUnit ?? "C";
                await WriteJson(context, HealthResponsePresenter.Present(
                    result,
                    unit,
                    _layouts.SavedLayoutsCouldNotBeRead,
                    uptimeSeconds));
                return;
            }
        }

        await WriteJson(context, new
        {
            status = "UNKNOWN",
            reason,
            concerns = HealthResponsePresenter.RecoveryConcerns(_layouts.SavedLayoutsCouldNotBeRead),
            uptimeSeconds,
        });
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
            await WriteJson(context, _layouts.List().Select(item => new
            {
                item.Layout.Id,
                item.Layout.Name,
                item.Layout.Widgets,
                item.Layout.Focus,
                item.Layout.Sort,
                item.Builtin,
            }));
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

        var result = _layouts.Create(layout!);
        if (result == LayoutWriteResult.Conflict)
        {
            await EmptyStatus(context, StatusCodes.Status409Conflict);
            return;
        }

        if (result != LayoutWriteResult.Success)
        {
            await EmptyStatus(context, StatusCodes.Status400BadRequest);
            return;
        }

        await EmptyStatus(context, StatusCodes.Status201Created);
    }

    private async Task HandleLayout(HttpContext context, string id)
    {
        if (LayoutJson.IsReservedLayoutId(id))
        {
            if (HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
            {
                if (!Authorize(context))
                {
                    await EmptyStatus(context, StatusCodes.Status403Forbidden);
                    return;
                }

                await EmptyStatus(context, StatusCodes.Status400BadRequest);
                return;
            }

            await MethodNotAllowed(context);
            return;
        }

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

            if (_layouts.Replace(layout) != LayoutWriteResult.Success)
            {
                await EmptyStatus(context, StatusCodes.Status400BadRequest);
                return;
            }
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

            if (_layouts.Delete(id) != LayoutWriteResult.Success)
            {
                await EmptyStatus(context, StatusCodes.Status400BadRequest);
                return;
            }
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

        var result = _layouts.Import(layouts!);
        if (!result.Success)
        {
            await EmptyStatus(context, StatusCodes.Status400BadRequest);
            return;
        }

        await WriteJson(context, new { imported = result.Imported, renamed = result.Renamed });
    }

    private async Task HandleSettings(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            await WriteJson(context, _layouts.GetSettings());
            return;
        }

        if (!HttpMethods.IsPut(context.Request.Method))
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

        if (!LayoutJson.TryParseSettings(body.Content, out var update))
        {
            await EmptyStatus(context, StatusCodes.Status400BadRequest);
            return;
        }

        var current = _layouts.GetSettings();
        var next = new LayoutSettings(
            update!.HasActivePresetId ? update.ActivePresetId : current.ActivePresetId,
            update.HasTemperatureUnit ? update.TemperatureUnit : current.TemperatureUnit);
        if (_layouts.UpdateSettings(next) != LayoutWriteResult.Success)
        {
            await EmptyStatus(context, StatusCodes.Status400BadRequest);
            return;
        }

        await WriteJson(context, next);
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
