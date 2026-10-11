using FFmpeg.AutoGen;

namespace KodDesk.Core.Video;

public static unsafe class FFmpegSetup
{
    private static readonly Lock s_lock = new();
    private static bool s_done;

    /// <summary>AVERROR(EAGAIN) pe Windows.</summary>
    public const int AVERROR_EAGAIN = -11;

    public static void EnsureLoaded()
    {
        lock (s_lock)
        {
            if (s_done) return;
            ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            if (!File.Exists(Path.Combine(ffmpeg.RootPath, "avcodec-63.dll")))
                throw new FileNotFoundException("Lipsesc bibliotecile FFmpeg în " + ffmpeg.RootPath);
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            Log.Info("FFmpeg încărcat: " + ffmpeg.av_version_info());
            s_done = true;
        }
    }

    public static string ErrorText(int err)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(err, buf, 256);
        return new string((sbyte*)buf);
    }

    public static void Check(int ret, string what)
    {
        if (ret < 0) throw new InvalidOperationException($"{what}: {ErrorText(ret)} ({ret})");
    }
}
