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

public sealed record RemoteState(bool MicMuted, bool DiscordOutputActive, bool CaptureActive, bool ClipPlaying,
    long LibraryVersion, IReadOnlyList<int> ReplayChoices);

public sealed record RemoteClip(Guid Id, string Name, string? Category, bool Favorite, double Duration);

public sealed record RemoteResult(bool Ok, string Message);

/// <summary>What the phone can see and do. Implemented by the app.</summary>
public interface IRemoteBackend
{
    RemoteState GetState();
    IReadOnlyList<RemoteClip> GetClips();
    Task<RemoteResult> PlayClipAsync(Guid id);
    Task<RemoteResult> ReplayAsync(int seconds);
    Task<RemoteResult> StopAsync();
    Task<RemoteResult> ToggleMuteAsync();
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
            o.Limits.MaxRequestBodySize = 4096; // the API takes no bodies
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
        api.MapPost("/replay/{seconds:int}", (int seconds) => backend.ReplayAsync(Math.Clamp(seconds, 1, 60)));
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
