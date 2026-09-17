namespace Jellyfin.Database.Testing.Synthetic;

/// <summary>
/// The size and content of a synthetic library.
/// </summary>
/// <param name="Items">The number of library items.</param>
/// <param name="Users">The number of users.</param>
/// <param name="Seed">The seed of the generated values. The same options give the same rows.</param>
/// <param name="EdgeValues">Whether every table also gets rows holding the extreme values of each column type.</param>
public sealed record SyntheticLibraryOptions(int Items, int Users, int Seed = 1, bool EdgeValues = false)
{
    /// <summary>
    /// Gets the small library used by the gating tests.
    /// </summary>
    public static SyntheticLibraryOptions Small { get; } = new(1_000, 3);

    /// <summary>
    /// Gets the small library with extreme values.
    /// </summary>
    public static SyntheticLibraryOptions SmallEdge { get; } = new(1_000, 3, EdgeValues: true);

    /// <summary>
    /// Gets the large library used by the nightly runs.
    /// </summary>
    public static SyntheticLibraryOptions Large { get; } = new(100_000, 5);
}
