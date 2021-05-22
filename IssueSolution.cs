/*
------Usage Example -----------------------------------------------------------------
        private unsafe void Decode(string url)
        {
            AVHWDeviceType devType = AVHWDeviceType.AV_HWDEVICE_TYPE_QSV;

            Console.WriteLine("Decode start");

            using (var decoder = new VideoStreamDecoder(url, devType))
            {
                var sourceSize = decoder.FrameSize;
                var outSize = new Size((int)(decoder.FrameSize.Width*0.5F), (int)(decoder.FrameSize.Height*0.5F));
                var sourcePxF = AVPixelFormat.AV_PIX_FMT_NV12;
                var outPxF = AVPixelFormat.AV_PIX_FMT_BGR24;

                Console.WriteLine("Selected sourcePxF = " + sourcePxF);

                using (VideoFrameConverter frameConverter = new VideoFrameConverter(
                sourceSize, sourcePxF,
                outSize, outPxF))
                {
                    long frameNum = 0;

                    Console.WriteLine("Frame Encode start");

                    while (decoder.TryDecodeNextFrame(out var rawFrame) && started)
                    {
                        var frame = frameConverter.Convert(rawFrame);

                        using (Bitmap bitmap = (Bitmap)new Bitmap(frame.width, frame.height, frame.linesize[0],
                               System.Drawing.Imaging.PixelFormat.Format24bppRgb, (IntPtr)frame.data[0]))
                        {
                            //Send to subscriber
                            if (OnFilterResultEvent != null && started)
                                OnFilterResultEvent(
                                    new MoveDetectionArgs() { OutputFrame = (Bitmap)bitmap.Clone(), IsSuccess = true },
                                    this);
                        }

                        frameNum += 1;
                    }
                    
                    Console.WriteLine("END Frame Encode " + frameNum);
                }
            }
        }

        private static AVPixelFormat GetHWPixelFormat(AVHWDeviceType hWDevice)
        {
            switch (hWDevice)
            {
                case AVHWDeviceType.AV_HWDEVICE_TYPE_NONE:
                    return AVPixelFormat.AV_PIX_FMT_NONE;

                case AVHWDeviceType.AV_HWDEVICE_TYPE_VDPAU:
                    return AVPixelFormat.AV_PIX_FMT_VDPAU;

                case AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA:
                    return AVPixelFormat.AV_PIX_FMT_CUDA;

                case AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI:
                    return AVPixelFormat.AV_PIX_FMT_YUV420P;

                case AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2:
                    return AVPixelFormat.AV_PIX_FMT_NV12;

                case AVHWDeviceType.AV_HWDEVICE_TYPE_QSV:
                    return AVPixelFormat.AV_PIX_FMT_QSV;

                case AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX:
                    return AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX;

                case AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA:
                    return AVPixelFormat.AV_PIX_FMT_NV12;

                case AVHWDeviceType.AV_HWDEVICE_TYPE_DRM:
                    return AVPixelFormat.AV_PIX_FMT_DRM_PRIME;

                case AVHWDeviceType.AV_HWDEVICE_TYPE_OPENCL:
                    return AVPixelFormat.AV_PIX_FMT_OPENCL;

                case AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC:
                    return AVPixelFormat.AV_PIX_FMT_MEDIACODEC;

                default:
                    return AVPixelFormat.AV_PIX_FMT_NONE;
            }
        }
------Usage Example Ends -----------------------------------------------------------------
*/

