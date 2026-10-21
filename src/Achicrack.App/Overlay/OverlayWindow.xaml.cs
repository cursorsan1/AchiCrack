using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Achicrack.App.Infrastructure;
using Achicrack.Core.Settings;

namespace Achicrack.App.Overlay;

/// <summary>
/// Transparent, click-through, non-activating topmost window that shows one notification card
/// and animates it in and out. One instance per notification; it closes itself afterwards.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int FrameRate = 60;
    private static readonly Duration InDuration = TimeSpan.FromMilliseconds(450);
    private static readonly Duration OutDuration = TimeSpan.FromMilliseconds(380);
    private IntPtr _hwnd;

    public OverlayWindow(NotificationCardModel model)
    {
        InitializeComponent();
        Card.DataContext = model;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeOverlay(_hwnd);
        };
    }

    /// <summary>Shows the card, waits for the configured duration and hides it again.</summary>
    public async Task PlayAsync(NotificationStyle style, CancellationToken cancellationToken)
    {
        CardHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = CardHost.DesiredSize;
        Position(style, size);

        Show();
        NativeMethods.BringToTopNoActivate(_hwnd);

        // Some games re-assert their own z-order; keep the popup on top while it is visible.
        var keepOnTop = new DispatcherTimer(TimeSpan.FromMilliseconds(750), DispatcherPriority.Background,
            (_, _) => NativeMethods.BringToTopNoActivate(_hwnd), Dispatcher);
        keepOnTop.Start();
        try
        {
            await AnimateAsync(style, size, entering: true);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(style.DurationSeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // shutting down: still fade out quickly below
            }

            await AnimateAsync(style, size, entering: false);
        }
        finally
        {
            keepOnTop.Stop();
            Close();
        }
    }

    /// <summary>
    /// Places the window so it extends from the card to the screen edge the card slides in from;
    /// the card then enters from the edge without being clipped and without a full-screen layered window.
    /// </summary>
    private void Position(NotificationStyle style, Size size)
    {
        // Primary screen in device-independent pixels (covers borderless fullscreen games on that screen).
        var screenW = SystemParameters.PrimaryScreenWidth;
        var screenH = SystemParameters.PrimaryScreenHeight;
        var isLeft = style.Position is NotificationPosition.TopLeft or NotificationPosition.BottomLeft;
        var isCenter = style.Position is NotificationPosition.TopCenter or NotificationPosition.BottomCenter;
        var isBottom = style.Position is NotificationPosition.BottomLeft or NotificationPosition.BottomCenter or NotificationPosition.BottomRight;
        var animation = EffectiveAnimation(style);

        var cardX = isLeft ? style.MarginX : isCenter ? (screenW - size.Width) / 2 : screenW - size.Width - style.MarginX;
        var cardY = isBottom ? screenH - size.Height - style.MarginY : style.MarginY;

        Left = cardX;
        Top = cardY;
        Width = size.Width;
        Height = size.Height;
        CardHost.HorizontalAlignment = HorizontalAlignment.Left;
        CardHost.VerticalAlignment = VerticalAlignment.Top;

        if (animation == NotificationAnimation.Slide)
        {
            Width = size.Width + style.MarginX;
            if (isLeft)
            {
                Left = 0;
                CardHost.HorizontalAlignment = HorizontalAlignment.Right;
            }
        }
        else if (animation == NotificationAnimation.Drop)
        {
            Height = size.Height + style.MarginY;
            if (!isBottom)
            {
                Top = 0;
                CardHost.VerticalAlignment = VerticalAlignment.Bottom;
            }
        }

        Left = Math.Max(0, Left);
        Top = Math.Max(0, Top);
    }

    private static NotificationAnimation EffectiveAnimation(NotificationStyle style) =>
        style.Animation == NotificationAnimation.Slide &&
        style.Position is NotificationPosition.TopCenter or NotificationPosition.BottomCenter
            ? NotificationAnimation.Drop
            : style.Animation;

    private Task AnimateAsync(NotificationStyle style, Size size, bool entering)
    {
        var duration = entering ? InDuration : OutDuration;
        IEasingFunction ease = entering
            ? new CubicEase { EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseIn };

        var isLeft = style.Position is NotificationPosition.TopLeft or NotificationPosition.BottomLeft;
        var isBottom = style.Position is NotificationPosition.BottomLeft or NotificationPosition.BottomCenter or NotificationPosition.BottomRight;
        var animation = EffectiveAnimation(style);

        var tcs = new TaskCompletionSource();
        var opacity = Make(entering ? 0 : 1, entering ? 1 : 0, duration, ease, freeze: false);
        opacity.Completed += (_, _) => tcs.TrySetResult(); // must be hooked before the timeline is frozen
        opacity.Freeze();

        switch (animation)
        {
            case NotificationAnimation.Slide:
            {
                var offset = (size.Width + style.MarginX) * (isLeft ? -1 : 1);
                CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                    Make(entering ? offset : 0, entering ? 0 : offset * 0.35, duration, ease));
                break;
            }

            case NotificationAnimation.Drop:
            {
                var offset = (size.Height + style.MarginY) * (isBottom ? 1 : -1);
                CardTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                    Make(entering ? offset : 0, entering ? 0 : offset * 0.5, duration, ease));
                break;
            }

            case NotificationAnimation.Pop:
            {
                var scale = Make(entering ? 0.85 : 1, entering ? 1 : 0.92, duration,
                    entering ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } : ease);
                CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scale);
                CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scale);
                break;
            }
        }

        CardHost.BeginAnimation(OpacityProperty, opacity);
        // Never let a stopped clock stall the notification queue.
        return Task.WhenAny(tcs.Task, Task.Delay(duration.TimeSpan + TimeSpan.FromSeconds(1)));
    }

    private static DoubleAnimation Make(double from, double to, Duration duration, IEasingFunction ease, bool freeze = true)
    {
        var a = new DoubleAnimation(from, to, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        Timeline.SetDesiredFrameRate(a, FrameRate);
        if (freeze)
        {
            a.Freeze();
        }

        return a;
    }
}
