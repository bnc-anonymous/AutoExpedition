using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoExpedition;

/// <summary>
/// Prices finished Expedition Tablets on the official trade site: one instant-buyout search for rare Expedition
/// Tablets carrying a tablet's searchable modifiers at least at its rolls (less a slack), then one fetch of the
/// cheapest listings. Each answer is cached under its query and kept on disk.
///
/// **One request at a time, from one background task, gated on the site's own limits.** The search and fetch policies
/// state their rules in X-Rate-Limit-Ip as hits:period:lockout, and how many hits the IP already has in
/// X-Rate-Limit-Ip-State. Read on 2026-10-09: searches 5:10:60, 15:60:300, 30:300:1800, 600:21600:3600; fetches
/// 12:4:10, 16:12:300, 50:300:300, 1000:21600:1800. Those are the starting rules until a response states its own. A
/// request is held while sending it would reach any rule's limit, while a lockout or Retry-After is running, and for
/// the player's own minimum spacing and searches-per-five-minutes cap - which may be stricter and never looser.
///
/// No login is sent. Searches and fetches answered without one on 2026-10-09, though the website itself asks for it.
/// </summary>
internal static class TabletPricing
{
    private const string Site = "https://www.pathofexile.com/api/trade2";

    /// <summary>The User-Agent every request carries; the site asks API users to identify themselves.</summary>
    private const string Agent = "AutoExpedition/1.0 (ExileCore2 plugin)";

    /// <summary>
    /// One listing as fetched: its price as listed and when it was indexed. Converted to exalts where it is drawn, at the
    /// reward valuation's rates of the moment. See Tablets.ExaltsOf.
    /// </summary>
    internal sealed record Listing(double Amount, string Currency, DateTime Indexed, Dictionary<string, string> Rolls);

    /// <summary>
    /// One answer to one query: the listings fetched, how many there were in all, when, any error, and the trade site's
    /// id for the search, which opens it in the browser. Answers saved before the id was kept have none.
    /// </summary>
    internal sealed record Answer(List<Listing> Listings, int Total, DateTime When, string Error, string SearchId = "");

    /// <summary>
    /// A query to run: its cache key, the league and the search body, and which answers make it unnecessary. See
    /// TighterAnswer.
    /// </summary>
    private sealed record Query(string Key, string League, string Body, Func<Answer, bool> Settles, int SearchCap);

    /// <summary>
    /// How many of a search's listings are fetched: the most one fetch request takes, whatever the price method uses.
    /// The site orders listings by its own idea of each currency's worth, which is not NinjaPricer's, so its first five
    /// need not be the five cheapest; the price sorts what is fetched at the valuation's rates. Ten cost no more
    /// requests than five. See Tablets.PriceOfAnswer.
    /// </summary>
    private const int ListingsFetched = 10;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly ConcurrentDictionary<string, Answer> Answers = new(StringComparer.Ordinal);

    /// <summary>
    /// Queries waiting to be sent, each with its priority, the highest next. Under WaitingLock. See Tablets.Priced.
    /// </summary>
    private static readonly Dictionary<string, (Query Query, long Priority)> Waiting = new(StringComparer.Ordinal);

    private static readonly object WaitingLock = new();

    /// <summary>The query being sent now, so it is not queued again meanwhile.</summary>
    private static string _sending = "";

    private static readonly Policy Searches = new("search", "5:10:60,15:60:300,30:300:1800,600:21600:3600");

    private static readonly Policy Fetches = new("fetch", "12:4:10,16:12:300,50:300:300,1000:21600:1800");

    private static int _working;

    /// <summary>
    /// When each query last failed. A failed query is not sent again for FailureRetry, and a failure never replaces a
    /// price already held: the older price stays, stale, until a query succeeds.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTime> Failures = new(StringComparer.Ordinal);

    private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(2);

    private static bool _loaded;

    /// <summary>One save at a time: the background task saves after each answer and the plugin as it unloads.</summary>
    private static readonly object SaveLock = new();

    private static bool _dirty;

    /// <summary>What the client is doing, for the settings panel and the dump.</summary>
    internal static string Status { get; private set; } = "idle";

