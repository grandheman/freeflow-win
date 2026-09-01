using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace FreeFlow.App.Platform.Context;

/// <summary>
/// Captures the foreground window as a JPEG data URL for the vision context model.
/// </summary>
/// <remarks>
/// <para>
/// Windows replacement for the ScreenCaptureKit path in
/// <c>Sources/AppContextService.swift</c>.
/// </para>
/// <para>
/// <c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c> is used rather than a
/// screen-region grab: it captures the target window's own pixels even when another
/// window overlaps it, and it works for the composited (DWM-rendered) content that
/// modern applications use. It does fail for some hardware-accelerated surfaces and
/// for protected content, which is reported as an error rather than a black image.
/// </para>
/// <para>
/// This reads the user's screen. It only runs when context awareness is explicitly
/// enabled, the image is never written to disk, and it is discarded as soon as the
/// context request completes.
/// </para>
/// </remarks>
public static class ScreenCapture
{
    /// <summary>Longest edge of the downscaled screenshot, matching the macOS default.</summary>
    public const int DefaultMaxDimension = 1024;

    /// <summary>Ceiling on the encoded data URL, so an oversized image never reaches the provider.</summary>
    public const int MaxDataUrlLength = 500_000;

    private const long JpegQuality = 50;

    public sealed record Result(string? DataUrl, string? MimeType, string? Error);

    public static Result CaptureForegroundWindow(int maxDimension = DefaultMaxDimension)
    {
        var windowHandle = NativeMethods.GetForegroundWindow();
        if (windowHandle == IntPtr.Zero) return new Result(null, null, "No foreground window.");

        try
        {
            using var bitmap = CaptureWindow(windowHandle);
            if (bitmap is null) return new Result(null, null, "Window could not be captured.");

            using var scaled = Downscale(bitmap, maxDimension);
            var bytes = EncodeJpeg(scaled);

            var dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
            if (dataUrl.Length > MaxDataUrlLength)
            {
                return new Result(null, null, "Screenshot too large after compression.");
            }

            return new Result(dataUrl, "image/jpeg", null);
        }
        catch (Exception error)
        {
            return new Result(null, null, error.Message);
        }
    }

    private static Bitmap? CaptureWindow(IntPtr windowHandle)
    {
        if (!NativeMethods.GetWindowRect(windowHandle, out var rect)) return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            var deviceContext = graphics.GetHdc();
            try
            {
                if (!NativeMethods.PrintWindow(windowHandle, deviceContext, NativeMethods.PW_RENDERFULLCONTENT))
                {
                    bitmap.Dispose();
                    return null;
                }
            }
            finally
            {
                graphics.ReleaseHdc(deviceContext);
            }
        }

        return bitmap;
    }

    private static Bitmap Downscale(Bitmap source, int maxDimension)
    {
        var longestEdge = Math.Max(source.Width, source.Height);
        if (longestEdge <= maxDimension) return (Bitmap)source.Clone();

        var scale = (double)maxDimension / longestEdge;
        var width = Math.Max(1, (int)(source.Width * scale));
        var height = Math.Max(1, (int)(source.Height * scale));

        var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);

        return scaled;
    }

    private static byte[] EncodeJpeg(Bitmap bitmap)
    {
        var encoder = FindJpegEncoder()
            ?? throw new InvalidOperationException("No JPEG encoder is available.");

        using var parameters = new EncoderParameters(1);
        using var qualityParameter = new EncoderParameter(Encoder.Quality, JpegQuality);
        parameters.Param[0] = qualityParameter;

        using var stream = new MemoryStream();
        bitmap.Save(stream, encoder, parameters);
        return stream.ToArray();
    }

    private static ImageCodecInfo? FindJpegEncoder()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid) return codec;
        }
        return null;
    }

    private static class NativeMethods
    {
        /// <summary>Captures DWM-composited content, not just the classic GDI surface.</summary>
        public const uint PW_RENDERFULLCONTENT = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    }
}
