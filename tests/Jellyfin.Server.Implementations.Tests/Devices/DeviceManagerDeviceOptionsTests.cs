using System;
using System.Threading.Tasks;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.Devices;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Devices;

/// <summary>
/// Covers two writers storing the options of the same device. The lookup the write starts from is a read, so the write
/// lock that queues writes inside one server does not cover it, and two callers here can miss each other's row just as
/// a second server on the same database can. The other writer stores its row between what the call it competes with
/// reads and what it writes, which is where a second server's commit falls.
/// </summary>
public sealed class DeviceManagerDeviceOptionsTests : IDisposable
{
    private const string DeviceId = "device";

    private readonly CompetingWriter _otherWriter = new();
    private readonly ITestDatabase _database;

    public DeviceManagerDeviceOptionsTests()
    {
        _database = TestDatabase.Create(new TestDatabaseOptions
        {
            ApplicationPaths = Mock.Of<IApplicationPaths>(),
            Interceptors = [_otherWriter]
        });
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task UpdateDeviceOptions_WhenAnotherWriterStoredTheOptionsFirst_StoresTheNameItWasGiven()
    {
        var manager = CreateManager();
        var otherManager = CreateManager();
        _otherWriter.ArmAsync(() => otherManager.UpdateDeviceOptions(DeviceId, "theirs"));

        await manager.UpdateDeviceOptions(DeviceId, "mine");

        Assert.True(_otherWriter.Wrote, "The other writer never got in, so the insert was not made to meet a stored row.");

        using var context = _database.CreateDbContext();
        var stored = Assert.Single(context.DeviceOptions);
        Assert.Equal("mine", stored.CustomName);

        // Devices are named from the manager's own copy of the row, so that has to be the stored one.
        var options = manager.GetDeviceOptions(DeviceId);
        Assert.NotNull(options);
        Assert.Equal(stored.Id, options.Id);
        Assert.Equal("mine", options.CustomName);
    }

    [Fact]
    public async Task UpdateDeviceOptions_AskedTwice_StoresOneRowWithTheLastName()
    {
        var manager = CreateManager();

        await manager.UpdateDeviceOptions(DeviceId, "first");
        await manager.UpdateDeviceOptions(DeviceId, "second");

        using var context = _database.CreateDbContext();
        var stored = Assert.Single(context.DeviceOptions);
        Assert.Equal("second", stored.CustomName);
    }

    private DeviceManager CreateManager()
        => new(_database.CreateDbContextFactory(), Mock.Of<IUserManager>(), _database.Provider);
}
