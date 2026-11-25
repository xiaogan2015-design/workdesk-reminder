using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkDesk
{
    internal static class SummaryImageCodec
    {
        public static string Encode(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                if (stream.Length > SummaryImage.MaxBytes) throw new IOException("单张图片保存后不得超过 12 MB。");
                return Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length);
            }
        }

        public static BitmapSource RestoreScreenshotOpacity(BitmapSource bitmap)
        {
            if (bitmap.Format != PixelFormats.Bgra32 && bitmap.Format != PixelFormats.Pbgra32) return bitmap;
            int stride = checked(bitmap.PixelWidth * 4);
            var pixels = new byte[checked(stride * bitmap.PixelHeight)];
            bitmap.CopyPixels(pixels, stride, 0);

            // Some Windows clipboard screenshots carry BGR color with an unused all-zero alpha channel.
            int color = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] != 0) return bitmap;
                color |= pixels[i] | pixels[i + 1] | pixels[i + 2];
            }
            if (color == 0) return bitmap;
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        }

        public static void RepairLegacy(SummaryImage image)
        {
            if (image.AlphaChecked) return;
            try
            {
                using (var stream = new MemoryStream(image.ReadPng()))
                {
                    var bitmap = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    var repaired = RestoreScreenshotOpacity(bitmap);
                    if (!ReferenceEquals(repaired, bitmap)) image.Png = Encode(repaired);
                    image.AlphaChecked = true;
                }
            }
            catch (IOException) { /* Keep the original attachment if it cannot be repaired. */ }
            catch (FormatException) { }
            catch (NotSupportedException) { }
        }
    }
}
