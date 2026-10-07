using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Echodeck.Core.Hotkeys;

namespace Echodeck.App.Controls;

/// <summary>
/// Click, then press a key combination. Backspace/Delete clears it, Esc cancels.
/// While focused it raises <see cref="CapturingChanged"/> so global hotkeys can be suspended;
/// otherwise pressing an already-registered combo would trigger it instead of being recorded.
/// The bound <see cref="Gesture"/> is the text form ("Ctrl+NumPad1") or null for none.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).ShowGesture()));

    /// <summary>Raised with true when any hotkey box starts recording, false when it stops.</summary>
    public static event EventHandler<bool>? CapturingChanged;

    private bool _capturing;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        MinWidth = 140;
        Padding = new Thickness(4, 2, 4, 2);
        ToolTip = "Click, then press the shortcut. Backspace clears it, Esc cancels.";
        ShowGesture();
    }

    public string? Gesture
    {
        get => (string?)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    /// <summary>Shown below/next to the box by the page when a combination is rejected.</summary>
    public string? LastError { get; private set; }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _capturing = true;
        LastError = null;
        Text = "Press a shortcut…";
        CapturingChanged?.Invoke(this, true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        StopCapturing();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;

        Key key = e.Key switch
        {
            Key.System => e.SystemKey,      // Alt combinations arrive as Key.System
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        var mods = ToModifiers(Keyboard.Modifiers);

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            Text = mods == HotkeyModifiers.None ? "Press a shortcut…" : new HotkeyGesture(mods, 0).ToString().Replace("Key00", "…");
            return;
        }
        if (mods == HotkeyModifiers.None && key == Key.Escape) { Finish(); return; }
        if (mods == HotkeyModifiers.None && key is Key.Back or Key.Delete) { Gesture = null; Finish(); return; }

        var gesture = new HotkeyGesture(mods, KeyInterop.VirtualKeyFromKey(key));
        if (gesture.ValidationError is { } error)
        {
            LastError = error;
            Text = "✖ " + error;
            return;
        }
        Gesture = gesture.ToString();
        Finish();
    }

    private void Finish()
    {
        StopCapturing();
        // Move focus off the box so the next keypress isn't recorded again.
        Keyboard.ClearFocus();
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private void StopCapturing()
    {
        if (!_capturing) return;
        _capturing = false;
        ShowGesture();
        CapturingChanged?.Invoke(this, false);
    }

    private void ShowGesture()
    {
        if (_capturing) return;
        Text = string.IsNullOrEmpty(Gesture) ? "— none —" : Gesture;
    }

    private static HotkeyModifiers ToModifiers(ModifierKeys keys)
    {
        var m = HotkeyModifiers.None;
        if (keys.HasFlag(ModifierKeys.Control)) m |= HotkeyModifiers.Control;
        if (keys.HasFlag(ModifierKeys.Alt)) m |= HotkeyModifiers.Alt;
        if (keys.HasFlag(ModifierKeys.Shift)) m |= HotkeyModifiers.Shift;
        if (keys.HasFlag(ModifierKeys.Windows)) m |= HotkeyModifiers.Windows;
        return m;
    }
}
