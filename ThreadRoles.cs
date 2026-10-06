using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// What each worker in the pool is for, read from one line of text.
///
/// **Three settings used to describe a worker and they indexed the same number without knowing about each
/// other.** The tearing mix named an operator per stream, a list named the streams that never adopt, and a
/// modulo decided which streams drew their own opening - so stream 3 came out as the only segment-biased worker
/// AND a hedge, `reach` was duplicated on two streams, and streams 5 to 7 had no bias at all. None of that was
/// intended by anybody; it fell out of three rules sharing an index. One line per worker cannot collide with
/// itself.
///
/// The grammar, entries separated by semicolons, one per worker, in worker order:
///
/// <code>
/// even opening=continue adopt=no ; reach opening=enumerated keep-opening=75 ; seg opening=enumerated keep-opening=75 ; opening=fresh
/// </code>
///
/// <list type="bullet">
/// <item>a bare word is the tearing bias - seg, worst, rel, reach, or even for no bias - with an optional
/// share as <c>reach:70</c>, meaning seventy per cent of that worker's tears;</item>
/// <item><c>opening=</c> is the opening the worker starts from: <c>continue</c> keeps the plan already in
/// hand, so the incumbent is never lost; <c>enumerated</c> takes its share of the enumerated openings;
/// <c>fresh</c> draws its own; <c>tour</c> draws its own and then routes it through a rich target it does not
/// reach, a different one per touring worker; <c>refine</c> draws its own, then from partway into the window -
/// at its first chance and at every kick after - rebuilds from the first links of the pool's best
/// chain;</item>
/// <item><c>refine-after=NN</c> is how far into the window, as a percentage, a refining worker waits before it
/// first rebuilds from the pool's best. 50 when not given;</item>
/// <item><c>keep-leader=NN</c> is the percentage of the pool's best chain a refining worker keeps before
/// rebuilding the rest. Without it the refining workers spread evenly: two keep 33% and 67%, four keep 20%, 40%,
/// 60% and 80%;</item>
/// <item><c>keep-opening=NN</c> is the share of the window, as a percentage, for which a worker keeps its
/// enumerated opening: chains that change those first links are refused and the links are not reordered.
/// It does nothing for a worker whose opening is continue or fresh;</item>
/// <item><c>adopt=</c> is no longer read and is reported as a complaint: sharing the best chain between workers
/// was removed, so there is nothing for it to switch.</item>
/// </list>
///
/// **Unknown words are complaints, not silence.** The tearing mix was deliberately forgiving, so `reech` read
/// as "no bias" and the pool quietly stopped being the one that was configured. Every word this cannot place is
/// reported, and <see cref="Said"/> prints what was understood per worker so a dump says what ran rather than
/// what was typed.
///
/// A missing entry, or a pool with more workers than the line describes, leaves those workers at the defaults:
/// no bias and a share of the enumerated branches.
/// </summary>
internal static class ThreadRoles
{
    /// <summary>What one worker is for. See ThreadRoles for the text it is read from.</summary>
    internal sealed record Role(
        int Tear = -1,

        /// <summary>
        /// The share of this worker's tears the bias takes, as a PERCENTAGE of a hundred, not a fraction of one.
        ///
        /// The units matter and getting them wrong inverted the bias. Repair's weighting reads it as
        /// `100 - Share` split between the other three operators, so a share stored as 0.7 instead of 70 gave
        /// the favoured operator a weight of 0.7 against 33.1 for each of the others - every specialist worker
        /// avoiding the one operator it was meant to lean on. Measured: reach fell from 509 tries a press to 69,
        /// and the batch median from 9,823 to 8,627.
        /// </summary>
        double Share = 0d,
        Opens Opening = Opens.Enumerated,
        /// <summary>
        /// The share of the window, nought to one, for which an enumerated opening is kept. Written in the line as
        /// keep-opening=NN, a percentage. Only a worker given an enumerated opening has anything to keep. See
        /// Repair's Keeps and Frozen.
        /// </summary>
        double KeepOpening = 0.5d,

        /// <summary>
        /// The share of the pool's best chain, nought to one, a refining worker keeps, or below nought to spread
        /// the refining workers evenly. Written in the line as keep-leader=NN, a percentage. See Opens.Refine.
        /// </summary>
        double KeepLeader = -1d,

        /// <summary>
        /// How far into the window, nought to one, a refining worker waits before it first rebuilds from the pool's
        /// best. Written in the line as refine-after=NN, a percentage.
        ///
        /// Not nought, because the pool's first best arrives within a second and is a weak chain: with no wait,
        /// on Scorched Cay (draws 11-30, 8s), the refiners rebuilt from chains as low as 11,928 in a press whose
        /// best was 15,097, and four refiners lowered the mean press maximum from 15,135 to 15,036. 50 is chosen,
        /// not measured.
        /// </summary>
        double RefineAfter = 0.5d,

        /// <summary>
        /// Which remnants this worker searches as though they were must take: none (remnants=free), every one
        /// (remnants=all), or every one and then fewer in turn (remnants=descending). The chains it finds are judged
        /// by the pool on what they really score. See Repair.WithMustTake and Repair.SearchDescendingRemnants.
        /// </summary>
        RemnantsHeld HoldsRemnants = RemnantsHeld.Free);

