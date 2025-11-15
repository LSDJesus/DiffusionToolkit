using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Diffusion.Embeddings;

/// <summary>
/// CLIP BPE (Byte Pair Encoding) tokenizer.
/// Based on OpenAI's CLIP tokenizer implementation.
/// </summary>
public class CLIPTokenizer
{
    private readonly Dictionary<string, int> _encoder; // token -> id
    private readonly Dictionary<int, string> _decoder; // id -> token
    private readonly Dictionary<(string, string), int> _bpeRanks; // BPE merge rules
    private readonly Regex _pattern;
    private readonly Dictionary<string, string> _cache = new();

    // Special tokens
    private const string StartToken = "<|startoftext|>";
    private const string EndToken = "<|endoftext|>";
    private readonly int _startTokenId;
    private readonly int _endTokenId;

    public CLIPTokenizer(string vocabPath, string mergesPath)
    {
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"Vocabulary file not found: {vocabPath}");
        if (!File.Exists(mergesPath))
            throw new FileNotFoundException($"Merges file not found: {mergesPath}");

        // Load vocabulary (token -> id mapping)
        var vocabJson = File.ReadAllText(vocabPath);
        _encoder = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabJson) 
            ?? throw new InvalidOperationException("Failed to load vocabulary");

        // Create reverse mapping (id -> token)
        _decoder = _encoder.ToDictionary(kv => kv.Value, kv => kv.Key);

        // Load BPE merge rules
        _bpeRanks = new Dictionary<(string, string), int>();
        var mergeLines = File.ReadAllLines(mergesPath).Skip(1); // Skip header line
        int rank = 0;
        foreach (var line in mergeLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(' ');
            if (parts.Length == 2)
            {
                _bpeRanks[(parts[0], parts[1])] = rank++;
            }
        }

        // CLIP tokenization pattern: matches words, numbers, and punctuation
        _pattern = new Regex(
            @"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        // Get special token IDs
        _startTokenId = _encoder[StartToken];
        _endTokenId = _encoder[EndToken];
    }

    /// <summary>
    /// Encode text to token IDs with CLIP-style padding and truncation.
    /// </summary>
    /// <param name="text">Input text</param>
    /// <param name="maxLength">Maximum sequence length (default 77 for CLIP)</param>
    /// <returns>Array of token IDs, padded to maxLength</returns>
    public int[] Encode(string text, int maxLength = 77)
    {
        // Initialize with start token
        var tokens = new List<int> { _startTokenId };

        // Tokenize text
        if (!string.IsNullOrWhiteSpace(text))
        {
            var matches = _pattern.Matches(text.ToLower());
            foreach (Match match in matches)
            {
                var token = match.Value;
                var bpeTokens = BPE(token);
                
                foreach (var bpeToken in bpeTokens)
                {
                    if (_encoder.TryGetValue(bpeToken, out int id))
                    {
                        tokens.Add(id);
                        
                        // Stop if we reach max length - 1 (reserve space for end token)
                        if (tokens.Count >= maxLength - 1)
                            break;
                    }
                }
                
                if (tokens.Count >= maxLength - 1)
                    break;
            }
        }

        // Add end token
        tokens.Add(_endTokenId);

        // Pad to max length
        while (tokens.Count < maxLength)
        {
            tokens.Add(0); // Pad token ID is 0
        }

        // Truncate if necessary
        if (tokens.Count > maxLength)
        {
            tokens = tokens.Take(maxLength - 1).ToList();
            tokens.Add(_endTokenId); // Ensure end token
        }

        return tokens.ToArray();
    }

    /// <summary>
    /// Apply Byte Pair Encoding to a token.
    /// </summary>
    private List<string> BPE(string token)
    {
        // Check cache
        if (_cache.TryGetValue(token, out string? cached))
        {
            return cached.Split(' ').ToList();
        }

        // Convert to character list
        var word = token.Select(c => c.ToString()).ToList();
        var pairs = GetPairs(word);

        if (pairs.Count == 0)
        {
            _cache[token] = token;
            return new List<string> { token };
        }

        while (true)
        {
            // Find pair with lowest rank (highest priority merge)
            var bigram = pairs
                .Where(pair => _bpeRanks.ContainsKey(pair))
                .OrderBy(pair => _bpeRanks[pair])
                .FirstOrDefault();

            if (bigram == default)
                break;

            var (first, second) = bigram;
            var newWord = new List<string>();
            int i = 0;

            while (i < word.Count)
            {
                int j = word.IndexOf(first, i);
                if (j == -1)
                {
                    newWord.AddRange(word.Skip(i));
                    break;
                }

                newWord.AddRange(word.Skip(i).Take(j - i));
                i = j;

                if (i < word.Count - 1 && word[i] == first && word[i + 1] == second)
                {
                    newWord.Add(first + second);
                    i += 2;
                }
                else
                {
                    newWord.Add(word[i]);
                    i += 1;
                }
            }

            word = newWord;
            if (word.Count == 1)
                break;

            pairs = GetPairs(word);
        }

        var result = string.Join(" ", word);
        _cache[token] = result;
        return word;
    }

    /// <summary>
    /// Get all adjacent pairs in a word.
    /// </summary>
    private HashSet<(string, string)> GetPairs(List<string> word)
    {
        var pairs = new HashSet<(string, string)>();
        for (int i = 0; i < word.Count - 1; i++)
        {
            pairs.Add((word[i], word[i + 1]));
        }
        return pairs;
    }

    /// <summary>
    /// Decode token IDs back to text.
    /// </summary>
    public string Decode(int[] tokenIds)
    {
        var tokens = tokenIds
            .Where(id => id != 0 && _decoder.ContainsKey(id)) // Skip padding
            .Select(id => _decoder[id])
            .Where(t => t != StartToken && t != EndToken); // Skip special tokens

        return string.Join("", tokens)
            .Replace("</w>", " ")
            .Trim();
    }
}
