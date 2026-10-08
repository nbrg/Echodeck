using Echodeck.Remote;
using Echodeck.Remote.TestHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Usage: dotnet run -- [--port 5899] [--control 5898]
int port = Arg("--port", 5899);
int controlPort = Arg("--control", 5898);

var backend = new FakeBackend();
var server = new RemoteServer(NullLoggerFactory.Instance);
await server.StartAsync(port, FakeBackend.Token, backend);

// Control API (test-only, separate port so it can never be confused with the app's /api).
var builder = WebApplication.CreateBuilder();
builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(controlPort));
builder.Logging.ClearProviders();
var control = builder.Build();
control.MapGet("/health", () => Results.Ok("ok"));
control.MapPost("/reset", () => { backend.Reset(); return Results.Ok(); });
control.MapPost("/problems", (List<RemoteProblem> problems) => { backend.Problems = problems; return Results.Ok(); });
control.MapPost("/fail", (FailureRequest f) => { backend.Failures[f.Action] = f.Message; return Results.Ok(); });
control.MapPost("/warn", (FailureRequest f) => { backend.Warnings[f.Action] = f.Message; return Results.Ok(); });
control.MapPost("/transcript", (TranscriptRequest t) => { backend.SetTranscript(t.Name, t.Transcript); return Results.Ok(); });
control.MapGet("/calls", () => backend.Calls.ToArray());
control.MapGet("/clips", () => backend.GetClips());
control.MapGet("/categories", () => backend.GetCategories());
await control.StartAsync();

Console.WriteLine($"Echodeck phone UI test host: page http://127.0.0.1:{port}/#t={FakeBackend.Token}  control http://127.0.0.1:{controlPort}");
await Task.Delay(Timeout.Infinite);

int Arg(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
}

public sealed record FailureRequest(string Action, string Message);
public sealed record TranscriptRequest(string Name, string? Transcript);
