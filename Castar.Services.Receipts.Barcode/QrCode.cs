using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using ZXingCpp;

namespace Castar.Services.Receipts.Barcode;

public sealed class QrCode : IQrCode
{
    public const int MaxImageBytes = 20 * 1024 * 1024;
    private const long MaxPixels = 16_000_000;
    private const int MaxEnhancedSide = 1800;
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(2);

    public string? Decode(Stream image, CancellationToken cancellationToken = default)
    {
        if (image.CanSeek && image.Length - image.Position > MaxImageBytes)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new SKManagedStream(image, disposeManagedStream: false);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            return null;

        var timer = Stopwatch.StartNew();
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        if (codec.GetPixels(info, out var pixels) != SKCodecResult.Success)
            return null;

        var luminance = ReadLuminance(pixels, width * height, cancellationToken);
        using var reader = new BarcodeReader
        {
            Formats = new BarcodeFormats(BarcodeFormat.QRCode),
            TryHarder = true,
            TryRotate = true,
            TryInvert = true,
            TryDownscale = true,
            MaxNumberOfSymbols = 1
        };
        if (Scan(reader, luminance, width, height) is { } code)
            return code;

        // Overlap the center and corner crops so a small QR near a boundary stays intact.
        foreach (var region in SearchRegions(width, height))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed >= ScanBudget)
                break;

            var (enhanced, scanWidth, scanHeight) = Enhance(luminance, width, region);
            if (Scan(reader, enhanced, scanWidth, scanHeight) is { } candidate)
                return candidate;
        }

        return null;
    }

    private static byte[] ReadLuminance(byte[] pixels, int length, CancellationToken cancellationToken)
    {
        var luminance = new byte[length];
        for (var index = 0; index < length; index++)
        {
            if ((index & 0xFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var pixel = index * 4;
            var gray = (29 * pixels[pixel] + 150 * pixels[pixel + 1] + 77 * pixels[pixel + 2] + 128) >> 8;
            var alpha = pixels[pixel + 3];
            luminance[index] = (byte)((gray * alpha + 255 * (255 - alpha) + 127) / 255);
        }
        return luminance;
    }

    private static IEnumerable<SKRectI> SearchRegions(int width, int height)
    {
        yield return SKRectI.Create(width, height);
        if (Math.Min(width, height) < 80)
            yield break;

        var cropWidth = width * 2 / 3;
        var cropHeight = height * 2 / 3;
        var right = width - cropWidth;
        var bottom = height - cropHeight;
        yield return SKRectI.Create(right / 2, bottom / 2, cropWidth, cropHeight);
        yield return SKRectI.Create(0, 0, cropWidth, cropHeight);
        yield return SKRectI.Create(right, 0, cropWidth, cropHeight);
        yield return SKRectI.Create(0, bottom, cropWidth, cropHeight);
        yield return SKRectI.Create(right, bottom, cropWidth, cropHeight);
    }

    private static (byte[] Pixels, int Width, int Height) Enhance(byte[] source, int stride, SKRectI region)
    {
        var edge = Math.Max(region.Width, region.Height);
        var scale = Math.Min(edge < 900 ? 2.0 : 1.0, (double)MaxEnhancedSide / edge);
        var width = Math.Max(1, (int)(region.Width * scale));
        var height = Math.Max(1, (int)(region.Height * scale));
        var output = new byte[width * height];
        Span<int> histogram = stackalloc int[256];
        histogram.Clear();

        for (var y = 0; y < height; y++)
        {
            var row = (region.Top + (int)((long)y * region.Height / height)) * stride + region.Left;
            for (var x = 0; x < width; x++)
            {
                var value = source[row + (int)((long)x * region.Width / width)];
                output[y * width + x] = value;
                histogram[value]++;
            }
        }

        var low = HistogramPercentile(histogram, output.Length / 100);
        var high = HistogramPercentile(histogram, output.Length - 1 - output.Length / 100);
        if (high - low < 8)
        {
            low = HistogramPercentile(histogram, 0);
            high = HistogramPercentile(histogram, output.Length - 1);
        }
        if (high <= low)
            return (output, width, height);

        Span<byte> contrast = stackalloc byte[256];
        for (var value = 0; value < contrast.Length; value++)
            contrast[value] = (byte)Math.Clamp((value - low) * 255 / (high - low), 0, 255);
        for (var index = 0; index < output.Length; index++)
            output[index] = contrast[output[index]];

        return (output, width, height);
    }

    private static int HistogramPercentile(ReadOnlySpan<int> histogram, int index)
    {
        for (var value = 0; value < histogram.Length; value++)
        {
            index -= histogram[value];
            if (index < 0)
                return value;
        }
        return histogram.Length - 1;
    }

    private static string? Scan(BarcodeReader reader, byte[] pixels, int width, int height)
    {
        // ZXingCpp 0.5.3 ImageView borrows its buffer, so keep it pinned until scanning finishes.
        var buffer = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var view = new ImageView(buffer.AddrOfPinnedObject(), width, height, ImageFormat.Lum);
            var barcodes = reader.From(view);
            try
            {
                return barcodes.FirstOrDefault(barcode => barcode.IsValid)?.Text;
            }
            finally
            {
                foreach (var barcode in barcodes)
                    barcode.Dispose();
                GC.KeepAlive(view);
            }
        }
        finally
        {
            buffer.Free();
        }
    }
}
