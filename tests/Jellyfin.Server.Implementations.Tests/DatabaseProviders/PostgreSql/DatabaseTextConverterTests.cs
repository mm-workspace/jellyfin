using Jellyfin.Database.Providers.PostgreSQL.ValueConverters;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

public class DatabaseTextConverterTests
{
    [Fact]
    public void ConvertToProvider_ReturnsWhatSqliteStores()
    {
        // Lone surrogates cannot go through attribute arguments without being replaced by the compiler.
        (string Value, string Expected)[] cases =
        [
            ("a\0b\0", "ab"),
            ("pair 🎬 kept", "pair 🎬 kept"),
            ("lone high \uD83C end", "lone high � end"),
            ("lone low \uDFAC end", "lone low � end"),
            ("reversed \uDFAC\uD83C", "reversed ��"),
            ("trailing \uD83C", "trailing �")
        ];
        var convert = new DatabaseTextConverter().ConvertToProvider;

        Assert.All(cases, c => Assert.Equal(c.Expected, convert(c.Value)));
    }

    [Fact]
    public void ConvertFromProvider_ReturnsTheStoredText()
    {
        const string Value = "nothing to change";

        Assert.Equal(Value, new DatabaseTextConverter().ConvertFromProvider(Value));
    }
}
