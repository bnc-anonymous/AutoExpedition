using ExileCore2;
using ExileCore2.PoEMemory;
using System;
using System.Collections.Generic;

namespace AutoExpedition;

/// <summary>
/// One read of remote memory, or the fallback.
///
/// Every memory object in this HUD throws at arbitrary times - zoning, dying, a hot reload - and a
/// throw inside Render takes the whole overlay down, not just this plugin. So nothing reads the
/// game without going through here.
/// </summary>
internal static class Safe
{
    /// <summary>
    /// Runs something that reads the game and returns nothing, swallowing a bad read.
    ///
    /// For work called from the HOST's own loops rather than from this plugin's. A throw out of an
    /// entity callback is a throw inside ExileCore2's walk over its entity list, and the cost of
    /// one unreadable entity should not be the rest of the area never arriving.
    /// </summary>
    public static void Do(Action work)
    {
        try
        {
            work();
        }
        catch
        {
            // Deliberately silent: the caller is a per-entity callback, and a line per bad read
            // during a streaming area would be the log and nothing else.
        }
    }

    public static T Read<T>(Func<T> read, T fallback)
    {
        LeafCalls.GameReads++;

        try
        {
            return read();
        }
        catch
        {
            LeafCalls.GameReadThrows++;

            return fallback;
        }
    }

    /// <summary>
    /// The same read, with the thing being read passed in rather than captured.
    ///
    /// For the hot path only, and it exists for one reason: allocation. The closure form above
    /// captures locals, so the compiler emits a display class holding them plus a delegate pointing
    /// at it - one or two heap allocations per call. The draw path made about seventeen such calls
    /// per marker across eighty five markers, which is some fourteen hundred allocations a frame,
    /// or eighty five thousand a second. All of it Gen0 garbage the collector shrugs off, but the
    /// collections still happen, and they arrive as frame spikes rather than as steady cost.
    ///
    /// Written as <c>Safe.Read(target, static t =&gt; t.Entity.Pos, fallback)</c>, the lambda
    /// captures nothing, so the delegate is cached once by the runtime and the call allocates
    /// nothing at all.
    /// </summary>
    public static TValue Read<TState, TValue>(TState state, Func<TState, TValue> read, TValue fallback)
    {
        LeafCalls.GameReads++;

        try
        {
            return read(state);
        }
        catch
        {
            LeafCalls.GameReadThrows++;

            return fallback;
        }
    }

    /// <summary>For a read whose only purpose is its side effect on a string builder.</summary>
    public static void Try(Action act)
    {
        try
        {
            act();
        }
        catch
        {
            // The caller is describing something for a dump; a field that cannot be read is a
            // field the dump simply does not mention.
        }
    }

    /// <summary>
    /// A node's children, read out of its own pointer array rather than asked for.
    ///
    /// **Element.Children drops children this finds.** The passive tree's node container reports
    /// thirteen hundred children and hands back a list far shorter than that; read straight from
    /// the array, all thirteen hundred resolve into the real nodes, with their positions and their
    /// text. Whatever ExileCore2 does to build that list, it is lossy on at least one node, and
    /// every walk in this plugin was inheriting the loss.
    ///
    /// **It is not why the log filled up.** That was PathFromRoot, and this was written while
    /// chasing it on the strength of a diagnostic of mine that was itself wrong - a loop that ran
    /// to ChildCount while indexing a shorter list, so it called every index past the end an empty
    /// slot. Kept because reading the array is both more faithful and no more expensive, not
    /// because it fixed anything. See Panels.Path for what did.
    ///
    /// Null pointers are dropped here; nothing else filters. One copy of the rule, because there
    /// are eight walks in this plugin and all of them want the same answer.
    /// </summary>
    public static IList<Element> Kids(Element at)
    {
        if (at == null)
            return null;

        var pointers = Slots(at);

        if (pointers == null)
            return null;

        var kids = new List<Element>(pointers.Length);

        foreach (var pointer in pointers)
        {
            if (pointer <= 0)
                continue;

            // The state-taking overload, so a child costs a child rather than a child plus the
            // closure that fetched it. The tree walk behind a click reads every element on screen.
            var kid = Read((At: at, Pointer: pointer),
                static x => x.At.GetObject<Element>(x.Pointer), null);

            if (kid != null)
                kids.Add(kid);
        }

        return kids.Count > 0 ? kids : null;
    }

    /// <summary>The raw child pointers of a node, empty slots and all. What Kids is built on.</summary>
    /// <summary>
    /// One child by position, without building the rest of them.
    ///
    /// **Walking a path down the interface wants one child per level, not all of them.** Kids
    /// materialises an Element for every sibling, and resolving a written-down path threw all but
    /// one of those away at each step - measured at 3.6MB of throwaway objects for a single pass
    /// over the furniture list, which is twenty such paths. See Panels.Resolve.
    ///
    /// Counted the way Kids counts, skipping the empty slots, so the index means the same thing in
    /// both: a slot holding nothing is not a child in either.
    /// </summary>
    public static Element Kid(Element at, int index)
    {
        if (at == null || index < 0)
            return null;

        var pointers = Slots(at);

        if (pointers == null)
            return null;

        // **Only the child asked for is materialised.**
        //
        // This read every pointer into an Element on the way past and threw all but one away, so
        // asking for child twenty cost twenty-one objects out of game memory. Panels.Resolve walks
        // twenty furniture paths that way and Snapshot runs the lot, which measured at 245KB a
        // frame in Blocked/Furnished - about 1.5MB per rebuild, nearly all of it discarded on the
        // step after it was made.
        //
        // **The counting is NOT quite what it was, and the difference is worth stating.** Before,
        // a pointer that read back null was skipped without consuming an index - which could only
        // be known by reading it, which is the cost being removed. Now every positive pointer
        // before the index consumes one, so a child list holding a pointer that fails to read
        // resolves to a different element than it used to.
        //
        // Taken deliberately. A positive pointer failing to materialise means the read threw, which
        // is a memory fault rather than an ordinary state - and on the frame that happens the whole
        // snapshot is suspect anyway. Past the index the old behaviour is kept exactly: a null read
        // falls through to the next candidate.
        //
        // If panel detection ever starts naming the wrong element, this is the first thing to put
        // back.
        var seen = 0;

        foreach (var pointer in pointers)
        {
            if (pointer <= 0)
                continue;

            if (seen < index)
            {
                seen++;

                continue;
            }

            var kid = Read((At: at, Pointer: pointer),
                static x => x.At.GetObject<Element>(x.Pointer), null);

            if (kid == null)
                continue;

            return kid;
        }

        return null;
    }

    private static long[] Slots(Element at) =>
        at == null
            ? null
            : Read(at, static e =>
            {
                var slots = e.Elem.Childs;

                // An empty array, a nonsense one, or one so large that reading it is itself a bug.
                if (slots.First <= 0 || slots.Last <= slots.First ||
                    slots.ElementCount<long>() > 20000)
                    return null;

                var pointers = e.M.ReadStdVector<long>(slots);

                return pointers is { Length: > 0 } ? pointers : null;
            }, null);
}