    /// <summary>Which remnants a worker searches as though they were must take. See Role.HoldsRemnants.</summary>
    internal enum RemnantsHeld
    {
        /// <summary>Only those the player marked.</summary>
        Free,

        /// <summary>Every remnant, for the whole search.</summary>
        All,

        /// <summary>Every remnant, then every one but one, then but two, a slice of the window each. See Repair.SearchDescendingRemnants.</summary>
        Descending,
    }

    /// <summary>
    /// Which opening a worker starts from.
    ///
    /// The words are the ones the rest of the plugin uses: what the enumerator produces is "enumerated
    /// openings" in the setting that switches it on and in the dump that lists them, so a reader who greps
    /// the token finds the setting and the readout too.
    /// </summary>
    internal enum Opens
    {
        /// <summary>Its share of the enumerated openings, or its own draw when none were enumerated.</summary>
        Enumerated,

        /// <summary>The plan already in hand, so the incumbent is never lost.</summary>
        Continue,

        /// <summary>A construction of its own, which is the hedge against a wrong enumeration.</summary>
        Fresh,

        /// <summary>
        /// A construction of its own, then routed through a rich target it does not reach - the richest for the
        /// first touring worker, the second richest for the second, and so on. For taking a worker somewhere the
        /// pool would not otherwise go. See Planner.TourThrough.
        /// </summary>
        Tour,

        /// <summary>
        /// A construction of its own, searched like any other worker's until RefineAfter of the window has passed.
        /// From then, the pool's best chain's first links with the rest rebuilt - once as soon as that best is
        /// better than its own record, and again at every kick. Never restarted for being behind the
        /// pool, since following the pool is what it is for. See Repair's RefinedStart.
        /// </summary>
        Refine,

        /// <summary>
        /// A chain built from an order of the capturable remnants - the best for the first worker with this opening,
        /// the second best for the second, and so on - or its own draw when none was built. See RemnantOrder and
        /// PlanEnvironment.RemnantOrderChains.
        /// </summary>
        RemnantOrder,
    }

    /// <summary>The operator names a bias may be given, in the order the search numbers them.</summary>
    internal static readonly string[] Tears = ["seg", "worst", "rel", "reach"];

    /// <summary>What the last read understood, per worker, and what it could not place. For the dump.</summary>
    internal static string Said { get; private set; } = "not read";

    /// <summary>
    /// Reads the line into one role per worker, and records what it understood.
    ///
    /// Never throws and never refuses a pool: a line that cannot be read leaves every worker at the defaults and
    /// says so, because a search that will not start is worse than one configured by accident - and the
    /// complaint is on the page either way.
    /// </summary>
    internal static Role[] Read(string said, int workers)
    {
        workers = Math.Max(1, workers);

        var roles = new Role[workers];
        var complaints = new List<string>();
        var entries = (said ?? "").Split(';', StringSplitOptions.TrimEntries);

        for (var i = 0; i < workers; i++)
            roles[i] = i < entries.Length ? One(entries[i], i, complaints) : new Role();

        for (var i = workers; i < entries.Length; i++)
        {
            if (entries[i].Length > 0)
                complaints.Add($"worker {i} described but the pool has {workers}");
        }

        Said = Spell(roles, complaints);

        return roles;
    }

