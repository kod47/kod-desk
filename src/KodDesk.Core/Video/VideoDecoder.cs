using FFmpeg.AutoGen;

namespace KodDesk.Core.Video;

/// <summary>Decoder H.264 (software, multi-thread pe slice-uri) cu conversie în BGRA.</summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private SwsContext* _sws;
    private int _swsW, _swsH, _swsFmt = -1;
    private readonly bool _fullRange;
    private Yuv444Converter? _yuv444;

    public VideoDecoder(bool fullRange)
    {
        FFmpegSetup.EnsureLoaded();
        _fullRange = fullRange;
        try
        {
            var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null) throw new NotSupportedException("Decoder H.264 indisponibil");
            _ctx = ffmpeg.avcodec_alloc_context3(codec);
            _ctx->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 8);
            _ctx->thread_type = ffmpeg.FF_THREAD_SLICE;
            _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            FFmpegSetup.Check(ffmpeg.avcodec_open2(_ctx, codec, null), "avcodec_open2 decoder");
            _frame = ffmpeg.av_frame_alloc();
            _pkt = ffmpeg.av_packet_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decodează un pachet. Dacă rezultă un cadru, îl convertește în BGRA în bufferul obținut de la <paramref name="target"/>.
    /// Returnează true dacă a fost produs un cadru.
    /// </summary>
    public bool Decode(ReadOnlySpan<byte> data, FrameBuffer target)
    {
        bool produced = false;
        fixed (byte* p = data)
        {
            _pkt->data = p;
            _pkt->size = data.Length;
            int ret = ffmpeg.avcodec_send_packet(_ctx, _pkt);
            _pkt->data = null;
            _pkt->size = 0;
            FFmpegSetup.Check(ret, "avcodec_send_packet");
        }

        while (true)
        {
            int ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (ret == FFmpegSetup.AVERROR_EAGAIN || ret == ffmpeg.AVERROR_EOF) break;
            FFmpegSetup.Check(ret, "avcodec_receive_frame");
            try
            {
                Convert(target);
                produced = true;
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
        return produced;
    }

    private void Convert(FrameBuffer target)
    {
        int w = _frame->width, h = _frame->height, fmt = _frame->format;
        var (ptr, stride) = target.GetBackBuffer(w, h);

        if (fmt is (int)AVPixelFormat.AV_PIX_FMT_YUV444P or (int)AVPixelFormat.AV_PIX_FMT_YUVJ444P)
        {
            _yuv444 ??= new Yuv444Converter(_fullRange);
            _yuv444.Convert(_frame->data[0], _frame->linesize[0], _frame->data[1], _frame->linesize[1],
                _frame->data[2], _frame->linesize[2], (byte*)ptr, stride, w, h);
        }
        else
        {
            if (_sws == null || w != _swsW || h != _swsH || fmt != _swsFmt)
            {
                if (_sws != null) ffmpeg.sws_freeContext(_sws);
                _sws = ffmpeg.sws_getContext(w, h, (AVPixelFormat)fmt, w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);
                if (_sws == null) throw new InvalidOperationException("sws_getContext (decoder) a eșuat");
                // Intervalul de culoare vine din VideoFormat (controlat de noi), nu din VUI.
                VideoEncoder.SetColorspace(_sws, _fullRange, yuvIsSource: true);
                _swsW = w; _swsH = h; _swsFmt = fmt;
            }
            var dst = new[] { (byte*)ptr };
            var dstStride = new[] { stride };
            ffmpeg.sws_scale(_sws, _frame->data.ToArray(), _frame->linesize.ToArray(), 0, h, dst, dstStride);
        }
        target.Publish();
    }

    public void Dispose()
    {
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
    }
}