    /// <summary>Where the answers are kept between sessions. Set by the plugin from its config directory.</summary>
    internal static string CacheFile { get; set; } = "";

    /// <summary>The cached answer for this query, or null when there is none. Never queues anything. See Enqueue.</summary>
    internal static Answer Cached(string key)
    {
        Load();

        return Answers.TryGetValue(key, out var answer) ? answer : null;
    }

    /// <summary>Whether this query is waiting to be sent or being sent now.</summary>
    internal static bool IsQueued(string key)
    {
        lock (WaitingLock)
            return key == _sending || Waiting.ContainsKey(key);
    }

    /// <summary>
    /// Queues a query for the background task at the given priority, raised if it is already waiting lower. Not while it
    /// is being sent, and not within FailureRetry of its last failure. settles names the answers that make it
    /// unnecessary by the time its turn comes. See Next.
    /// </summary>
    /// <param name="searchCap">The most searches in any five minutes this query may be sent within, counting the
    /// player's own. See TabletRerollingSettings.SweepSearchesPerFiveMinutes and HoveredSearchesPerFiveMinutes.</param>
    internal static void Enqueue(string key, string league, string body, Func<Answer, bool> settles,
        long priority, int searchCap, TabletRerollingSettings settings)
    {
        Load();

        if (Failures.TryGetValue(key, out var failed) && DateTime.UtcNow - failed < FailureRetry)
            return;

        lock (WaitingLock)
        {
            if (key != _sending)
                Waiting[key] = Waiting.TryGetValue(key, out var waiting)
                    ? (waiting.Query with { SearchCap = Math.Max(waiting.Query.SearchCap, searchCap) }, Math.Max(waiting.Priority, priority))
                    : (new Query(key, league, body, settles, searchCap), priority);
        }

        Pump(settings);
    }

    /// <summary>How many queries are waiting to be sent.</summary>
    internal static int Pending
    {
        get
        {
            lock (WaitingLock)
                return Waiting.Count;
        }
    }

    /// <summary>
    /// The highest-priority waiting query, taken off the queue once the search limits let it go, or null when none is
    /// waiting. The queue is looked at again every QueuePoll while waiting, so a query queued meanwhile at a higher
    /// priority - the hovered tablet, with its higher cap - goes first. A query an answer has settled since it was
    /// queued is dropped: the answer that settles it is often the one just received.
    /// </summary>
    private static async Task<Query> Next(TimeSpan spacing)
    {
        while (true)
        {
            Query top;

            lock (WaitingLock)
            {
                if (Waiting.Count == 0)
                    return null;

                var next = Waiting.MaxBy(x => x.Value.Priority);

                if (RelatedAnswers(next.Key).Any(x => x.Tighter && next.Value.Query.Settles(x.Answer)))
                {
                    Waiting.Remove(next.Key);
                    continue;
                }

                top = next.Value.Query;

                if (Searches.WaitFor(spacing, (top.SearchCap, 300)) <= TimeSpan.Zero)
                {
                    Waiting.Remove(next.Key);
                    _sending = next.Key;
                    Searches.Record();

                    return top;
                }
            }

            var wait = Searches.WaitFor(spacing, (top.SearchCap, 300));

            Status = $"waiting {wait.TotalSeconds:0}s for the search limit";
            await Task.Delay(wait < QueuePoll ? wait + TimeSpan.FromMilliseconds(50) : QueuePoll);
        }
    }

    /// <summary>How often a query waiting on the search limits looks at the queue again. See Next.</summary>
    private static readonly TimeSpan QueuePoll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A cache key cut into what it searches besides its modifiers, each modifier's minimum or null for any, and the
    /// modifier set it belongs to: the prefix and the modifiers' stats without their minimums.
    /// </summary>
    private sealed record KeyParts(string Prefix, Dictionary<string, int?> Minimums, string ModifierSet);

    private static readonly ConcurrentDictionary<string, KeyParts> PartsByKey = new(StringComparer.Ordinal);

    /// <summary>The cached keys of each modifier set, so the answers related to a key are found without a scan of all.</summary>
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> KeysByModifierSet =
        new(StringComparer.Ordinal);

