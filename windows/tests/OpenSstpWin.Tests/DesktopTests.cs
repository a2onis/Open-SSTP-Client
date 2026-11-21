using System.Runtime.ExceptionServices;
using OpenSstpWin;
using Xunit;

namespace OpenSstpWin.Tests;

public class DesktopTests
{
    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "secret")]
    public void PasswordIsOnlyPersistedWhenRequested(bool remember, string storedPassword)
    {
        var stored = new Settings { Password = "old", CaBundlePath = "ca.pem", CustomRoutes = new() { "10.0.0.0/8" } };
        var session = MainForm.PrepareSettings(stored, "secret", remember);

        Assert.Equal(storedPassword, stored.Password);
        Assert.Equal("secret", session.Password);
        Assert.Equal("ca.pem", session.CaBundlePath);
        Assert.Equal(stored.CustomRoutes, session.CustomRoutes);
        Assert.NotSame(stored.CustomRoutes, session.CustomRoutes);
    }

    [Fact]
    public void WindowCanBeCreatedOnAnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(new Settings());
                Assert.NotEqual(IntPtr.Zero, form.Handle);
                Assert.Equal("Open SSTP Client", form.Text);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
