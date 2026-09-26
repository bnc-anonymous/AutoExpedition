using ExileCore2;
using ExileCore2.PoEMemory;
using System;
using System.Collections.Generic;

namespace AutoExpedition;

/// <summary>
/// The game's own "Expedition Complete", which is the only thing that says an encounter is over.
///
/// Everything else the plugin has to end a run on is a guess about circumstances - you walked away,
/// enough time passed, you went to the other dig site. This is a statement, and it fires on the last
/// monster's death, which is exactly the moment after which nothing more will be unearthed. For the
/// spawn census that is the difference between a measurement and a lower bound.
///
/// **Found by looking, not by the API.** ExileCore2 names no element for it - it names the
/// combinations window, the detonator and the vendor panels and stops there. So it was found the
/// same way the combinations button was: dump every visible label in the interface while the thing
/// is on screen, and there it was at child 44,0,0,0,0 of IngameUi, 432 by 45 pixels, reading
/// "Expedition Complete".
///
/// **Addressed by text, with the path as a cache.** The path is what makes it cheap to check and
/// the text is what makes it right: a child index is a fact about this patch, where the words are a
/// fact about the feature. So the remembered path is tried first and believed only while it still
/// reads the expected words, and a search re-finds it when it does not.
///
/// The search is throttled hard because it walks the interface, and nothing needs to know about
/// this within a frame - the popup stays up for seconds.
/// </summary>
internal static class Finished
{
    /// <summary>What the popup says. The whole handle, since nothing else identifies it.</summary>
    private const string Says = "Expedition Complete";

    /// <summary>Where it was last found, so the usual case is five array reads.</summary>
    /// <summary>Where the banner was last seen to live, before anything was learnt. See Forget.</summary>
    private static readonly int[] Shipped = [44, 0, 0, 0, 0];

    private static int[] _path = Shipped;

    /// <summary>
    /// Forgets the learnt path and the backoff, so the next look starts as a fresh session would.
    ///
    /// For the reset button. The backoff in particular survives everything else - it is what makes
    /// a second measurement run cheaper than the first for reasons that have nothing to do with
    /// what is being measured. See Caches.Clear.
    /// </summary>
    public static void Forget()
    {
        _path = Shipped;
        _looked = DateTime.MinValue;
        _watched = DateTime.MinValue;
        _wait = Rarely;
        _was = false;
        Seen = 0;
        Searches = 0;
    }

    private static DateTime _looked;

    /// <summary>When the banner was last looked for at all. See Watch and Between.</summary>
    private static DateTime _watched;

    /// <summary>How long to wait before searching again, doubling while searches fail. See Showing.</summary>
    private static TimeSpan _wait = Rarely;

    /// <summary>How often the banner is looked for. See Watch.</summary>
    private static readonly TimeSpan Between = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The longest the doubling backs off to.
    ///
    /// **Measured at 29.8ms for one search**, which is two dropped frames, and it landed in the
    /// worst frame Tick reported. Two seconds was picked to be sure of catching a banner while it
    /// stands; fifteen trades that certainty for the stutter, which is the right way round for a
    /// warning about a game bug. The warning is also now something that can be switched off.
    /// See Showing and DebugSettings.WatchFinished.
    /// </summary>
    private static readonly TimeSpan Most = TimeSpan.FromSeconds(15);

    /// <summary>How often the interface may be searched when the remembered path stops working.</summary>
    /// <summary>
    /// How many times the interface has been searched for the banner since the plugin loaded.
    ///
    /// A search is a depth-six walk of everything visible, and whether it is running at all is the
    /// difference between two quite different explanations of what this costs. Printed by the dump.
    /// </summary>
    public static int Searches { get; private set; }

    private static readonly TimeSpan Rarely = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long between searches when the written-down path still reaches something. See Showing.
    /// </summary>
    private static readonly TimeSpan Seldom = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many times the banner has come up in this area.
    ///
    /// **It should fire once per expedition and it does not.** Seen firing twice at one dig site -
    /// once on killing an ice-encased unique and once, legitimately, on the last monster there - and
    /// the map's other site was unusable afterwards. Counting them is what lets the plugin say so
    /// while it is happening, rather than the player finding out by walking across the map to a site
    /// that will not take an explosive. See Watch.
    /// </summary>
    public static int Seen { get; private set; }

    private static bool _was;

    public static void AreaChange()
    {
        _looked = DateTime.MinValue;
        _wait = Rarely;
        Seen = 0;
        _was = false;
    }

