using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Achicrack.Core.Notifications;
using Achicrack.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Achicrack.App.Overlay;

/// <summary>Renders queued notifications with <see cref="OverlayWindow"/> on the UI thread.</summary>
internal sealed class WpfNotificationPresenter(Dispatcher dispatcher, Func<NotificationStyle> style, ILogger logger)
    : INotificationPresenter
{
    public static readonly string DefaultSoundPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "trophy.wav");

    private MediaPlayer? _player;

    public async Task PresentAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        var current = style();
        await await dispatcher.InvokeAsync(async () =>
        {
            var window = new OverlayWindow(NotificationCardModel.From(request, current));
            if (current.SoundEnabled)
            {
                PlaySound(current);
            }

            await window.PlayAsync(current, cancellationToken);
        });
    }

    /// <summary>Plays the configured (or built-in) sound. Must run on the UI thread.</summary>
    public void PlaySound(NotificationStyle current)
    {
        var path = string.IsNullOrWhiteSpace(current.SoundPath) || !File.Exists(current.SoundPath)
            ? DefaultSoundPath
            : current.SoundPath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            _player ??= new MediaPlayer();
            _player.Stop();
            _player.Open(new Uri(path, UriKind.Absolute));
            _player.Volume = current.SoundVolume;
            _player.Play();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not play notification sound {Path}", path);
        }
    }
}
