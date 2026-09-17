using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// A foreign key of the import model.
/// </summary>
/// <param name="Name">The constraint name.</param>
/// <param name="Columns">The dependent columns.</param>
/// <param name="PrincipalTable">The principal table.</param>
/// <param name="PrincipalColumns">The principal columns.</param>
/// <param name="OnDelete">The delete behaviour.</param>
internal sealed record ImportForeignKey(string Name, IReadOnlyList<string> Columns, string PrincipalTable, IReadOnlyList<string> PrincipalColumns, ReferentialAction OnDelete);
