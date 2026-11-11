using Kinora.Core.Settings;

namespace Kinora.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "kinora-settings-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Fact]
    public void A_folded_sidebar_stays_folded_at_the_next_start()
    {
        Assert.False(new AppSettings().SidebarCollapsed);
        new AppSettings { SidebarCollapsed = true }.Save(_path);
        Assert.True(AppSettings.Load(_path).SidebarCollapsed);
    }
}
