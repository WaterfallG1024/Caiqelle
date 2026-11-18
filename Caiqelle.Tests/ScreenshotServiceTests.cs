using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Caiqelle.Services;

namespace Caiqelle.Tests
{
    public class ScreenshotServiceTests
    {
        [Theory]
        [InlineData(1920, 1080)]
        [InlineData(2560, 1080)]
        [InlineData(3440, 1440)]
        [InlineData(1366, 768)]
        [InlineData(1921, 1083)]
        public void GetTileRect_CoversEntireFrameWithoutGapsOrOverlaps(int width, int height)
        {
            long totalArea = 0;

            for (int i = 0; i < ScreenshotService.RewindTileCount; i++)
            {
                var rect = ScreenshotService.GetTileRect(i, width, height);
                Assert.True(rect.Width > 0);
                Assert.True(rect.Height > 0);
                Assert.True(rect.X >= 0 && rect.Right <= width);
                Assert.True(rect.Y >= 0 && rect.Bottom <= height);
                totalArea += (long)rect.Width * rect.Height;
            }

            Assert.Equal((long)width * height, totalArea);
        }

        [Fact]
        public void ComposeTilesToJpegBytes_CreatesValidJpeg()
        {
            var service = new ScreenshotService();
            int w = 160;
            int h = 120;
            var tiles = new byte[]?[ScreenshotService.RewindTileCount];

            for (int i = 0; i < ScreenshotService.RewindTileCount; i++)
            {
                var rect = ScreenshotService.GetTileRect(i, w, h);
                using var tileBmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(tileBmp))
                {
                    g.Clear(Color.FromArgb(255, (i * 15) % 256, 100, 200));
                }
                using var ms = new MemoryStream();
                tileBmp.Save(ms, ImageFormat.Jpeg);
                tiles[i] = ms.ToArray();
            }

            byte[] composed = service.ComposeTilesToJpegBytes(tiles, w, h);

            Assert.True(composed.Length > 2);
            Assert.Equal(0xFF, composed[0]);
            Assert.Equal(0xD8, composed[1]);

            using var outMs = new MemoryStream(composed);
            using var outBmp = new Bitmap(outMs);
            Assert.Equal(w, outBmp.Width);
            Assert.Equal(h, outBmp.Height);
        }
    }
}
