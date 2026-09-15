using System.Net;
using DirectDrop.Core.Models;
using DirectDrop.Server.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DirectDrop.Server;

/// <summary>
/// Owns the lifetime of the embedded Kestrel server for one DirectDrop
/// session. A fresh WebApplication is built on every Start() call (and torn
/// down on Stop()/dispose), which is what lets the port change between
/// sessions without restarting the whole desktop process.
/// </summary>
public sealed class DirectDropServer : IAsyncDisposable
{
    private WebApplication? _app;

    public int Port { get; private set; }
    public IPAddress? BoundAddress { get; private set; }
    public string? BaseUrl => BoundAddress is null ? null : $"http://{BoundAddress}:{Port}";

    /// <summary>
    /// Starts Kestrel bound to <paramref name="bindAddress"/>, trying
    /// <paramref name="preferredPort"/> first and walking forward through
    /// up to <paramref name="maxPortAttempts"/> ports if it's already in use.
    /// Never fails silently - it either returns the bound port or throws.
    /// </summary>
    public async Task<int> StartAsync(
        SessionState session,
        IPAddress bindAddress,
        string wwwRootPath,
        int preferredPort = 8765,
        int maxPortAttempts = 20,
        CancellationToken cancellationToken = default)
    {
        Exception? lastFailure = null;

        for (int port = preferredPort; port < preferredPort + maxPortAttempts; port++)
        {
            var builder = WebApplication.CreateBuilder();

            // The desktop app has its own logging (AppLogger -> the local log
            // file); ASP.NET Core's console logger would just be noise (and
            // there's no console window in a WinExe anyway).
            builder.Logging.ClearProviders();

            builder.WebHost.UseKestrel(options =>
            {
                // Bind the listener to all IPv4 interfaces on this port. The QR code still
                // advertises the specific hotspot IPv4 selected by NetworkInfoService,
                // but listening on all interfaces avoids failures when Windows exposes
                // the Mobile Hotspot address through a virtual adapter whose binding
                // semantics differ between Windows builds. This does not change the
                // transfer pipeline, protocol, buffering, or throughput path.
                options.Listen(IPAddress.Any, port);
                // No request body size cap: DirectDrop streams large files
                // as one contiguous request and never buffers the whole file.
                options.Limits.MaxRequestBodySize = null;

                // This is the actual fix for "photos upload fine but videos
                // stall partway through": Kestrel's default MinRequestBodyDataRate
                // (240 bytes/sec, after a 5s grace period) kills a connection
                // that isn't receiving bytes fast enough. A photo is small
                // enough to clear the whole request before that ever kicks in.
                // A video is bigger AND the iPhone often needs a few seconds
                // to export/decode it from Photos (especially if it's stored
                // in iCloud and has to download first) before the browser can
                // even start pushing bytes - that pause alone is enough to
                // trip the 5s/240B-per-sec floor and Kestrel aborts the
                // request, which looks exactly like a stall that never
                // recovers. Disabling both the request and response minimum
                // data rate removes that artificial ceiling so large and/or
                // slow-arriving files (any type, any size) are allowed to
                // take as long as they need.
                options.Limits.MinRequestBodyDataRate = null;
                options.Limits.MinResponseDataRate = null;

                // Keep the connection alive long enough for large local-LAN
                // transfers and slow phone/browser startup conditions.
                options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
            });

            builder.Environment.WebRootPath = wwwRootPath;
            builder.Services.AddSingleton(session);

            WebApplication app = builder.Build();

            try
            {
                app.UseMiddleware<TokenAuthMiddleware>();
                app.UseDefaultFiles();
                app.UseStaticFiles();

                StatusEndpoints.Map(app);
                UploadEndpoints.Map(app);
                DownloadEndpoints.Map(app);

                await app.StartAsync(cancellationToken);

                _app = app;
                Port = port;
                BoundAddress = bindAddress;
                return port;
            }
            catch (IOException ex) when (LooksLikePortInUse(ex))
            {
                lastFailure = ex;
                await app.DisposeAsync();
                // loop and try the next port
            }
            catch
            {
                // Never leave a half-started WebApplication alive when a bind
                // or listener initialization fails for a reason other than
                // port contention. A leaked listener is exactly the kind of
                // stale server state that can make the next DirectDrop start
                // look broken until the whole app is restarted.
                await app.DisposeAsync();
                throw;
            }
        }

        throw new InvalidOperationException(
            $"Could not find a free port between {preferredPort} and {preferredPort + maxPortAttempts - 1}.",
            lastFailure);
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
        Port = 0;
        BoundAddress = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static bool LooksLikePortInUse(IOException ex) =>
        ex.Message.Contains("already in use", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || ex.InnerException is System.Net.Sockets.SocketException
        {
            SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse
        };
}
