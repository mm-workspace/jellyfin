using Jellyfin.Database.Providers.PostgreSQL.ValueConverters;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

public class DatabaseTextConverterTests
{
    [Fact]
    public void MakeStorable_ReturnsWhatSqliteStores()
    {
        // Lone surrogates cannot go through attribute arguments without being replaced by the compiler.
        (string Value, string Expected)[] cases =
        [
            ("a\0b\0", "ab"),
            ("pair 🎬 kept", "pair 🎬 kept"),
            ("lone high \uD83C end", "lone high \uFFFD end"),
            ("lone low \uDFAC end", "lone low \uFFFD end"),
            ("reversed \uDFAC\uD83C", "reversed \uFFFD\uFFFD"),
            ("trailing \uD83C", "trailing \uFFFD")
        ];

        Assert.All(cases, c => Assert.Equal(c.Expected, DatabaseTextConverter.MakeStorable(c.Value)));
    }

    [Fact]
    public void MakeStorable_NothingToChange_ReturnsTheSameInstance()
    {
        const string Value = "nothing to change";

        Assert.Same(Value, DatabaseTextConverter.MakeStorable(Value));
    }
}
