using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Api.Middleware;

/// <summary>
/// Lets browsers keep the web client's content-hashed files. Webpack names its chunks after their
/// content, but the static file middleware sends no Cache-Control for them: browsers guess a
/// freshness from Last-Modified (the image's build time) and revalidate them in waves of round
/// trips on most loads. Applied from <see cref="ResponseTimeMiddleware"/>: the image replaces
/// Jellyfin.Api, not Jellyfin.Server, where the static files are set up.
/// </summary>
public static class WebClientCaching
{
    private static readonly StringValues _immutable = new("public, max-age=31536000, immutable");

    /// <summary>
    /// Marks a response for a content-hashed web client file as immutable, unless something
    /// else (a plugin rewriting the file) already chose its caching.
    /// </summary>
    /// <param name="context">The HTTP context, as the response starts.</param>
    public static void Apply(HttpContext context)
    {
        var response = context.Response;
        if (response.StatusCode is not (StatusCodes.Status200OK or StatusCodes.Status304NotModified)
            || !StringValues.IsNullOrEmpty(response.Headers.CacheControl))
        {
            return;
        }

        var path = context.Request.Path.Value;
        if (path is null || !path.Contains("/web/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsContentHashedWebFile(Path.GetFileName(path)))
        {
            response.Headers.CacheControl = _immutable;
        }
    }

    /// <summary>
    /// Whether a web client file carries a hash of its content in its name
    /// (<c>name.[contenthash].chunk.js</c>). Not the chunks of HTML templates: plugins rewrite
    /// those on the fly (File Transformation), under the same name.
    /// </summary>
    /// <param name="fileName">The file's name.</param>
    /// <returns>Whether the file can be cached as immutable.</returns>
    public static bool IsContentHashedWebFile(string fileName)
    {
        if (fileName.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var part in fileName.Split('.'))
        {
            if (part.Length >= 16 && part.All(char.IsAsciiHexDigitLower))
            {
                return true;
            }
        }

        return false;
    }
}
