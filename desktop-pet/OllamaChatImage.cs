using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopPet;

internal sealed record OllamaChatImage(string Name, string Base64, BitmapSource Preview)
{
    internal const int MaxFiles = 2;
    internal const int MaxFileBytes = 15 * 1024 * 1024;
    internal const long MaxPixels = 40_000_000;
    internal const int MaxEdge = 1280;

    internal ChatStoredImage ToStored() => new(Name, Base64);

    internal static OllamaChatImage FromStored(ChatStoredImage stored)
    {
        if (stored.Base64.Length == 0 || stored.Base64.Length > MaxFileBytes * 4 / 3 + 4)
            throw new InvalidDataException("保存的图片数据大小异常。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(stored.Base64); }
        catch (FormatException ex) { throw new InvalidDataException("保存的图片数据已损坏。", ex); }
        if (bytes.Length < 8 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71 || bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10)
            throw new InvalidDataException("保存的图片不是有效 PNG。");
        using var buffer = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(buffer, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("保存的图片格式异常。");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth > MaxEdge || frame.PixelHeight > MaxEdge) throw new InvalidDataException("保存的图片尺寸超出限制。");
        return Prepare(stored.Name, frame, CancellationToken.None);
    }

    public static Task<OllamaChatImage> FromFileAsync(string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length == 0 || file.Length > MaxFileBytes)
                throw new InvalidDataException("图片文件需小于 15 MB。");
            using var buffer = new MemoryStream();
            var bytes = new byte[64 * 1024];
            int count;
            while ((count = file.Read(bytes, 0, bytes.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffer.Length + count > MaxFileBytes) throw new InvalidDataException("图片文件需小于 15 MB。");
                buffer.Write(bytes, 0, count);
            }
            var header = buffer.GetBuffer();
            var length = buffer.Length;
            bool png = length >= 8 && header[0] == 137 && header[1] == 80 && header[2] == 78 && header[3] == 71 && header[4] == 13 && header[5] == 10 && header[6] == 26 && header[7] == 10;
            bool jpeg = length >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255;
            bool gif = length >= 6 && header[0] == 71 && header[1] == 73 && header[2] == 70 && header[3] == 56 && (header[4] == 55 || header[4] == 57) && header[5] == 97;
            bool bmp = length >= 2 && header[0] == 66 && header[1] == 77;
            if (!(png || jpeg || gif || bmp)) throw new InvalidDataException("请选择真实的 PNG、JPEG、BMP 或 GIF 图片（GIF 取第一帧）。");
            buffer.Position = 0;
            var decoder = BitmapDecoder.Create(buffer, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            var frame = decoder.Frames[0];
            CheckDimensions(frame.PixelWidth, frame.PixelHeight);
            buffer.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = buffer;
            if (frame.PixelWidth > MaxEdge || frame.PixelHeight > MaxEdge)
            {
                if (frame.PixelWidth >= frame.PixelHeight) bitmap.DecodePixelWidth = MaxEdge;
                else bitmap.DecodePixelHeight = MaxEdge;
            }
            bitmap.EndInit();
            bitmap.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            return Prepare(Path.GetFileName(path), bitmap, cancellationToken);
        }, cancellationToken);

    public static Task<OllamaChatImage> FromClipboardAsync(BitmapSource bitmap, CancellationToken cancellationToken)
    {
        CheckDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
        var frozen = bitmap.Clone();
        frozen.Freeze();
        return Task.Run(() => Prepare("剪贴板图片", frozen, cancellationToken), cancellationToken);
    }

    private static void CheckDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 16000 || height > 16000 || (long)width * height > MaxPixels)
            throw new InvalidDataException("图片分辨率过大；请使用 4000 万像素以内、单边不超过 16000 像素的图片。");
    }

    private static OllamaChatImage Prepare(string name, BitmapSource source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckDimensions(source.PixelWidth, source.PixelHeight);
        var ratio = Math.Min(1.0, (double)MaxEdge / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource resized = ratio < 1 ? new TransformedBitmap(source, new ScaleTransform(ratio, ratio)) : source;
        var converted = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        // Recreate pixels rather than forwarding the original frame: no EXIF or other file metadata is sent.
        var clean = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        clean.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(clean));
        using var output = new MemoryStream();
        encoder.Save(output);
        cancellationToken.ThrowIfCancellationRequested();
        return new OllamaChatImage(name, Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length)), clean);
    }
}
