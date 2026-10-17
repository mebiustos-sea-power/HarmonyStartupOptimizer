using System;
using System.Collections.Generic;

namespace HarmonyStartupOptimizer;

// Stores raw successful resolutions, before callers' Harmony postfixes transform them.
// No resolver, game API or Harmony code is executed while holding the cache lock.
internal sealed class TypeLookupCache
{
    private readonly object gate = new object();
    private readonly Dictionary<string, Type> entries = new Dictionary<string, Type>(StringComparer.Ordinal);
    private long generation;
    private long hits;
    private bool active = true;

    internal long Hits { get { lock (gate) return hits; } }

    internal LookupRequest? Begin(string? name)
    {
        lock (gate)
        {
            if (!active) return null;
            Type? found = null;
            if (name != null) entries.TryGetValue(name, out found);
            return new LookupRequest(this, name, generation, found);
        }
    }

    internal void Returned(LookupRequest request, Type? result)
    {
        lock (gate)
        {
            Validate(request);
            request.OriginalReturned = true;
            request.RawResult = result;
        }
    }

    internal void Finish(LookupRequest request, Exception? error)
    {
        lock (gate)
        {
            Validate(request);
            if (request.Finished) return;
            request.Finished = true;
            bool failed = error != null || (!request.IsHit && !request.OriginalReturned);
            if (request.IsHit && !failed) hits++;
            if (active && request.Generation == generation && request.Name != null)
            {
                if (failed) entries.Remove(request.Name);
                else if (request.RawResult != null) entries[request.Name] = request.RawResult;
            }
        }
    }

    internal void Invalidate()
    {
        lock (gate)
        {
            if (!active) return;
            generation++; entries.Clear();
        }
    }

    internal void Deactivate()
    {
        lock (gate)
        {
            if (!active) return;
            active = false; generation++; entries.Clear();
        }
    }

    private void Validate(LookupRequest request)
    { if (request.Owner != this) throw new InvalidOperationException("A lookup request belongs to a different cache."); }
}

internal sealed class LookupRequest
{
    internal readonly TypeLookupCache Owner;
    internal readonly string? Name;
    internal readonly long Generation;
    internal readonly Type? CachedResult;
    internal Type? RawResult;
    internal bool OriginalReturned, Finished;
    internal bool IsHit => CachedResult != null;

    internal LookupRequest(TypeLookupCache owner, string? name, long generation, Type? cached)
    { Owner = owner; Name = name; Generation = generation; CachedResult = RawResult = cached; }
}
