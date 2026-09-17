using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.PostgreSQL;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

public sealed class PostgreSqlOptionsReaderTests : IDisposable
{
    private const string Secret = "s3cr3t-Pa55word";
    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-pg-options-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingLogger _logger = new();

    public PostgreSqlOptionsReaderTests()
    {
        Directory.CreateDirectory(_configDirectory);
    }

    [Fact]
    public void Read_HostOnly_AppliesDefaults()
    {
        var settings = Read("Host=db.example.com;Database=jellyfin;Username=jellyfin");
        var builder = new NpgsqlConnectionStringBuilder(settings.ConnectionString);

        Assert.Equal(20, builder.MaxPoolSize);
        Assert.Equal(0, builder.MinPoolSize);
        Assert.Equal(300, builder.ConnectionIdleLifetime);
        Assert.Equal(30, builder.KeepAlive);
        Assert.Equal(15, builder.Timeout);
        Assert.Equal(60, builder.CommandTimeout);
        Assert.Equal(60, settings.CommandTimeout);
        Assert.False(builder.IncludeErrorDetail);
        Assert.False(builder.Multiplexing);
        Assert.Equal("UTC", builder.Timezone);
        Assert.StartsWith("Jellyfin/", builder.ApplicationName, StringComparison.Ordinal);
        Assert.Equal(SslMode.Require, builder.SslMode);
        Assert.False(settings.EnableSensitiveDataLogging);
    }

    [Fact]
    public void Read_ValuesInConnectionString_AreKept()
    {
        var builder = new NpgsqlConnectionStringBuilder(Read("Host=db;Maximum Pool Size=100;Command Timeout=5;Application Name=custom;SSL Mode=Disable;Include Error Detail=true").ConnectionString);

        Assert.Equal(100, builder.MaxPoolSize);
        Assert.Equal(5, builder.CommandTimeout);
        Assert.Equal("custom", builder.ApplicationName);
        Assert.Equal(SslMode.Disable, builder.SslMode);
        Assert.True(builder.IncludeErrorDetail);
    }

    [Fact]
    public void Read_Options_OverrideConnectionString()
    {
        var settings = Read(
            "Host=db;Port=5432;MaxPoolSize=100",
            ("host", "other"),
            ("port", "6543"),
            ("database", "jf"),
            ("username", "jfuser"),
            ("max-pool-size", "7"),
            ("command-timeout", "0"),
            ("ssl-mode", "verifyfull"),
            ("EnableSensitiveDataLogging", "true"));
        var builder = new NpgsqlConnectionStringBuilder(settings.ConnectionString);

        Assert.Equal("other", builder.Host);
        Assert.Equal(6543, builder.Port);
        Assert.Equal("jf", builder.Database);
        Assert.Equal("jfuser", builder.Username);
        Assert.Equal(7, builder.MaxPoolSize);
        Assert.Equal(0, settings.CommandTimeout);
        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
        Assert.True(settings.EnableSensitiveDataLogging);
    }

