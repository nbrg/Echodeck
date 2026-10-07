using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Echodeck.Audio.Capture;
using Echodeck.Audio.Devices;
using Echodeck.Core.Audio;
using Echodeck.Core.Discord;
using Echodeck.Core.Infrastructure;
using Echodeck.Core.Settings;
using Echodeck.Core.Mixing;
using Echodeck.Audio.Output;
using Echodeck.Audio.Setup;

namespace Echodeck.Audio.Diagnostics;

/// <summary>Builds the plain-text report behind the "Copy diagnostics" button.</summary>
public sealed class DiagnosticsReport
{
    private readonly DiscordCaptureService _capture;
    private readonly RollingAudioBuffer _buffer;
    private readonly AudioDeviceService _devices;
    private readonly IProcessSnapshotProvider _processes;
    private readonly SettingsService _settings;
    private readonly FileLoggerProvider _log;
    private readonly AppPaths _paths;
    private readonly MicrophoneCaptureService _mic;
    private readonly VirtualOutputService _output;
    private readonly MicJitterBuffer _jitter;
    private readonly AudioSetupMonitor _setup;

    public DiagnosticsReport(DiscordCaptureService capture, RollingAudioBuffer buffer, AudioDeviceService devices,
        IProcessSnapshotProvider processes, SettingsService settings, FileLoggerProvider log, AppPaths paths,
        MicrophoneCaptureService mic, VirtualOutputService output, MicJitterBuffer jitter, AudioSetupMonitor setup)
    {
        _mic = mic;
        _output = output;
        _jitter = jitter;
        _setup = setup;
        _capture = capture;
        _buffer = buffer;
        _devices = devices;
        _processes = processes;
        _settings = settings;
        _log = log;
        _paths = paths;
    }

    public string Build()
    {
        var sb = new StringBuilder();
        void Section(string title) => sb.AppendLine().AppendLine($"== {title} ==");

        sb.AppendLine($"Echodeck {Assembly.GetEntryAssembly()?.GetName().Version} diagnostics — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({Environment.OSVersion.Version}), {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}, process {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"Process loopback supported by OS: {ProcessLoopbackSource.IsSupportedByOs}");
        sb.AppendLine($"Working set: {Environment.WorkingSet / (1024 * 1024)} MB, GC heap: {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
        sb.AppendLine($"Data folder: {_paths.Root}");

        Section("Capture");
        var status = _capture.Status;
        sb.AppendLine($"State: {status.State}, method: {status.Method}");
        sb.AppendLine($"Summary: {status.Summary}");
        sb.AppendLine($"Discord: {status.DiscordFlavor ?? "-"} PID {status.DiscordProcessId?.ToString() ?? "-"}");
        sb.AppendLine($"Source: {status.SourceDescription ?? "-"}");
        sb.AppendLine($"Buffer: capacity {_buffer.Capacity.TotalSeconds:F0}s, filled {_buffer.Available.TotalSeconds:F1}s, frames written {_buffer.TotalFramesWritten}");

        Section("Microphone → Discord");
        sb.AppendLine($"Microphone: {(_mic.Status.Active ? "active" : "inactive")} {_mic.Status.DeviceName} — {_mic.Status.Message}");
        sb.AppendLine($"Discord output: {(_output.Status.Active ? "active" : "inactive")} {_output.Status.DeviceName} — {_output.Status.Message}");
        sb.AppendLine($"Mic buffer: {_jitter.BufferedFrames} frames (target {_jitter.TargetFrames}), underruns {_jitter.Underruns}, drift corrections {_jitter.CorrectedFrames} frames, overflow drops {_jitter.DroppedOnOverflowFrames} frames");

        Section("Setup check");
        var issues = _setup.Issues;
        if (issues.Count == 0) sb.AppendLine("no problems found");
        foreach (var issue in issues) sb.AppendLine($"{issue.Severity}: {issue.Problem} Fix: {issue.Fix}");

        Section("Discord processes");
        try
        {
            var instances = DiscordProcessLocator.FindInstances(_processes.GetProcesses());
            if (instances.Count == 0) sb.AppendLine("none running");
            foreach (var i in instances)
                sb.AppendLine($"{i.Flavor}: root {i.RootProcessId}, tree [{string.Join(", ", i.ProcessIds)}]");
        }
        catch (Exception ex) { sb.AppendLine($"error: {ex.Message}"); }

        Section("Output devices");
        foreach (var d in _devices.GetOutputDevices()) sb.AppendLine($"{(d.IsDefault ? "* " : "  ")}{d.Name}  [{d.Id}]");
        Section("Input devices");
        foreach (var d in _devices.GetInputDevices()) sb.AppendLine($"{(d.IsDefault ? "* " : "  ")}{d.Name}  [{d.Id}]");
        var (cableIn, cableOut) = _devices.FindVirtualCable();
        sb.AppendLine($"VB-CABLE: input {(cableIn is null ? "MISSING" : "found")}, output {(cableOut is null ? "MISSING" : "found")}");

        Section("Settings");
        sb.AppendLine(JsonSerializer.Serialize(_settings.Current, new JsonSerializerOptions { WriteIndented = true }));

        Section("Recent log");
        foreach (string line in _log.GetRecentLines().TakeLast(200)) sb.AppendLine(line);
        return sb.ToString();
    }
}
