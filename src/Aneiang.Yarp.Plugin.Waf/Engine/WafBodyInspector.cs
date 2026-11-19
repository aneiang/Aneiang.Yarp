using System.Buffers;
using System.Net;
using System.Text;
using Aneiang.Yarp.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aneiang.Yarp.Plugin.Waf;

/// <summary>
/// Decides whether a body may be read, and reads it without buffering more than it scans.
/// </summary>
/// <remarks>
/// Two things changed from the previous implementation, and both were causing real failures:
/// every POST/PUT/PATCH body was read regardless of content type, which meant binary uploads were
/// pattern-matched as if they were text; and the read called <c>EnableBuffering()</c> with no
/// bound, which spilled the entire upload to a temp file to scan the first 100KB of it.
/// </remarks>
internal static class WafBodyInspector
{
    public static bool IsScannable(string? contentType, WafBodyInspectionOptions options)
    {
        if (!options.Enabled || options.MaxBytes <= 0) return false;

        if (string.IsNullOrWhiteSpace(contentType)) return options.InspectUnknownContentType;

        var value = contentType.Trim();

        foreach (var candidate in options.ContentTypes ?? [])
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (value.StartsWith(candidate.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Structured syntax suffixes: application/vnd.api+json, application/soap+xml, ...
        return value.Contains("+json", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("+xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the decoded body text, or <c>null</c> when the body is not going to be inspected.
    /// </summary>
    /// <remarks>
    /// Bodies without a declared length are skipped. Kestrel enforces <c>Content-Length</c>, so a
    /// declared length can be trusted and the buffer can be sized exactly; a chunked body cannot be
    /// bounded without buffering all of it, and buffering all of it is the thing being avoided.
    /// </remarks>
    public static async Task<string?> TryReadAsync(
        HttpContext context,
        WafBodyInspectionOptions options,
        ILogger logger)
    {
        var request = context.Request;

        if (request.ContentLength is not { } declared || declared <= 0) return null;

        // Never truncate: a prefix scan of a large body costs the same as buffering it and finds
        // only what the attacker chose to put at the front.
        if (declared > options.MaxBytes) return null;

        var limit = (int)declared;

        // Buffer in memory only: the threshold is the declared length, so FileBufferingReadStream
        // never spills to a temp file. bufferLimit is the same value because reading past the limit
        // is exactly what is being prevented.
        request.EnableBuffering(limit + 1, limit);

        var buffer = ArrayPool<byte>.Shared.Rent(limit);
        try
        {
            var read = 0;
            while (read < limit)
            {
                var chunk = await request.Body.ReadAsync(buffer.AsMemory(read, limit - read), context.RequestAborted);
                if (chunk == 0) break;
                read += chunk;
            }

            request.Body.Position = 0;

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            if (request.ContentType?.Contains("urlencoded", StringComparison.OrdinalIgnoreCase) == true)
                text = WebUtility.UrlDecode(text);

            return text;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            request.Body.Position = 0;
            logger.LogDebug(exception, "WAF could not read the request body; the body was not inspected.");
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