    /// <summary>Parses a key as TabletSearch.KeyOf writes it: "league|rarity[|mods&lt;=N]|uses=U|stat&gt;=min;stat;...".</summary>
    private static KeyParts PartsOf(string key) => PartsByKey.GetOrAdd(key, static k =>
    {
        var cut = Math.Max(0, k.LastIndexOf('|'));
        var minimums = new Dictionary<string, int?>(StringComparer.Ordinal);

        foreach (var filter in k[(cut + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = filter.IndexOf(">=", StringComparison.Ordinal);

            minimums[at < 0 ? filter : filter[..at]] = at >= 0 &&
                int.TryParse(filter[(at + 2)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var min)
                    ? min
                    : null;
        }

        return new KeyParts(k[..cut], minimums, k[..cut] + "|" + string.Join(";", minimums.Keys.OrderBy(x => x, StringComparer.Ordinal)));
    });

    /// <summary>Keeps an answer and indexes its key under its modifier set.</summary>
    private static void Store(string key, Answer answer)
    {
        Answers[key] = answer;
        Interlocked.Increment(ref _answersVersion);
        KeysByModifierSet.GetOrAdd(PartsOf(key).ModifierSet, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
            .TryAdd(key, 0);
    }

    /// <summary>
    /// The cached answers to other searches of the same modifier set - the same league, rarity, modifier count and uses,
    /// the same modifiers and no others - with how each relates to this key.
    ///
    /// Tighter: every minimum at least this key's (a minimum of "any" is the lowest). A tighter search finds a subset
    /// of this one's listings, so given as many listings as the price takes, this key's price is no higher than its
    /// price: a ceiling. Looser: every minimum at most this key's. A looser search finds a superset, so its price is a
    /// floor for this key's - exactly when this key has as many listings as the price takes, and near it otherwise.
    /// </summary>
    internal static IEnumerable<(string Key, Answer Answer, bool Tighter, bool Looser)> RelatedAnswers(string key)
    {
        var mine = PartsOf(key);

        if (!KeysByModifierSet.TryGetValue(mine.ModifierSet, out var keys))
            yield break;

        foreach (var other in keys.Keys)
        {
            if (other == key || !Answers.TryGetValue(other, out var answer))
                continue;

            var theirs = PartsOf(other);
            var tighter = mine.Minimums.All(m => theirs.Minimums.TryGetValue(m.Key, out var t) && (m.Value is not { } least || t >= least));
            var looser = mine.Minimums.All(m => theirs.Minimums.TryGetValue(m.Key, out var t) && (t is not { } most || m.Value >= most));

            if (tighter || looser)
                yield return (other, answer, tighter, looser);
        }
    }

    /// <summary>Counts every change to the answers, so a figure worked out from many of them knows when to work it out again.</summary>
    internal static int AnswersVersion => Volatile.Read(ref _answersVersion);

    private static int _answersVersion;

    /// <summary>
    /// The cached answers to searches of the same league, rarity, modifier count and uses as this key, for some of its
    /// modifiers and no others, each modifier at a minimum no higher than this key's (a minimum of "any" is the
    /// lowest), with the stats each searched. Not RelatedAnswers: these are other modifier sets, and no bound.
    /// </summary>
    internal static IEnumerable<(Answer Answer, IReadOnlyCollection<string> Stats)> AnswersForFewerModifiers(string key)
    {
        Load();

        var mine = PartsOf(key);

        foreach (var (other, answer) in Answers)
        {
            if (other == key)
                continue;

            var theirs = PartsOf(other);

            if (theirs.Prefix != mine.Prefix || theirs.Minimums.Count == 0 || theirs.Minimums.Count >= mine.Minimums.Count)
                continue;

            if (theirs.Minimums.All(t => mine.Minimums.TryGetValue(t.Key, out var m) && (t.Value is not { } least || m >= least)))
                yield return (answer, theirs.Minimums.Keys);
        }
    }

    /// <summary>Forgets every answer, in memory and on disk.</summary>
    internal static void Clear()
    {
        Answers.Clear();
        Interlocked.Increment(ref _answersVersion);
        KeysByModifierSet.Clear();
        _dirty = true;
        Save(force: true);
    }

    /// <summary>Starts the background task if there is work and it is not already running.</summary>
    private static void Pump(TabletRerollingSettings settings)
    {
        if (Pending == 0 || Interlocked.CompareExchange(ref _working, 1, 0) != 0)
            return;

        var spacing = TimeSpan.FromSeconds(Math.Max(0.5, settings.TradeSite.SecondsBetweenRequests.Value));

        Task.Run(async () =>
        {
            try
            {
                while (await Next(spacing) is { } query)
                {
                    Answer answer;

                    try
                    {
                        answer = await Run(query, spacing);
                    }
                    catch (Exception e)
                    {
                        answer = new Answer([], 0, DateTime.UtcNow, e.Message);
                    }

                    if (answer.Error.Length == 0)
                    {
                        Failures.TryRemove(query.Key, out _);
                        Store(query.Key, answer);
                        _dirty = true;
                    }
                    else
                    {
                        Failures[query.Key] = DateTime.UtcNow;

                        if (!Answers.TryGetValue(query.Key, out var held) || held.Error.Length > 0)
                        {
                            Store(query.Key, answer);
                            _dirty = true;
                        }
                    }

                    // Cleared only once the answer or the failure is recorded, so the query is not queued again meanwhile.
                    lock (WaitingLock)
                        _sending = "";

                    Save(force: false);
                }
            }
            finally
            {
                Status = Pending == 0 ? "idle" : Status;
                Interlocked.Exchange(ref _working, 0);
            }
        });
    }

    /// <summary>One query's search and fetch. Its search turn is already taken and recorded by Next.</summary>
    /// <summary>How long a search's id is used again to open it, rather than a new search. How long the site keeps
    /// one is not known; a search this young is taken to be there still.</summary>
    private static readonly TimeSpan SearchIdReusedFor = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Opens a search on the trade site in the default browser: the cached answer's search when its id is young enough,
    /// else a new search, made within the given cap like any other and then opened. Not cached: the answer it gives is
    /// only the id.
    /// </summary>
    internal static void OpenInBrowser(string key, string league, string body, int searchCap, TabletRerollingSettings settings)
    {
        Load();

        if (Answers.TryGetValue(key, out var held) && held is { Error.Length: 0, SearchId.Length: > 0 } &&
            DateTime.UtcNow - held.When < SearchIdReusedFor)
        {
            Open(league, held.SearchId);
            return;
        }

        var spacing = TimeSpan.FromSeconds(Math.Max(0.5, settings.TradeSite.SecondsBetweenRequests.Value));

        Task.Run(async () =>
        {
            try
            {
                await Searches.WaitTurn(spacing, (searchCap, 300));

                using var search = new HttpRequestMessage(HttpMethod.Post, $"{Site}/search/poe2/{Uri.EscapeDataString(league)}")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };

                search.Headers.UserAgent.ParseAdd(Agent);

                using var searched = await Client.SendAsync(search);
                var text = await searched.Content.ReadAsStringAsync();

                Searches.Read(searched, text);

                if (searched.IsSuccessStatusCode && JObject.Parse(text).Value<string>("id") is { Length: > 0 } id)
                    Open(league, id);
                else
                    Status = $"opening on the trade site failed: search {(int)searched.StatusCode}";
            }
            catch (Exception e)
            {
                Status = $"opening on the trade site failed: {e.Message}";
            }
        });
    }

    /// <summary>When a search was last opened in the browser, for the "Opened in browser" note. MinValue before any.</summary>
    internal static DateTime OpenedAt { get; private set; } = DateTime.MinValue;

    private static void Open(string league, string id)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            $"https://www.pathofexile.com/trade2/search/poe2/{Uri.EscapeDataString(league)}/{id}") { UseShellExecute = true });
        OpenedAt = DateTime.UtcNow;
    }

