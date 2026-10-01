using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// The outcome of one import step.
/// </summary>
/// <param name="FormatVersion">The version of this format.</param>
/// <param name="Step">The step.</param>
/// <param name="ServerVersion">The version of the server that ran the step.</param>
/// <param name="StartedUtc">When the step started.</param>
/// <param name="CompletedUtc">When the step completed.</param>
/// <param name="Findings">The findings, errors first.</param>
internal sealed record ImportReport(
    int FormatVersion,
    ImportStep Step,
    string ServerVersion,
    DateTime StartedUtc,
    DateTime CompletedUtc,
    IReadOnlyList<ImportFinding> Findings)
{
    /// <summary>
    /// The format version this server writes.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// Gets a value indicating whether the step found no errors.
    /// </summary>
    [JsonIgnore]
    public bool Succeeded => Findings.All(f => f.Severity != ImportFindingSeverity.Error);
}
