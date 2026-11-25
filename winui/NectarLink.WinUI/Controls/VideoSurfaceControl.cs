// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NectarLink.WinUI.Services;

namespace NectarLink.WinUI.Controls;

/// <summary>
/// High-throughput BGRX video frame surface for phone screen mirroring, individual
/// Android app windows, and the webcam live preview. Drops backlogged intermediate
/// frames so latency stays minimal and updates on the UI DispatcherQueue.
/// </summary>
public sealed partial class VideoSurfaceControl : Grid
{
    private readonly Image _image = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
    };

    private WriteableBitmap? _bitmap;
    private uint _texWidth;
    private uint _texHeight;
    private string? _stream;
    private readonly object _frameLock = new();
    private (uint Width, uint Height, byte[] Bytes)? _pendingFrame;
    private bool _renderScheduled;
    private readonly DispatcherQueue _dispatcher;

    public event Action<uint, uint>? FrameDimensionsChanged;

    public bool HasFrame => _bitmap is not null;

    public VideoSurfaceControl()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Children.Add(_image);
    }

    public void AttachStream(string stream)
    {
        if (_stream == stream) return;
        DetachStream();
        _stream = stream;
        if (!string.IsNullOrEmpty(stream))
        {
            NativeCore.SubscribeVideo(stream, EnqueueFrame, RequestClear);
        }
    }

    public void DetachStream()
    {
        if (!string.IsNullOrEmpty(_stream))
        {
            NativeCore.UnsubscribeVideo(_stream);
            _stream = null;
        }
        Clear();
    }

    private void EnqueueFrame(uint width, uint height, byte[] bgrx)
    {
        bool needSchedule;
        lock (_frameLock)
        {
            _pendingFrame = (width, height, bgrx);
            needSchedule = !_renderScheduled;
            _renderScheduled = true;
        }
        if (needSchedule)
        {
            _dispatcher.TryEnqueue(FlushLatestFrame);
        }
    }

    private void RequestClear()
    {
        lock (_frameLock)
        {
            _pendingFrame = null;
        }
        _dispatcher.TryEnqueue(Clear);
    }

    private void FlushLatestFrame()
    {
        (uint Width, uint Height, byte[] Bytes)? frame;
        lock (_frameLock)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
            _renderScheduled = false;
        }
        if (frame is not { Width: > 0, Height: > 0 } f)
            return;

        if (_bitmap is null || _texWidth != f.Width || _texHeight != f.Height)
        {
            _texWidth = f.Width;
            _texHeight = f.Height;
            _bitmap = new WriteableBitmap((int)f.Width, (int)f.Height);
            _image.Source = _bitmap;
            FrameDimensionsChanged?.Invoke(f.Width, f.Height);
        }

        // Convert BGRX alpha channel (byte 3 = 0xFF) in place or write directly.
        // Windows 32-bit BGRX from OpenH264/MediaFoundation expects opaque alpha in WriteableBitmap.
        byte[] pixels = f.Bytes;
        int len = Math.Min(pixels.Length, (int)(f.Width * f.Height * 4));
        for (int i = 3; i < len; i += 4)
        {
            pixels[i] = 0xFF;
        }

        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Seek(0, SeekOrigin.Begin);
            stream.Write(pixels, 0, len);
        }
        _bitmap.Invalidate();
    }

    public void Clear()
    {
        _bitmap = null;
        _texWidth = 0;
        _texHeight = 0;
        _image.Source = null;
    }
}