    private static async Task<Answer> Run(Query query, TimeSpan spacing)
    {
        Status = "searching";

        var url = $"{Site}/search/poe2/{Uri.EscapeDataString(query.League)}";
        using var search = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(query.Body, Encoding.UTF8, "application/json"),
        };

        search.Headers.UserAgent.ParseAdd(Agent);

        using var searched = await Client.SendAsync(search);

        var searchText = await searched.Content.ReadAsStringAsync();

        Searches.Read(searched, searchText);

        if (!searched.IsSuccessStatusCode)
            return new Answer([], 0, DateTime.UtcNow, $"search {(int)searched.StatusCode}: {Shorten(searchText)}");

        var found = JObject.Parse(searchText);
        var id = found.Value<string>("id") ?? "";
        var total = found.Value<int?>("total") ?? 0;
        var ids = (found["result"] as JArray)?.Select(x => x.Value<string>()).Where(x => x != null).Take(ListingsFetched)
            .ToList() ?? [];

        if (ids.Count == 0)
            return new Answer([], total, DateTime.UtcNow, "", id);

        Status = "waiting to fetch";
        await Fetches.WaitTurn(spacing, null);

        Status = "fetching";

        using var fetch = new HttpRequestMessage(HttpMethod.Get,
            $"{Site}/fetch/{string.Join(",", ids)}?query={Uri.EscapeDataString(id)}&realm=poe2");

