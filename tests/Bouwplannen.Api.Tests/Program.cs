using System.Diagnostics;
using Bouwplannen.Api.Tests;

// Minimal test runner: runs every test, prints the outcome, exits non-zero on any failure (CI-friendly).
// Optional argument: part of a test name to run only matching tests.
var filter = args.FirstOrDefault();
var tests = ProjectApiTests.All().Where(t => filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
var failed = 0;
var total = Stopwatch.StartNew();

foreach (var (name, run) in tests)
{
    var sw = Stopwatch.StartNew();
    try
    {
        await run();
        Console.WriteLine($"  ✓ {name} ({sw.ElapsedMilliseconds} ms)");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  ✗ {name}");
        Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
        if (ex is not AssertionException) Console.WriteLine(ex.StackTrace);
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Count - failed} geslaagd, {failed} mislukt ({total.ElapsedMilliseconds} ms)");
return failed == 0 ? 0 : 1;
