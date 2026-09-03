using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NzbDrone.Core.Books.Calibre
{
    public static class CalibreCoverNormalizer
    {
        // A standard book-cover canvas (2:3). Delivering every cover at the same
        // dimensions and aspect ratio keeps library cards uniform instead of
        // stretching or letterboxing per-source scans differently.
        private const int TargetWidth = 600;
        private const int TargetHeight = 900;

        private static bool IsProgressiveJpeg(byte[] data)
        {
            if (data == null || data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            {
                return false;
            }

            var i = 2;

            while (i < data.Length - 1)
            {
                if (data[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                var marker = data[i + 1];

                // SOF2 = progressive DCT.
                if (marker == 0xC2)
                {
                    return true;
                }

                // Other Start-Of-Frame markers = baseline/extended: not progressive.
                if (marker == 0xC0 || marker == 0xC1 || marker == 0xC3)
                {
                    return false;
                }

                // Standalone markers without a length payload.
                if (marker == 0xD8 || marker == 0xD9 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01 || marker == 0xFF)
                {
                    i += 2;
                    continue;
                }

                if (i + 3 >= data.Length)
                {
                    return false;
                }

                var segLength = (data[i + 2] << 8) | data[i + 3];

                if (segLength < 2)
                {
                    return false;
                }

                i += 2 + segLength;
            }

            return false;
        }

        public static byte[] Normalize(byte[] source)
        {
            if (source == null || source.Length == 0)
            {
                return source;
            }

            if (IsProgressiveJpeg(source))
            {
                // ImageSharp 3.1.x mis-decodes progressive JPEGs (planar components read
                // as a 3x-wide image), so re-encoding one corrupts it. Leave these covers
                // at their native size rather than mangle them.
                return source;
            }

            try
            {
                using var image = Image.Load<Rgba32>(source);

                if (image.Width == TargetWidth && image.Height == TargetHeight)
                {
                    return source;
                }

                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(TargetWidth, TargetHeight),

                    // Pad preserves the whole cover (no cropping of titles or borders);
                    // the fill is black, which is invisible on dark covers and neutral
                    // otherwise.
                    Mode = ResizeMode.Pad,
                    Position = AnchorPositionMode.Center,
                    PadColor = Color.Black
                }));

                using var output = new MemoryStream();
                image.Save(output, new JpegEncoder { Quality = 90 });
                return output.ToArray();
            }
            catch
            {
                // Never let a cover-shaping problem block the metadata push.
                return source;
            }
        }
    }
}
