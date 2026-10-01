using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Database.Providers.PostgreSQL;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

public partial class PostgreSqlReadmeTests
{
    [Fact]
    public void Readme_OptionTable_ListsEveryOption()
    {
        var readme = File.ReadAllLines(Path.Combine(FindRepositoryRoot(), "src", "Jellyfin.Database", "Jellyfin.Database.Providers.PostgreSQL", "readme.md"));

        var documented = readme
            .Where(line => line.StartsWith("| `", StringComparison.Ordinal))
            .SelectMany(line => OptionName().Matches(line.Split('|')[1]).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(PostgreSqlOptionsReader.KnownKeys.Order(StringComparer.OrdinalIgnoreCase), documented.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jellyfin.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex OptionName();
}
