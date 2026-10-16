namespace ClaudeUsage.Services;

// Readers tolerate a writer, deletion during reset, a locked file and mid-stream IO failure.
internal static class MonthlyUsageFiles
{
    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsage", "usage-log");

    internal static IEnumerable<string> Between(string directory, DateTimeOffset from, DateTimeOffset to)
    {
        if (to < from) yield break;
        var month = new DateTime(from.UtcDateTime.Year, from.UtcDateTime.Month, 1);
        var last = new DateTime(to.UtcDateTime.Year, to.UtcDateTime.Month, 1);
        while (month <= last)
        {
            yield return Path.Combine(directory, $"{month:yyyy-MM}.jsonl");
            if (month == last) yield break;
            month = month.AddMonths(1);
        }
    }

    internal static string[] All(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "????-??.jsonl")
                .Where(path => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _))
                .Order(StringComparer.Ordinal).ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    internal static IEnumerable<string> Lines(string path)
    {
        StreamReader reader;
        try { reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete)); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        using (reader)
        {
            while (true)
            {
                string? line;
                try { line = reader.ReadLine(); }
                catch (IOException) { yield break; }
                if (line is null) yield break;
                if (!string.IsNullOrWhiteSpace(line)) yield return line;
            }
        }
    }
}
