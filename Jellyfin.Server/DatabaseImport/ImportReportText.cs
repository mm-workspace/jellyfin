using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Server.Implementations.DatabaseImport;

namespace Jellyfin.Server.DatabaseImport;

/// <summary>
/// Formats an import report for people.
/// </summary>
internal static class ImportReportText
{
    /// <summary>
    /// Formats a report.
    /// </summary>
    /// <param name="report">The report.</param>
    /// <returns>The text.</returns>
    public static string Format(ImportReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Jellyfin PostgreSQL import: {report.Step}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Server {report.ServerVersion}, started {report.StartedUtc:u}, completed {report.CompletedUtc:u}");
        text.AppendLine(report.Succeeded ? "Result: passed" : "Result: failed");
        foreach (var group in report.Findings.GroupBy(f => f.Severity).OrderByDescending(g => g.Key))
        {
            text.AppendLine().AppendLine(group.Key == ImportFindingSeverity.Error ? "Errors:" : "Warnings:");
            foreach (var finding in group)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {Describe(finding)}");
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Describes one finding on a single line.
    /// </summary>
    /// <param name="finding">The finding.</param>
    /// <returns>The description.</returns>
    public static string Describe(ImportFinding finding)
    {
        var location = string.Join(' ', new[] { finding.Table is null ? null : finding.Column is null ? finding.Table : $"{finding.Table}.{finding.Column}", finding.Index }.Where(p => p is not null));
        var keys = finding.PrimaryKeySamples.Count == 0 ? string.Empty : $" (keys: {string.Join(", ", finding.PrimaryKeySamples)})";
        return string.Create(CultureInfo.InvariantCulture, $"{finding.Check} {location} x{finding.Count}{keys}").Replace("  ", " ", System.StringComparison.Ordinal);
    }
}
