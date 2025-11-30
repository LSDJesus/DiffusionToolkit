using Diffusion.Database.PostgreSQL;
using System.Text.RegularExpressions;

namespace TestPostgreSQL;

/// <summary>
/// Test duplicate detection with real ORIG/FINAL image pairs
/// </summary>
public static class DuplicateDetectionTest
{
    private const string TestImagesPath = @"D:\AI\Github_Desktop\DiffusionToolkit\_test_images\anillustrious_v5Vae";
    private const string ConnectionString = "Host=localhost;Port=5436;Database=diffusion_images;Username=diffusion;Password=diffusion_toolkit_secure_2025";

    public static async Task RunAsync()
    {
        Console.WriteLine("\n=== Duplicate Detection Test with ORIG/FINAL Pairs ===\n");

        // Step 1: Analyze test images on disk
        Console.WriteLine("Step 1: Analyzing test images on disk");
        Console.WriteLine("─────────────────────────────────────");
        
        if (!Directory.Exists(TestImagesPath))
        {
            Console.WriteLine($"❌ Test images folder not found: {TestImagesPath}");
            return;
        }
        
        var files = Directory.GetFiles(TestImagesPath, "*.png");
        var origFiles = files.Where(f => f.Contains("_ORIG", StringComparison.OrdinalIgnoreCase)).ToList();
        var finalFiles = files.Where(f => f.Contains("_FINAL", StringComparison.OrdinalIgnoreCase)).ToList();
        
        Console.WriteLine($"Total PNG files: {files.Length}");
        Console.WriteLine($"ORIG files: {origFiles.Count}");
        Console.WriteLine($"FINAL files: {finalFiles.Count}");
        
        // Analyze file sizes
        var origSizes = origFiles.Select(f => new FileInfo(f).Length).ToList();
        var finalSizes = finalFiles.Select(f => new FileInfo(f).Length).ToList();
        
        if (origSizes.Count > 0 && finalSizes.Count > 0)
        {
            Console.WriteLine($"\nFile size analysis:");
            Console.WriteLine($"  ORIG average:  {origSizes.Average() / 1024:F0} KB");
            Console.WriteLine($"  FINAL average: {finalSizes.Average() / 1024:F0} KB");
            Console.WriteLine($"  Size ratio:    {finalSizes.Average() / origSizes.Average():F2}x");
        }
        
        // Find matching pairs by timestamp proximity
        Console.WriteLine($"\nMatching ORIG→FINAL pairs by timestamp:");
        var pairs = FindPairsByTimestamp(origFiles, finalFiles);
        Console.WriteLine($"  Found {pairs.Count} timestamp-matched pairs");
        
        if (pairs.Count > 0)
        {
            var sample = pairs.First();
            Console.WriteLine($"\n  Sample pair:");
            Console.WriteLine($"    ORIG:  {Path.GetFileName(sample.orig)} ({new FileInfo(sample.orig).Length / 1024} KB)");
            Console.WriteLine($"    FINAL: {Path.GetFileName(sample.final)} ({new FileInfo(sample.final).Length / 1024} KB)");
        }
        Console.WriteLine();

        // Step 2: Connect to database and test detection
        Console.WriteLine("Step 2: Connecting to PostgreSQL database");
        Console.WriteLine("─────────────────────────────────────────");
        
        PostgreSQLDataStore dataStore;
        try
        {
            dataStore = new PostgreSQLDataStore(ConnectionString);
            await dataStore.Create(() => new object(), _ => { });
            Console.WriteLine("✓ Connected to PostgreSQL\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to connect: {ex.Message}");
            Console.WriteLine("  Make sure Docker is running with: docker-compose up -d");
            return;
        }

        // Step 3: Get duplicate detection stats for entire database
        Console.WriteLine("Step 3: Running duplicate detection on database");
        Console.WriteLine("───────────────────────────────────────────────");

        var stats = await dataStore.GetDuplicateDetectionStatsAsync();
        Console.WriteLine($"Database statistics:");
        Console.WriteLine($"  Total images:      {stats.TotalImages}");
        Console.WriteLine($"  ORIG images:       {stats.OriginalCount}");
        Console.WriteLine($"  FINAL images:      {stats.FinalCount}");
        Console.WriteLine($"  Unique generations:{stats.UniqueGenerations}");
        Console.WriteLine($"  Duplicate groups:  {stats.DuplicateGroups}");
        Console.WriteLine($"  With embeddings:   {stats.ImagesWithEmbeddings}");
        Console.WriteLine();

        // Find ORIG/FINAL pairs by filename
        Console.WriteLine("Finding ORIG/FINAL pairs by filename pattern...");
        var dbPairs = await dataStore.FindOrigFinalPairsByFilenameAsync(limit: 10);
        
        if (dbPairs.Count > 0)
        {
            Console.WriteLine($"✓ Found {dbPairs.Count} ORIG/FINAL pairs in database:");
            foreach (var pair in dbPairs.Take(5))
            {
                Console.WriteLine($"\n  Seed {pair.Seed}:");
                Console.WriteLine($"    ORIG:  {pair.OriginalWidth}×{pair.OriginalHeight} ({pair.OriginalFileSize / 1024} KB)");
                Console.WriteLine($"    FINAL: {pair.FinalWidth}×{pair.FinalHeight} ({pair.FinalFileSize / 1024} KB)");
                Console.WriteLine($"    Upscale: {pair.UpscaleFactor:F2}x");
                Console.WriteLine($"    Model: {pair.Model}");
            }
            if (dbPairs.Count > 5)
                Console.WriteLine($"\n  ... and {dbPairs.Count - 5} more pairs");
        }
        else
        {
            Console.WriteLine("  No ORIG/FINAL pairs found in database by filename");
            Console.WriteLine("  (Test images may not be scanned into database yet)");
        }
        Console.WriteLine();

        // Find workflow variant groups
        Console.WriteLine("Finding workflow variant groups (seed+model+prompt)...");
        var groups = await dataStore.FindWorkflowVariantGroupsAsync(limit: 10);
        
        if (groups.Count > 0)
        {
            Console.WriteLine($"✓ Found {groups.Count} variant groups:");
            foreach (var group in groups.Take(5))
            {
                var resMin = (int)Math.Sqrt(group.MinResolution);
                var resMax = (int)Math.Sqrt(group.MaxResolution);
                Console.WriteLine($"\n  Seed {group.Seed}: {group.VariantCount} variants");
                Console.WriteLine($"    Model: {group.Model}");
                Console.WriteLine($"    Resolution: ~{resMin}px → ~{resMax}px ({group.UpscaleFactor:F2}x)");
                if (group.IsLikelyOrigFinalPair)
                    Console.WriteLine($"    ⚡ Detected as ORIG/FINAL pair!");
            }
            if (groups.Count > 5)
                Console.WriteLine($"\n  ... and {groups.Count - 5} more groups");
        }
        else
        {
            Console.WriteLine("  No variant groups found");
        }
        Console.WriteLine();

        // Step 4: Dry run deletion
        Console.WriteLine("Step 4: Testing batch operations (dry run)");
        Console.WriteLine("──────────────────────────────────────────");
        
        var wouldDelete = await dataStore.MarkOriginalImagesForDeletionAsync(dryRun: true);
        if (wouldDelete > 0)
        {
            Console.WriteLine($"⚠ {wouldDelete} ORIG images could be marked for deletion");
            Console.WriteLine("  (All have matching FINAL versions)");
            
            // Estimate space savings
            if (stats.OriginalCount > 0)
            {
                var avgOrigSize = origSizes.Count > 0 ? origSizes.Average() : 1_500_000;
                var potentialSavings = wouldDelete * avgOrigSize / (1024 * 1024);
                Console.WriteLine($"  Potential space savings: ~{potentialSavings:F0} MB");
            }
        }
        else
        {
            Console.WriteLine("  No ORIG images found that have matching FINAL versions");
        }
        Console.WriteLine();

        // Summary
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine("Duplicate Detection Test Complete!");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"✓ Test images on disk: {files.Length} ({origFiles.Count} ORIG, {finalFiles.Count} FINAL)");
        Console.WriteLine($"✓ Timestamp-matched pairs: {pairs.Count}");
        Console.WriteLine($"✓ Database images: {stats.TotalImages}");
        Console.WriteLine($"✓ Database ORIG/FINAL pairs: {dbPairs.Count}");
        Console.WriteLine($"✓ Workflow variant groups: {groups.Count}");
        
