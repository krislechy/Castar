using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Castar.Compontents;

public partial class UserAvatarEditor : UserControl
{
    internal const int MaximumBytes = 1024 * 1024;
    private const int PreviewSourcePixels = 4096;
    private const int FullPixels = 1024;
    private const int CompressedPixels = 480;
    private readonly BitmapSource source;
    private CroppedBitmap? crop;
    private byte[]? candidate;
    private bool compressed;
    private bool ready;

    internal UserAvatarEditor(BitmapSource source)
    {
        this.source = source;
        InitializeComponent();
        ready = true;
        UpdateCrop();
        UpdateCandidate();
    }

    internal byte[]? Result { get; private set; }

    internal static BitmapSource ReadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        var orientation = frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort value
            ? value : (ushort)1;
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (Math.Max(width, height) > PreviewSourcePixels)
        {
            if (width >= height)
                image.DecodePixelWidth = PreviewSourcePixels;
            else
                image.DecodePixelHeight = PreviewSourcePixels;
        }
        image.EndInit();
        image.Freeze();

        Transform? rotation = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new MatrixTransform(0, 1, 1, 0, 0, 0),
            6 => new RotateTransform(90),
            7 => new MatrixTransform(0, -1, -1, 0, 0, 0),
            8 => new RotateTransform(270),
            _ => null
        };
        if (rotation is null)
            return image;

        var oriented = new TransformedBitmap(image, rotation);
        oriented.Freeze();
        return oriented;
    }

    private void Crop_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ready)
            UpdateCrop();
    }

    private void UpdateCrop()
    {
        var edge = Math.Max(1, (int)(Math.Min(source.PixelWidth, source.PixelHeight) / Zoom.Value));
        var left = (int)Math.Round((source.PixelWidth - edge) * (Horizontal.Value + 1) / 2);
        var top = (int)Math.Round((source.PixelHeight - edge) * (Vertical.Value + 1) / 2);
        crop = new CroppedBitmap(source, new Int32Rect(left, top, edge, edge));
        Preview.ImageSource = crop;
        candidate = null;
        Result = null;
    }

    private void Compress_Click(object sender, RoutedEventArgs e)
    {
        compressed = true;
        candidate = null;
        UpdateCandidate();
        PopupDialog.SetContentWarning(this, null);
    }

    internal ValidatePopupDialog Validate()
    {
        UpdateCandidate();
        Result = candidate is { Length: <= MaximumBytes } ? candidate : null;
        return Result is not null
            ? new(true, null)
            : new(false, "Размер аватара превышает 1 МБ. Нажмите «Сжать».");
    }

    private void UpdateCandidate()
    {
        if (candidate is null && crop is not null)
        {
            BitmapSource bitmap = crop;
            var limit = compressed ? CompressedPixels : FullPixels;
            if (bitmap.PixelWidth > limit)
            {
                var scale = (double)limit / bitmap.PixelWidth;
                bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            candidate = stream.ToArray();
        }
        Compress.Visibility = candidate is { Length: > MaximumBytes } ? Visibility.Visible : Visibility.Collapsed;
    }
}
