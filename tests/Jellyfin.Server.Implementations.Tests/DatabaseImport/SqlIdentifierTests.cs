using Jellyfin.Server.Implementations.DatabaseImport;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport;

public class SqlIdentifierTests
{
    [Theory]
    [InlineData("BaseItems", "\"BaseItems\"")]
    [InlineData("rowid", "\"rowid\"")]
    [InlineData("Plugin \"Thing\"", "\"Plugin \"\"Thing\"\"\"")]
    [InlineData("", "\"\"")]
    public void Quote_Identifier_KeepsItWhole(string identifier, string expected)
    {
        Assert.Equal(expected, SqlIdentifier.Quote(identifier));
    }
}