        if (stats.TotalImages == 0 || stats.OriginalCount == 0)
        {
            Console.WriteLine("\n💡 Tip: To test with these images, scan the _test_images folder");
            Console.WriteLine("   into Diffusion Toolkit and run this test again.");
        }
    }

    /// <summary>
    /// Find ORIG/FINAL pairs by matching timestamps in filenames
    /// </summary>
    private static List<(string orig, string final)> FindPairsByTimestamp(List<string> origFiles, List<string> finalFiles)
    {
        var pairs = new List<(string, string)>();
        var timestampPattern = new Regex(@"(\d{4}-\d{2}-\d{2}-\d{6})");
        
        foreach (var orig in origFiles)
        {
            var origMatch = timestampPattern.Match(Path.GetFileName(orig));
            if (!origMatch.Success) continue;
            
            var origTime = DateTime.ParseExact(origMatch.Groups[1].Value, "yyyy-MM-dd-HHmmss", null);
            
            // Find the FINAL image saved within 5 minutes after this ORIG
            var matchingFinal = finalFiles.FirstOrDefault(final =>
            {
                var finalMatch = timestampPattern.Match(Path.GetFileName(final));
                if (!finalMatch.Success) return false;
                
                var finalTime = DateTime.ParseExact(finalMatch.Groups[1].Value, "yyyy-MM-dd-HHmmss", null);
                var diff = (finalTime - origTime).TotalMinutes;
                
                // FINAL should be 0.5-5 minutes after ORIG (processing time for detailers + upscale)
                return diff > 0.5 && diff < 5;
            });
            
            if (matchingFinal != null)
            {
                pairs.Add((orig, matchingFinal));
            }
        }
        
        return pairs;
    }
}
