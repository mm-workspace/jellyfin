using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Server.Implementations.DatabaseImport;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport;

public class ValueCanonicalizerTests
{
    private static readonly ImportModel _model = ImportModel.ForPostgreSql();

    [Theory]
    [InlineData("2024-05-01 12:34:56.1234567", "2024-05-01T12:34:56.123457")]
    [InlineData("2024-05-01 12:34:56", "2024-05-01T12:34:56.000000")]
    [InlineData("2024-05-01 12:34:56.0000005", "2024-05-01T12:34:56.000000")]
    [InlineData("2024-05-01 12:34:56.0000015", "2024-05-01T12:34:56.000002")]
    [InlineData("2024-05-01 12:34:56.0000025", "2024-05-01T12:34:56.000002")]
    [InlineData("2024-05-01T12:34:56.5", "2024-05-01T12:34:56.500000")]
    [InlineData("0001-01-01 00:00:00", "-infinity")]
    [InlineData("9999-12-31 23:59:59.9999999", "infinity")]
    [InlineData("9999-12-31 23:59:59.9999994", "9999-12-31T23:59:59.999999")]
    public void Timestamp_SqliteText_RoundsLikePostgreSql(string stored, string expected)
    {
        Assert.Equal(expected, ValueCanonicalizer.Timestamp(stored));
    }

    [Fact]
    public void Timestamp_PostgreSqlValue_MatchesSqliteText()
    {
        var fromPostgreSql = new DateTime(2024, 5, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234570);

        Assert.Equal(ValueCanonicalizer.Timestamp("2024-05-01 12:34:56.1234567"), ValueCanonicalizer.Timestamp(fromPostgreSql));
        Assert.Equal("-infinity", ValueCanonicalizer.Timestamp(DateTime.MinValue));
        Assert.Equal("infinity", ValueCanonicalizer.Timestamp(DateTime.MaxValue));
    }

    [Fact]
    public void Canonicalize_ValuesFromBothDatabases_AreEqual()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        (string Table, string Column, object Sqlite, object PostgreSql)[] pairs =
        [
            ("BaseItems", "Id", id.ToString("D").ToUpperInvariant(), id),
            ("BaseItems", "IsFolder", 1L, true),
            ("BaseItems", "IsFolder", 0L, false),
            // SQLite keeps a float column as a double.
            ("BaseItems", "CommunityRating", 0.30000001192092896d, 0.3f),
            ("BaseItems", "CommunityRating", -0.0d, 0f),
            ("BaseItems", "RunTimeTicks", 123456789012L, 123456789012L),
            ("BaseItems", "ExtraType", 2L, 2),
            ("Users", "RowVersion", 4000000000L, 4000000000L),
            ("KeyframeData", "KeyframeTicks", "[0,1000,2000]", new long[] { 0, 1000, 2000 }),
            ("KeyframeData", "KeyframeTicks", "[]", Array.Empty<long>()),
            ("BaseItems", "Name", "Élodie \t\n\\ \"quoted\" \U0001F3AC", "Élodie \t\n\\ \"quoted\" \U0001F3AC"),
        ];

        Assert.All(pairs, pair =>
        {
            var column = Column(pair.Table, pair.Column);
            Assert.Equal(ValueCanonicalizer.Canonicalize(column, pair.Sqlite), ValueCanonicalizer.Canonicalize(column, pair.PostgreSql));
        });
    }

    [Fact]
    public void Canonicalize_Null_IsDifferentFromEveryValue()
    {
        var name = Column("BaseItems", "Name");

        Assert.Equal(ValueCanonicalizer.Null, ValueCanonicalizer.Canonicalize(name, null));
        Assert.Equal(ValueCanonicalizer.Null, ValueCanonicalizer.Canonicalize(name, DBNull.Value));
        Assert.NotEqual(ValueCanonicalizer.Null, ValueCanonicalizer.Canonicalize(name, string.Empty));
        Assert.NotEqual(ValueCanonicalizer.Null, ValueCanonicalizer.Canonicalize(Column("KeyframeData", "KeyframeTicks"), "[]"));
    }

    [Fact]
    public void Canonicalize_EveryColumnTypeOfTheModel_IsSupported()
    {
        var samples = new Dictionary<Type, object>
        {
            [typeof(bool)] = true,
            [typeof(int)] = 1,
            [typeof(long)] = 1L,
            [typeof(uint)] = 1L,
            [typeof(float)] = 1f,
            [typeof(double)] = 1d,
            [typeof(Guid)] = Guid.NewGuid(),
            [typeof(DateTime)] = DateTime.UtcNow,
            [typeof(string)] = "text",
            [typeof(byte[])] = new byte[] { 0, 1 },
        };

        foreach (var column in _model.Tables.SelectMany(t => t.Columns))
        {
            var type = column.ClrType.IsEnum ? typeof(int) : column.ClrType;
            var sample = column.IsArray ? new long[] { 1 } : samples[type];
            Assert.NotEqual(ValueCanonicalizer.Null, ValueCanonicalizer.Canonicalize(column, sample));
        }
    }

    [Fact]
    public void TableContentHash_DoesNotDependOnRowOrder()
    {
        var columns = _model.GetTable("ActivityLogs").Columns;
        object?[] Row(int id) => columns.Select(c => c.Name switch
        {
            "Id" => (object?)(long)id,
            "Name" => "entry " + id,
            _ => null
        }).ToArray();

        var forward = new TableContentHash();
        var backward = new TableContentHash();
        for (var i = 0; i < 5; i++)
        {
            forward.AddRow(columns, Row(i));
            backward.AddRow(columns, Row(4 - i));
        }

        var changed = new TableContentHash();
        for (var i = 0; i < 5; i++)
        {
            changed.AddRow(columns, Row(i == 2 ? 7 : i));
        }

        Assert.Equal(forward.Value, backward.Value);
        Assert.NotEqual(forward.Value, changed.Value);
        Assert.StartsWith("5:", forward.Value, StringComparison.Ordinal);
    }

    private static ImportColumn Column(string table, string column) => _model.GetTable(table).Columns.Single(c => c.Name == column);
}
