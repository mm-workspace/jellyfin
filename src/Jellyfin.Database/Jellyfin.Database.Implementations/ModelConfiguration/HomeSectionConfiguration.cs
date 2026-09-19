using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// FluentAPI configuration for the HomeSection entity.
/// </summary>
public class HomeSectionConfiguration : IEntityTypeConfiguration<HomeSection>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<HomeSection> builder)
    {
        // The table is named after the entity type because the context had no set for it. With the set, EF would
        // name it HomeSections instead.
        builder.ToTable("HomeSection");
    }
}
