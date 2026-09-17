using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Kept for tests that still derive from the old name. New tests derive from <see cref="DbTestFixture"/>.
/// </summary>
public abstract class SqliteDbTestFixture : DbTestFixture
{
    protected SqliteDbTestFixture(params IInterceptor[] interceptors)
        : base(interceptors)
    {
    }
}