    /// <summary>
    /// Counts the banner on its rising edge. Called once a tick, unlike Showing, which is a question.
    ///
    /// Separate from Showing because Showing is asked from several places and by the dump; a query
    /// that counts would count differently depending on who looked. See Seen.
    /// </summary>
    public static void Watch(GameController gc, bool hunt = true)
    {
        // **Polled, not read every frame.** The banner stands for several seconds, so looking four
        // times a second catches every one there is and looking sixty times a second buys no
        // earlier answer for fifteen times the cost. Neither half of the look is cheap: following
        // the written-down path reads a child-pointer array at every level it passes, and a search
        // that comes up empty is a depth-six walk of everything visible.
        if (DateTime.UtcNow - _watched < Between)
            return;

        _watched = DateTime.UtcNow;

        var now = Showing(gc, hunt);

        if (now && !_was)
            Seen++;

        _was = now;
    }

    /// <summary>Whether the completion popup is on screen.</summary>
    public static bool Showing(GameController gc, bool hunt = true)
    {
        var root = Safe.Read(() => (Element)gc.IngameState.IngameUi, null);

        if (root == null)
            return false;

        // **Nothing to follow is not the same as following nothing.** Follow hands back the root it
        // was given when the path is empty, and Reads then tested the ROOT of the interface - which
        // is not where the banner is, and whose Text is whatever the game keeps on the top element.
        // The test cannot be true and the read is not free.
        Element at;

        using (Spent.On("Banners/Follow"))
            at = _path.Length > 0 ? Follow(root, _path) : null;

        bool said;

        using (Spent.On("Banners/Reads"))
            said = Reads(at);

        if (said)
            return true;

        // **A path that still resolves is not a broken path.**
        //
        // Reads is false in two quite different situations: the words are not on screen, which is
        // the case on very nearly every frame of a session, and the path no longer reaches the
        // element that would carry them, which happens when a patch moves the interface about.
        // Only the second is worth searching for, and they were treated the same.
        //
        // So the ordinary case - no banner up - ran a depth-six walk of the whole visible interface
        // twice a second for the length of the session. Measured at 7.8MB of throwaway objects a
        // search and 259KB a frame averaged out, which was the largest single allocator left in the
        // plugin and the thing behind Banners' twenty-one millisecond worst frame.
        //
        // An empty path counts as broken, which is what makes the first search happen at all: with
        // nothing to follow, Follow hands back the root it was given.
        var broken = at == null;

        // The backstop covers what resolving cannot rule out - a moved interface where the old path
        // still reaches SOME element, just not the right one. Rare enough to check seldom, since
        // it can only change when the game is patched, which is not during a session.
        // Handed in by the caller, which knows whether the answer could matter. See Banners.
        if (!hunt || DateTime.UtcNow - _looked < (broken ? _wait : Seldom))
            return false;

        _looked = DateTime.UtcNow;

        Searches++;

        var found = new List<int>();

        bool got;

        using (Spent.On("Banners/Search"))
            got = Search(root, found, 0);

        if (!got)
        {
            // **Doubling, because a failed search nearly always means there is no banner.** The
            // words can only be found while they are on screen, so a walk that comes up empty is
            // the ordinary state of a session rather than a sign of looking in the wrong place -
            // and repeating it twice a second for ever was the largest allocator left in the
            // plugin. Capped short enough to still catch a banner while it stands.
            _wait = _wait < Most ? _wait + _wait : Most;

            return false;
        }

        _path = found.ToArray();
        _wait = Rarely;

        return true;
    }

    /// <summary>The element at a remembered path, or null when the path no longer leads anywhere.</summary>
    private static Element Follow(Element root, IReadOnlyList<int> path)
    {
        var at = root;

        foreach (var step in path)
        {
            at = Safe.Kid(at, step);

            if (at == null)
                return null;
        }

        return at;
    }

    /// <summary>Whether this element is the popup: visible, and saying what the popup says.</summary>
    private static bool Reads(Element element) =>
        element != null &&
        Safe.Read(element, static e => e.IsVisible, false) &&
        (Safe.Read(element, static e => e.Text, null) ?? "")
        .IndexOf(Says, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Depth-first for the words, recording the path taken to reach them.
    ///
    /// Visible branches only, which is most of the saving: the hidden half of the interface is
    /// enormous and permanently there, and none of it can be a popup that is on screen.
    /// </summary>
    private static bool Search(Element element, List<int> path, int depth)
    {
        if (element == null || depth > 6)
            return false;

        var kids = Safe.Kids(element);

        if (kids == null)
            return false;

        for (var i = 0; i < kids.Count; i++)
        {
            var kid = kids[i];

            if (kid == null || !Safe.Read(kid, static e => e.IsVisible, false))
                continue;

            path.Add(i);

            if (Reads(kid) || Search(kid, path, depth + 1))
                return true;

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }
}
