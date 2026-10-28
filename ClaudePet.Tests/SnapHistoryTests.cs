using System.IO;
using System.Text;
using ClaudePet;

namespace ClaudePet.Tests;

public class SnapHistoryTests
{
    [Fact]
    public void History_round_trips_and_is_not_plain_text_on_disk()
    {
        var file = Path.Combine(Path.GetTempPath(), $"snaps-{Guid.NewGuid()}.dat");
        try
        {
            var history = new SnapHistory(file);
            var id = Guid.NewGuid();
            history.Add(new SnapHistory.Entry(id, new DateTime(2026, 10, 15, 23, 0, 0), @"E:\MES3", "why is the build red", @"C:\shot.png", []));
            history.AddReply(id, "Missing semicolon in OrderView.tsx.");
            history.AddReply(Guid.NewGuid(), "not this one");

            var entry = Assert.Single(new SnapHistory(file).Load());
            Assert.Equal("why is the build red", entry.Question);
            Assert.Equal(["Missing semicolon in OrderView.tsx."], entry.Replies);

            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("build red", raw);
            Assert.DoesNotContain("OrderView", raw);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Crop_keeps_only_the_selected_pixels()
    {
        var file = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid()}.png");
        try
        {
            using (var full = new System.Drawing.Bitmap(200, 100))
            {
                full.SetPixel(50, 20, System.Drawing.Color.Red);
                full.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }

            SnipWindow.Crop(file, new System.Windows.Int32Rect(50, 20, 30, 10));

            using var part = new System.Drawing.Bitmap(file);
            Assert.Equal((30, 10), (part.Width, part.Height));
            Assert.Equal(System.Drawing.Color.Red.ToArgb(), part.GetPixel(0, 0).ToArgb());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Unreadable_history_reads_as_empty()
    {
        var file = Path.Combine(Path.GetTempPath(), $"snaps-{Guid.NewGuid()}.dat");
        try
        {
            File.WriteAllText(file, "not encrypted");
            Assert.Empty(new SnapHistory(file).Load());
        }
        finally
        {
            File.Delete(file);
        }
    }
}