    [Theory]
    [InlineData("/var/run/postgresql", true)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("db", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("192.168.1.10:5432", true)]
    [InlineData("fd00::1", true)]
    [InlineData("pg.example.com", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("db,pg.example.com", false)]
    public void IsLocalOrPrivateHost_Host_ReturnsExpected(string host, bool expected)
    {
        Assert.Equal(expected, PostgreSqlOptionsReader.IsLocalOrPrivateHost(host));
    }

    [Theory]
    [InlineData("password\n")]
    [InlineData("password\r\n")]
    [InlineData("password")]
    public void Read_PasswordFileRelativeToConfig_KeepsThePasswordOutOfTheConnectionString(string content)
    {
        var path = Path.Combine(_configDirectory, "pg-password");
        File.WriteAllText(path, content);

        var settings = Read("Host=db", ("password-file", "pg-password"));

        Assert.Equal(path, settings.PasswordFile);
        Assert.Null(new NpgsqlConnectionStringBuilder(settings.ConnectionString).Password);
        Assert.Contains("Password=password file", settings.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password\n")]
    [InlineData("password\r\n")]
    [InlineData("password")]
    public async Task ReadPasswordFile_TrimsOneTrailingNewline(string content)
    {
        var path = Path.Combine(_configDirectory, "pg-password");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);

        Assert.Equal("password", PostgreSqlOptionsReader.ReadPasswordFile(path));
        Assert.Equal("password", await PostgreSqlOptionsReader.ReadPasswordFileAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_PasswordFileAbsolute_IsResolvedAsIs()
    {
        var path = Path.Combine(_configDirectory, "absolute-password");
        File.WriteAllText(path, Secret);

        var settings = Read("Host=db", ("password-file", path));

        Assert.Equal(path, settings.PasswordFile);
        Assert.Equal(Secret, PostgreSqlOptionsReader.ReadPasswordFile(path));
    }

    [Fact]
    public void Read_PasswordFileAndPasswordInConnectionString_UsesTheFileAndWarns()
    {
        var path = Path.Combine(_configDirectory, "pg-password");
        File.WriteAllText(path, "from-file");

        var settings = Read($"Host=db;Password={Secret}", ("password-file", path));

        Assert.Equal(path, settings.PasswordFile);
        Assert.Null(new NpgsqlConnectionStringBuilder(settings.ConnectionString).Password);
        Assert.Contains(_logger.Messages, m => m.Contains("password file replaces the password", StringComparison.Ordinal));
        Assert.All(_logger.Messages, m => Assert.DoesNotContain(Secret, m, StringComparison.Ordinal));
    }

    [Fact]
    public void ReadPasswordFile_MissingFile_ThrowsNamingThePath()
    {
        var path = Path.Combine(_configDirectory, "does-not-exist");

        var exception = Assert.Throws<InvalidOperationException>(() => PostgreSqlOptionsReader.ReadPasswordFile(path));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_MissingPasswordFile_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Read("Host=db", ("password-file", "does-not-exist")));
    }

    [Fact]
    public void Read_PasswordFileReadableByOthers_Warns()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_configDirectory, "open-password");
        File.WriteAllText(path, Secret);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        Read("Host=db", ("password-file", path));

        Assert.Contains(_logger.Messages, m => m.Contains("readable by other users", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_PasswordInConnectionString_WarnsWithoutLeakingIt()
    {
        var settings = Read($"Host=db;Password={Secret}");

        Assert.Null(settings.PasswordFile);
        Assert.Contains(_logger.Messages, m => m.Contains("password is stored in the database configuration", StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, settings.Description, StringComparison.Ordinal);
        Assert.All(_logger.Messages, m => Assert.DoesNotContain(Secret, m, StringComparison.Ordinal));
    }

    [Fact]
    public void Read_UnknownOption_Warns()
    {
        Read("Host=db", ("no-such-option", "x"));

        Assert.Contains(_logger.Messages, m => m.Contains("no-such-option", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("max-pool-size", "lots")]
    [InlineData("port", "70000")]
    [InlineData("pooling", "maybe")]
    [InlineData("ssl-mode", "sometimes")]
    public void Read_InvalidOptionValue_ThrowsNamingKeyNotValue(string key, string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Read("Host=db", (key, value)));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_InvalidConnectionString_DoesNotLeakIt()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Read($"Host=db;Password={Secret};NotAKeyword=1"));

        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_NoHost_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Read("Database=jellyfin"));
    }

    [Fact]
    public void Read_Multiplexing_IsDisabled()
    {
        var builder = new NpgsqlConnectionStringBuilder(Read("Host=db;Multiplexing=true").ConnectionString);

        Assert.False(builder.Multiplexing);
    }

    [Fact]
    public void Read_Default_TurnsJitOffAndKeepsItOffInThePool()
    {
        var settings = Read("Host=db");

        Assert.True(settings.DisableJit);
        Assert.True(new NpgsqlConnectionStringBuilder(settings.ConnectionString).NoResetOnClose);
    }

    [Theory]
    [InlineData("server", false)]
    [InlineData("SERVER", false)]
    [InlineData("off", true)]
    public void Read_JitOption_IsApplied(string value, bool disableJit)
    {
        var settings = Read("Host=db", ("jit", value));

        Assert.Equal(disableJit, settings.DisableJit);
        Assert.Equal(disableJit, new NpgsqlConnectionStringBuilder(settings.ConnectionString).NoResetOnClose);
    }

    [Fact]
    public void Read_InvalidJitOption_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Read("Host=db", ("jit", "sometimes")));

        Assert.Contains("jit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ResetOnCloseInConnectionString_IsKeptAndWarns()
    {
        var settings = Read("Host=db;No Reset On Close=false");

        Assert.False(new NpgsqlConnectionStringBuilder(settings.ConnectionString).NoResetOnClose);
        Assert.Contains(_logger.Messages, m => m.Contains("JIT compilation cannot be kept off", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        Directory.Delete(_configDirectory, true);
    }

    private PostgreSqlConnectionSettings Read(string connectionString, params (string Key, string Value)[] options)
    {
        var providerOptions = new CustomDatabaseOptions
        {
            PluginName = string.Empty,
            PluginAssembly = string.Empty,
            ConnectionString = connectionString
        };
        foreach (var (key, value) in options)
        {
            providerOptions.Options.Add(new CustomDatabaseOption { Key = key, Value = value });
        }

        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.ConfigurationDirectoryPath).Returns(_configDirectory);

        return PostgreSqlOptionsReader.Read(
            new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-PostgreSQL", CustomProviderOptions = providerOptions },
            paths.Object,
            _logger);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
