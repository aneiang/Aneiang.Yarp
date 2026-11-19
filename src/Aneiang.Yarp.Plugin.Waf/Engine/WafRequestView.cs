using System.Net;
using Aneiang.Yarp.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Aneiang.Yarp.Plugin.Waf;

/// <summary>
/// A read-only view of the request, resolved once per request so conditions do not each
/// walk the headers and decode the query string again.
/// </summary>
/// <remarks>
/// The body is the exception: it is filled in later, and only when the policy actually needs it.
/// <see cref="BodyText"/> staying <c>null</c> therefore means "not inspected", never "empty" —
/// conditions against the body treat both as no-match, but the difference is what the logs show.
/// </remarks>
public sealed class WafRequestView
{
    public required HttpContext Context { get; init; }
    public required string Method { get; init; }

    /// <summary>Request target as the client sent it, before normalisation. Path traversal hides here.</summary>
    public required string RawPath { get; init; }

    /// <summary>Path after percent-decoding, which is what humans writing path conditions expect.</summary>
    public required string DecodedPath { get; init; }

    /// <summary>Query string without the leading <c>?</c>, percent-decoded.</summary>
    public required string DecodedQuery { get; init; }

    public required string? ClientIp { get; init; }
    public required string? ContentType { get; init; }

    /// <summary>Declared body size, or 0 when the client did not declare one.</summary>
    public required long BodySize { get; init; }

    /// <summary>Decoded body text, or <c>null</c> when the body was not inspected.</summary>
    public string? BodyText { get; set; }

    public static WafRequestView Create(HttpContext context)
    {
        var request = context.Request;
        var raw = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var rawPath = request.Path.Value ?? "";

        if (!string.IsNullOrEmpty(raw))
        {
            var queryIndex = raw.IndexOf('?');
            rawPath = queryIndex >= 0 ? raw[..queryIndex] : raw;
        }

        return new WafRequestView
        {
            Context = context,
            Method = request.Method ?? "",
            RawPath = rawPath,
            DecodedPath = Decode(rawPath),
            DecodedQuery = DecodeQueryString(request.QueryString.Value),
            ClientIp = ClientIpResolver.GetClientIp(context),
            ContentType = request.ContentType,
            BodySize = request.ContentLength ?? 0
        };
    }

    /// <summary>Header value, or empty when absent. Multiple values are joined, which is what a caller wants to match against.</summary>
    public string Header(string name) => Context.Request.Headers.TryGetValue(name, out var value)
        ? value.ToString()
        : "";

    public string Cookie(string name) => Context.Request.Cookies.TryGetValue(name, out var value) ? value : "";

    private static string Decode(string value) =>
        string.IsNullOrEmpty(value) ? "" : WebUtility.UrlDecode(value);

    private static string DecodeQueryString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var trimmed = value.TrimStart('?');
        return trimmed.Length == 0 ? "" : WebUtility.UrlDecode(trimmed);
    }
}
