using FFmpeg.AutoGen;

namespace KodDesk.Core.Video;

public delegate void PacketHandler(ReadOnlySpan<byte> data, bool keyframe);

/// <summary>
/// Encoder H.264 cu latență mică. Încearcă pe rând encoderele hardware (NVENC, QuickSync, AMF), apoi x264.
/// Culoarea: BT.709; intervalul (full/limited) este transmis explicit viewer-ului, deci nu apare imaginea „spălată”.
/// Pe NVENC și x264 se folosește 4:4:4 (fără subeșantionarea culorii), deci textul colorat rămâne clar.
/// </summary>
public sealed unsafe class VideoEncoder : IDisposable
{
    /// <param name="PixFmt">Formatul trimis encoderului. BGRA = conversia de culoare o face encoderul (pe GPU).</param>
    /// <param name="Yuv444">Dacă fluxul rezultat este 4:4:4.</param>
    /// <param name="FullRange">
    /// Intervalul YUV real produs. Conversia internă RGB→YUV a NVENC produce limited range (16-235);
    /// dacă ar fi interpretat ca full range, imaginea ar arăta „spălată”. Viewer-ul primește valoarea corectă prin VideoFormat.
    /// </param>
    private sealed record Candidate(string Label, string Codec, AVPixelFormat PixFmt, bool Yuv444, bool FullRange, (string key, string value)[] Options);

    private static readonly (string, string)[] s_nvencCommon =
        [("preset", "p4"), ("tune", "ull"), ("rc", "vbr"), ("zerolatency", "1"), ("delay", "0"), ("forced-idr", "1")];

    private static readonly Candidate[] s_candidates =
    [
        // Conversia de culoare pe GPU: cel mai rapid (de ~2x față de conversia pe CPU).
        new("nvenc-bgra444", "h264_nvenc", AVPixelFormat.AV_PIX_FMT_BGRA, true, false, [.. s_nvencCommon, ("profile", "high444p"), ("rgb_mode", "yuv444")]),
        new("nvenc-yuv444", "h264_nvenc", AVPixelFormat.AV_PIX_FMT_YUV444P, true, true, [.. s_nvencCommon, ("profile", "high444p")]),
        new("nvenc-nv12", "h264_nvenc", AVPixelFormat.AV_PIX_FMT_NV12, false, true, [.. s_nvencCommon, ("profile", "high")]),
        new("qsv-nv12", "h264_qsv", AVPixelFormat.AV_PIX_FMT_NV12, false, true,
            [("preset", "veryfast"), ("async_depth", "1"), ("profile", "high"), ("forced_idr", "1"), ("low_power", "0")]),
        new("amf-nv12", "h264_amf", AVPixelFormat.AV_PIX_FMT_NV12, false, true,
            [("usage", "ultralowlatency"), ("quality", "balanced"), ("rc", "vbr_peak"), ("profile", "high")]),
        new("x264-yuv444", "libx264", AVPixelFormat.AV_PIX_FMT_YUV444P, true, true,
            [("preset", "superfast"), ("tune", "zerolatency"), ("profile", "high444")]),
    ];

    public static IReadOnlyList<string> CandidateLabels => s_candidates.Select(c => c.Label).ToArray();

    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private SwsContext* _sws;
    private long _lastPts = -1;

    public string Name { get; }
    public bool Yuv444 { get; }
    public bool FullRange { get; }
    public int Width { get; }
    public int Height { get; }

