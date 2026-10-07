using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Echodeck.Remote;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echodeck.Core.Tests;

public sealed class RemoteServerTests : IAsyncLifetime
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly FakeBackend _backend = new();
    private readonly RemoteServer _server = new(NullLoggerFactory.Instance);
    private HttpClient _http = null!;

    private sealed class FakeBackend : IRemoteBackend
    {
        public readonly Guid ClipId = Guid.NewGuid();
        public readonly List<string> Calls = new();
        public RemoteState GetState() => new(false, true, true, false, 7, new[] { 5, 10 });
        public IReadOnlyList<RemoteClip> GetClips() => new[] { new RemoteClip(ClipId, "Trust me", "CS2", true, 1.5) };
        public Task<RemoteResult> PlayClipAsync(Guid id) { Calls.Add("play:" + id); return Task.FromResult(new RemoteResult(true, "ok")); }
        public Task<RemoteResult> ReplayAsync(int seconds) { Calls.Add("replay:" + seconds); return Task.FromResult(new RemoteResult(true, "ok")); }
        public Task<RemoteResult> StopAsync() { Calls.Add("stop"); return Task.FromResult(new RemoteResult(true, "ok")); }
        public Task<RemoteResult> ToggleMuteAsync() { Calls.Add("mute"); return Task.FromResult(new RemoteResult(true, "ok")); }
    }

    public async Task InitializeAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await _server.StartAsync(port, Token, _backend);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    private HttpRequestMessage Authed(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add(RemoteServer.TokenHeader, Token);
        return req;
    }

    [Fact]
    public async Task ServesThePage_WithoutToken()
    {
        string html = await _http.GetStringAsync("/");
        Assert.Contains("Echodeck", html);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/manifest.webmanifest")).StatusCode);
        Assert.Equal("image/png", (await _http.GetAsync("/icon.png")).Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Api_RejectsMissingOrWrongToken(string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/stop");
        if (token is not null) req.Headers.Add(RemoteServer.TokenHeader, token);
        var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task Api_WithToken_ReachesBackend()
    {
        var state = await (await _http.SendAsync(Authed(HttpMethod.Get, "/api/state"))).Content.ReadFromJsonAsync<RemoteState>();
        Assert.Equal(7, state!.LibraryVersion);

        var clips = await (await _http.SendAsync(Authed(HttpMethod.Get, "/api/clips"))).Content.ReadFromJsonAsync<List<RemoteClip>>();
        Assert.Equal("Trust me", Assert.Single(clips!).Name);

        Assert.True((await _http.SendAsync(Authed(HttpMethod.Post, $"/api/clips/{_backend.ClipId}/play"))).IsSuccessStatusCode);
        Assert.True((await _http.SendAsync(Authed(HttpMethod.Post, "/api/replay/500"))).IsSuccessStatusCode);
        Assert.True((await _http.SendAsync(Authed(HttpMethod.Post, "/api/mic/toggle-mute"))).IsSuccessStatusCode);
        Assert.Equal(new[] { "play:" + _backend.ClipId, "replay:60", "mute" }, _backend.Calls);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }
}