    private static Role One(string entry, int worker, List<string> complaints)
    {
        var role = new Role();

        if (entry.Length == 0)
            return role;

        foreach (var word in entry.Split(' ', StringSplitOptions.RemoveEmptyEntries |
                                             StringSplitOptions.TrimEntries))
        {
            var equals = word.IndexOf('=');

            if (equals < 0)
            {
                role = Biased(role, word, worker, complaints);

                continue;
            }

            var key = word[..equals].Trim();
            var value = word[(equals + 1)..].Trim();

            switch (key.ToLowerInvariant())
            {
                case "opening":
                    switch (value.ToLowerInvariant())
                    {
                        case "continue":
                            role = role with { Opening = Opens.Continue };

                            break;

                        case "enumerated":
                            role = role with { Opening = Opens.Enumerated };

                            break;

                        case "fresh":
                            role = role with { Opening = Opens.Fresh };

                            break;

                        case "tour":
                            role = role with { Opening = Opens.Tour };

                            break;

                        case "refine":
                            role = role with { Opening = Opens.Refine };

                            break;

                        case "remnant-order":
                            role = role with { Opening = Opens.RemnantOrder };

                            break;

                        default:
                            complaints.Add(
                                $"worker {worker}: \"{value}\" is not continue, enumerated, fresh, tour or refine");

                            break;
                    }

                    break;

                case "keep-opening":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kept))
                        role = role with { KeepOpening = Math.Clamp(kept, 0d, 100d) / 100d };
                    else
                        complaints.Add($"worker {worker}: keep-opening=\"{value}\" is not a number");

                    break;

                case "keep-leader":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var leader))
                        role = role with { KeepLeader = Math.Clamp(leader, 0d, 100d) / 100d };
                    else
                        complaints.Add($"worker {worker}: keep-leader=\"{value}\" is not a number");

                    break;

                case "refine-after":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var after))
                        role = role with { RefineAfter = Math.Clamp(after, 0d, 100d) / 100d };
                    else
                        complaints.Add($"worker {worker}: refine-after=\"{value}\" is not a number");

                    break;

                case "remnants":
                    if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
                        role = role with { HoldsRemnants = RemnantsHeld.All };
                    else if (string.Equals(value, "descending", StringComparison.OrdinalIgnoreCase))
                        role = role with { HoldsRemnants = RemnantsHeld.Descending };
                    else if (string.Equals(value, "free", StringComparison.OrdinalIgnoreCase))
                        role = role with { HoldsRemnants = RemnantsHeld.Free };
                    else
                        complaints.Add($"worker {worker}: remnants=\"{value}\" is not free, all or descending");

                    break;

                case "adopt":
                    complaints.Add($"worker {worker}: adopt= no longer does anything - sharing the best chain " +
                                   "between workers was removed - so it can be deleted from the line");

                    break;

                default:
                    complaints.Add($"worker {worker}: \"{key}\" is not opening, keep-opening, keep-leader, refine-after or remnants");

                    break;
            }
        }

        return role;
    }

    /// <summary>
    /// A role written back in the roles line's own words - the bias, then opening=, and whichever of
    /// keep-opening=, keep-leader= and refine-after= that opening uses - so a readout names a worker the way
    /// the line that configured it does. Only the words that do something for that opening are written.
    /// </summary>
    internal static string RoleInLine(Role role)
    {
        if (role == null)
            return "no role";

        var bias = role.Tear < 0
            ? "even"
            : role.Share == 70d
                ? Tears[role.Tear]
                : string.Create(CultureInfo.InvariantCulture, $"{Tears[role.Tear]}:{role.Share:0}");
        var said = $"{bias} opening={role.Opening.ToString().ToLowerInvariant()}";

        if (role.Opening == Opens.Enumerated)
            said += string.Create(CultureInfo.InvariantCulture, $" keep-opening={role.KeepOpening * 100d:0}");

        if (role.Opening == Opens.Refine)
        {
            if (role.KeepLeader >= 0d)
                said += string.Create(CultureInfo.InvariantCulture, $" keep-leader={role.KeepLeader * 100d:0}");

            said += string.Create(CultureInfo.InvariantCulture, $" refine-after={role.RefineAfter * 100d:0}");
        }

        if (role.HoldsRemnants != RemnantsHeld.Free)
            said += " remnants=" + role.HoldsRemnants.ToString().ToLowerInvariant();

        return said;
    }

    /// <summary>The tearing bias, with the share the old tearing mix allowed after a colon.</summary>
    private static Role Biased(Role role, string word, int worker, List<string> complaints)
    {
        var share = 70d;
        var name = word;
        var colon = word.IndexOf(':');

        if (colon >= 0)
        {
            name = word[..colon].Trim();

            if (double.TryParse(word[(colon + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var asked))
                share = Math.Clamp(asked, 1d, 100d);
            else
                complaints.Add($"worker {worker}: \"{word[(colon + 1)..]}\" is not a share");
        }

        if (string.Equals(name, "even", StringComparison.OrdinalIgnoreCase))
            return role with { Tear = -1, Share = 0d };

        for (var i = 0; i < Tears.Length; i++)
        {
            if (string.Equals(name, Tears[i], StringComparison.OrdinalIgnoreCase))
                return role with { Tear = i, Share = share };
        }

        complaints.Add($"worker {worker}: \"{name}\" is not {string.Join(", ", Tears)} or even");

        return role;
    }

    private static string Spell(Role[] roles, List<string> complaints)
    {
        var b = new StringBuilder();

        b.AppendLine(complaints.Count == 0
            ? $"    {roles.Length} worker(s) described, nothing unread"
            : $"    {roles.Length} worker(s) described, {complaints.Count} COMPLAINT(S):");

        foreach (var complaint in complaints)
            b.AppendLine($"      !! {complaint}");

        b.AppendLine("    worker  tears          opening     keeps it  keeps of the best  refines after");

        for (var i = 0; i < roles.Length; i++)
        {
            var role = roles[i];
            var tears = role.Tear < 0
                ? "even"
                : string.Create(CultureInfo.InvariantCulture, $"{Tears[role.Tear]} {role.Share:0}%");

            b.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"    {i,6}  {tears,-13}  {role.Opening.ToString().ToLowerInvariant(),-10}  " +
                $"{role.KeepOpening,7:P0}  " +
                $"{(role.Opening != Opens.Refine ? "-" : role.KeepLeader < 0d ? "spread" : role.KeepLeader.ToString("P0", CultureInfo.InvariantCulture)),-17}  " +
                $"{(role.Opening != Opens.Refine ? "-" : role.RefineAfter.ToString("P0", CultureInfo.InvariantCulture))}"));
        }

        return b.ToString().TrimEnd();
    }
}
