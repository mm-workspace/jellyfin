using System.Linq;
using System.Reflection;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

public class PostgreSqlKnownIssueTests
{
    /// <summary>
    /// The number of tests known to fail on PostgreSQL. Fixing one means lowering this number; it must never grow.
    /// </summary>
    private const int KnownIssueCount = 6;

    [Fact]
    public void KnownIssues_DoNotGrow()
    {
        var count = typeof(PostgreSqlKnownIssueTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Count(m => m.GetCustomAttributes<TraitAttribute>().Any(a => a.Name == "Postgres" && a.Value == "KnownIssue"));

        Assert.True(count <= KnownIssueCount, $"{count} tests are marked as known PostgreSQL issues, more than the {KnownIssueCount} allowed.");
        Assert.True(count >= KnownIssueCount, $"Only {count} tests are marked as known PostgreSQL issues. Lower {nameof(KnownIssueCount)} to {count}.");
    }
}
