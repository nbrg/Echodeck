using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Echodeck.Remote;

/// <param name="ClipPlaying">A clip is playing into Discord.</param>
/// <param name="PreviewPlaying">A preview is playing on the PC headphones.</param>
public sealed record RemoteState(bool MicMuted, bool DiscordOutputActive, bool CaptureActive, bool ClipPlaying,
    long LibraryVersion, IReadOnlyList<int> SaveChoices, bool PreviewPlaying = false);

public sealed record RemoteClip(Guid Id, string Name, string? Category, bool Favorite, double Duration, DateTime CreatedAt);

/// <param name="ClipId">Set when the action created or changed a clip (e.g. "save last N s").</param>
public sealed record RemoteResult(bool Ok, string Message, Guid? ClipId = null);

/// <summary>Waveform for the phone's trim editor: peaks scaled 0–100.</summary>
public sealed record RemoteWaveform(double Duration, IReadOnlyList<int> Peaks);

/// <summary>A selection inside a clip, in seconds. Name/AsCopy are only used when saving a trim.</summary>
public sealed record RangeRequest(double Start, double End, string? Name = null, bool AsCopy = false);

public sealed record RenameRequest(string Name);

/// <summary>What the phone can see and do. Implemented by the app.</summary>
public interface IRemoteBackend
{
    RemoteState GetState();
    IReadOnlyList<RemoteClip> GetClips();
    Task<RemoteResult> PlayClipAsync(Guid id);
    Task<RemoteResult> SaveLastAsync(int seconds);
    /// <summary>Stops clips playing into Discord and any preview on the PC headphones.</summary>
    Task<RemoteResult> StopAsync();
    Task<RemoteResult> ToggleMuteAsync();

    // Trim editor
    Task<RemoteWaveform?> GetWaveformAsync(Guid id, int buckets);
    /// <summary>The clip's WAV file, for previewing on the phone itself.</summary>
    Task<byte[]?> GetAudioAsync(Guid id);
    Task<RemoteResult> PlayRangeToDiscordAsync(Guid id, RangeRequest range);
    Task<RemoteResult> PreviewRangeOnPcAsync(Guid id, RangeRequest range);
    Task<RemoteResult> SaveTrimAsync(Guid id, RangeRequest range);
    Task<RemoteResult> RenameAsync(Guid id, string name);
    Task<RemoteResult> DeleteAsync(Guid id);
}

/// <summary>
/// Local-network web server for the phone/tablet soundboard.
/// <list type="bullet">
/// <item>GET / serves a single touch-friendly page (embedded in this assembly, works offline).</item>
/// <item>Everything under /api needs the pairing token (header <c>X-Echodeck-Token</c>), compared in
/// constant time. Without it nobody else on the Wi-Fi can trigger clips.</item>
/// <item>Plain HTTP on the LAN only, never exposed to the internet by Echodeck. Windows Firewall
/// asks once whether to allow it.</item>
/// </list>
/// </summary>
public sealed class RemoteServer : IAsyncDisposable
{
    public const string TokenHeader = "X-Echodeck-Token";

    private readonly ILoggerProvider? _logProvider;
    private readonly ILogger _logger;
    private WebApplication? _app;

    public RemoteServer(ILoggerFactory loggerFactory, ILoggerProvider? logProvider = null)
    {
        _logger = loggerFactory.CreateLogger<RemoteServer>();
        _logProvider = logProvider;
    }

    public bool IsRunning => _app is not null;
    public int Port { get; private set; }

