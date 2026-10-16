using System;
using System.Linq;
using System.Threading;
using HarmonyStartupOptimizer;

internal static class Program
{
    private static int passed;
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
    private static TypeLookupCache Cache() => new TypeLookupCache();

    private static Type? Resolve(TypeLookupCache cache, string? name, Func<Type?> original, Func<Type?, Type?>? postfix = null)
    {
        LookupRequest? request = cache.Begin(name);
        Exception? failure = null;
        try
        {
            Type? raw;
            if (request?.CachedResult != null) raw = request.CachedResult;
            else { raw = original(); if (request != null) cache.Returned(request, raw); }
            return postfix == null ? raw : postfix(raw);
        }
        catch (Exception error) { failure = error; throw; }
        finally { if (request != null) cache.Finish(request, failure); }
    }

    private static int Main()
    {
        try
        {
            Test("repeat queries preserve Type identity and run the resolver once", () =>
            {
                int originals = 0; var cache = Cache();
                Type? Original() { originals++; return typeof(string); }
                Assert(Resolve(cache, "A", Original) == typeof(string) && Resolve(cache, "A", Original) == typeof(string), "Original type changed.");
                Assert(originals == 1, "Successful resolution was not reused.");
            });
            Test("case, namespaces and assembly qualifications remain separate queries", () =>
            {
                int originals = 0; var cache = Cache();
                foreach (string name in new[] { "a", "A", "N.A", "A, Assembly", "a" }) Resolve(cache, name, () => { originals++; return typeof(int); });
                Assert(originals == 4, "Distinct lookup names were combined.");
            });
            Test("missing results are retried and later successful results can be reused", () =>
            {
                int originals = 0; var cache = Cache();
                Type? Original() => ++originals < 3 ? null : typeof(int);
                Assert(Resolve(cache, "A", Original) == null && Resolve(cache, "A", Original) == null, "Missing result changed.");
                Assert(Resolve(cache, "A", Original) == typeof(int) && Resolve(cache, "A", Original) == typeof(int), "Newly available type was missed.");
                Assert(originals == 3, "A missing result was cached.");
            });
            Test("null query does not collide with printable query names", () =>
            {
                var cache = Cache(); int originals = 0;
                Resolve(cache, null, () => { originals++; return null; });
                Resolve(cache, "<null>", () => { originals++; return typeof(int); });
                Resolve(cache, null, () => { originals++; return null; });
                Resolve(cache, "<null>", () => { originals++; return typeof(int); });
                Assert(originals == 3, "Null lookup was cached or conflated with a string.");
            });
            Test("lookup exceptions propagate unchanged and do not populate the cache", () =>
            {
                var cache = Cache(); var expected = new InvalidOperationException("fixture");
                try { Resolve(cache, "A", () => throw expected); throw new Exception("Exception swallowed."); }
                catch (InvalidOperationException error) { Assert(ReferenceEquals(error, expected), "Exception changed."); }
                int originals = 0; Resolve(cache, "A", () => { originals++; return typeof(int); });
                Assert(originals == 1, "Failure was cached.");
            });
            Test("postfix transformation runs once per call against the raw cached type", () =>
            {
                var cache = Cache(); int originals = 0, postfixes = 0;
                Type? Original() { originals++; return typeof(string); }
                Type? Postfix(Type? raw) { postfixes++; return raw == typeof(string) ? typeof(int) : typeof(bool); }
                Assert(Resolve(cache, "A", Original, Postfix) == typeof(int) && Resolve(cache, "A", Original, Postfix) == typeof(int), "A transformed result was cached and transformed twice.");
                Assert(originals == 1 && postfixes == 2, "The original or postfix ran an incorrect number of times.");
            });
            Test("an exception after resolution prevents publication", () =>
            {
                var cache = Cache(); var expected = new InvalidOperationException("postfix fixture");
                try { Resolve(cache, "A", () => typeof(int), _ => throw expected); }
                catch (InvalidOperationException error) { Assert(ReferenceEquals(error, expected), "Postfix failure changed."); }
                int originals = 0; Resolve(cache, "A", () => { originals++; return typeof(int); });
                Assert(originals == 1, "A failed invocation populated the cache.");
            });
            Test("assembly changes invalidate existing entries", () =>
            {
                var cache = Cache(); Resolve(cache, "A", () => typeof(string)); cache.Invalidate();
                Assert(Resolve(cache, "A", () => typeof(int)) == typeof(int), "An old assembly result was reused.");
            });
            Test("a resolution spanning an assembly change cannot repopulate stale entries", () =>
            {
                var cache = Cache(); LookupRequest request = cache.Begin("A")!;
                cache.Invalidate(); cache.Returned(request, typeof(string)); cache.Finish(request, null);
                Assert(Resolve(cache, "A", () => typeof(int)) == typeof(int), "An in-flight stale result was published.");
            });
            Test("deactivation clears types and blocks later or in-flight publication", () =>
            {
                var cache = Cache(); Resolve(cache, "A", () => typeof(string)); LookupRequest request = cache.Begin("B")!;
                cache.Deactivate(); cache.Returned(request, typeof(int)); cache.Finish(request, null);
                Assert(cache.Begin("A") == null && cache.Begin("B") == null, "Deactivation retained or accepted cached data.");
            });
            Test("concurrent first resolutions complete without sharing request state", () =>
            {
                var cache = Cache(); const int workers = 8;
                using var entered = new CountdownEvent(workers); using var release = new ManualResetEventSlim();
                Exception? failure = null;
                var threads = Enumerable.Range(0, workers).Select(_ => new Thread(() =>
                {
                    try { LookupRequest request = cache.Begin("A")!; entered.Signal(); release.Wait(); cache.Returned(request, typeof(int)); cache.Finish(request, null); }
                    catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
                })).ToArray();
                foreach (Thread thread in threads) thread.Start();
                bool ready = entered.Wait(5000); release.Set(); foreach (Thread thread in threads) thread.Join();
                Assert(ready && failure == null, "Concurrent lookup completion failed.");
                Assert(Resolve(cache, "A", () => throw new Exception("A completed result was not reused.")) == typeof(int), "Concurrent Type changed.");
            });
            Console.WriteLine($"PASS: {passed} behavioral checks"); return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}
