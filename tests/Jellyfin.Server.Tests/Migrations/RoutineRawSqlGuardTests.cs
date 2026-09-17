using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Jellyfin.Server.Migrations;
using Jellyfin.Server.Migrations.Routines;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

/// <summary>
/// Migration routines run on every database provider, so SQL written for SQLite must only run on SQLite.
/// </summary>
public class RoutineRawSqlGuardTests
{
    private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<ushort, OpCode> _opCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => (ushort)o.Value);

    [Fact]
    public void Routines_RunningRawSql_CheckForSqlite()
    {
        var routines = typeof(JellyfinMigrationAttribute).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<JellyfinMigrationAttribute>() is not null)
            .ToList();
        var rawSqlRoutines = routines.Where(t => CalledMethods(t).Any(IsRawSql)).ToList();

        // Without the known raw SQL users the scan would check nothing.
        Assert.Contains(typeof(StripEmbeddedLinkedChildren), rawSqlRoutines);
        Assert.Contains(typeof(MigrateActivityLogDb), rawSqlRoutines);

        var unguarded = rawSqlRoutines.Where(t => !CalledMethods(t).Any(IsSqliteCheck)).Select(t => t.Name);
        Assert.Empty(unguarded);
    }

    private static bool IsRawSql(MethodBase method)
    {
        return (method.DeclaringType == typeof(RelationalDatabaseFacadeExtensions)
                && (method.Name.StartsWith("ExecuteSql", StringComparison.Ordinal)
                    || method.Name.StartsWith("SqlQuery", StringComparison.Ordinal)
                    || method.Name.Equals("GetDbConnection", StringComparison.Ordinal)))
            || (method.DeclaringType == typeof(RelationalQueryableExtensions) && method.Name.StartsWith("FromSql", StringComparison.Ordinal));
    }

    private static bool IsSqliteCheck(MethodBase method)
        => method.DeclaringType == typeof(SqliteDatabaseFacadeExtensions) && method.Name.Equals("IsSqlite", StringComparison.Ordinal);

    private static IEnumerable<MethodBase> CalledMethods(Type type)
    {
        // Lambdas, local functions and async state machines are compiled into the type or its nested types.
        var methods = type.GetMethods(AllDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AllDeclared));
        return methods.SelectMany(CalledMethods).Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(CalledMethods));
    }

    private static List<MethodBase> CalledMethods(MethodBase method)
    {
        var called = new List<MethodBase>();
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return called;
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaringType ? declaringType.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            ushort value = il[position++];
            if (value == 0xFE)
            {
                value = (ushort)(0xFE00 | il[position++]);
            }

            switch (_opCodes[value].OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    position += 8;
                    break;
                case OperandType.InlineSwitch:
                    position += 4 + (4 * BitConverter.ToInt32(il, position));
                    break;
                case OperandType.InlineMethod:
                    called.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, position), typeArguments, methodArguments)!);
                    position += 4;
                    break;
                default:
                    position += 4;
                    break;
            }
        }

        return called;
    }
}
