using System.Diagnostics;
using DiskScout.Core;

internal static class ScanBenchmark
{
    public static async Task<int> RunAsync(string? existingPath)
    {
        var ownsFixture = existingPath is null;
        var root = existingPath is null
            ? Path.Combine(Path.GetTempPath(), $"DiskScout.Benchmark.{Guid.NewGuid():N}")
            : Path.GetFullPath(existingPath);

        try
        {
            if (ownsFixture)
            {
                Console.WriteLine("Creating fixture: 20,000 files / 1,001 directories...");
                for (var group = 0; group < 200; group++)
                {
                    for (var child = 0; child < 4; child++)
                    {
                        var folder = Path.Combine(root, $"Group{group:D3}", $"Child{child}");
                        Directory.CreateDirectory(folder);
                        for (var file = 0; file < 25; file++)
                        {
                            var extension = file % 3 == 0 ? ".zip" : file % 3 == 1 ? ".cs" : ".txt";
                            using var stream = File.Create(Path.Combine(folder, $"File{file:D2}{extension}"));
                            stream.SetLength((group * 101 + child * 37 + file * 13) % 4096);
                        }
                    }
                }
            }

            var scanner = new DiskScanner();
            var options = new ScanOptions(root, LargeFileThresholdBytes: 0, MaxLargeFiles: 2_000);
            ScanResult reference = await scanner.ScanAsync(options);
            for (var i = 0; i < 2; i++)
                await scanner.ScanAsync(options);

            var elapsed = new List<double>();
            var allocated = new List<long>();
            for (var i = 0; i < 7; i++)
            {
                var allocationStart = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew();
                var result = await scanner.ScanAsync(options);
                watch.Stop();
                allocated.Add(GC.GetTotalAllocatedBytes(precise: true) - allocationStart);
                elapsed.Add(watch.Elapsed.TotalMilliseconds);

                if (result.FileCount != reference.FileCount || result.TotalBytes != reference.TotalBytes ||
                    result.DirectoryCount != reference.DirectoryCount)
                    throw new InvalidOperationException("Benchmark contents changed between scans.");
            }

            elapsed.Sort();
            allocated.Sort();
            Console.WriteLine($"Runtime: {Environment.Version}; OS: {Environment.OSVersion}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            Console.WriteLine($"Files: {reference.FileCount:N0}; directories: {reference.DirectoryCount:N0}; bytes: {reference.TotalBytes:N0}; retained: {reference.LargeFiles.Count:N0}");
            Console.WriteLine($"Warm scan, 7 runs: median {elapsed[3]:F2} ms; min {elapsed[0]:F2} ms; max {elapsed[^1]:F2} ms");
            Console.WriteLine($"Median managed allocation: {allocated[3]:N0} bytes ({allocated[3] / 1048576d:F2} MiB)");
            return 0;
        }
        finally
        {
            // Only delete the temporary fixture created by this invocation, never a supplied path.
            if (ownsFixture && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