        fetch.Headers.UserAgent.ParseAdd(Agent);

        using var fetched = await Client.SendAsync(fetch);

        var fetchText = await fetched.Content.ReadAsStringAsync();

        Fetches.Read(fetched, fetchText);

        if (!fetched.IsSuccessStatusCode)
            return new Answer([], total, DateTime.UtcNow, $"fetch {(int)fetched.StatusCode}: {Shorten(fetchText)}", id);

        var listings = new List<Listing>();

        foreach (var result in (JObject.Parse(fetchText)["result"] as JArray) ?? [])
        {
            var price = result?["listing"]?["price"];
            var amount = price?.Value<double?>("amount") ?? 0d;
            var currency = price?.Value<string>("currency") ?? "";
            var indexed = result?["listing"]?.Value<DateTime?>("indexed") ?? DateTime.MinValue;

            Learned.Enqueue(result?["item"]?["explicitMods"] as JArray);

            if (amount > 0d)
                listings.Add(new Listing(amount, currency, indexed, RollsOf(result?["item"]?["explicitMods"] as JArray)));
        }

        return new Answer(listings, total, DateTime.UtcNow, "", id);
    }

    /// <summary>
    /// The explicit modifiers of fetched listings, each naming its affix and its trade stat, for filling in the
    /// modifier table's Trade stat column. Drained on the render thread. See TabletRerollingSettings.Learn.
    /// </summary>
    internal static readonly ConcurrentQueue<JArray> Learned = new();

    /// <summary>
    /// Each explicit modifier's roll on a fetched listing, by trade stat ("explicit.stat_..."), written as the listing
    /// writes it: the first number in its description with its per cent sign when it has one, without a plus
    /// ("Expeditions have +40% ... chance" reads "40%", "Map has 1 additional random Modifier" reads "1"). For the hover
    /// table.
    /// </summary>
    private static Dictionary<string, string> RollsOf(JArray mods)
    {
        var rolls = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var mod in mods ?? [])
        {
            var hash = mod?.Value<string>("hash") ?? "";

            // **The game's markup out first, keeping what it shows.** A description reads "[ContainsExpedition2|Verisium
            // Remnants] have +31% chance...", and the first number in it was the 2 of the tag's name: every listing's
            // runic modifiers roll read 2 (173 of 173 in the cache, 2026-10-09).
            var shown = Regex.Replace(mod?.Value<string>("description") ?? "", @"\[([^\]|]*)\|([^\]]*)\]", "$2");
            shown = Regex.Replace(shown, @"\[([^\]]*)\]", "$1");

            var number = Regex.Match(shown, @"-?\d+(\.\d+)?%?");

            if (hash.StartsWith("stat.", StringComparison.Ordinal) && number.Success)
                rolls[hash["stat.".Length..]] = number.Value;
        }

        return rolls;
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..160] + "...";

    // ------------------------------------------------------------------ the cache on disk

    private static void Load()
    {
        if (_loaded)
            return;

        _loaded = true;

        LoadLimits();

        try
        {
            if (CacheFile.Length == 0 || !File.Exists(CacheFile))
                return;

            var saved = JsonConvert.DeserializeObject<Dictionary<string, Answer>>(File.ReadAllText(CacheFile));

            foreach (var (key, answer) in saved ?? [])
            {
                if (answer?.Listings != null)
                    Store(key, answer);
            }
        }
        catch (Exception)
        {
            // **Kept aside, not overwritten.** An unreadable cache starts empty, and the next save would otherwise write
            // the new answers over every old one.
            try
            {
                File.Move(CacheFile, Path.ChangeExtension(CacheFile, ".unreadable.json"), true);
            }
            catch (Exception)
            {
                // Left where it is; the next save replaces it.
            }
        }
    }

    /// <summary>Writes any unsaved answers now. Called as the plugin unloads, so a reload loses none.</summary>
    internal static void Flush() => Save(force: true);

    /// <summary>
    /// Writes the answers to CacheFile after every one: they come a few seconds apart at most, and an answer written
    /// later is one a reload loses. Through a temporary file swapped in, so a reload mid-write leaves the old file whole.
    /// </summary>
    private static void Save(bool force)
    {
        SaveLimits();

        if (!_dirty || CacheFile.Length == 0)
            return;

        lock (SaveLock)
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile) ?? ".");

            var writing = CacheFile + ".tmp";

            File.WriteAllText(writing, JsonConvert.SerializeObject(Answers.ToDictionary(x => x.Key, x => x.Value)));
            File.Move(writing, CacheFile, true);
            _dirty = false;
        }
        catch (Exception)
        {
            // Tried again on the next answer.
        }
    }

    // ------------------------------------------------------------------ the rate limits

    /// <summary>
    /// One of the site's rate-limit policies: its rules as last stated, our own requests under it, and any lockout.
    /// </summary>
    private sealed class Policy(string name, string rules)
    {
        /// <summary>
        /// Added to every lockout. A request sent the moment a 187-second lockout ended was refused again on 2026-10-09,
        /// that time with no wait stated.
        /// </summary>
        private static readonly TimeSpan LockoutMargin = TimeSpan.FromSeconds(5);

        /// <summary>The wait after a refusal that states none and trips no rule in the state sent with it.</summary>
        private static readonly TimeSpan UnstatedLockout = TimeSpan.FromSeconds(60);

        private readonly object _lock = new();
        private readonly List<DateTime> _sent = [];
        private List<(int Hits, int Period, int Lockout)> _rules = Parse(rules);
        private DateTime _blockedUntil = DateTime.MinValue;

        /// <summary>
        /// The IP's hits per rule as the last response stated them, and when. They include requests this plugin did not
        /// send or no longer remembers - the player's own searches on the website, and everything before a reload.
        /// </summary>
        private List<(int Hits, int Period, int Lockout)> _state = [];

        private DateTime _stateAt = DateTime.MinValue;

        /// <summary>The last refusal, for the status line, or empty when there has been none.</summary>
        private string _refusal = "";

        /// <summary>The rules and the IP's state as the site last stated them, for the status line.</summary>
        internal string Said { get; private set; } = $"{name}: {rules} (built in until a response states its own)";

        /// <summary>
        /// How long until a request may go, without waiting or recording anything: no lockout running, the spacing since
        /// the last request past, one more request within every site rule less one, and within the player's own cap given
        /// as (hits, seconds). See Record.
        ///
        /// Every rule counts the larger of two figures: this plugin's own requests in its period, and the hits the last
        /// response stated for a site rule of that period plus this plugin's requests since. The stated hits include
        /// the player's own searches in the browser, and are taken to stay until that response is a full period old,
        /// because the state does not say when within the period they fell.
        /// </summary>
        internal TimeSpan WaitFor(TimeSpan spacing, (int Hits, int Period)? cap)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var wait = TimeSpan.Zero;

                if (_blockedUntil > now)
                    wait = _blockedUntil - now;

                if (_sent.Count > 0 && now - _sent[^1] < spacing)
                    wait = Max(wait, spacing - (now - _sent[^1]));

                for (var i = 0; i < _rules.Count + (cap.HasValue ? 1 : 0); i++)
                {
                    var site = i < _rules.Count;
                    var (hits, period) = site ? (_rules[i].Hits, _rules[i].Period) : cap!.Value;
                    var length = TimeSpan.FromSeconds(period);
                    var own = _sent.Where(t => now - t < length).OrderBy(t => t).ToList();

                    // A site rule is kept one short of its limit, so a search of the player's own never tips it over.
                    // The player's cap is the number it names.
                    var limit = site ? Math.Max(1, hits - 1) : Math.Max(1, hits);

                    if (own.Count >= limit)
                        wait = Max(wait, own[0] + length - now);

                    var stated = _rules.FindIndex(r => r.Period == period);

                    if (stated >= 0 && stated < _state.Count && _state[stated].Period == period && now - _stateAt < length &&
                        _state[stated].Hits + _sent.Count(t => t > _stateAt) >= limit)
                        wait = Max(wait, _stateAt + length - now);
                }

                return wait;
            }
        }

        /// <summary>Records a request as sent now. See WaitFor.</summary>
        internal void Record()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;

                _sent.Add(now);
                _sent.RemoveAll(t => now - t > TimeSpan.FromHours(6.5));
                _limitsChanged = true;
            }
        }

        /// <summary>What this policy knows, for keeping across a reload. See SaveLimits.</summary>
        internal PolicyKept Kept()
        {
            lock (_lock)
                return new PolicyKept
                {
                    Sent = [.. _sent],
                    BlockedUntil = _blockedUntil,
                    Rules = _rules.Select(r => new[] { r.Hits, r.Period, r.Lockout }).ToList(),
                    State = _state.Select(r => new[] { r.Hits, r.Period, r.Lockout }).ToList(),
                    StateAt = _stateAt,
                };
        }

        /// <summary>Takes back what a reload kept: the requests sent, a lockout running, and the rules and state stated.</summary>
        internal void Restore(PolicyKept kept)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;

                _sent.Clear();
                _sent.AddRange((kept.Sent ?? []).Where(t => now - t <= TimeSpan.FromHours(6.5)));
                _blockedUntil = Max(_blockedUntil, kept.BlockedUntil);

                if (kept.Rules is { Count: > 0 } rules && rules.All(r => r is { Length: 3 }))
                    _rules = rules.Select(r => (r[0], r[1], r[2])).ToList();

                if (kept.State is { } state && state.All(r => r is { Length: 3 }))
                {
                    _state = state.Select(r => (r[0], r[1], r[2])).ToList();
                    _stateAt = kept.StateAt;
                }
            }
        }

        /// <summary>Waits until a request may go, then records it. See WaitFor.</summary>
        internal async Task WaitTurn(TimeSpan spacing, (int Hits, int Period)? cap)
        {
            while (WaitFor(spacing, cap) is var wait && wait > TimeSpan.Zero)
            {
                Status = $"waiting {wait.TotalSeconds:0}s for the {name} limit";
                await Task.Delay(wait + TimeSpan.FromMilliseconds(100));
            }

            Record();
        }

        /// <summary>
        /// Takes the rules, the state and, on a refusal, how long to wait. A refusal's wait is the first of: its
        /// Retry-After header; the "wait N seconds" in its body; the shortest lockout of a rule the state shows at its
        /// limit; one minute. Never the longest rule's lockout - a refusal at 04:17:37 on 2026-10-09 stated no wait and
        /// had every rule under its limit, and taking the longest lockout held searches for an hour.
        /// </summary>
        internal void Read(HttpResponseMessage response, string body)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var stated = Header(response, "X-Rate-Limit-Ip");
                var state = Header(response, "X-Rate-Limit-Ip-State");

                if (stated.Length > 0)
                    _rules = Parse(stated);

                if (state.Length > 0)
                {
                    _state = Parse(state);
                    _stateAt = now;
                }

                // An active lockout is the third field of the state: seconds still to wait.
                foreach (var (_, _, lockout) in _state)
                {
                    if (lockout > 0)
                        _blockedUntil = Max(_blockedUntil, now.AddSeconds(lockout) + LockoutMargin);
                }

                if (response.StatusCode == (HttpStatusCode)429)
                {
                    var (after, source) = RefusalWait(response, body);

                    _blockedUntil = Max(_blockedUntil, now + after + LockoutMargin);
                    _refusal = $", refused at {DateTime.Now:HH:mm:ss} and waiting {after.TotalSeconds:0}s ({source})";
                }

                Said = $"{name}: rules {stated}, state {state} ({DateTime.Now:HH:mm:ss}){_refusal}";
                _limitsChanged = true;
            }
        }

        private (TimeSpan After, string Source) RefusalWait(HttpResponseMessage response, string body)
        {
            if (response.Headers.RetryAfter?.Delta is { } delta)
                return (delta, "Retry-After");

            var said = Regex.Match(body, @"wait (\d+) seconds");

            if (said.Success && int.TryParse(said.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                return (TimeSpan.FromSeconds(seconds), "stated in the reply");

            var tripped = _rules.Where((rule, i) => i < _state.Count && _state[i].Hits >= rule.Hits).Select(rule => rule.Lockout).ToList();

            return tripped.Count > 0
                ? (TimeSpan.FromSeconds(tripped.Min()), "the lockout of the rule at its limit")
                : (UnstatedLockout, "no wait stated");
        }

        private static string Header(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";

        private static List<(int, int, int)> Parse(string text)
        {
            var parsed = new List<(int, int, int)>();

            foreach (var rule in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = rule.Split(':');

                if (parts.Length == 3 &&
                    int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) &&
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) &&
                    int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                    parsed.Add((a, b, c));
            }

            return parsed;
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    }

    /// <summary>Both policies' last stated rules and state, for the status line.</summary>
    internal static string Limits => $"{Searches.Said}; {Fetches.Said}";

    /// <summary>A policy as kept across a reload. All times UTC. See SaveLimits.</summary>
    internal sealed class PolicyKept
    {
        public List<DateTime> Sent { get; set; }
        public DateTime BlockedUntil { get; set; }
        public List<int[]> Rules { get; set; }
        public List<int[]> State { get; set; }
        public DateTime StateAt { get; set; }
    }

    /// <summary>
    /// Where both policies are kept between sessions, beside the answers. Without it a reload forgot the searches just
    /// sent - the player's own cap counted from nothing again - and any lockout running, so the first search after a
    /// reload went out blind. See LoadLimits.
    /// </summary>
    private static string LimitsFile =>
        CacheFile.Length == 0 ? "" : Path.Combine(Path.GetDirectoryName(CacheFile) ?? ".", "tablet_rate_limits.json");

    /// <summary>Whether either policy has changed since the limits were last written.</summary>
    private static volatile bool _limitsChanged;

    /// <summary>Takes back both policies as last kept. Called once, as the answers are first loaded.</summary>
    private static void LoadLimits()
    {
        try
        {
            if (LimitsFile.Length == 0 || !File.Exists(LimitsFile))
                return;

            var kept = JsonConvert.DeserializeObject<Dictionary<string, PolicyKept>>(File.ReadAllText(LimitsFile));

            if (kept?.GetValueOrDefault("search") is { } searches)
                Searches.Restore(searches);

            if (kept?.GetValueOrDefault("fetch") is { } fetches)
                Fetches.Restore(fetches);
        }
        catch (Exception)
        {
            // Unreadable: the limits start from what the site says next, as they did before they were kept.
        }
    }

    /// <summary>Writes both policies when either has changed. Called wherever the answers are saved.</summary>
    private static void SaveLimits()
    {
        if (!_limitsChanged || LimitsFile.Length == 0)
            return;

        try
        {
            _limitsChanged = false;

            var writing = LimitsFile + ".tmp";

            File.WriteAllText(writing, JsonConvert.SerializeObject(new Dictionary<string, PolicyKept>
            {
                ["search"] = Searches.Kept(),
                ["fetch"] = Fetches.Kept(),
            }));
            File.Move(writing, LimitsFile, true);
        }
        catch (Exception)
        {
            _limitsChanged = true;
        }
    }
}
