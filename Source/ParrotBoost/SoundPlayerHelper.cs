using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;

namespace ParrotBoost;

internal static class SoundPlayerHelper
{
    private static MediaPlayer? _player;
    private static string? _cachedFilePath;

    public static void PlayBoostSound()
    {
        try
        {
            if (string.IsNullOrEmpty(_cachedFilePath) || !File.Exists(_cachedFilePath))
            {
                string targetDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "JGS");
                Directory.CreateDirectory(targetDir);
                _cachedFilePath = Path.Combine(targetDir, "parrot.mp3");

                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("ParrotBoost.parrot.mp3")
                                   ?? asm.GetManifestResourceStream("parrot.mp3");
                if (stream != null)
                {
                    using var fs = new FileStream(_cachedFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    stream.CopyTo(fs);
                }
                else
                {
                    string localCandidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "parrot.mp3");
                    if (File.Exists(localCandidate))
                    {
                        _cachedFilePath = localCandidate;
                    }
                }
            }

            if (!string.IsNullOrEmpty(_cachedFilePath) && File.Exists(_cachedFilePath))
            {
                System.Windows.Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    try
                    {
                        _player ??= new MediaPlayer();
                        _player.Open(new Uri(_cachedFilePath, UriKind.Absolute));
                        _player.Volume = 0.85;
                        _player.Play();
                    }
                    catch
                    {
                    }
                });
            }
        }
        catch
        {
        }
    }
}

