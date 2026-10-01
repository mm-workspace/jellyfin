using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Server.DatabaseImport;
using Jellyfin.Server.Implementations.DatabaseImport;
using Xunit;

namespace Jellyfin.Server.Tests.DatabaseImport;

/// <summary>
/// Keeps the shipped pgloader load file in line with the model and with the options the import was tested with.
/// </summary>
public class LoadFileTests
{
    private static readonly string _loadFile = ReadLoadFile();

    [Fact]
    public void IncludedTables_AreTheTablesOfTheModel()
    {
        // A schema change that adds a table must add it here too, or pgloader would silently skip it.
        Assert.Equal(ImportModel.ForPostgreSql().Tables.Select(t => t.Name), Names("INCLUDING ONLY TABLE NAMES LIKE"));
    }

    [Fact]
    public void ExcludedTables_HaveNoForeignKeys()
    {
        // pgloader fails to recreate foreign keys of excluded tables when identifiers are quoted.
        Assert.Equal(["__EFMigrationsHistory", "__EFMigrationsLock", "sqlite_%"], Names("EXCLUDING TABLE NAMES LIKE"));
    }

    [Theory]
    [InlineData("data only")]
    [InlineData("truncate")]
    [InlineData("quote identifiers")]
    [InlineData("on error stop")]
    [InlineData("reset no sequences")]
    [InlineData("foreign keys")]
    [InlineData("concurrency = 1")]
    [InlineData("SET PostgreSQL PARAMETERS timezone TO 'UTC'")]
    [InlineData("FROM sqlite:///import/" + PostgreSqlImportCommand.SnapshotFileName)]
    [InlineData("CAST type integer to bigint drop typemod using (lambda (x) (when x (princ-to-string x)))")]
    [InlineData("column KeyframeData.KeyframeTicks to text using (lambda (x) (when x (concatenate (quote string) \"{\" (string-trim \"[]\" x) \"}\")))")]
    public void RequiredOption_IsPresent(string option)
    {
        Assert.Contains(option, Command(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("drop indexes")]
    [InlineData("on error resume next")]
    [InlineData("disable triggers")]
    [InlineData("include drop")]
    [InlineData("create tables")]
    [InlineData("create indexes")]
    [InlineData("create schemas")]
    [InlineData("reset sequences")]
    [InlineData("no truncate")]
    [InlineData("no foreign keys")]
    public void ForbiddenOption_IsAbsent(string option)
    {
        Assert.DoesNotContain(option, Command().Replace("reset no sequences", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Target_TakesItsConnectionFromTheEnvironmentWithoutAPassword()
    {
        Assert.Contains("INTO postgresql://{{PGUSER}}@{{PGHOST}}:{{PGPORT}}/{{PGDATABASE}}\n", Command(), StringComparison.Ordinal);
        Assert.Single(Regex.Matches(Command(), "postgresql://"));
        Assert.DoesNotMatch(@"workers = [5-9]|workers = \d\d|concurrency = [2-9]", Command());
    }

    [Fact]
    public void TestedImage_MatchesThePinFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "postgresql-import", "pgloader.image")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var image = File.ReadAllText(Path.Combine(directory.FullName, "tests", "postgresql-import", "pgloader.image")).Trim();
        Assert.Matches("^ghcr.io/dimitri/pgloader@sha256:[0-9a-f]{64}$", image);
        Assert.Contains($"-- Tested image: {image}\n", _loadFile, StringComparison.Ordinal);
    }

    private static string ReadLoadFile()
    {
        using var stream = typeof(PostgreSqlImportCommand).Assembly.GetManifestResourceStream("Jellyfin.Server.Resources.PostgreSqlImport.jellyfin.load")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    private static string Command() => string.Join('\n', _loadFile.Split('\n').Where(l => !l.StartsWith("--", StringComparison.Ordinal)));

    private static string[] Names(string clause)
    {
        var start = Command().IndexOf(clause, StringComparison.Ordinal);
        Assert.True(start >= 0, clause);
        var list = Command()[(start + clause.Length)..];
        list = list[..list.IndexOf(clause.StartsWith("INCLUDING", StringComparison.Ordinal) ? "\n\n" : ";", StringComparison.Ordinal)];
        return Regex.Matches(list, "'([^']*)'").Select(m => m.Groups[1].Value).ToArray();
    }
}