public sealed unsafe class VideoStreamDecoder : IDisposable
    {
        private readonly AVCodecContext* _pCodecContext;
        private readonly AVFormatContext* _pFormatContext;

        private readonly AVStream* _videoStream;

        private readonly int _streamIndex;

        private readonly AVFrame* _pFrame;

        private readonly AVFrame* _receivedFrame;

        private readonly AVPacket* _pPacket;

        public string CodecName { get; }
        public Size FrameSize { get; }
        public AVPixelFormat PixelFormat { get; private set; }

        private bool isHwAccelerate = false;
        public AVPixelFormat HWDevicePixelFormat { get; private set; }

        private AVCodecContext_get_format get_fmt = (x, t) =>
        {
            return GetFormat(x, t);
        };

        public VideoStreamDecoder(string url, AVHWDeviceType HWDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            _pFormatContext = ffmpeg.avformat_alloc_context();
            _receivedFrame = ffmpeg.av_frame_alloc();

            var pFormatContext = _pFormatContext;

            ffmpeg.avformat_open_input(&pFormatContext, url, null, null).ThrowExceptionIfError();

            AVCodec* codec = null;
            AVBufferRef *codecBuff = null;

            if (HWDeviceType is AVHWDeviceType.AV_HWDEVICE_TYPE_QSV)
            {
                codec = ffmpeg.avcodec_find_decoder_by_name("h264_qsv");
                codec->id = AVCodecID.AV_CODEC_ID_H264;

                for (int i = 0; i < _pFormatContext->nb_streams; i++)
                {
                    AVStream* st = _pFormatContext->streams[i];

                    if (st->codecpar->codec_id == AVCodecID.AV_CODEC_ID_H264 && _videoStream == null)
                    {
                        _videoStream = st;
                        Console.WriteLine("Stream founded!");
                    }
                    else
                    {
                        st->discard = AVDiscard.AVDISCARD_ALL;
                    }
                }
                _streamIndex = _videoStream->index;
            }
            else
            {
                _streamIndex = ffmpeg.av_find_best_stream(_pFormatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            }

            _pCodecContext = ffmpeg.avcodec_alloc_context3(codec);

            if (HWDeviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                if (ffmpeg.av_hwdevice_ctx_create(&_pCodecContext->hw_device_ctx, 
                    HWDeviceType, "auto", null, 0) < 0)
                {
                    throw new Exception("HW device init ERROR!");
                }
                else
                {
                    Console.WriteLine("Device " + HWDeviceType + " init OK");
                    isHwAccelerate = true;
                }

            }

            if (_pCodecContext == null)
                throw new Exception("Codec init error");

            //ffmpeg.avformat_find_stream_info(_pFormatContext, null);

            if (_videoStream->codecpar->extradata != null)
            {
                int size = (int)(_videoStream->codecpar->extradata_size);
                _pCodecContext->extradata = (byte*)ffmpeg.av_mallocz((ulong)size + 
                    ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
                _pCodecContext->extradata_size = (int)size + 
                    ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE;

                FFmpegHelper.memcpy((IntPtr)_pCodecContext->extradata, 
                    (IntPtr)_videoStream->codecpar->extradata, 
                    size);

                //Or just
                /*for (int i = 0; i < size; i++)
                    _pCodecContext->extradata[i] = _videoStream->codecpar->extradata[i];*/

            }

            if (HWDeviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_QSV)
            {
                _pCodecContext->get_format = get_fmt;
            }

            ffmpeg.avcodec_parameters_to_context(_pCodecContext, _videoStream->codecpar);
            ffmpeg.avcodec_open2(_pCodecContext, codec, null);

            CodecName = ffmpeg.avcodec_get_name(codec->id);
            FrameSize = new Size(_videoStream->codecpar->width,_videoStream->codecpar->height);
            PixelFormat = _pCodecContext->sw_pix_fmt;

            Console.WriteLine("Codec: " + CodecName.ToString());
            Console.WriteLine("Size: " + FrameSize.ToString());
            Console.WriteLine("PixelFormat: " + PixelFormat.ToString());


            _pPacket = ffmpeg.av_packet_alloc();
            _pFrame = ffmpeg.av_frame_alloc();
        }

        public void Dispose()
        {
            var pFrame = _pFrame;
            ffmpeg.av_frame_free(&pFrame);

            var pPacket = _pPacket;
            ffmpeg.av_packet_free(&pPacket);

            ffmpeg.avcodec_close(_pCodecContext);
            var pFormatContext = _pFormatContext;
            ffmpeg.avformat_close_input(&pFormatContext);
        }

        public static AVPixelFormat GetFormat(AVCodecContext* context, AVPixelFormat *px_fmts)
        {
            while (*px_fmts != AVPixelFormat.AV_PIX_FMT_NONE)
            {
                if(*px_fmts == AVPixelFormat.AV_PIX_FMT_QSV)
                {
                    AVHWFramesContext* fr_ctx;
                    AVQSVFramesContext* qsv_fr_ctx;


                    context->hw_frames_ctx = ffmpeg.av_hwframe_ctx_alloc(context->hw_device_ctx);
             

                    fr_ctx = (AVHWFramesContext*) context->hw_frames_ctx->data;
                    qsv_fr_ctx = (AVQSVFramesContext*)fr_ctx->hwctx;

                    int initialPoolSize = 32;

                    fr_ctx->format = AVPixelFormat.AV_PIX_FMT_QSV;
                    fr_ctx->sw_format = context->sw_pix_fmt;
                    fr_ctx->width = context->coded_width.FFALIGN(initialPoolSize);
                    fr_ctx->height = context->coded_height.FFALIGN(initialPoolSize);
                    fr_ctx->initial_pool_size = initialPoolSize;
                    qsv_fr_ctx->frame_type = (int)MFX_MEMTYPE.MFX_MEMTYPE_VIDEO_MEMORY_DECODER_TARGET;

                    ffmpeg.av_hwframe_ctx_init(context->hw_frames_ctx).ThrowExceptionIfError();

                    return AVPixelFormat.AV_PIX_FMT_QSV;

                }
                px_fmts++;
            }

            return AVPixelFormat.AV_PIX_FMT_NONE;
        }


        public bool TryDecodeNextFrame(out AVFrame frame)
        {
            //Обнуление фреймов
            ffmpeg.av_frame_unref(_pFrame);
            ffmpeg.av_frame_unref(_receivedFrame);

            int error;

            do
            {
                try
                {
                    do
                    {
                        //Стереть пакет
                        ffmpeg.av_packet_unref(_pPacket);

                        error = ffmpeg.av_read_frame(_pFormatContext, _pPacket);

                        if (error == ffmpeg.AVERROR_EOF)
                        {
                            frame = *_pFrame;
                            return false;
                        }

                    } while (_pPacket->stream_index != _streamIndex);

                    ffmpeg.avcodec_send_packet(_pCodecContext, _pPacket);
                }
                finally
                {
                    ffmpeg.av_packet_unref(_pPacket);
                }

                error = ffmpeg.avcodec_receive_frame(_pCodecContext, _pFrame);
            }
            while (error == ffmpeg.AVERROR(ffmpeg.EAGAIN));

            if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                Console.WriteLine("error == ffmpeg.AVERROR(ffmpeg.EAGAIN)");
            }

            if (isHwAccelerate)
            {
                ffmpeg.av_hwframe_transfer_data(_receivedFrame, _pFrame, 0).ThrowExceptionIfError();
                frame = *_receivedFrame;
                //Console.WriteLine("Transfer OK");
            }
            else
            {
                //Console.WriteLine("PIX is not HW");
                frame = *_pFrame;
            }

            return true;
        }

        public struct AVQSVFramesContext
        {
            public IntPtr *surfaces;
            public int nb_surfaces;
            public int frame_type;
        }

        public IReadOnlyDictionary<string, string> GetContextInfo()
        {
            AVDictionaryEntry* tag = null;
            var result = new Dictionary<string, string>();
            while ((tag = ffmpeg.av_dict_get(_pFormatContext->metadata, "", tag, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
            {
                var key = Marshal.PtrToStringAnsi((IntPtr)tag->key);
                var value = Marshal.PtrToStringAnsi((IntPtr)tag->value);

                Console.WriteLine("ContextInfo key: " + key+ "val: " + value);

                result.Add(key, value);
            }

            return result;
        }
    }

    public sealed unsafe class VideoFrameConverter : IDisposable
    {
        private readonly IntPtr _convertedFrameBufferPtr;
        private readonly Size _destinationSize;
        private readonly byte_ptrArray4 _dstData;
        private readonly int_array4 _dstLinesize;
        private readonly SwsContext* _pConvertContext;

        public VideoFrameConverter(Size sourceSize, AVPixelFormat sourcePixelFormat,
            Size destinationSize, AVPixelFormat destinationPixelFormat)
        {
            _destinationSize = destinationSize;

            _pConvertContext = ffmpeg.sws_getContext(sourceSize.Width, sourceSize.Height, sourcePixelFormat,
            destinationSize.Width,
            destinationSize.Height, destinationPixelFormat,
            ffmpeg.SWS_FAST_BILINEAR, null, null, null);

            if (_pConvertContext == null) throw new ApplicationException("Could not initialize the conversion context.");

            var convertedFrameBufferSize = ffmpeg.av_image_get_buffer_size(destinationPixelFormat,
                destinationSize.Width, destinationSize.Height, 1);

            _convertedFrameBufferPtr = Marshal.AllocHGlobal(convertedFrameBufferSize);

            _dstData = new byte_ptrArray4();
            _dstLinesize = new int_array4();

            ffmpeg.av_image_fill_arrays(ref _dstData, ref _dstLinesize, (byte*)_convertedFrameBufferPtr, 
                destinationPixelFormat, destinationSize.Width, destinationSize.Height, 1);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_convertedFrameBufferPtr);
            ffmpeg.sws_freeContext(_pConvertContext);
        }

        public AVFrame Convert(AVFrame sourceFrame)
        {
            ffmpeg.sws_scale(_pConvertContext, sourceFrame.data, sourceFrame.linesize, 0, 
                sourceFrame.height, _dstData, _dstLinesize);

            var data = new byte_ptrArray8();
            data.UpdateFrom(_dstData);

            var linesize = new int_array8();
            linesize.UpdateFrom(_dstLinesize);

            return new AVFrame
            {
                data = data,
                linesize = linesize,
                width = _destinationSize.Width,
                height = _destinationSize.Height
            };
        }
    }

    public enum AVPixDescFlag
    {
        ///
        // Pixel format is big-endian.
        ///
        AV_PIX_FMT_FLAG_BE = 1 << 0,

        ///
        // Pixel format has a palette in data[1], values are indexes in this palette.
        ///
        AV_PIX_FMT_FLAG_PAL = 1 << 1,

        /**
         * All values of a component are bit-wise packed end to end.
         */
        AV_PIX_FMT_FLAG_BITSTREAM= 1 << 2,

        ///
        // Pixel format is an HW accelerated format.
        ///
        AV_PIX_FMT_FLAG_HWACCEL = 1 << 3,

        ///
        // At least one pixel component is not in the first data plane.
        ///
        AV_PIX_FMT_FLAG_PLANAR = 1 << 4,

        ///
        // The pixel format contains RGB-like data (as opposed to YUV/grayscale).
        ///
        AV_PIX_FMT_FLAG_RGB   = 1 << 5,

        ///
        // The pixel format has an alpha channel. This is set on all formats that
        // support alpha in some way, including AV_PIX_FMT_PAL8. The alpha is always
        // straight, never pre-multiplied.
        //
        // If a codec or a filter does not support alpha, it should set all alpha to
        // opaque, or use the equivalent pixel formats without alpha component, e.g.
        // AV_PIX_FMT_RGB0 (or AV_PIX_FMT_RGB24 etc.) instead of AV_PIX_FMT_RGBA.
        ///
        AV_PIX_FMT_FLAG_ALPHA = 1 << 7,

        ///
        // The pixel format is following a Bayer pattern
        ///
        AV_PIX_FMT_FLAG_BAYER = 1 << 8,

        ///
        // The pixel format contains IEEE-754 floating point values. Precision (double,
        // single, or half) should be determined by the pixel size (64, 32, or 16 bits).
        ///
        AV_PIX_FMT_FLAG_FLOAT = 1 << 9
    }

    /// <summary>
    /// Type of memory for QSV Decoder
    /// From Intel Media SDK
    /// </summary>
    public enum MFX_MEMTYPE
    {
        MFX_MEMTYPE_DXVA2_DECODER_TARGET = 0x0010,
        MFX_MEMTYPE_DXVA2_PROCESSOR_TARGET = 0x0020,
        MFX_MEMTYPE_VIDEO_MEMORY_DECODER_TARGET = MFX_MEMTYPE_DXVA2_DECODER_TARGET,
        MFX_MEMTYPE_VIDEO_MEMORY_PROCESSOR_TARGET = MFX_MEMTYPE_DXVA2_PROCESSOR_TARGET,
        MFX_MEMTYPE_SYSTEM_MEMORY = 0x0040,
        MFX_MEMTYPE_RESERVED1 = 0x0080,

        MFX_MEMTYPE_FROM_ENCODE = 0x0100,
        MFX_MEMTYPE_FROM_DECODE = 0x0200,
        MFX_MEMTYPE_FROM_VPPIN = 0x0400,
        MFX_MEMTYPE_FROM_VPPOUT = 0x0800,
        MFX_MEMTYPE_FROM_ENC = 0x2000,
        MFX_MEMTYPE_FROM_PAK = 0x4000, //reserved

        MFX_MEMTYPE_INTERNAL_FRAME = 0x0001,
        MFX_MEMTYPE_EXTERNAL_FRAME = 0x0002,
        MFX_MEMTYPE_OPAQUE_FRAME = 0x0004,
        MFX_MEMTYPE_EXPORT_FRAME = 0x0008,
        MFX_MEMTYPE_SHARED_RESOURCE = MFX_MEMTYPE_EXPORT_FRAME,

        MFX_MEMTYPE_VIDEO_MEMORY_ENCODER_TARGET = 0x1000,

        MFX_MEMTYPE_RESERVED2 = 0x1000
    }

    /// <summary>
    /// Расширяющие методы необходимые для работы библиотек
    /// </summary>
    internal static class FFmpegHelper
    {
        public static int FFALIGN(this int x, int a)
        {
            return (((x) + (a) - 1) & ~((a) - 1));
        }

        /// <summary>
        /// Copy bytes
        /// </summary>
        /// <param name="dest">From</param>
        /// <param name="src">To</param>
        /// <param name="count">Size</param>
        [DllImport("msvcrt.dll", SetLastError = false)]
        public static extern IntPtr memcpy(IntPtr dest, IntPtr src, int count);

        public static unsafe string av_strerror(int error)
        {
            var bufferSize = 1024;
            var buffer = stackalloc byte[bufferSize];
            ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);
            var message = Marshal.PtrToStringAnsi((IntPtr)buffer);
            return message;
        }

        public static int ThrowExceptionIfError(this int error)
        {
            if (error < 0) throw new ApplicationException(av_strerror(error));
            return error;
        }
    }
