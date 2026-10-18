namespace JackiVpn.Domain.Activity;

/// <summary>Media Encoder's encoding log: which jobs finished and how (output file name and status only).</summary>
public static class AmeLog
{
    public sealed record Job(string Output, bool Ok);

    /// <summary>
    /// Reads the jobs in a piece of the log. Each job lists its output file ("Output File: …") and ends with
    /// "File Successfully Encoded" or a failure line ("Encoding Failed", "Error"…).
    /// </summary>
    public static IEnumerable<Job> Jobs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? output = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Output File:", StringComparison.OrdinalIgnoreCase))
            {
                output = Path.GetFileName(line["Output File:".Length..].Trim());
            }
            else if (output is not null && line.Contains("Successfully Encoded", StringComparison.OrdinalIgnoreCase))
            {
                yield return new Job(output, true);
                output = null;
            }
            else if (output is not null && (line.Contains("Encoding Failed", StringComparison.OrdinalIgnoreCase) || line.Contains("Export Error", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Error", StringComparison.OrdinalIgnoreCase)))
            {
                yield return new Job(output, false);
                output = null;
            }
        }
    }
}
