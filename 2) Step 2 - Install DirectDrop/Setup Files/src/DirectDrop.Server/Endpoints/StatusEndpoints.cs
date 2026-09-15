using System.Text.Json;
using DirectDrop.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DirectDrop.Server.Endpoints;

public static class StatusEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { status = "ready" }));

        app.MapGet("/api/session", (SessionState session) => Results.Ok(new
        {
            pcName = session.PcDisplayName,
            startedAt = session.StartedAt,
        }));

        app.MapGet("/api/devices", (SessionState session) =>
            Results.Ok(session.ConnectedDevices.Values
                .OrderByDescending(d => d.LastSeenUtc).ToList()));

        app.MapGet("/api/transfers", (SessionState session) => Results.Ok(Snapshot(session)));

        // Lightweight Server-Sent Events channel. This avoids a heavyweight
        // SignalR/WebSocket dependency while giving both the browser and the
        // desktop a common, server-authoritative stream of transfer state.
        // The payload is deliberately the same shape as /api/transfers.
        app.MapGet("/api/events", async (HttpContext context, SessionState session, CancellationToken cancellationToken) =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers.Connection = "keep-alive";

            string lastJson = "";
            while (!cancellationToken.IsCancellationRequested)
            {
                string json = JsonSerializer.Serialize(Snapshot(session));
                if (!String.Equals(json, lastJson, StringComparison.Ordinal))
                {
                    await context.Response.WriteAsync($"event: transfers\ndata: {json}\n\n", cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                    lastJson = json;
                }
                else
                {
                    await context.Response.WriteAsync(": heartbeat\n\n", cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                }

                await Task.Delay(200, cancellationToken);
            }
        });
    }

    private static object Snapshot(SessionState session) => session.Transfers.Values
        .OrderByDescending(t => t.StartedAt)
        .Take(50)
        .Select(t => new
        {
            t.Id,
            t.FileName,
            t.TotalBytes,
            t.TransferredBytes,
            t.PercentComplete,
            Direction = t.Direction.ToString(),
            Status = t.Status.ToString(),
            SpeedBytesPerSecond = t.CurrentBytesPerSecond,
            EtaSeconds = t.EstimatedTimeRemaining?.TotalSeconds,
            t.ErrorMessage,
        }).ToList();
}
