using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.DbConfiguration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// Builds the PostgreSQL connection settings from the database configuration.
/// </summary>
internal static class PostgreSqlOptionsReader
{
    internal const int DefaultMaxPoolSize = 20;
    internal const int DefaultCommandTimeout = 60;

    private static readonly HashSet<string> _knownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "host",
        "port",
        "database",
        "username",
        "password-file",
        "ssl-mode",
        "root-certificate",
        "max-pool-size",
        "min-pool-size",
        "connection-idle-lifetime",
        "keepalive",
        "connect-timeout",
        "command-timeout",
        "application-name",
        "include-error-detail",
        "pooling",
        "jit",
        "EnableSensitiveDataLogging"
    };

    /// <summary>
    /// Gets the option keys the reader understands.
    /// </summary>
    internal static IReadOnlyCollection<string> KnownKeys => _knownKeys;

    /// <summary>
    /// Reads the connection settings.
    /// </summary>
    /// <param name="databaseConfiguration">The database configuration.</param>
    /// <param name="applicationPaths">The application paths, used to resolve relative file paths.</param>
    /// <param name="logger">The logger for configuration warnings.</param>
    /// <returns>The connection settings.</returns>
    public static PostgreSqlConnectionSettings Read(DatabaseConfigurationOptions databaseConfiguration, IApplicationPaths? applicationPaths, ILogger logger)
    {
        var providerOptions = databaseConfiguration.CustomProviderOptions;
        var options = (IEnumerable<CustomDatabaseOption>?)providerOptions?.Options ?? [];

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(providerOptions?.ConnectionString ?? string.Empty);
        }
        catch (ArgumentException)
        {
            // The exception message can contain the connection string, which may hold a password.
            throw new InvalidOperationException("The PostgreSQL connection string in the database configuration is invalid.");
        }

        var passwordSource = string.IsNullOrEmpty(builder.Password) ? "none" : "connection string";
        if (passwordSource != "none")
        {
            logger.LogWarning("The PostgreSQL password is stored in the database configuration. Prefer the password-file option so the password is not stored in database.xml.");
        }

        // Keys the connection string sets itself keep their value; defaults only fill the gaps.
        var rawConnectionString = new DbConnectionStringBuilder { ConnectionString = providerOptions?.ConnectionString ?? string.Empty };
        var explicitlySet = rawConnectionString.Keys.Cast<string>().Select(NormalizeKeyword).ToHashSet(StringComparer.Ordinal);
        bool IsSet(params string[] keywords) => keywords.Any(k => explicitlySet.Contains(NormalizeKeyword(k)));

        foreach (var option in options)
        {
            if (!_knownKeys.Contains(option.Key))
            {
                logger.LogWarning("Ignoring unknown PostgreSQL database option {Key}.", option.Key);
            }
        }

        string? GetOption(string key) => options.LastOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;

        if (GetOption("host") is { } host)
        {
            builder.Host = host;
        }

        if (GetOption("port") is { } port)
        {
            builder.Port = ParseInt("port", port, 1, 65535);
        }

        if (GetOption("database") is { } database)
        {
            builder.Database = database;
        }

        if (GetOption("username") is { } username)
        {
            builder.Username = username;
        }

        string? passwordFile = null;
        if (GetOption("password-file") is { } passwordFileOption)
        {
            // The file is read whenever a connection opens, so the password stays out of the connection string and a
            // rotated password takes effect without a restart. Reading it once now fails fast on a bad path.
            passwordFile = ResolvePath(passwordFileOption, applicationPaths);
            CheckPasswordFile(passwordFile, logger);
            if (!string.IsNullOrEmpty(builder.Password))
            {
                logger.LogWarning("The PostgreSQL password file replaces the password in the connection string. Remove the password from the connection string.");
                builder.Password = null;
            }

            passwordSource = "password file";
        }

        builder.MaxPoolSize = ValueOrDefault("max-pool-size", builder.MaxPoolSize, DefaultMaxPoolSize, 1, 1000, "Maximum Pool Size", "MaxPoolSize");
        builder.MinPoolSize = ValueOrDefault("min-pool-size", builder.MinPoolSize, 0, 0, 1000, "Minimum Pool Size", "MinPoolSize");
        builder.ConnectionIdleLifetime = ValueOrDefault("connection-idle-lifetime", builder.ConnectionIdleLifetime, 300, 1, int.MaxValue, "Connection Idle Lifetime");
        builder.KeepAlive = ValueOrDefault("keepalive", builder.KeepAlive, 30, 0, int.MaxValue, "Keepalive");
        builder.Timeout = ValueOrDefault("connect-timeout", builder.Timeout, 15, 0, 1024, "Timeout");
        var commandTimeout = ValueOrDefault("command-timeout", builder.CommandTimeout, DefaultCommandTimeout, 0, int.MaxValue, "Command Timeout");
        builder.CommandTimeout = commandTimeout;

        if (GetOption("application-name") is { } applicationName)
        {
            builder.ApplicationName = applicationName;
        }
        else if (!IsSet("Application Name"))
        {
            builder.ApplicationName = "Jellyfin/" + typeof(PostgreSqlDatabaseProvider).Assembly.GetName().Version?.ToString(3);
        }

        if (GetOption("include-error-detail") is { } includeErrorDetail)
        {
            builder.IncludeErrorDetail = ParseBool("include-error-detail", includeErrorDetail);
        }
        else if (!IsSet("Include Error Detail", "Include Error Details"))
        {
            builder.IncludeErrorDetail = false;
        }

        if (GetOption("pooling") is { } pooling)
        {
            builder.Pooling = ParseBool("pooling", pooling);
        }

        if (GetOption("ssl-mode") is { } sslMode)
        {
            builder.SslMode = Enum.TryParse<SslMode>(sslMode, true, out var parsed)
                ? parsed
                : throw new InvalidOperationException("The PostgreSQL database option ssl-mode has an invalid value.");
        }
        else if (!IsSet("SSL Mode"))
        {
            builder.SslMode = IsLocalOrPrivateHost(builder.Host) ? SslMode.Prefer : SslMode.Require;
        }

        if (GetOption("root-certificate") is { } rootCertificate)
        {
            builder.RootCertificate = ResolvePath(rootCertificate, applicationPaths);
        }

        if (builder.Multiplexing)
        {
            logger.LogWarning("Multiplexing is not supported by Jellyfin and has been disabled.");
            builder.Multiplexing = false;
        }

        // Jellyfin stores UTC values; a session time zone other than UTC would shift values read back as text.
        builder.Timezone = "UTC";

        // Jellyfin's item queries are large enough to make PostgreSQL compile them, which takes far longer than running them.
        var disableJit = GetOption("jit") is not { } jit || jit.Equals("off", StringComparison.OrdinalIgnoreCase)
            || (jit.Equals("server", StringComparison.OrdinalIgnoreCase)
                ? false
                : throw new InvalidOperationException("The PostgreSQL database option jit has an invalid value."));
        if (disableJit)
        {
            // Resetting a pooled connection would turn JIT compilation back on.
            if (!IsSet("No Reset On Close"))
            {
                builder.NoResetOnClose = true;
            }
            else if (!builder.NoResetOnClose)
            {
                logger.LogWarning("JIT compilation cannot be kept off because the connection string sets No Reset On Close to false. Remove it, or turn JIT off for the database role.");
            }
        }

        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            throw new InvalidOperationException("The PostgreSQL database configuration does not name a host.");
        }

        var sensitiveDataLogging = GetOption("EnableSensitiveDataLogging") is { } sensitive && ParseBool("EnableSensitiveDataLogging", sensitive);

        var description = string.Create(
            CultureInfo.InvariantCulture,
            $"Host={builder.Host}; Port={builder.Port}; Database={builder.Database}; Username={builder.Username}; SSL Mode={builder.SslMode}; Maximum Pool Size={builder.MaxPoolSize}; Command Timeout={builder.CommandTimeout}; Password={passwordSource}");

        return new PostgreSqlConnectionSettings(builder.ConnectionString, passwordFile, commandTimeout, sensitiveDataLogging, description, disableJit);

        int ValueOrDefault(string key, int current, int defaultValue, int min, int max, params string[] keywords)
        {
            if (GetOption(key) is { } value)
            {
                return ParseInt(key, value, min, max);
            }

            return IsSet(keywords) ? current : defaultValue;
        }
    }

    private static string NormalizeKeyword(string keyword) => keyword.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>
    /// Gets a value indicating whether a host is reached without leaving the machine or a private network, where TLS is optional by default.
    /// </summary>
    /// <param name="host">The host or hosts from the connection string.</param>
    /// <returns><c>true</c> for Unix sockets, loopback, single-label names and private addresses.</returns>
    internal static bool IsLocalOrPrivateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        return host.Split(',').All(h => IsLocalOrPrivateSingleHost(h.Trim()));
    }

    private static bool IsLocalOrPrivateSingleHost(string host)
    {
        // Strip a port, but not from bare IPv6 addresses.
        if (host.StartsWith('[') && host.Contains(']', StringComparison.Ordinal))
        {
            host = host[1..host.IndexOf(']', StringComparison.Ordinal)];
        }
        else if (host.Count(c => c == ':') == 1)
        {
            host = host[..host.IndexOf(':', StringComparison.Ordinal)];
        }

        if (host.StartsWith('/') || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal)
            {
                return true;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                return bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254);
            }

            return false;
        }

        // A name without dots is resolved on the local network, e.g. a container name in a compose project.
        return !host.Contains('.', StringComparison.Ordinal);
    }

    private static void CheckPasswordFile(string fullPath, ILogger logger)
    {
        // Read and dropped: only whether the file can be read matters here.
        ReadPasswordFile(fullPath);

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(fullPath);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
            {
                logger.LogWarning("The PostgreSQL password file {Path} is readable by other users. Restrict it to the account running Jellyfin (chmod 600).", fullPath);
            }
        }
    }

    /// <summary>
    /// Reads the password from a password file.
    /// </summary>
    /// <param name="fullPath">The full path of the file.</param>
    /// <returns>The content of the file without one trailing line break.</returns>
    /// <exception cref="InvalidOperationException">The file could not be read.</exception>
    internal static string ReadPasswordFile(string fullPath)
    {
        try
        {
            return TrimLineBreak(File.ReadAllText(fullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The PostgreSQL password file '{fullPath}' could not be read.", ex);
        }
    }

    /// <summary>
    /// Reads the password from a password file.
    /// </summary>
    /// <param name="fullPath">The full path of the file.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The content of the file without one trailing line break.</returns>
    /// <exception cref="InvalidOperationException">The file could not be read.</exception>
    internal static async ValueTask<string> ReadPasswordFileAsync(string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            return TrimLineBreak(await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The PostgreSQL password file '{fullPath}' could not be read.", ex);
        }
    }

    private static string TrimLineBreak(string content)
    {
        if (content.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return content[..^2];
        }

        return content.EndsWith('\n') ? content[..^1] : content;
    }

    private static string ResolvePath(string path, IApplicationPaths? applicationPaths)
    {
        if (Path.IsPathRooted(path) || applicationPaths is null)
        {
            return path;
        }

        return Path.Combine(applicationPaths.ConfigurationDirectoryPath, path);
    }

    private static int ParseInt(string key, string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < min || result > max)
        {
            throw new InvalidOperationException($"The PostgreSQL database option {key} has an invalid value.");
        }

        return result;
    }

    private static bool ParseBool(string key, string value)
    {
        return bool.TryParse(value, out var result)
            ? result
            : throw new InvalidOperationException($"The PostgreSQL database option {key} has an invalid value.");
    }
}
