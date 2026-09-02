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

        public static byte[] Normalize(byte[] source)
        {
            if (source == null || source.Length == 0)
            {
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
