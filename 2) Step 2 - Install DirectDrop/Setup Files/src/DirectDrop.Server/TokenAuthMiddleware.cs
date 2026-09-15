using DirectDrop.Core;
using DirectDrop.Core.Models;
using Microsoft.AspNetCore.Http;

namespace DirectDrop.Server;

/// <summary>
/// Rejects any request under "/api" that doesn't carry the current session's
/// token, either as the "X-DirectDrop-Token" header (used by fetch()/XHR from
/// app.js) or a "token" query string parameter (needed for plain
/// &lt;a href&gt; downloads, which can't set custom headers).
///
/// The static site shell (index.html/app.js/styles.css) is intentionally left
/// ungated: it contains no session data or file contents, so serving it costs
/// nothing, and gating it would break normal browser behaviour like caching a
/// stylesheet across a page refresh where the query string might not be
/// re-attached. Every route that actually touches files, the file list, or
/// session/device info lives under "/api" and IS gated. "/api/health" is the
/// one exception within "/api": the desktop app polls it locally to confirm
/// Kestrel is listening before it shows the QR code, and it reveals nothing
/// beyond "the process is alive".
/// </summary>
public sealed class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public TokenAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, SessionState session)
    {
        if (!RequiresToken(context.Request.Path))
        {
            await _next(context);
            return;
        }

        string? provided = context.Request.Headers["X-DirectDrop-Token"].FirstOrDefault()
                            ?? context.Request.Query["token"].FirstOrDefault();

        if (!TokenValidator.IsValid(provided, session.Token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Invalid or missing DirectDrop session token. Re-scan the QR code.");
            return;
        }

        string remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // DirectDrop only ever serves one device at a time (see SessionState's
        // TryAuthorizeDevice doc comment for why this lives here rather than
        // being a Mobile Hotspot setting) - a second device gets a plain 409
        // instead of silently sharing bandwidth with the first.
        if (!session.TryAuthorizeDevice(remoteIp))
        {
            session.BlockedDeviceAttempts[remoteIp] = DateTimeOffset.UtcNow;
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(
                "DirectDrop supports one connected device at a time. Wait for the other device to finish, then reload this page.");
            return;
        }

        session.NoteDeviceSeen(remoteIp, context.Request.Headers.UserAgent.FirstOrDefault());

        await _next(context);
    }

    private static bool RequiresToken(PathString path)
    {
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.StartsWithSegments("/api/health", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
