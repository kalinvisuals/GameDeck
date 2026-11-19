using GameDeck.App;
using GameDeck.App.Tray;
using GameDeck.Core.Audio;
using GameDeck.Core.Config;
using GameDeck.Core.Security;
using Xunit;

namespace GameDeck.Tests;

public class AudioSetupFormTests
{
    /// <summary>
    /// Builds and paints the Windows "Audio setup" window against the real engine and this PC's
    /// devices, off-screen. Set GAMEDECK_SNAPSHOT_DIR to also save a PNG of it.
    /// </summary>
    [Fact]
    public void Audio_setup_window_builds_and_renders_with_real_devices()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var store = new ConfigStore(TestAudio.TempDir());
                store.Update(c => { c.Audio.MonitorEnabled = false; });
                using var engine = new AudioEngine();
                var service = new GameDeckService(store, engine, new PairingService());
                service.Start();
                using var form = new AudioSetupForm(service) { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false };
                form.Show(); // off-screen, so child controls get handles and paint
                Application.DoEvents();
                var area = Screen.PrimaryScreen!.WorkingArea;
                Assert.True(form.Height <= area.Height, $"window {form.Height}px taller than screen {area.Height}px");
                // Paint the whole content (not just the visible scroll area) for the snapshot.
                var content = form.Controls[0];
                using var bmp = new Bitmap(content.Width + 48, content.Height + 48);
                using (var g = Graphics.FromImage(bmp)) g.Clear(Theme.Background);
                content.DrawToBitmap(bmp, new Rectangle(24, 24, content.Width, content.Height));
                Assert.True(content.Width > 400 && content.Height > 400);
                if (Environment.GetEnvironmentVariable("GAMEDECK_SNAPSHOT_DIR") is { Length: > 0 } dir)
                    bmp.Save(Path.Combine(dir, "audio-setup.png"));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
