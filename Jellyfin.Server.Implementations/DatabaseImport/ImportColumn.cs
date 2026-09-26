using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// A column of the import model.
/// </summary>
/// <param name="Name">The column name.</param>
/// <param name="ClrType">The CLR type of the property, without nullability.</param>
/// <param name="ProviderClrType">The CLR type the provider stores, after value conversion, without nullability.</param>
/// <param name="StoreType">The store type.</param>
/// <param name="IsNullable">Whether the column accepts NULL.</param>
/// <param name="MaxLength">The maximum length, if the model declares one.</param>
/// <param name="IsArray">Whether the column stores an array.</param>
internal sealed record ImportColumn(string Name, Type ClrType, Type ProviderClrType, string StoreType, bool IsNullable, int? MaxLength, bool IsArray)
{
    /// <summary>
    /// Creates the description of a relational column.
    /// </summary>
    /// <param name="column">The column.</param>
    /// <returns>The description.</returns>
    public static ImportColumn Create(IColumn column)
    {
        var property = column.PropertyMappings[0].Property;
        var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        var providerType = column.PropertyMappings[0].TypeMapping.Converter?.ProviderClrType ?? property.ClrType;
        providerType = Nullable.GetUnderlyingType(providerType) ?? providerType;
        return new ImportColumn(column.Name, clrType, providerType, column.StoreType, column.IsNullable, property.GetMaxLength(), column.StoreType.EndsWith("[]", StringComparison.Ordinal));
    }
}
