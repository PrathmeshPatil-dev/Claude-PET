using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClaudePet;

// The speech bubble above the pet: a text box to give it a command, and its replies.
public partial class TalkWindow : Window
{
    private readonly DispatcherTimer _hide = new();
    private Window? _anchor;

    public event Action<string>? CommandEntered;
    public event Action? Dismissed;   // closed without an answer (Esc, or clicked elsewhere)

    public TalkWindow()
    {
        InitializeComponent();
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
    }

    private bool _skippable;   // Enter on an empty box answers "" (skip) instead of doing nothing

    public void Ask(Window anchor, string question = "what next?", bool skippable = false)
    {
        _skippable = skippable;
        _hide.Stop();
        _anchor = anchor;
        Reply.Text = question;
        Input.Text = "";
        InputRow.Visibility = Visibility.Visible;
        Place();
        Activate();
        Input.Focus();
        Keyboard.Focus(Input);
    }

    public void Say(Window anchor, string text, double seconds = 4)
    {
        _anchor = anchor;
        Reply.Text = text;
        InputRow.Visibility = Visibility.Collapsed;
        Place();
        _hide.Interval = TimeSpan.FromSeconds(seconds);
        _hide.Stop();
        _hide.Start();
    }

    // Keep the bubble above the pet while it walks.
    public void Follow()
    {
        if (IsVisible && _anchor != null)
            Place();
    }

    private void Place()
    {
        if (!IsVisible)
            Show();
        UpdateLayout();
        var anchor = _anchor!;
        var area = SystemParameters.VirtualScreenWidth;
        Left = Math.Clamp(anchor.Left + anchor.Width / 2 - ActualWidth / 2, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + area - ActualWidth);
        Top = Math.Max(SystemParameters.VirtualScreenTop, anchor.Top + anchor.Height * 0.2 - ActualHeight);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            Dismissed?.Invoke();
        }
        else if (e.Key == Key.Enter && (Input.Text.Trim().Length > 0 || _skippable))
        {
            var text = Input.Text.Trim();
            Input.Text = "";
            CommandEntered?.Invoke(text);
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (InputRow.Visibility == Visibility.Visible)
        {
            Hide();   // clicked elsewhere: drop the question
            Dismissed?.Invoke();
        }
    }
}
