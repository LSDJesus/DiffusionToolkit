using Diffusion.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Diffusion.Scanner;

/// <summary>
/// Interface for embedding registry (implemented by PostgreSQL data store)
/// Defined in Diffusion.Common to avoid circular dependencies
/// </summary>
// NOTE: Use IEmbeddingRegistry from Diffusion.Common namespace

/// <summary>
/// Embedding extraction and matching utility
/// Handles explicit embeddings (embedding:name:weight) and implicit matching against known embeddings
/// </summary>
public class EmbeddingExtractor
{
    private readonly IEmbeddingRegistry _registry;
    private List<string> _personalEmbeddingNames = new();
    private DateTime _registrySyncTime = DateTime.MinValue;
    private const int REGISTRY_CACHE_MINUTES = 60;

    public EmbeddingExtractor(IEmbeddingRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Extract embeddings from prompt text
    /// Returns: list of (name, weight, isImplicit) tuples
    /// </summary>
    public async Task<List<(string Name, decimal Weight, bool IsImplicit)>> ExtractEmbeddingsAsync(
        string prompt,
        string negativePrompt = null)
    {
        if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(negativePrompt))
            return new List<(string, decimal, bool)>();

        var explicitEmbeddings = ExtractExplicitEmbeddings(prompt);
        explicitEmbeddings.AddRange(ExtractExplicitEmbeddings(negativePrompt));

        // Ensure registry is fresh
        await EnsureRegistryFreshAsync();

        // Add implicit embeddings (matched against personal embeddings)
        var implicitEmbeddings = await ExtractImplicitEmbeddingsAsync(prompt);
        implicitEmbeddings.AddRange(await ExtractImplicitEmbeddingsAsync(negativePrompt));

        // Combine and deduplicate (explicit takes priority over implicit)
        var result = new Dictionary<string, (decimal Weight, bool IsImplicit)>();

        // Add implicit first
        foreach (var (name, weight, isImplicit) in implicitEmbeddings)
        {
            var key = name.ToLowerInvariant();
            if (!result.ContainsKey(key))
            {
                result[key] = (weight, isImplicit);
            }
        }

        // Add explicit (overrides implicit)
        foreach (var (name, weight, isImplicit) in explicitEmbeddings)
        {
            var key = name.ToLowerInvariant();
            result[key] = (weight, false); // Explicit always takes priority
        }

        return result.Select(kvp => (kvp.Key, kvp.Value.Weight, kvp.Value.IsImplicit)).ToList();
    }

    /// <summary>
    /// Extract explicit embeddings: embedding:name:weight or embedding:name
    /// </summary>
    private List<(string Name, decimal Weight, bool IsImplicit)> ExtractExplicitEmbeddings(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<(string, decimal, bool)>();

        var embeddings = new List<(string, decimal, bool)>();
        var pattern = @"embedding:([a-zA-Z0-9_\-\.]+)(?::([0-9.]+))?";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);

        foreach (Match match in regex.Matches(text))
        {
            var name = match.Groups[1].Value;
            var weight = match.Groups[2].Success
                ? decimal.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 1.0m;

            embeddings.Add((name, weight, false)); // false = explicit
        }

        return embeddings;
    }

    /// <summary>
    /// Extract implicit embeddings by matching against personal embedding registry
    /// Uses word-boundary matching to avoid false positives
    /// </summary>
    private async Task<List<(string Name, decimal Weight, bool IsImplicit)>> ExtractImplicitEmbeddingsAsync(
        string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _personalEmbeddingNames.Count == 0)
            return new List<(string, decimal, bool)>();

        var embeddings = new List<(string, decimal, bool)>();

        // Create word boundaries pattern for each embedding name
        // Only match if surrounded by whitespace, punctuation, or end of string
        foreach (var embeddingName in _personalEmbeddingNames)
        {
            // Escape special regex characters in embedding name
            var escapedName = Regex.Escape(embeddingName);
            var pattern = $@"\b{escapedName}\b";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);

            if (regex.IsMatch(text))
            {
                embeddings.Add((embeddingName, 1.0m, true)); // true = implicit
            }
        }

        return embeddings;
    }

    /// <summary>
    /// Ensure registry cache is fresh
    /// </summary>
    private async Task EnsureRegistryFreshAsync()
    {
        var now = DateTime.UtcNow;
        if ((now - _registrySyncTime).TotalMinutes < REGISTRY_CACHE_MINUTES)
            return; // Cache still fresh

        try
        {
            _personalEmbeddingNames = await _registry.GetPersonalEmbeddingNamesAsync();
            _registrySyncTime = now;

            Logger.Log($"EmbeddingExtractor: Synced {_personalEmbeddingNames.Count} personal embeddings");
        }
        catch (Exception ex)
        {
            Logger.Log($"EmbeddingExtractor: Failed to sync registry: {ex.Message}");
            // Fall back to cached list if available
        }
    }

    /// <summary>
    /// Clean prompt by removing embedding tags (optional, for cleaner storage)
    /// </summary>
    public string RemoveEmbeddingTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Remove explicit embedding:name:weight and embedding:name patterns
        var pattern = @"\s*embedding:[a-zA-Z0-9_\-\.]+(?::[0-9.]+)?\s*";
        var cleaned = Regex.Replace(text, pattern, " ", RegexOptions.IgnoreCase);

        // Clean up extra spaces
        cleaned = Regex.Replace(cleaned, @"\s+", " ");

        return cleaned.Trim();
    }
}
