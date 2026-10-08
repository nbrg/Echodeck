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
        private Task<RemoteResult> Ok(string call, Guid? id = null) { Calls.Add(call); return Task.FromResult(new RemoteResult(true, "ok", id)); }

        public RemoteState GetState() => new(false, true, true, false, 7, new[] { 5, 30 });
        public IReadOnlyList<RemoteClip> GetClips() => new[] { new RemoteClip(ClipId, "Trust me", "CS2", true, 1.5, DateTime.Now) };
        public Task<RemoteResult> PlayClipAsync(Guid id) => Ok("play:" + id);
        public Task<RemoteResult> SaveLastAsync(int seconds) => Ok("save:" + seconds, ClipId);
        public Task<RemoteResult> StopAsync() => Ok("stop");
        public Task<RemoteResult> ToggleMuteAsync() => Ok("mute");
        public Task<RemoteWaveform?> GetWaveformAsync(Guid id, int buckets) =>
            Task.FromResult<RemoteWaveform?>(id == ClipId ? new RemoteWaveform(1.5, Enumerable.Repeat(50, buckets).ToArray()) : null);
        public Task<byte[]?> GetAudioAsync(Guid id) => Task.FromResult<byte[]?>(id == ClipId ? new byte[] { 82, 73, 70, 70 } : null);
        public Task<RemoteResult> PlayRangeToDiscordAsync(Guid id, RangeRequest r) => Ok($"discord:{r.Start}-{r.End}");
        public Task<RemoteResult> PreviewRangeOnPcAsync(Guid id, RangeRequest r) => Ok($"pc:{r.Start}-{r.End}");
        public Task<RemoteResult> SaveTrimAsync(Guid id, RangeRequest r) => Ok($"trim:{r.Start}-{r.End}:{r.Name}:{r.AsCopy}");
        public Task<RemoteResult> RenameAsync(Guid id, string name) => Ok("rename:" + name);
        public Task<RemoteResult> DeleteAsync(Guid id) => Ok("delete");
        public IReadOnlyList<string> GetCategories() => new[] { "Bob", "CS2" };
        public Task<RemoteResult> SetFavoriteAsync(Guid id, bool favorite) => Ok("fav:" + favorite);
        public Task<RemoteResult> SetCategoryAsync(Guid id, string? category) => Ok("cat:" + (category ?? "<none>"));
        public Task<RemoteResult> AddCategoryAsync(string name) => Ok("newcat:" + name);
        public Task<RemoteResult> PlayLastAsync() => Ok("play-last");
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
        var saved = await (await _http.SendAsync(Authed(HttpMethod.Post, "/api/save/500"))).Content.ReadFromJsonAsync<RemoteResult>();
        Assert.Equal(_backend.ClipId, saved!.ClipId);
        Assert.True((await _http.SendAsync(Authed(HttpMethod.Post, "/api/mic/toggle-mute"))).IsSuccessStatusCode);
        Assert.Equal(new[] { "play:" + _backend.ClipId, "save:120", "mute" }, _backend.Calls);
    }

    [Fact]
    public async Task TrimEditorEndpoints_Work()
    {
        var wave = await (await _http.SendAsync(Authed(HttpMethod.Get, $"/api/clips/{_backend.ClipId}/waveform?buckets=80")))
            .Content.ReadFromJsonAsync<RemoteWaveform>();
        Assert.Equal(80, wave!.Peaks.Count);

        var audio = await _http.SendAsync(Authed(HttpMethod.Get, $"/api/clips/{_backend.ClipId}/audio"));
        Assert.Equal("audio/wav", audio.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Authed(HttpMethod.Get, $"/api/clips/{Guid.NewGuid()}/audio"))).StatusCode);

        HttpRequestMessage Json(string path, object body)
        {
            var req = Authed(HttpMethod.Post, path);
            req.Content = JsonContent.Create(body);
            return req;
        }
        var id = _backend.ClipId;
        Assert.True((await _http.SendAsync(Json($"/api/clips/{id}/trim", new { start = 0.5, end = 1.25, name = "Short", asCopy = true }))).IsSuccessStatusCode);
        Assert.True((await _http.SendAsync(Json($"/api/clips/{id}/preview-range", new { start = 0.1, end = 0.2 }))).IsSuccessStatusCode);
        Assert.True((await _http.SendAsync(Json($"/api/clips/{id}/play-range", new { start = 0.1, end = 0.2 }))).IsSuccessStatusCode);
        Assert.True((await _http.SendAsync(Json($"/api/clips/{id}/rename", new { name = "New" }))).IsSuccessStatusCode);
        Assert.True((await _http.SendAsync(Authed(HttpMethod.Post, $"/api/clips/{id}/delete"))).IsSuccessStatusCode);
        Assert.Equal(new[] { "trim:0.5-1.25:Short:True", "pc:0.1-0.2", "discord:0.1-0.2", "rename:New", "delete" }, _backend.Calls);
    }

    [Fact]
    public async Task LibraryEndpoints_Work()
    {
        var cats = await (await _http.SendAsync(Authed(HttpMethod.Get, "/api/categories"))).Content.ReadFromJsonAsync<List<string>>();
        Assert.Equal(new[] { "Bob", "CS2" }, cats);

        HttpRequestMessage Json(string path, object body)
        {
            var req = Authed(HttpMethod.Post, path);
            req.Content = JsonContent.Create(body);
            return req;
        }
        var id = _backend.ClipId;
        Assert.True((await _http.SendAsync(Json($"/api/clips/{id}/favorite", new { favorite = true }))).IsSuccessStatusCode);
        Assert.True((await _http.SendAsync(Json($"/api/clips/{id}/category", new { category = "Bob" }))).IsSuccessStatusCode);
        Assert.True((await _http.SendAsync(Json($"/api/clips/{id}/category", new { category = (string?)null }))).IsSuccessStatusCode);
        Assert.True((await _http.SendAsync(Json("/api/categories", new { name = "Alice" }))).IsSuccessStatusCode);
        Assert.True((await _http.SendAsync(Authed(HttpMethod.Post, "/api/play-last"))).IsSuccessStatusCode);
        Assert.Equal(new[] { "fav:True", "cat:Bob", "cat:<none>", "newcat:Alice", "play-last" }, _backend.Calls);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }
}
