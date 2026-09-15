using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DirectDrop.Core;
using DirectDrop.Core.Models;
using DirectDrop.Server;
using Xunit;

namespace DirectDrop.Tests;

/// <summary>
/// These tests start a real DirectDropServer bound to loopback and talk to
/// it with a real HttpClient - no mocking of Kestrel or of the endpoints.
/// The same scenarios are also exercised, byte-for-byte, by
/// tools/DirectDrop.SmokeTest, which requires no NuGet access at all; that
/// tool is what actually ran during development. These xUnit tests exist so
/// the same coverage shows up in `dotnet test` and in CI.
/// </summary>
public sealed class ServerIntegrationTests : IAsyncLifetime
{
    private readonly string _sharedDir = Path.Combine(Path.GetTempPath(), "dd-it-shared-" + Guid.NewGuid().ToString("N"));
    private readonly string _uploadDir = Path.Combine(Path.GetTempPath(), "dd-it-upload-" + Guid.NewGuid().ToString("N"));
    private readonly string _wwwroot = Path.Combine(Path.GetTempPath(), "dd-it-www-" + Guid.NewGuid().ToString("N"));
    private readonly DirectDropServer _server = new();
    private readonly string _token = TokenValidator.GenerateToken();
    private HttpClient _http = null!;
    private byte[] _existingFileBytes = null!;
    private const string ExistingFileName = "video.mp4";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_sharedDir);
        Directory.CreateDirectory(_uploadDir);
        Directory.CreateDirectory(_wwwroot);
        await File.WriteAllTextAsync(Path.Combine(_wwwroot, "index.html"), "<html></html>");

        _existingFileBytes = RandomNumberGenerator.GetBytes(64 * 1024 + 123);
        await File.WriteAllBytesAsync(Path.Combine(_sharedDir, ExistingFileName), _existingFileBytes);

        var session = new SessionState
        {
            Token = _token,
            SharedFolder = _sharedDir,
            UploadDestination = _uploadDir,
            PcDisplayName = "Test-PC",
        };
        _session = session;
        session.RegisterSharedFile(Path.Combine(_sharedDir, ExistingFileName));

        int port = await _server.StartAsync(session, IPAddress.Loopback, _wwwroot, preferredPort: 19765);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        Directory.Delete(_sharedDir, recursive: true);
        Directory.Delete(_uploadDir, recursive: true);
        Directory.Delete(_wwwroot, recursive: true);
    }

    private HttpRequestMessage AuthedRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-DirectDrop-Token", _token);
        return req;
    }

    [Fact]
    public async Task Health_check_requires_no_token()
    {
        var res = await _http.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Api_routes_reject_missing_token()
    {
        var res = await _http.GetAsync("/api/files");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Api_routes_reject_wrong_token()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/files");
        req.Headers.Add("X-DirectDrop-Token", "not-the-real-token");
        var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task File_list_includes_the_seeded_file_with_correct_size()
    {
        var res = await _http.SendAsync(AuthedRequest(HttpMethod.Get, "/api/files"));
        var files = await res.Content.ReadFromJsonAsync<List<JsonElement>>();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var entry = Assert.Single(files!);
        Assert.Equal(ExistingFileName, entry.GetProperty("relativePath").GetString());
        Assert.Equal(_existingFileBytes.Length, entry.GetProperty("sizeBytes").GetInt64());
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..%2f..%2fetc%2fpasswd")]
    [InlineData("/etc/passwd")]
    public async Task Download_rejects_path_traversal_attempts(string maliciousPath)
    {
        var res = await _http.SendAsync(AuthedRequest(HttpMethod.Get,
            $"/api/files/download?path={Uri.EscapeDataString(maliciousPath)}"));

        Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_returns_the_exact_original_bytes()
    {
        var res = await _http.SendAsync(AuthedRequest(HttpMethod.Get, $"/api/files/download?path={ExistingFileName}"));
        byte[] downloaded = await res.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(_existingFileBytes, downloaded);
    }

    [Fact]
    public async Task Range_request_returns_partial_content_with_correct_bytes()
    {
        var req = AuthedRequest(HttpMethod.Get, $"/api/files/download?path={ExistingFileName}");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, _existingFileBytes.Length - 1);

        var res = await _http.SendAsync(req);
        byte[] partial = await res.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.Equal(_existingFileBytes[1000..], partial);
    }

    [Fact]
    public async Task Phone_can_cancel_a_pc_to_phone_transfer_server_side()
    {
        string transferId = _http.BaseAddress is not null
            ? "dl-cancel-" + Guid.NewGuid().ToString("N")
            : throw new InvalidOperationException();

        // Queue the same server-side transfer the desktop creates when it
        // publishes a file. The phone's cancel action must be able to reach
        // the server and change the authoritative state, not just abort its
        // own browser request.
        var session = GetSessionFromServerForTestOnly();
        TransferItem transfer = session.GetOrAddTransfer(
            transferId, ExistingFileName, _existingFileBytes.Length, TransferDirection.Download);
        Assert.Equal(TransferStatus.Queued, transfer.Status);

        var res = await _http.SendAsync(AuthedRequest(
            HttpMethod.Post, $"/api/files/cancel/{transferId}"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(TransferStatus.Cancelled, transfer.Status);
    }

    private SessionState GetSessionFromServerForTestOnly()
    {
        // The test server registers SessionState as the server's singleton; the
        // integration test keeps no separate reference, so recover it from the
        // test fixture through a small helper field populated at startup.
        return _session!;
    }

    [Fact]
    public async Task Contiguous_upload_round_trips_exact_bytes_across_a_simulated_resume()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(500_000);
        const string fileName = "phone-video.mov";

        var initRes = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/upload/init")
        {
            Headers = { { "X-DirectDrop-Token", _token } },
            Content = JsonBody(new { fileName, fileSize = payload.Length }),
        });
        var initJson = await initRes.Content.ReadFromJsonAsync<JsonElement>();
        string uploadId = initJson.GetProperty("uploadId").GetString()!;

        int firstLength = payload.Length / 2;
        var firstReq = AuthedRequest(HttpMethod.Post, $"/api/upload/stream/{uploadId}");
        firstReq.Headers.Add("X-Upload-Offset", "0");
        firstReq.Content = new ByteArrayContent(payload[..firstLength]);
        var firstRes = await _http.SendAsync(firstReq);
        Assert.True(firstRes.IsSuccessStatusCode);

        var statusRes = await _http.SendAsync(AuthedRequest(HttpMethod.Get, $"/api/upload/status/{uploadId}"));
        var statusJson = await statusRes.Content.ReadFromJsonAsync<JsonElement>();
        long offset = statusJson.GetProperty("offset").GetInt64();
        Assert.Equal(firstLength, offset);

        var resumeReq = AuthedRequest(HttpMethod.Post, $"/api/upload/stream/{uploadId}");
        resumeReq.Headers.Add("X-Upload-Offset", offset.ToString());
        resumeReq.Content = new ByteArrayContent(payload[(int)offset..]);
        var resumeRes = await _http.SendAsync(resumeReq);
        Assert.True(resumeRes.IsSuccessStatusCode);

        var completeRes = await _http.SendAsync(AuthedRequest(HttpMethod.Post, $"/api/upload/complete/{uploadId}"));
        Assert.Equal(HttpStatusCode.OK, completeRes.StatusCode);

        byte[] onDisk = await File.ReadAllBytesAsync(Path.Combine(_uploadDir, fileName));
        Assert.Equal(payload, onDisk);
        Assert.False(File.Exists(Path.Combine(_uploadDir, ".directdrop-tmp", uploadId + ".part")));
    }

    [Fact]
    public async Task Upload_rejects_a_resume_offset_that_does_not_match_server_state()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(10_000);
        var initRes = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/upload/init")
        {
            Headers = { { "X-DirectDrop-Token", _token } },
            Content = JsonBody(new { fileName = "x.bin", fileSize = payload.Length }),
        });
        var initJson = await initRes.Content.ReadFromJsonAsync<JsonElement>();
        string uploadId = initJson.GetProperty("uploadId").GetString()!;

        var req = AuthedRequest(HttpMethod.Post, $"/api/upload/stream/{uploadId}");
        req.Headers.Add("X-Upload-Offset", "5000");
        req.Content = new ByteArrayContent(payload);
        var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Second_server_started_on_an_occupied_port_falls_back_to_the_next_one()
    {
        var otherSession = new SessionState
        {
            Token = TokenValidator.GenerateToken(),
            SharedFolder = _sharedDir,
            UploadDestination = _uploadDir,
            PcDisplayName = "Other-PC",
        };
        var otherServer = new DirectDropServer();
        try
        {
            int occupiedPort = _server.Port;
            int newPort = await otherServer.StartAsync(otherSession, IPAddress.Loopback, _wwwroot, preferredPort: occupiedPort);
            Assert.NotEqual(occupiedPort, newPort);
        }
        finally
        {
            await otherServer.StopAsync();
        }
    }

    private static HttpContent JsonBody(object value) =>
        new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