    private VideoEncoder(Candidate c, int width, int height, int bitrateKbps, int fps)
    {
        Name = c.Label;
        Yuv444 = c.Yuv444;
        FullRange = c.FullRange;
        Width = width;
        Height = height;

        try
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name(c.Codec);
            if (codec == null) throw new NotSupportedException("Encoder indisponibil: " + c.Codec);

            _ctx = ffmpeg.avcodec_alloc_context3(codec);
            _ctx->width = width;
            _ctx->height = height;
            _ctx->pix_fmt = c.PixFmt;
            _ctx->time_base = new AVRational { num = 1, den = 1000 };
            _ctx->framerate = new AVRational { num = fps, den = 1 };
            _ctx->bit_rate = bitrateKbps * 1000L;
            _ctx->rc_max_rate = bitrateKbps * 1500L;
            _ctx->rc_buffer_size = bitrateKbps * 1000;
            _ctx->gop_size = fps * 120; // keyframe-urile se cer explicit; acesta e doar o plasă de siguranță
            _ctx->max_b_frames = 0;
            _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            _ctx->slices = 4;
            _ctx->color_range = c.FullRange ? AVColorRange.AVCOL_RANGE_JPEG : AVColorRange.AVCOL_RANGE_MPEG;
            _ctx->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            _ctx->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            _ctx->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;

            foreach (var (key, value) in c.Options)
                ffmpeg.av_opt_set(_ctx->priv_data, key, value, 0);

            FFmpegSetup.Check(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2 " + c.Codec);

            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)c.PixFmt;
            _frame->width = width;
            _frame->height = height;
            _frame->color_range = _ctx->color_range;
            _frame->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            FFmpegSetup.Check(ffmpeg.av_frame_get_buffer(_frame, 64), "av_frame_get_buffer");

            _pkt = ffmpeg.av_packet_alloc();

            if (c.PixFmt != AVPixelFormat.AV_PIX_FMT_BGRA)
            {
                _sws = ffmpeg.sws_getContext(width, height, AVPixelFormat.AV_PIX_FMT_BGRA, width, height, c.PixFmt,
                    (int)(SwsFlags.SWS_BILINEAR | SwsFlags.SWS_ACCURATE_RND | SwsFlags.SWS_FULL_CHR_H_INP), null, null, null);
                if (_sws == null) throw new InvalidOperationException("sws_getContext a eșuat");
                SetColorspace(_sws, yuvFullRange: true, yuvIsSource: false);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Configurează conversia RGB&lt;-&gt;YUV pentru BT.709 cu intervalul dat.</summary>
    internal static void SetColorspace(SwsContext* sws, bool yuvFullRange, bool yuvIsSource)
    {
        var coef = ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
        var table = new int_array4();
        for (uint i = 0; i < 4; i++) table[i] = coef[i];
        int yuvRange = yuvFullRange ? 1 : 0;
        // Parametri: inv_table (sursă), srcRange, table (destinație), dstRange. Partea RGB este mereu full range.
        if (yuvIsSource)
            ffmpeg.sws_setColorspaceDetails(sws, table, yuvRange, table, 1, 0, 1 << 16, 1 << 16);
        else
            ffmpeg.sws_setColorspaceDetails(sws, table, 1, table, yuvRange, 0, 1 << 16, 1 << 16);
    }

    /// <param name="onlyLabel">Doar pentru teste/diagnostic: forțează un anumit encoder.</param>
    public static VideoEncoder Create(int width, int height, int bitrateKbps, int fps, string? onlyLabel = null)
    {
        FFmpegSetup.EnsureLoaded();
        // Dimensiuni pare (cerință pentru 4:2:0).
        width &= ~1;
        height &= ~1;
        foreach (var c in s_candidates)
        {
            if (onlyLabel != null && c.Label != onlyLabel) continue;
            try
            {
                var enc = new VideoEncoder(c, width, height, bitrateKbps, fps);
                Log.Info($"Encoder: {c.Label} {(enc.Yuv444 ? "4:4:4" : "4:2:0")} {width}x{height} {bitrateKbps} kbps {fps} fps");
                return enc;
            }
            catch (Exception ex)
            {
                Log.Info($"Encoder {c.Label} indisponibil: {ex.Message}");
            }
        }
        throw new NotSupportedException("Nu am găsit niciun encoder H.264 funcțional.");
    }

    /// <summary>Encodează o imagine BGRA. Pachetele rezultate sunt transmise prin <paramref name="onPacket"/>.</summary>
    public void Encode(nint bgra, int stride, long timestampMs, bool forceKeyframe, PacketHandler onPacket)
    {
        FFmpegSetup.Check(ffmpeg.av_frame_make_writable(_frame), "av_frame_make_writable");

        if (_sws != null)
        {
            var src = new[] { (byte*)bgra };
            var srcStride = new[] { stride };
            ffmpeg.sws_scale(_sws, src, srcStride, 0, Height, _frame->data.ToArray(), _frame->linesize.ToArray());
        }
        else
        {
            // Encoderul primește BGRA și face singur conversia (pe GPU).
            byte* dst = _frame->data[0];
            int dstStride = _frame->linesize[0];
            int rowBytes = Width * 4;
            for (int y = 0; y < Height; y++)
                Buffer.MemoryCopy((byte*)bgra + (long)y * stride, dst + (long)y * dstStride, rowBytes, rowBytes);
        }

        var pts = Math.Max(timestampMs, _lastPts + 1);
        _lastPts = pts;
        _frame->pts = pts;
        if (forceKeyframe)
        {
            _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            _frame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
        }
        else
        {
            _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            _frame->flags &= ~ffmpeg.AV_FRAME_FLAG_KEY;
        }

        FFmpegSetup.Check(ffmpeg.avcodec_send_frame(_ctx, _frame), "avcodec_send_frame");
        while (true)
        {
            int ret = ffmpeg.avcodec_receive_packet(_ctx, _pkt);
            if (ret == FFmpegSetup.AVERROR_EAGAIN || ret == ffmpeg.AVERROR_EOF) break;
            FFmpegSetup.Check(ret, "avcodec_receive_packet");
            try
            {
                onPacket(new ReadOnlySpan<byte>(_pkt->data, _pkt->size), (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0);
            }
            finally
            {
                ffmpeg.av_packet_unref(_pkt);
            }
        }
    }

    public void Dispose()
    {
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
    }
}
