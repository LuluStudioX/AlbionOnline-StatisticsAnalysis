using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StatisticsAnalysisTool.Island;

internal static class LaborerConfigHelper
{
    public const string PlotGuidKey = "PlotGuid";
    public const string NoneValue = "None";

    public static string LaborerKey(int slot) => $"Laborer{slot}";
    public static string LaborerNameKey(int slot) => $"LaborerName{slot}";
    public static string JournalKey(int slot) => $"Journal{slot}";
    public static string JournalTierKey(int slot) => $"JournalTier{slot}";
    public static string DispatchTimeKey(int slot) => $"DispatchTime{slot}";
    public static string LootReadyKey(int slot)    => $"LootReady{slot}";
    public const string PlotPlantedAtKey = "PlantedAt";

    public static string FormatUtc(DateTime utc)
        => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static bool TryParseUtc(string value, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out result);
    }

    public static Dictionary<string, string> ParseConfiguration(string configuration)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return result;
        }

        foreach (var rawLine in configuration.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.Contains(':'))
            {
                continue;
            }

            var split = line.Split(new[] { ':' }, 2);
            if (split.Length != 2)
            {
                continue;
            }

            var key = split[0].Trim();
            var value = split[1].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = value;
            }
        }

        return result;
    }

    public static string BuildConfiguration(Dictionary<string, string> config)
    {
        var lines = new List<string>();

        for (var slot = 1; slot <= 3; slot++)
        {
            AddIfPresent(lines, config, LaborerKey(slot));
            AddIfPresent(lines, config, JournalKey(slot));
            AddIfPresent(lines, config, JournalTierKey(slot));
        }

        foreach (var kv in config.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (lines.Any(l => l.StartsWith($"{kv.Key}:", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            lines.Add($"{kv.Key}: {kv.Value}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string NormalizeLaborerType(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim()
                   .Replace(" ", "_", StringComparison.OrdinalIgnoreCase)
                   .Replace("-", "_", StringComparison.OrdinalIgnoreCase)
                   .ToUpperInvariant();
    }

    public static string NormalizeLaborerFullName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var collapsed = string.Join(' ', name.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(collapsed.ToLowerInvariant());
    }

    public static string ToDisplayLaborerType(string value)
    {
        var normalized = NormalizeLaborerType(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Unknown";
        }

        return normalized switch
        {
            "MERCENARY" => "Mercenary",
            "MAGE" => "Mage",
            "IMBUER" => "Imbuer",
            "HUNTER" => "Hunter",
            "GAMEKEEPER" => "Gamekeeper",
            "BLACKSMITH" => "Blacksmith",
            "PROSPECTOR" => "Prospector",
            "CROPPER" => "Cropper",
            "FLETCHER" => "Fletcher",
            "TINKER" => "Tinker",
            "LUMBERJACK" => "Lumberjack",
            "STONECUTTER" => "Stonecutter",
            "FISHERMAN" => "Fisherman",
            _ => string.Join(' ', normalized.Split('_')
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()))
        };
    }

    private static readonly IReadOnlyDictionary<string, string> JournalByType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MERCENARY"] = "Mercenary's Journal",
            ["MAGE"] = "Imbuer's Journal",
            ["IMBUER"] = "Imbuer's Journal",
            ["BLACKSMITH"] = "Blacksmith's Journal",
            ["FLETCHER"] = "Fletcher's Journal",
            ["TINKER"] = "Tinker's Journal",
            ["HUNTER"] = "Hunter's Journal",
            ["GAMEKEEPER"] = "Gamekeeper's Journal",
            ["PROSPECTOR"] = "Prospector's Journal",
            ["CROPPER"] = "Cropper's Journal",
            ["LUMBERJACK"] = "Lumberjack's Journal",
            ["STONECUTTER"] = "Stonecutter's Journal",
            ["FISHERMAN"] = "Fisherman's Journal",
        };

    public static string GetJournalName(string rawLaborerType)
    {
        var display = ToDisplayLaborerType(rawLaborerType);
        return GetJournalName(rawLaborerType, display);
    }

    public static string GetJournalName(string rawLaborerType, string displayType)
    {
        if (string.IsNullOrWhiteSpace(rawLaborerType))
        {
            return $"{displayType}'s Journal";
        }

        var norm = NormalizeLaborerType(rawLaborerType);
        return JournalByType.TryGetValue(norm, out var journal) ? journal : $"{displayType}'s Journal";
    }

    private static void AddIfPresent(List<string> lines, Dictionary<string, string> config, string key)
    {
        if (config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{key}: {value}");
        }
    }
}
