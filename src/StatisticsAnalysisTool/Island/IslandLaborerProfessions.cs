using StatisticsAnalysisTool.Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StatisticsAnalysisTool.Island;

// Maps the game's internal laborer token (WOOD, HUNTER, HIDE, ...) to the real profession
// (Lumberjack, Fletcher, Gamekeeper, ...). The tokens are deliberately misleading in the game data
// (HUNTER => Fletcher, HIDE => Gamekeeper, WOOD => Lumberjack), so the profession name is never
// hardcoded: it is read from the laborer-contract item's localized name
// (T{tier}_LABOURER_CONTRACT_{token} => "<Tier> <Profession> Contract"). Resolution is built lazily
// from the loaded item data and cached. The token stays the stable, language-independent key used by
// detection/persistence/slot-matching; the profession is a display concern only.
public static class IslandLaborerProfessions
{
    private const string ContractInfix = "_LABOURER_CONTRACT_";

    private static readonly object _buildLock = new();
    private static Dictionary<string, string> _professionByToken;   // WOOD -> Lumberjack
    private static Dictionary<string, string> _tokenByProfession;   // LUMBERJACK -> WOOD

    public static string GetProfession(string tokenOrValue)
    {
        var token = NormalizeToken(tokenOrValue);
        if (string.IsNullOrEmpty(token))
        {
            return string.Empty;
        }

        EnsureBuilt();
        if (_professionByToken.TryGetValue(token, out var profession) && !string.IsNullOrEmpty(profession))
        {
            return profession;
        }

        // Item data may not have been loaded when the cache was first built — resolve on demand.
        var resolved = ResolveProfessionFromContract(token);
        if (!string.IsNullOrEmpty(resolved))
        {
            lock (_buildLock)
            {
                _professionByToken[token] = resolved;
                _tokenByProfession[NormalizeToken(resolved)] = token;
            }
            return resolved;
        }

        // Never display blank: fall back to a readable form of the raw token.
        return ToReadable(token);
    }

    public static string GetToken(string professionOrToken)
    {
        var key = NormalizeToken(professionOrToken);
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        EnsureBuilt();
        if (_professionByToken.ContainsKey(key))
        {
            return key;
        }

        return _tokenByProfession.TryGetValue(key, out var token) ? token : key;
    }

    public static IReadOnlyList<string> AllProfessions()
    {
        EnsureBuilt();
        return _professionByToken.Values
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Strip journal suffixes and normalize casing so a building token (WOOD) and a journal token
    // (WOOD_FULL / WOOD_EMPTY) resolve to the same key.
    public static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var token = value.Trim().ToUpperInvariant().Replace(' ', '_').Replace('-', '_');
        foreach (var suffix in new[] { "_FULL", "_EMPTY" })
        {
            if (token.EndsWith(suffix, StringComparison.Ordinal))
            {
                token = token[..^suffix.Length];
            }
        }

        return token;
    }

    private static void EnsureBuilt()
    {
        if (_professionByToken != null)
        {
            return;
        }

        lock (_buildLock)
        {
            if (_professionByToken != null)
            {
                return;
            }

            var byToken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byProfession = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in ItemController.Items)
            {
                var uniqueName = item?.UniqueName;
                if (string.IsNullOrEmpty(uniqueName))
                {
                    continue;
                }

                var idx = uniqueName.IndexOf(ContractInfix, StringComparison.Ordinal);
                if (idx < 0)
                {
                    continue;
                }

                var token = uniqueName[(idx + ContractInfix.Length)..];
                if (string.IsNullOrEmpty(token) || byToken.ContainsKey(token))
                {
                    continue;
                }

                var profession = ExtractProfession(item.LocalizedNames?.EnUs);
                if (string.IsNullOrEmpty(profession))
                {
                    continue;
                }

                byToken[token] = profession;
                byProfession[NormalizeToken(profession)] = token;
            }

            _professionByToken = byToken;
            _tokenByProfession = byProfession;
        }
    }

    private static string ResolveProfessionFromContract(string token)
    {
        foreach (var tier in new[] { 4, 5, 6, 7, 8, 3, 2 })
        {
            var item = ItemController.GetItemByUniqueName($"T{tier}_LABOURER_CONTRACT_{token}");
            var profession = ExtractProfession(item?.LocalizedNames?.EnUs);
            if (!string.IsNullOrEmpty(profession))
            {
                return profession;
            }
        }

        return string.Empty;
    }

    // "Adept Lumberjack Contract" -> "Lumberjack" (profession is the word preceding "Contract").
    private static string ExtractProfession(string contractEnUsName)
    {
        if (string.IsNullOrWhiteSpace(contractEnUsName))
        {
            return string.Empty;
        }

        var words = contractEnUsName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var contractIndex = Array.FindLastIndex(words, w => w.Equals("Contract", StringComparison.OrdinalIgnoreCase));
        return contractIndex <= 0 ? string.Empty : words[contractIndex - 1];
    }

    private static string ToReadable(string token)
        => string.Join(' ', token.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
}