    public async Task StartAsync(int port, string token, IRemoteBackend backend)
    {
        await StopAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "Echodeck.Remote",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.ListenAnyIP(port);
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = 8192; // only small JSON bodies (trim ranges, names)
        });
        builder.Logging.ClearProviders();
        if (_logProvider is not null) builder.Logging.AddProvider(_logProvider);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        var app = builder.Build();
        byte[] tokenBytes = Encoding.UTF8.GetBytes(token);

        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });

        app.MapGet("/", () => Results.Content(ReadAsset("index.html"), "text/html; charset=utf-8"));
        // Exceptions inside handlers become a JSON error the page can show, never a crash.
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception ex) when (!ctx.Response.HasStarted)
            {
                _logger.LogWarning(ex, "Remote request {Path} failed", ctx.Request.Path);
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new RemoteResult(false, ex.Message));
            }
        });
        app.MapGet("/manifest.webmanifest", () => Results.Content(ReadAsset("manifest.webmanifest"), "application/manifest+json"));
        app.MapGet("/icon.png", () => Results.Bytes(ReadAssetBytes("icon.png"), "image/png"));

        var api = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
        {
            string presented = ctx.HttpContext.Request.Headers[TokenHeader].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), tokenBytes))
                return Results.Json(new RemoteResult(false, "Not paired — scan the QR code in Echodeck again."), statusCode: 401);
            return await next(ctx);
        });
        api.MapGet("/state", () => backend.GetState());
        api.MapGet("/clips", () => backend.GetClips());
        api.MapPost("/clips/{id:guid}/play", (Guid id) => backend.PlayClipAsync(id));
        api.MapPost("/save/{seconds:int}", (int seconds) => backend.SaveLastAsync(Math.Clamp(seconds, 1, 120)));
        api.MapGet("/clips/{id:guid}/waveform", async (Guid id, int? buckets) =>
            await backend.GetWaveformAsync(id, Math.Clamp(buckets ?? 600, 50, 2000)) is { } w ? Results.Ok(w) : Results.NotFound());
        api.MapGet("/clips/{id:guid}/audio", async (Guid id) =>
            await backend.GetAudioAsync(id) is { } wav ? Results.Bytes(wav, "audio/wav") : Results.NotFound());
        api.MapPost("/clips/{id:guid}/play-range", (Guid id, RangeRequest range) => backend.PlayRangeToDiscordAsync(id, range));
        api.MapPost("/clips/{id:guid}/preview-range", (Guid id, RangeRequest range) => backend.PreviewRangeOnPcAsync(id, range));
        api.MapPost("/clips/{id:guid}/trim", (Guid id, RangeRequest range) => backend.SaveTrimAsync(id, range));
        api.MapPost("/clips/{id:guid}/rename", (Guid id, RenameRequest req) => backend.RenameAsync(id, req.Name ?? ""));
        api.MapPost("/clips/{id:guid}/delete", (Guid id) => backend.DeleteAsync(id));
        api.MapPost("/stop", () => backend.StopAsync());
        api.MapPost("/mic/toggle-mute", () => backend.ToggleMuteAsync());

        await app.StartAsync();
        _app = app;
        Port = port;
        _logger.LogInformation("Phone remote listening on port {Port}", port);
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(timeout.Token);
        }
        finally
        {
            await app.DisposeAsync();
            _logger.LogInformation("Phone remote stopped");
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>
    /// Pairing links for each usable local IPv4 address (Wi-Fi/Ethernet first). The token goes in
    /// the URL fragment, which browsers never send over the network or put in server logs.
    /// </summary>
    public static IReadOnlyList<string> PairingUrls(int port, string token) =>
        LocalAddresses().Select(ip => $"http://{ip}:{port}/#t={token}").ToList();

    internal static IEnumerable<IPAddress> LocalAddresses()
    {
        var candidates = new List<(IPAddress Ip, int Rank)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            // Virtual adapters (Hyper-V, WSL, VPNs) usually have no gateway: list them last.
            int rank = (hasGateway ? 0 : 10) + (nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet ? 0 : 1);
            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address) &&
                    !addr.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    candidates.Add((addr.Address, rank));
            }
        }
        return candidates.OrderBy(c => c.Rank).Select(c => c.Ip).Distinct();
    }

    private static string ReadAsset(string name) => Encoding.UTF8.GetString(ReadAssetBytes(name));

    private static byte[] ReadAssetBytes(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("wwwroot/" + name)
                           ?? throw new FileNotFoundException(name);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
