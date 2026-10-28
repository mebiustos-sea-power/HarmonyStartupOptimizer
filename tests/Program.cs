using System;
using System.Linq;
using System.Collections.ObjectModel;
using System.Reflection;
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
            Test("startup duration is captured once and remains fixed during the result display", () =>
            {
                double elapsed = 24.8;
                int reads = 0;
                var timing = new StartupTiming(() => { reads++; return elapsed; });
                Assert(timing.Seconds == null && reads == 0, "Startup duration was finalized before the menu was rendered.");
                Assert(timing.Complete() && timing.Seconds == elapsed, "The first menu render did not capture elapsed startup time.");
                elapsed = 300;
                Assert(!timing.Complete() && timing.Seconds == 24.8 && reads == 1,
                    "Result display time or another load changed the completed startup duration.");
            });
            Test("reference indices preserve first matches, missing results and appended data", () =>
            {
                var first = new Entry("a"); var second = new Entry("a");
                var values = new ObservableCollection<Entry> { first, second, new Entry(null) };
                using var index = new ReferenceIndex<Entry, (string?, int)>(values, values, e => (e.Key, 0));
                Assert(ReferenceEquals(index.Find(("a", 0)), first), "A later duplicate replaced the first match.");
                Assert(index.Find(("b", 0)) == null, "A missing reference unexpectedly resolved.");
                var added = new Entry("b"); values.Add(added);
                Assert(ReferenceEquals(index.Find(("b", 0)), added), "A previous missing result hid appended data.");
                Assert(ReferenceEquals(index.Find((null, 0)), values[2]), "Null reference keys changed meaning.");
            });
            Test("reference indices follow reordering, replacement, removal, reset and key edits", () =>
            {
                var first = new Entry("a"); var second = new Entry("a");
                var values = new ObservableCollection<Entry> { first, second };
                using var index = new ReferenceIndex<Entry, string>(values, values, e => e.Key!);
                Assert(ReferenceEquals(index.Find("a"), first), "Initial first-match order changed.");
                values.Move(1, 0); Assert(ReferenceEquals(index.Find("a"), second), "Reordering did not invalidate the first match.");
                var replaced = new Entry("a"); values[0] = replaced;
                Assert(ReferenceEquals(index.Find("a"), replaced), "Same-count replacement retained the old object.");
                replaced.Key = "b"; index.InvalidateItem(replaced);
                Assert(ReferenceEquals(index.Find("a"), first) && ReferenceEquals(index.Find("b"), replaced), "A changed reference key was stale.");
                values.Remove(first); Assert(index.Find("a") == null, "A removed item remained available.");
                values.Clear(); values.Add(new Entry("c"));
                Assert(index.Find("b") == null && ReferenceEquals(index.Find("c"), values[0]), "Reset retained prior-generation data.");
            });
            Test("repeated reference searches index each entry once and extend only appended data", () =>
            {
                var values = new ObservableCollection<Entry>(Enumerable.Range(0, 2000).Select(i => new Entry(i.ToString())));
                int reads = 0;
                using var index = new ReferenceIndex<Entry, string>(values, values, e => { reads++; return e.Key!; });
                for (int i = 0; i < 5000; i++) Assert(ReferenceEquals(index.Find((i % values.Count).ToString()), values[i % values.Count]), "Indexed result changed.");
                Assert(reads == values.Count, "Repeated queries rebuilt or rescanned the data.");
                values.Add(new Entry("added"));
                Assert(ReferenceEquals(index.Find("added"), values[values.Count - 1]) && reads == values.Count, "Appending rebuilt the previous index.");
            });
            Test("disabled or failed modules do not stop independent modules and common shutdown releases all", () =>
            {
                int errors = 0;
                var modules = new OptimizationSet((_, error) => errors++);
                var disabled = new ModuleFixture(); var failed = new ModuleFixture { FailInstall = true }; var active = new ModuleFixture();
                Assert(!modules.Install(disabled, false) && !disabled.Active, "Disabled module was installed.");
                Assert(!modules.Install(failed, true) && !failed.Active && errors == 1, "Failed installation was not rolled back and reported.");
                Assert(modules.Install(active, true) && active.Active, "Another module's failure blocked installation.");
                modules.Stop(); modules.Stop();
                Assert(!active.Active && modules.ActiveNames.Length == 0, "Common shutdown retained an optimization.");
            });
            Test("custom type-name getters retain their observable behavior", () =>
            {
                var cache = new TypeNameCache(); var type = new NameFixture();
                Assert(cache.FullName(type) == "1" && cache.FullName(type) == "2", "Custom FullName getters were cached.");
                Assert(cache.Name(type) == "3" && cache.Name(type) == "4", "Custom Name getters were cached.");
            });
            Test("different type queries reuse enumeration without sharing mutable result arrays", () =>
            {
                var assembly = new EnumerationFixture(); var cache = new AssemblyTypeCache();
                Type[] first = cache.GetTypes(assembly); first[0] = typeof(Guid);
                Type[] second = cache.GetTypes(assembly); second[1] = typeof(bool);
                Type[] third = cache.GetTypes(assembly);
                Assert(assembly.Calls == 1 && third.SequenceEqual(new[] { typeof(string), typeof(int) }),
                    "Enumeration was repeated or a caller changed the cached type list.");
            });
            Test("failed enumerations are retried and retain the original exception", () =>
            {
                var assembly = new EnumerationFixture(); var cache = new AssemblyTypeCache();
                var expected = new ReflectionTypeLoadException(new Type[] { typeof(string) }, new Exception[] { new TypeLoadException("fixture") });
                assembly.Error = expected;
                try { cache.GetTypes(assembly); throw new Exception("Enumeration failure was swallowed."); }
                catch (ReflectionTypeLoadException error) { Assert(ReferenceEquals(error, expected), "Enumeration exception changed."); }
                assembly.Error = null;
                Assert(cache.GetTypes(assembly).Length == 2 && assembly.Calls == 2, "A partial enumeration was cached.");
            });
            Test("enumeration spanning invalidation is retried and shutdown releases cached arrays", () =>
            {
                var assembly = new EnumerationFixture(); var cache = new AssemblyTypeCache();
                assembly.OnRead = cache.Invalidate; cache.GetTypes(assembly); assembly.OnRead = null;
                cache.GetTypes(assembly); cache.GetTypes(assembly);
                Assert(assembly.Calls == 2, "An enumeration spanning invalidation was published.");
                cache.Deactivate(); cache.GetTypes(assembly); cache.GetTypes(assembly);
                Assert(assembly.Calls == 4, "Shutdown retained an active enumeration cache.");
            });
            Test("repeat queries preserve Type identity and run the resolver once", () =>
            {
                int originals = 0; var cache = Cache();
                Type? Original() { originals++; return typeof(string); }
                Assert(Resolve(cache, "A", Original) == typeof(string) && Resolve(cache, "A", Original) == typeof(string), "Original type changed.");
                Assert(originals == 1, "Successful resolution was not reused.");
                Assert(cache.Hits == 1, "The displayed hit count included the initial resolution or missed a reuse.");
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

    private sealed class EnumerationFixture : Assembly
    {
        internal int Calls;
        internal Exception? Error;
        internal Action? OnRead;
        public override bool IsDynamic => false;
        public override Type[] GetTypes()
        {
            Calls++; OnRead?.Invoke();
            if (Error != null) throw Error;
            return new[] { typeof(string), typeof(int) };
        }
    }

    private sealed class NameFixture : TypeDelegator
    {
        private int reads;
        internal NameFixture() : base(typeof(string)) { }
        public override string FullName => (++reads).ToString();
        public override string Name => (++reads).ToString();
    }

    private sealed class Entry
    {
        internal string? Key;
        internal Entry(string? key) { Key = key; }
    }

    private sealed class ModuleFixture : IOptimizerModule
    {
        internal bool FailInstall, Active;
        public string Name => "fixture";
        public void Install() { Active = true; if (FailInstall) throw new InvalidOperationException("fixture failure"); }
        public void Uninstall() { Active = false; }
    }
}
