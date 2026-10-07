using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Echodeck.App.Services;
using Echodeck.Core.Settings;
using QRCoder;

namespace Echodeck.App.ViewModels;

/// <summary>
/// "Phone" tab: turn the local-network remote on/off, show the pairing QR code and link.
/// </summary>
public sealed partial class PhoneViewModel : ObservableObject, IDisposable
{
    private readonly RemoteHost _remote;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly Dispatcher _dispatcher;
    private bool _loading;

    public PhoneViewModel(RemoteHost remote, SettingsService settings, DialogService dialogs)
    {
        _remote = remote;
        _settings = settings;
        _dialogs = dialogs;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _loading = true;
        var s = settings.Current;
        Enabled = s.RemoteEnabled;
        PortText = s.RemotePort.ToString();
        _loading = false;

        Refresh();
        _remote.StatusChanged += OnRemoteStatusChanged;
    }

    public ObservableCollection<string> Urls { get; } = new();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _portText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _selectedUrl;
    [ObservableProperty] private BitmapSource? _qrCode;
    [ObservableProperty] private string _message = "";

    partial void OnEnabledChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.RemoteEnabled = value);
    }

    partial void OnSelectedUrlChanged(string? value) => QrCode = value is null ? null : MakeQr(value);

    [RelayCommand]
    private void ApplyPort()
    {
        if (int.TryParse(PortText, out int port) && port is >= 1024 and <= 65535)
        {
            _settings.Update(s => s.RemotePort = port);
            Message = "";
        }
        else
        {
            Message = "Use a port number between 1024 and 65535.";
        }
    }

    [RelayCommand]
    private void CopyLink()
    {
        if (SelectedUrl is null) return;
        try
        {
            Clipboard.SetText(SelectedUrl);
            Message = "Link copied. Send it to your phone, or just scan the QR code.";
        }
        catch (Exception ex)
        {
            Message = $"Couldn't copy: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenOnThisPc()
    {
        if (SelectedUrl is null) return;
        try { Process.Start(new ProcessStartInfo(SelectedUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Message = $"Couldn't open the browser: {ex.Message}"; }
    }

    [RelayCommand]
    private void NewPairingCode()
    {
        if (!_dialogs.Confirm("Create a new pairing code? Phones and tablets paired with the old code stop working until they scan the new one.",
                "New pairing code")) return;
        _settings.Update(s => s.RemoteToken = AppSettings.NewToken());
        Message = "New pairing code created — scan the QR code again on your devices.";
        Refresh();
    }

    private void OnRemoteStatusChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        StatusText = _remote.StatusText;
        IsRunning = _remote.IsRunning;
        string? previous = SelectedUrl;
        Urls.Clear();
        foreach (var url in _remote.PairingUrls()) Urls.Add(url);
        // Keep the same network selected if it's still there (the token part may have changed).
        SelectedUrl = Urls.FirstOrDefault(u => previous is not null && u.Split('#')[0] == previous.Split('#')[0]) ?? Urls.FirstOrDefault();
        if (SelectedUrl is not null) QrCode = MakeQr(SelectedUrl);
    }

    private static BitmapSource MakeQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void Dispose() => _remote.StatusChanged -= OnRemoteStatusChanged;
}
