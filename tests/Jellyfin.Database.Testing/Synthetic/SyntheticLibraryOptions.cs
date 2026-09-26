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
    /// Gets a small library (1 000 items) for the unit and end-to-end tests.
    /// </summary>
    public static SyntheticLibraryOptions Small { get; } = new(1_000, 3);

    /// <summary>
    /// Gets the small library with extreme values.
    /// </summary>
    public static SyntheticLibraryOptions SmallEdge { get; } = new(1_000, 3, EdgeValues: true);

    /// <summary>
    /// Gets a large library (100 000 items) for performance runs.
    /// </summary>
    public static SyntheticLibraryOptions Large { get; } = new(100_000, 5);
}
