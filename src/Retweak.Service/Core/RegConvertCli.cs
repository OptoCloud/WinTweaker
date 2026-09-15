using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Retweak.Service.Core;

/// <summary>
/// Implements `Retweak.Service.exe --convert-reg a.reg [b.reg ...] [--out file.json]`.
///
/// This only ever prints (or writes) a ready-to-paste Entries snippet; it never touches
/// appsettings.json itself and never applies anything. That is deliberate: the whole
/// config model is "you review what's enforced by reading a file," and an importer that
/// silently merged its own output into the live config would break that.
/// </summary>
public static class RegConvertCli
{
    public static int Run(string[] args)
    {
        var inputPaths = new List<string>();
        string? outPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("--out requires a file path.");
                    return 1;
                }

                outPath = args[++i];
                continue;
            }

            inputPaths.Add(args[i]);
        }

        if (inputPaths.Count == 0)
        {
            Console.Error.WriteLine("Usage: Retweak.Service.exe --convert-reg <file.reg> [more.reg ...] [--out entries.json]");
            return 1;
        }

        var allEntries = new List<object>();
        int skippedCount = 0;

        foreach (string path in inputPaths)
        {
            string content;
            try
            {
                content = ReadRegFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not read '{path}': {ex.Message}");
                return 1;
            }

            RegFileParser.Result result = RegFileParser.Parse(content);
            allEntries.AddRange(result.Entries.Select(BuildEntryJson));

            foreach (RegFileParser.SkippedItem skip in result.Skipped)
            {
                skippedCount++;
                Console.Error.WriteLine($"warning: {path}:{skip.Line}: {skip.Text} — {skip.Reason}");
            }
        }

        var document = new Dictionary<string, object>
        {
            ["Retweak"] = new Dictionary<string, object>
            {
                ["Entries"] = allEntries,
            },
        };

        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });

        if (outPath is not null)
        {
            File.WriteAllText(outPath, json);
        }
        else
        {
            Console.Out.WriteLine(json);
        }

        Console.Error.WriteLine(
            $"Converted {allEntries.Count} entr{(allEntries.Count == 1 ? "y" : "ies")}, skipped {skippedCount}. " +
            "Review before merging into appsettings.json — nothing has been applied.");

        return 0;
    }

    /// <summary>
    /// A BOM declares its own encoding, which <see cref="File.ReadAllText(string)"/> already
    /// sniffs correctly. Without one, this is very likely a legacy "REGEDIT4" export, which
    /// uses the system ANSI code page rather than UTF-8; decoding that as UTF-8 corrupts
    /// every non-ASCII byte into U+FFFD. Latin-1 maps each byte onto the matching code
    /// point 1:1, so it does not always reproduce the original ANSI code page's characters
    /// above 0x7F, but unlike UTF-8 it never corrupts them beyond recovery.
    /// </summary>
    private static string ReadRegFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool hasBom =
            (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) ||
            (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) ||
            (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        return hasBom ? File.ReadAllText(path) : Encoding.Latin1.GetString(bytes);
    }

    private static object BuildEntryJson(Configuration.BaselineEntry e)
    {
        var dict = new Dictionary<string, object?>
        {
            ["Scope"] = e.Scope,
            ["Path"] = e.Path,
            ["Name"] = e.Name,
        };

        if (e.EnsureAbsent)
        {
            dict["EnsureAbsent"] = true;
        }
        else
        {
            dict["Kind"] = e.Kind;
            if (e.ResolvedKind == RegistryValueKind.MultiString)
            {
                dict["Values"] = e.Values;
            }
            else
            {
                dict["Value"] = e.Value;
            }
        }

        dict["Watch"] = true;
        return dict;
    }
}
