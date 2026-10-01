using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.DatabaseImport;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport;

/// <summary>
/// The canonical form of values loaded from SQLite text is the form PostgreSQL gives them.
/// </summary>
[Trait("Provider", "PostgreSql")]
public class PostgreSqlCanonicalizationTests
{
    private static readonly ImportModel _model = ImportModel.ForPostgreSql();

    [Theory]
    [InlineData("2024-05-01 12:34:56.1234567")]
    [InlineData("2024-05-01 12:34:56.0000005")]
    [InlineData("2024-05-01 12:34:56.0000015")]
    [InlineData("2024-05-01 12:34:56.0000025")]
    [InlineData("2024-05-01 12:34:56.9999995")]
    [InlineData("1970-01-01 00:00:00")]
    [InlineData("9999-12-31 23:59:59.9999994")]
    public async Task Timestamp_ParsedByPostgreSql_MatchesTheCanonicalForm(string sqliteText)
    {
        // pgloader sends the SQLite text as is, with the session time zone set to UTC.
        var stored = await ScalarAsync<DateTime>("SELECT @value::timestamptz", sqliteText);

        Assert.Equal(ValueCanonicalizer.Timestamp(sqliteText), ValueCanonicalizer.Timestamp(stored));
    }

    [Fact]
    public async Task Timestamp_EverySeventhDigitTie_RoundsLikePostgreSql()
    {
        const string Seconds = "2024-05-01 12:34:56.";
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT (extract(epoch FROM ('{Seconds}' || lpad(n::text, 6, '0') || '5')::timestamptz) * 1000000)::bigint FROM generate_series(0, 999999) AS n ORDER BY n",
            connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var mismatches = new List<string>();
        for (var n = 0; await reader.ReadAsync(TestContext.Current.CancellationToken); n++)
        {
            var stored = DateTime.UnixEpoch.AddTicks(reader.GetInt64(0) * 10);
            var text = string.Create(CultureInfo.InvariantCulture, $"{Seconds}{n:D6}5");
            if (ValueCanonicalizer.Timestamp(text) != ValueCanonicalizer.Timestamp(stored))
            {
                mismatches.Add(text);
            }
        }

        Assert.Empty(mismatches);
    }

    [Theory]
    [InlineData("0.30000001192092896")]
    [InlineData("-0")]
    [InlineData("3.4028234663852886E+38")]
    [InlineData("1.401298464324817E-45")]
    public async Task Real_ParsedByPostgreSql_MatchesTheCanonicalForm(string sqliteText)
    {
        var column = _model.GetTable("BaseItems").Columns.Single(c => c.Name == "CommunityRating");
        var stored = await ScalarAsync<float>("SELECT @value::real", sqliteText);

        Assert.Equal(ValueCanonicalizer.Canonicalize(column, double.Parse(sqliteText, System.Globalization.CultureInfo.InvariantCulture)), ValueCanonicalizer.Canonicalize(column, stored));
    }

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var connectionString = TestDatabase.PostgreSqlConnectionString;
        Assert.SkipWhen(connectionString is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Timezone = "UTC" };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<T> ScalarAsync<T>(string sql, string value)
    {
        await using var connection = await OpenAsync();
#pragma warning disable CA2100 // The statements are constants of this class.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("value", value);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
