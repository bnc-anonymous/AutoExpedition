using ExileCore2;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AutoExpedition;

/// <summary>
/// One searchable table of everything the plugin thinks it knows about content.
///
/// **What the plugin believes about an object was spread over seven places.** Two weight sub-tabs
/// with forty six sliders between them, the rune table, the unknown entities list, the must-avoid
/// mods list, a measured size table in Extents, and the classification rules in Weighing. To answer
/// "what does this thing in front of me count for, and how big does the planner think it is" you
/// first had to know which of those it fell into - and for a recognised object there was no size row
/// anywhere in the menu at all, only a table in the source.
///
/// It reads every source, puts one row per thing with the same columns, and says where each number
/// is set. Every stored cell is editable here, from any row that has an id to write against -
/// Editing gates on that and on nothing else. It began as a reference that would edit only the
/// rows for objects it had never seen, on the reasoning that a built-in weight is a category and
/// two places to change one number is how the two drift apart. The second half of that was right
/// and the answer was the opposite one: the sliders went, and this is now the single place a
/// weight, a blast extent or a tag is set.
///
/// **Its own window rather than a panel in the settings.** The settings column is about four
/// hundred pixels and this is nine columns wide. ReAgent's debug window is the same idea.
///
/// It is read-only about the things it cannot honestly edit and quiet about the things it cannot
/// honestly say: a column is blank where the source has no answer, rather than showing a nought
/// that reads like a decision somebody made.
/// </summary>
internal static class Catalogue
{
    /// <summary>
    /// What a row still wants doing to it, which is the only part of "where did this come from"
    /// that anybody can act on.
    ///
    /// **The column used to name the store: built-in, unknown, measured, in this site.** That is
    /// four answers to a question nobody asked - the Set in column already said where a number was
    /// edited, and "in this site" is not a source at all, it is presence, which the Seen count says
    /// better. What was buried in it was the one useful bit: whether the plugin is valuing this
    /// thing on a placeholder nobody has agreed to.
    ///
    /// **Three states of one thing, so they are named alike.** It read "known / priced / needs
    /// pricing", which is an adjective, a verb and an instruction - and "priced" said what had been
    /// done rather than how the row stands. Known is the plugin's own answer, set is somebody
    /// else's, unset is nobody's. "Unknown" is deliberately not among them: it already means a kind
    /// in TargetKind and a list in Unknowns, neither of which is this.
    /// </summary>
    private const string Wants = "unset";

    /// <summary>
    /// The wash behind a row the plugin is guessing at. See Draw.
    ///
    /// Low alpha on purpose. It sits under every cell of the row, including the ones being typed
    /// into, so it has to stay clear of the text rather than compete with it.
    /// </summary>
    private static uint Unanswered =>
        ImGui.GetColorU32(new System.Numerics.Vector4(0.62f, 0.18f, 0.18f, 0.32f));

    /// <summary>
    /// The red a cell wears when what is in it does not parse, or names something that is not there.
    ///
    /// **Saturated, unlike Unanswered.** That wash marks a row wanting an answer; this reports a
    /// fault, and the two must not read the same - a row nobody has priced is ordinary, a row whose
    /// child does not exist is broken. Lighter than the frame red in Marked because it is drawn as
    /// text rather than behind it.
    /// </summary>
    private static readonly System.Numerics.Vector4 Wrong = new(0.95f, 0.45f, 0.45f, 1f);

    /// <summary>
    /// The colour of a value the row does not store, but which is being shown in a box you can type in.
    ///
    /// Dimmed rather than absent, because the box holds two different kinds of value and the reader has
    /// to be able to tell them apart at a glance: black text is this row's own answer, grey text is what
    /// the row would say if nobody wrote one. See Written.
    /// </summary>

    private const string Priced = "custom";
    private const string Known = "default";

    private sealed class Row
    {
        public string Name = "";
        public string Source = "";
        /// <summary>
        /// What this row is, in a word: Entity, Monster, Rune, Effect, Modifier, Wave, Pack, Chest.
        ///
        /// **One label, where there were two.** This was Kind - the scan's TargetKind as a string,
        /// which is empty for over half the table because a wave, a pack, a modifier and a rune are
        /// not things the scan classifies. Beside it sat the table's own Type, and having both was
        /// exactly the confusion the column was meant to end.
        ///
        /// So it resolves the way Weight and Tags already do: the row's own answer where somebody has
        /// written one, and the derived word where nobody has. It decides nothing either way - its
        /// job is to let you find the rows that are like this row, which is what Kind was pretending
        /// to do while also deciding what a thing was worth.
        /// </summary>
        public string Type = "";

        public float? Weight;
        public bool? Stacks;

        /// <summary>In world units, matching the slider on the unknown entities list.</summary>
        public float? Size;

        /// <summary>What the object is, derived. See Tags.</summary>
        public string Marks = "";

        public int Seen;

        /// <summary>
        /// When this object was first written down, or nothing for a shipped weight.
        ///
        /// Discovered rows have carried it all along - Unknowns.Priced.First, saved to the file -
        /// and nothing showed it. It is the one column that tells you what a map has just
        /// introduced: walk into a Grand expedition and a few dozen things arrive at once, priced on
        /// placeholders and sorted in among hundreds of settled rows.
        ///
        /// Blank for built-ins, which have no such moment - the plugin shipped knowing about them.
        /// </summary>
        public DateTime? First;

        /// <summary>Set on an unknown row - the store this row's numbers live in.</summary>
        public Unknowns.Priced Priced;


        /// <summary>What this row reads at the shipped default, for the reset to put back.</summary>
        public float? Shipped;

        /// <summary>What one remnant's waves are worth, for a row whose weight is a share of them.</summary>
        public float Waves;

        /// <summary>Which kind this row's tags are about, for a row that is not a discovered object.</summary>
        public string MarkKey = "";

        /// <summary>
        /// This row's id in the weight reference table files, which is where an edit lands.
        ///
        /// Every row has one: a discovered object under its key, a rune under its name, a recognised
        /// category under its kind and tier. Writing through it is what makes a change survive into
        /// the custom file rather than into whichever store happened to hold the number. See Wrt.Id.
        /// </summary>
        public string Filed = "";

        /// <summary>What the tags would be with nothing written down, so an edit knows when to stop.</summary>
        public string Derived = "";

        /// <summary>Whether this row's propagation is a rune's. See Catalogue.Runed.</summary>
        public bool Runic;



        public string Id = "";

        /// <summary>What it does, where anything can say - shown behind the name.</summary>
        public string Says = "";
    }

    /// <summary>
    /// The settings, kept so the button in the menu can open the window.
    ///
    /// The menu is drawn from the settings object and the window from the plugin, so the button has
    /// no other way to reach the switch it sets. Set on every Draw, which runs whether or not the
    /// window is up.
    /// </summary>
    private static AutoExpeditionSettings _settings;

    /// <summary>Whether the window is on screen, for the button to name itself.</summary>
    public static bool Open => _settings?.ShowCatalogue?.Value == true;

    /// <summary>What the table holds, beside the button, so opening it is an informed choice.</summary>
    public static string Says =>
        _rows.Count == 0 ? "" : $"{_rows.Count} rows";

    /// <summary>Opens the window, or closes it. The button and the key both come here.</summary>
    public static void Toggle()
    {
        if (_settings?.ShowCatalogue != null)
            _settings.ShowCatalogue.Value = !_settings.ShowCatalogue.Value;
    }

    private static List<Row> _rows = new();
    private static DateTime _built = DateTime.MinValue;
    private static string _search = "";
    private static int _sort;
    private static bool _down;

    /// <summary>
    /// How often the rows are rebuilt.
    ///
    /// Reflection over the weight tabs and a pass over the scan is not free and nothing in it moves
    /// at frame rate - a weight changes when somebody drags a slider, and the scan sweeps twice a
    /// second at most. Half a second keeps the table live enough to watch a dig site fill up.
    /// </summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMilliseconds(500);

    /// <summary>Draws the window, if it is switched on. Called from Render.</summary>
    public static void Draw(GameController gc, AutoExpeditionSettings settings, Scan scan)
    {
        _settings = settings;

        if (settings == null || !settings.ShowCatalogue)
            return;

        if (DateTime.UtcNow - _built > Fresh)
        {
            _rows = Build(settings, scan);
            _built = DateTime.UtcNow;
        }

        var open = settings.ShowCatalogue.Value;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(1180f, 640f), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Weight Reference Table###aeCatalogue", ref open))
        {
            ImGui.End();

            // Closing the window with its own cross is the same statement as pressing the button.
            settings.ShowCatalogue.Value = open;

            return;
        }

        settings.ShowCatalogue.Value = open;

        try
        {
            Head();
            Table(settings);
        }
        finally
        {
            ImGui.End();
        }
    }

    /// <summary>The search box and the one sentence that says what the sources mean.</summary>
    private static void Head()
    {
        Helping();
        ImGui.SameLine();
        ImGui.TextUnformatted("Search: ");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(320f);

        var search = _search ?? "";

        if (ImGui.InputText("###aeSearch", ref search, 64))
            _search = search;

        ImGui.SameLine();

        if (ImGui.SmallButton("Clear search###aeClear"))
            _search = "";

        ImGui.SameLine();

        var shown = Shown();
        var wants = 0;

        foreach (var row in _rows)
        {
            if (row.Source == Wants)
                wants++;
        }

        // The breakdown, because "some rows are missing" is a guess and a count is not. Live is what
        // is loaded in the dig site right now, which is nought in a hideout and is not a fault.
        var live = 0;

        foreach (var row in _rows)
        {
            if (row.Seen > 0)
                live++;
        }

        ImGui.TextDisabled($"{shown.Count} of {_rows.Count} rows - {live} in the site, " +
                           $"{wants} needing a price");

        Resets(shown);

        ImGui.Separator();
    }

    /// <summary>
    /// Put everything back, or put back only what the search is showing.
    ///
    /// **Behind a ctrl-click, because there is no undo.** A misclick here throws away every weight
    /// somebody has tuned and every object they walked a dig site to price. The same guard the
    /// reset-everything button in the settings uses, for the same reason.
    ///
    /// The second button is the one that gets used: search for what you are unsure about, look at
    /// what the search leaves, and put back only that. A reset that can only take everything is a
    /// reset nobody dares press.
    /// </summary>
    private static void Resets(List<Row> shown)
    {
        // **Greyed until Ctrl is down, rather than ignoring a click without it.** The same pattern
        // the reset-everything button in the settings uses, and for a better reason than matching
        // it: a button that looks pressable and does nothing reads as broken, so the guard has to
        // be visible before the click rather than only enforced after it.
        var armed = ImGui.GetIO().KeyCtrl;

        ImGui.BeginDisabled(!armed);

        if (ImGui.SmallButton("Reset all to defaults###aeResetAll") && armed)
        {
            foreach (var row in _rows)
                Reset(row);
        }

        ImGui.EndDisabled();

        Held("Puts every row in this table back to its shipped value.\n" +
             "That is every tuned weight and every object you have priced. There is no undo.");

        ImGui.SameLine();

        ImGui.BeginDisabled(!armed);

        if (ImGui.SmallButton(
                $"Reset rows visible in current search ({shown.Count})###aeResetShown") && armed)
        {
            foreach (var row in shown)
                Reset(row);
        }

        ImGui.EndDisabled();

        Held("Puts back only the rows the search is showing.\n" +
             "Narrow the search first, then reset what it leaves.");

        if (armed)
            return;

        ImGui.SameLine();
        ImGui.TextDisabled(" < Hold control to access");
    }

    /// <summary>A tooltip on the control just drawn.</summary>
    private static void Held(string says)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(says);
    }

    /// <summary>
    /// One row back to how it shipped.
    ///
    /// A built-in weight goes to the value a fresh settings object reads; an object you priced goes
    /// back to undecided, which is what puts it back on the needs-pricing list rather than leaving
    /// it looking answered at a number nobody chose.
    /// </summary>
    private static void Reset(Row row)
    {
        // **Putting a row back is dropping your disagreement with it**, which is all it takes now
        // that a weight exists in one place: the shipped table states it and yours overrides it, so
        // forgetting your row IS the reset. There is no settings node left to restore beside it.
        Wrt.Forget(row.Filed);

        if (row.MarkKey.Length > 0)
            Marks.Set(row.MarkKey, "");

        if (row.Priced == null)
            return;

        // **Shipped, not nominal.** A discovered object filed under a kind the plugin has an opinion
        // about starts at that opinion rather than at the nominal one - an entrance at twenty - so
        // putting it back means putting back the prior, not overwriting it with a number that was
        // never this row's default. Resetting used to hand every entrance in the game a weight of
        // one and a red label. See Priors.
        var prior = Priors.Weight(row.Priced.Kind);

        row.Priced.Weight = prior ?? Unknowns.Default;
        row.Priced.Set = prior != null;
        row.Priced.Marks = "";
        row.Priced.Stacks = true;
        row.Priced.Extent = Unknowns.Size;

        Unknowns.Touched();
    }

    /// <summary>
    /// What the columns mean, behind a marker in the corner.
    ///
    /// **The one place in this plugin where a help icon earns itself.** The menu has three and each
    /// covers a single row; this covers nine columns whose meanings are related - a propagation is
    /// aimed at a tag, a tag is what an entity is, a status says whether anybody has checked either -
    /// and explaining them one cell at a time made every cell wide enough to hold a paragraph.
    ///
    /// Wrapped at a fixed width, because a tooltip grows to its longest line: one worked example is
    /// forty words, and unwrapped it would be a single line across the screen.
    /// </summary>
    private static void Helping()
    {
        ImGui.TextDisabled("(?)");

        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 68f);

        Line("Weight", "All weights can be adjusted.");

        Line("Effect",
            "What this row does to everything the chain digs up after it, in one expression.\n" +
            "\n" +
            "\"monster.weight *= +20%\" is twenty per cent more of whatever is tagged monster. " +
            "The target says what it reaches, the attribute says what is scaled - weight is what a " +
            "thing is worth, effect is how strong an effect is - and a factor " +
            "works where an increase does not: \"rare_monster.weight *= 2\".\n" +
            "\n" +
            "\"... as some_stat\" is how two rows say they raise the SAME stat. Rows sharing a " +
            "stat add their shares and the total is one factor in the product; a row naming no " +
            "stat is its own factor and multiplies with everything.\n" +
            "\n" +
            "So a rare monster worth 20, under a relic giving +20% quantity and another " +
            "duplicating rares, is 20 x 1.2 x 2 = 48. Were those two the same stat, they would " +
            "add: 20 x (1 + 0.2 + 1.0) = 44.\n" +
            "\n" +
            "\"... here\" reaches this row's own children and nothing the chain unearths later - " +
            "a remnant's own socketed rune over its own waves.");

        Line("Tags",
            "What a thing IS, as words. An effect aims at a tag - \"monster.weight *= +20%\" - so "
            + "tags are what make a modifier able to find anything.\n"
            + "\n"
            + "Any word works and takes effect at once. Write \"runic_henge\" in a row's Tags "
            + "cell and \"runic_henge.weight *= +10%\" finds it on the next solve - no list to "
            + "be added to and nothing to rebuild. A word nothing carries scores nothing, and the "
            + "table says so rather than refusing it.\n"
            + "\n"
            + "The plugin fills this in where it recognises a thing - a monster marker derives "
            + "monster, a remnant derives remnant - and whatever you write REPLACES that, so a "
            + "cleared cell means \"this answers to nothing\", which is a thing you may mean.\n"
            + "\n"
            + "weight_modifiable decides whether a thing can be scaled at all. Derived for "
            + "monsters and chests and not for relics, remnants, barrels or entrances - write it "
            + "in if you want one of those lifted by an effect.");

        Line("Effect: scaling the other effects",
            "One row scales the OTHER modifiers rather than the monsters. Every rune in game reads " +
            "\"Monsters gain: ...\" except Power, which reads \"Runes gain: Empowered\" - a rate " +
            "on the other rates.\n" +
            "\n" +
            "It is said by what the effect aims at: \"rune.effect *= +50%\". That row's " +
            "percentage then multiplies the whole propagation payout of every link after it, and is " +
            "worth nothing by itself. A group cannot express that - a group of its own would pay " +
            "for the rune even on a chain carrying nothing for it to empower.");


        Line("Stacks",
            "Whether the propagation % can stack. Examples of propagation effects that do not " +
            "stack include \"Runic Monsters are Duplicated\". Read for relic modifiers only - the " +
            "rows filed under found: - which is why every other row leaves it blank. Runes are not " +
            "a case of it: two of a rune never stack, and that is a rule of the game rather than " +
            "something to set here.");

        Line("Size",
            "The entity marker size, in world units. This affects how close the bomb must be to " +
            "hit the entity, and can be tested by pressing \"v\" in game and hovering close to " +
            "it. Almost everything is the smallest an entity can be - 2.25 grid, which is " +
            "24.46 world - and that is what a new unknown entity is given. The exceptions are " +
            "the siren eggs and the sub-area entrance caps, both 17.3 grid.");

        Line("Seen", "How many times you have seen an entity.");

        Line("Status", "");
        Line("known",
            "Entities with values set by the developer of AutoExpedition.");
        Line("unset",
            "Entities you've seen that the developer hasn't seen, and the plugin doesn't know " +
            "about. Will likely have poorly defaulted Weight, Propagation, Size, and Tags. You can " +
            "modify these entries yourself, and any modifications will change their status to " +
            "\"set\", and remove that pesky red line warning towards the entity.");
        Line("set",
            "Unknown entities from \"unset\" that you've seen and changed the values for.");

        Line("Tags: the Verisium Sentry",
            "It has no tags at all, deliberately - it is an allied monster, so nothing should be " +
            "able to scale it and nothing can.");

        Line("Danger zone",
            "You can also \"right click > Forget\" on a Name entry to remove entities if something " +
            "bugs out. Strongly not recommended to Forget any non-buggy entities.");

        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>One heading and its paragraph, or a bare paragraph when the heading is empty.</summary>
    private static void Line(string name, string says)
    {
        if (name.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted(name);
        }

        // TextUnformatted, not TextWrapped: these lines carry per cent signs ("+125%", "the
        // propagation %") and TextWrapped is printf, which ate them. It wraps just the same inside
        // the wrap position Helping pushes. See Mixes.Wrapped.
        if (says.Length > 0)
            ImGui.TextUnformatted(says);
    }

    /// <summary>
    /// Which of the two table files answered for a row, or neither.
    ///
    /// **Custom beats default beats nothing**, and "nothing" is a real answer rather than a small
    /// one: it means the plugin is valuing this on a placeholder nobody agreed to.
    ///
    /// The second argument is the layer underneath the two files - the weights the plugin was built
    /// with, and the rows priced before these files existed. A row answered there but by neither
    /// file reads "default", because that is what it is: the shipped answer, just one that has not
    /// been moved into the shipped file yet.
    /// </summary>
    private static string Standing(string id, bool shipped)
    {
        // **The flag is asked first, because which FILE a row lives in cannot answer this.**
        //
        // A newly discovered object is written into the custom file the moment it is seen - that is
        // where the seeded prior goes - so asking the file said "custom" on every one of them, while
        // Set said nobody had agreed the number and the ground drew a red line over it. One row, two
        // answers, and the column that exists to show this state could never show it. See
        // Unknowns.Unread, which is what the red line reads.
        if (id.Length > 0 && Safe.Read(() => Wrt.Of(id)?.Set, null) == false)
            return Wants;

        var said = Wrt.Status(id);

        return said == "custom" ? Priced
            : said == "default" ? Known
            : shipped ? Known
            : Wants;
    }


    /// <summary>
    /// What this row's status is, and the one place to change it.
    ///
    /// **A question with no way to answer it is not a question, it is a complaint.** A discovered
    /// object arrives seeded and undecided, which puts a red line on the ground saying there is work
    /// here - and there was no gesture anywhere in the plugin that said "this number is right". A
    /// sub-area entrance whose four siblings were already agreed at twenty drew that line
    /// permanently, on an object the scan had classified and the objective had priced.
    ///
    /// Clicking answers it. It toggles, because a click can be a mistake, and because a number that
    /// looked right can stop looking right. Only rows that actually hold the flag are clickable: a
    /// shipped row has none, and Wrt.Row.Set says why - every shipped row is an answer somebody
    /// wrote, so the question does not arise.
    /// </summary>
    private static void Answered(Row row)
    {
        var stored = row.Filed.Length > 0
            ? Safe.Read(() => Wrt.Of(row.Filed)?.Set, null)
            : null;

        if (stored == null)
        {
            // Dimmed where it is settled and plain where it is not: "unset" is the one value in
            // this column that asks for something, so it is the one that should not be greyed out.
            if (row.Source == Wants)
                ImGui.TextUnformatted(row.Source);
            else
                ImGui.TextDisabled(row.Source);

            return;
        }

        if (ImGui.SmallButton(row.Source + "##answered"))
            Unknowns.AnsweredByHand(row.Filed, !stored.Value);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(stored.Value
                ? "Agreed. Click to put the question back - the row keeps its weight and starts " +
                  "asking to be checked again."
                : "Nobody has agreed this weight yet, so the object is marked on the ground.\n\n" +
                  "The number beside it is the starting point for its kind, not a decision. Click " +
                  "to say it is right, or type a different one and then click.");
        }
    }

    /// <summary>The rows the search leaves.</summary>
    private static List<Row> Shown()
    {
        var search = (_search ?? "").Trim();

        if (search.Length == 0)
            return _rows;

        return _rows.Where(r => Matches(r, search)).ToList();
    }

    /// <summary>
    /// Whether the search finds this row, over everything the table draws.
    ///
    /// **Every column that holds words, not a subset of them.** It used to miss the two that a
    /// propagation was typed into, so searching "rare_monster" found the rows TAGGED with it and
    /// not the row aiming at it - which is the row somebody was looking for, since the tag is
    /// derived and the aim is the thing they wrote.
    ///
    /// Those two cells are gone and the Effect column is where a row states what it does, so that
    /// is what the search reads: "rare_monster" finds what aims at rares, "as item_rarity" finds
    /// every row sharing that stat, "*= 2" finds the factors. It is the column most worth
    /// searching and it was the one column of words the search did not look at.
    ///
    /// **Columns only, and nothing the table does not draw.** This used to read a Where field
    /// holding "Weights" or "Weights - Unknown entities" - the settings section a row came from
    /// back when the table was a view over two of them. It appeared in no column, no hover and no
    /// editor, so searching "unknown" returned every discovered row with nothing on screen saying
    /// why, and the search read as broken. The field is gone; the Type column still says "Unknown"
    /// on exactly those rows, which is the same question asked of something visible.
    ///
    /// The key searched is Filed, which is what the Id column draws. Row.Id holds the bare name
    /// inside it - "power" where Filed is "rune:power", "devoureregg_01.ao,..." where Filed is that
    /// with a "found:" in front - and is what the widgets are pushed under rather than anything
    /// shown. Searching it found every row the drawn key would have and none of the prefixes, so
    /// "found:" matched nothing while sitting on screen in the Id column.
    /// </summary>
    private static bool Matches(Row row, string search)
    {
        bool Has(string said) =>
            said != null && said.Contains(search, StringComparison.OrdinalIgnoreCase);

        return Has(row.Name) || Has(row.Type) || Has(row.Source) || Has(row.Marks) ||
               Has(row.Filed) || Has(Effected(row));
    }

    /// <summary>
    /// The table's id, which carries a version - bump it whenever the COLUMN SET changes.
    ///
    /// **ImGui remembers a column's visibility and forgets the default.** DefaultHide is consulted
    /// only where there is no saved state for that column, and state is keyed by the table id and the
    /// column's INDEX - so folding ten columns away did nothing at all: every one of them had a
    /// Visible=1 inherited by position from the sixteen-column version, and the window opened exactly
    /// as wide as before.
    ///
    /// A new id is a table ImGui has never seen, so the defaults apply once and anything you change
    /// afterwards is remembered as usual. It costs the widths you had dragged, which is the right
    /// trade on the one change that moved every column anyway.
    ///
    /// The number is the schema, not the plugin version: leave it alone for a rename, bump it when a
    /// column is added, removed or reordered.
    /// </summary>
    private const string Named = "###aeTable9";

    private static void Table(AutoExpeditionSettings settings)
    {
        var rows = Steady(Shown());

        // **Fit the content, stretch the three that hold sentences.**
        //
        // Every column was WidthStretch under SizingStretchProp, which shares the window out in
        // proportion whatever is in it - so Stacks, a checkbox, claimed the same share of the screen
        // as a name, and the table was half again as wide as it needed to be. Fixed columns size to
        // their widest row; only Name, Propagation and Tags hold anything long enough to want more.
        //
        // Hideable puts ImGui's own column menu on the header - right-click any header to tick
        // columns off - which is the show/hide control, free and standard, rather than a row of
        // checkboxes of mine above the table. Resizable so a column that guesses wrong can be
        // dragged rather than lived with.
        // **Fourteen, and it was ten while eleven columns were being set up.** ImGui takes the count
        // as the truth and drops the surplus, so First seen was set up, written to, and never drawn.
        // Counted from the setup calls below rather than kept in step by hand.
        // **Five columns, and every other one still there to be switched on.**
        //
        // Sixteen columns is a table nobody can read, and most of them answer a question you ask once
        // a month: what is this thing's blast size, when did it first appear, how often is it drawn.
        // They are set up and hidden, so ImGui's own header menu - right-click any header - brings any
        // of them back permanently, and the row's editor shows all of them without switching anything
        // on. Hideable is what makes that menu exist.
        //
        // Propagation % and Multiplicative behaviour are not hidden, they are gone. Effect replaced
        // both: the magnitude, the target and the grouping are one expression now, and the old cells
        // were kept only while the translation was unproven. It is proven - nothing reads Combines for
        // a rune any more, and Propagation % had quietly gone blank on every rune row when their
        // settings bindings were dropped, which is worse than redundant.
        // **Counted from the list, never typed.** The count and the setup calls are two statements
        // of one number, and when the six hidden cells were given columns this was left at fifteen -
        // so they were declared, drawn into and sorted on, and ImGui had never been told they
        // existed. They appeared in no header menu and on no row. See Columns.
        if (!ImGui.BeginTable(Named, Columns.Length,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Sortable |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit |
                ImGuiTableFlags.Hideable | ImGuiTableFlags.Resizable))
            return;

        try
        {
            ImGui.TableSetupScrollFreeze(0, 1);

            foreach (var (header, flags, width) in Columns)
                ImGui.TableSetupColumn(header, flags, width);

            ImGui.TableHeadersRow();

            ReadSort();

            foreach (var row in rows)
                Draw(row);
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>Which column the header was last clicked on, and which way.</summary>
    private static void ReadSort()
    {
        var specs = ImGui.TableGetSortSpecs();

        if (!specs.SpecsDirty || specs.SpecsCount <= 0)
            return;

        _sort = specs.Specs.ColumnIndex;
        _down = specs.Specs.SortDirection == ImGuiSortDirection.Descending;
        specs.SpecsDirty = false;

        // Asking for a different order is not the movement Steady guards against - it is the one
        // time the table is supposed to rearrange itself - so the held order is dropped.
        _order.Clear();
    }

    /// <summary>The order last handed to the table, by row identity. See Steady.</summary>
    private static List<string> _order = new();

    /// <summary>The same identity the row's widgets are pushed under, so the two cannot disagree.</summary>
    private static string Identity(Row row) => row.Id.Length > 0 ? row.Id : row.Name;

    /// <summary>
    /// The rows in order, or exactly the order they were in a moment ago.
    ///
    /// **A table that re-sorts under the cursor is a table you cannot edit.** The list is rebuilt
    /// and re-sorted from live data, and the act of editing changes the data - type a weight into an
    /// unset row and its status becomes "custom", which under a Status sort moves it somewhere else
    /// in the table immediately. It reads as the row vanishing. The same happens under a Weight sort
    /// for the obvious reason, and under Propagation, and Size.
    ///
    /// So while any widget is active the order is frozen: whatever was on screen when the edit
    /// started is what stays on screen until it finishes. Rows are still rebuilt underneath, so the
    /// numbers in them are live - it is only their ORDER that is pinned, which is the part a hand
    /// following a row down the screen depends on.
    ///
    /// Held by IDENTITY rather than by row object, because the list is rebuilt every half second and
    /// the rebuild makes new Row objects - pinning the objects would survive exactly until the next
    /// rebuild and then collapse the table into whatever order Shown happened to produce. The same
    /// identity the widgets are pushed under, so a row's position and its widgets agree about what
    /// it is.
    ///
    /// A row that genuinely goes away - forgotten, or filtered out by a search - goes away, because
    /// the order is rebuilt against the live list every frame rather than kept as a list of rows.
    /// </summary>
    private static List<Row> Steady(List<Row> rows)
    {
        if (!ImGui.IsAnyItemActive() || _order.Count == 0)
        {
            var fresh = Sorted(rows);

            _order = fresh.ConvertAll(Identity);

            return fresh;
        }

        var live = new Dictionary<string, Row>(rows.Count);

        foreach (var row in rows)
            live[Identity(row)] = row;

        var held = new List<Row>(rows.Count);
        var placed = new HashSet<string>();

        foreach (var name in _order)
        {
            if (!live.TryGetValue(name, out var row) || !placed.Add(name))
                continue;

            held.Add(row);
        }

        // Anything the rebuild introduced while the edit was in progress. Appended rather than
        // sorted into place, because sorting it in is the movement this exists to prevent.
        foreach (var row in rows)
        {
            if (placed.Add(Identity(row)))
                held.Add(row);
        }

        _order = held.ConvertAll(Identity);

        return held;
    }

    /// <summary>
    /// What to store for a text cell somebody has emptied.
    ///
    /// **A blank is an answer wherever the shipped row has one to disagree with.** The two layers
    /// merge cell by cell and null means "no opinion", so clearing a cell the shipped file fills
    /// simply restored the shipped value: the Karui totem's effect came back every time it was
    /// deleted, and no amount of deleting it could win. An empty string is an opinion and wins the
    /// merge.
    ///
    /// Null where the shipped row says nothing either, so emptying a cell nobody had filled leaves
    /// the custom file free of cells that state nothing. Putting a row back to the shipped answer is
    /// a different action and still exists - see Wrt.Forget.
    /// </summary>
    private static string Emptied(string filed, Func<Wrt.Row, string> reads)
    {
        var shipped = filed.Length > 0 ? Safe.Read(() => reads(Wrt.Default(filed)), null) : null;

        return string.IsNullOrEmpty(shipped) ? null : "";
    }

    /// <summary>One of the row's stored cells, for sorting. Empty where there is no row to ask.</summary>
    private static string Celled(Row row, Func<Wrt.Row, string> pick) =>
        (row.Filed.Length > 0 ? Safe.Read(() => pick(Wrt.Of(row.Filed)), null) : null) ?? "";

    /// <summary>What a row's Effect cell holds, for sorting and searching. See Effecting.</summary>
    private static string Effected(Row row)
    {
        if (row.Filed.Length == 0)
            return "";

        var filed = Wrt.Of(row.Filed);

        return filed?.Effect ?? "";
    }

    /// <summary>
    /// The largest magnitude a row's Effect states, for ordering that column by what it does.
    ///
    /// **Alphabetical order on this column is close to useless.** The text opens with the target and
    /// buries the number in the middle, so "monster.weight *= +4%" sorts above
    /// "monster.weight *= +16%" on the character after the plus, and the whole column reads as
    /// unordered. What somebody scanning it wants is which rows do the most.
    ///
    /// **The largest rather than the sum**, because a row may state two effects aimed at different
    /// things, and four per cent on chests added to forty on monsters is a number that is true of
    /// nothing. The strongest single thing the row does is at least a fact about the row.
    ///
    /// A flat amount ranks beside a share, since Share carries the raw number for one and a fraction
    /// for the other - so "+= 3" outranks "*= +40%". The ordering has to be useful rather than
    /// dimensionally sound, and nothing in the table states both.
    /// </summary>
    private static float Magnitude(Row row)
    {
        if (row.Filed.Length == 0)
            return float.MinValue;

        var effects = Safe.Read(() => TableGrammar.EffectsOfRow(row.Filed, out _), null);

        if (effects == null || effects.Length == 0)
            return float.MinValue;

        var most = float.MinValue;

        foreach (var effect in effects)
            most = MathF.Max(most, effect.Share);

        return most;
    }

    private static List<Row> Sorted(List<Row> rows)
    {
        // A blank sorts last whichever way the column is pointing, because a missing answer is not
        // a small one - a built-in with no size is not the smallest object in the game.
        // **In the order the columns are set up, and they were all just renumbered.** The indices here
        // are the positions in Table, so folding eleven columns away moved every one of them - a sort
        // that silently orders by the wrong field is the kind of fault nobody reports because it looks
        // like the data.
        IEnumerable<Row> by = _sort switch
        {
            1 => rows.OrderBy(r => Safe.Read(() => TableGrammar.Total(r.Filed).Fixed, 0f)),
            2 => rows.OrderBy(r => r.Weight ?? float.MinValue),
            // By magnitude rather than by text, and by text only to break a tie, so two rows
            // doing the same amount stay in a settled order rather than swapping about. See Magnitude.
            3 => rows.OrderBy(Magnitude).ThenBy(Effected, StringComparer.OrdinalIgnoreCase),
            4 => rows.OrderBy(r => r.Marks, StringComparer.OrdinalIgnoreCase),
            5 => rows.OrderBy(r => r.Source, StringComparer.OrdinalIgnoreCase),
            6 => rows.OrderBy(r => Celled(r, x => x.Children), StringComparer.OrdinalIgnoreCase),
            7 => rows.OrderBy(r => Celled(r, x => x.Matches), StringComparer.OrdinalIgnoreCase),
            8 => rows.OrderBy(r => r.Type, StringComparer.OrdinalIgnoreCase),
            9 => rows.OrderBy(r => r.Size ?? float.MinValue),
            10 => rows.OrderBy(r => Safe.Read(() => Wrt.Of(r.Filed)?.Share, null) ?? float.MinValue),
            11 => rows.OrderBy(r => r.Stacks == null ? 2 : r.Stacks.Value ? 1 : 0),
            12 => rows.OrderBy(r => r.Seen),

            // Blank last whichever way it points, like the other columns that can be empty: a
            // built-in with no first sighting is not the oldest thing in the game.
            13 => rows.OrderBy(r => r.First ?? DateTime.MinValue),
            14 => rows.OrderBy(r => r.Filed, StringComparer.OrdinalIgnoreCase),

            // The six that had no column until they were given one. Without these, clicking their
            // headers fell through to the default and sorted by Name - which is the fault the note
            // above this switch describes, arriving the moment a column was added and not wired up.
            15 => rows.OrderBy(r => Celled(r, x => x.NameInGame), StringComparer.OrdinalIgnoreCase),
            16 => rows.OrderBy(r => Celled(r, x => x.Granter), StringComparer.OrdinalIgnoreCase),
            17 => rows.OrderBy(r => Celled(r, x => x.Kind), StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        };

        var ordered = _down ? by.Reverse() : by;

        return ordered.ToList();
    }

    private static void Draw(Row row)
    {
        ImGui.TableNextRow();

        // **The rows nobody has answered are the whole reason to open this table, and they were the
        // hardest to find in it.**
        //
        // Walking into a Grand expedition puts a few dozen new things on the list at once, every one
        // of them priced on a placeholder - and they arrive sorted among hundreds of rows that are
        // already settled, telling them apart one "unset" at a time down a dim column at the far
        // right. The work is to go through exactly those rows and give them a number, and nothing
        // about the table was pointing at them.
        //
        // The whole row rather than the status cell, because that is what has to be spotted while
        // scrolling, and the columns you then edit are at the other end of it. Faint enough to read
        // the text through - this marks a row for attention, it does not report a fault. See
        // Passes, where a saturated red means something is actually wrong.
        if (row.Source == Wants)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, Unanswered);

        // **The row's identity, and nothing that can change while it is being edited.**
        //
        // This used to include the status, which is precisely a field the act of editing changes:
        // typing a weight into an "unset" row makes it "custom", so every widget in the row got a
        // new ImGui id mid-interaction - which drops keyboard focus and can lose an edit in progress
        // in the neighbouring cells. An id has one job, to be the same next frame.
        ImGui.PushID(row.Id.Length > 0 ? row.Id : row.Name);

        try
        {
            ImGui.TableSetColumnIndex(0);

            // **An invisible strip across the whole row, drawn before anything else in it.**
            //
            // It is what hover and right-click land on, so both work anywhere along the row rather
            // than only on the name - which matters once most of the row's fields are folded away and
            // the editor is the way to reach them. AllowOverlap lets the real widgets in the later
            // columns take their own clicks; without it this would swallow every edit.
            // **As tall as a text box, because the row now holds text boxes.**
            //
            // A row is as tall as the first thing drawn in it, and this strip is plain text height
            // - while an InputText is that plus two lots of frame padding. Every editable cell
            // therefore hung below its own row and over the top of the next one. Asking for frame
            // height here makes the row the height of the tallest thing it can contain, which is
            // what it was already trying to be.
            ImGui.Selectable("##row", false,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap,
                new System.Numerics.Vector2(0f, ImGui.GetFrameHeight()));

            var over = ImGui.IsItemHovered();

            Editing(row);

            ImGui.SameLine(0f, 0f);
            ImGui.TextUnformatted(row.Name);

            if (over || ImGui.IsItemHovered())
                ImGui.SetTooltip(Whole(row));

            ImGui.TableSetColumnIndex(1);
            Totalled(row);

            ImGui.TableSetColumnIndex(2);
            Number(row, row.Weight, 0f, 2000f, 2, "w", (r, v) => r.Weight = v);

            ImGui.TableSetColumnIndex(3);
            Effecting(row);

            ImGui.TableSetColumnIndex(4);
            Marked(row);

            ImGui.TableSetColumnIndex(5);
            Answered(row);

            ImGui.TableSetColumnIndex(6);
            Kids(row);

            ImGui.TableSetColumnIndex(7);
            Binding(row);

            ImGui.TableSetColumnIndex(8);
            Typing(row);

            ImGui.TableSetColumnIndex(9);
            Number(row, row.Size, 4f, 200f, 2, "s", (r, v) => r.Size = v);

            ImGui.TableSetColumnIndex(10);
            Drawn(row);

            ImGui.TableSetColumnIndex(11);
            Stacking(row);

            ImGui.TableSetColumnIndex(12);
            Tallied(row);

            ImGui.TableSetColumnIndex(13);
            First(row);

            ImGui.TableSetColumnIndex(14);
            Called(row);

            ImGui.TableSetColumnIndex(15);
            Cell(row, "name_in_game", x => x.NameInGame, (r, v) => r.NameInGame = v);

            ImGui.TableSetColumnIndex(16);
            Cell(row, "granter", x => x.Granter, (r, v) => r.Granter = v);

            ImGui.TableSetColumnIndex(17);
            Cell(row, "kind", x => x.Kind, (r, v) => r.Kind = v);
        }
        finally
        {
            ImGui.PopID();
        }
    }

    /// <summary>
    /// Whether a second one is worth anything.
    ///
    /// Lifted out of Draw when the row shrank to five columns, unchanged: it is one of the fields the
    /// editor shows and the table hides.
    /// </summary>
    private static void Stacking(Row row)
    {
        if (row.Stacks == null)
        {
            // **A rune has no cell here and that is not the same as having nothing to stack.**
            //
            // A rune IS a modifier, so the general explanation - "this row is a weight rather than
            // a modifier" - reads as a mistake on the thirty four rune rows. The reason those have
            // no cell is a different one: two of a rune never stack, in the game, whatever anybody
            // would like, so there is no choice for a cell to hold. Saying which reason applies is
            // the whole job of this square.
            Blank(row.Filed.StartsWith("rune:", System.StringComparison.Ordinal)
                ? "Not a choice. Two of the same rune never stack - that is the game's rule, not a " +
                  "setting - so the chain pays a rune once, at the earliest link that carries it. " +
                  "See Planner.IsRuneBookedUpstream, which is where the rule lives."
                : "Nothing to stack. Stacking is a question about a relic MODIFIER - whether a " +
                  "second relic granting the same thing is worth anything - and this row is a " +
                  "weight rather than a modifier.");

            return;
        }

        if (row.Filed.Length == 0)
        {
            ImGui.TextUnformatted(row.Stacks.Value ? "yes" : "no");

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Read-only: this row has no id in the table files, so there is nowhere to keep " +
                    "a change. See the Status column.");
            }

            return;
        }

        var stacks = row.Stacks.Value;

        if (ImGui.Checkbox("###stacks", ref stacks))
            Wrt.Set(row.Filed, r => r.Stacks = stacks);
    }

    /// <summary>How many of this the scan has filed. See Row.Seen.</summary>
    private static void Tallied(Row row)
    {
        if (row.Seen > 0)
        {
            ImGui.TextUnformatted(row.Seen.ToString());

            return;
        }

        Blank("Never counted. Only objects the scan files under a key are tallied, so a shipped " +
              "weight has no count and something you have not met yet reads none.");
    }

    /// <summary>When this object was first written down. See Row.First.</summary>
    private static void First(Row row)
    {
        if (row.First == null)
        {
            Blank("No first sighting. This is a weight the plugin shipped with, so there was never " +
                  "a moment somebody met it.");

            return;
        }

        // Local, because it is being read against "when did I walk into that map" rather than
        // against anything else written down.
        var when = row.First.Value.ToLocalTime();

        ImGui.TextDisabled(when.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture));

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"First written down {when:dddd d MMMM yyyy, HH:mm:ss}, " +
                             $"{Ago(DateTime.UtcNow - row.First.Value)} ago.");
        }
    }

    /// <summary>
    /// What this row is called, which is what a Children cell on another row has to name.
    ///
    /// **Read-only.** An id is the row's identity rather than an answer about it: every Children cell
    /// pointing here names this string, so changing it would orphan them all at once - and a child
    /// that resolves to nothing looks exactly like a child nobody has written a row for, which is the
    /// one failure this window cannot afford.
    /// </summary>
    private static void Called(Row row)
    {
        if (row.Filed.Length == 0)
        {
            Blank("No id in the table files: this row is drawn from something the plugin knows " +
                  "about, but there is nothing to write down for it. See the Status column.");

            return;
        }

        ImGui.TextDisabled(row.Filed);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(row.Filed +
                             "\n\nThe key this row is stored and looked up under.\n\n" +
                             "A key with a / in it is also a NAME - what a Children cell on " +
                             "another row writes to take this one. Only the rows something " +
                             "else actually names are shaped that way, because those are the " +
                             "only keys anybody types." + "\n\n" +
                             "The rest are the code's own and a Children cell cannot use " +
                             "them: a found: key carries commas, and that cell splits on " +
                             "commas.");
        }
    }

    /// <summary>
    /// The whole row, read-only, for the hover.
    ///
    /// **Everything the five columns do not show.** Folding eleven columns away is only tolerable if
    /// they are still one gesture from being read - so the hover is the whole row and the editor is
    /// the whole row, and the columns are what you happen to want in front of you.
    /// </summary>
    private static string Whole(Row row)
    {
        var said = new StringBuilder();

        if (row.Filed.Length > 0)
            said.Append(row.Filed).AppendLine();

        // **What the game calls it, above everything the table says about it.** The Name column is
        // shaped for searching - "Strongbox:" isolates the family - which is not the wording on the
        // ground. This is, where anybody has written it down.
        //
        // **Written down, not read.** Nothing fills the In game column: it is typed, like every
        // other cell in the table, and the line used to say "not read yet" as though a read were
        // pending. On the rows that have no such cell to fill that reads as a fault in the plugin.
        //
        // **A rune is the case where the name is genuinely known**, and it needs no cell. Runes hang
        // off a remnant rather than standing in the world, so there is no object to read a label
        // from - but the mod carries a UserFriendlyName and the row is already titled with it, so
        // the game's own spelling is one call away. Derived rather than stored, because a second
        // copy of a name the game states is exactly the duplication the table exists to avoid.
        var ingame = row.Filed.Length > 0
            ? Safe.Read(() => Wrt.Of(row.Filed)?.NameInGame, null)
            : null;

        if (ingame is not { Length: > 0 } && Wrt.Id.RuneNamed(row.Filed) is { } rune)
        {
            // Said out loud, because the In game column beside this one is blank on a rune row and
            // the two would otherwise look like they disagree. They do not: one is a cell and this
            // is the game.
            var called = Safe.Read(() => RuneInfo.Called(rune), null);

            if (called is { Length: > 0 })
                ingame = called + "  (the game's own, not a cell)";
        }

        said.AppendLine().AppendLine(ingame is { Length: > 0 }
            ? "in game: " + ingame
            : "in game: nothing written");

        if (row.Says.Length > 0)
            said.AppendLine().AppendLine(row.Says);

        said.AppendLine();
        Line(said, "type", row.Type);
        Line(said, "children", row.Filed.Length > 0 ? Wrt.Of(row.Filed)?.Children : null);
        Line(said, "matches", row.Filed.Length > 0 ? Wrt.Of(row.Filed)?.Matches : null);
        Line(said, "tags", row.Marks);
        Line(said, "size", row.Size?.ToString("0.##", CultureInfo.InvariantCulture));

        // **A cell the table had nowhere to draw, which is how a number somebody chose went missing.**
        // Carries lost its column when v2 folded Scope, Carries and Combines into Effect, and the
        // folding left the stored value behind: the settings migration had written a tuned figure into
        // it, the Effect cell shows the scope instead, and between them the number was on disk and on
        // no screen. Asked where it was, nothing in the plugin could answer. It is printed whenever it
        // is there - unconditionally, since a cell that shows itself only when somebody already
        // suspects it is the fault this line exists to end.
        Line(said, "granted by", row.Filed.Length > 0 ? Wrt.Of(row.Filed)?.Granter : null);
        Line(said, "share", row.Filed.Length > 0
            ? Wrt.Of(row.Filed)?.Share?.ToString("0.####", CultureInfo.InvariantCulture)
            : null);
        Line(said, "stacks", row.Stacks == null ? null : row.Stacks.Value ? "yes" : "no");
        Line(said, "seen", row.Seen > 0 ? row.Seen.ToString() : null);
        Line(said, "first seen", row.First?.ToLocalTime().ToString("dd MMM HH:mm",
            CultureInfo.InvariantCulture));

        var wrongs = row.Filed.Length > 0
            ? Safe.Read(() => TableGrammar.Checked(row.Filed), null) ?? []
            : [];

        foreach (var wrong in wrongs)
            said.AppendLine().Append("!  ").Append(wrong);

        said.AppendLine().Append("right-click for the whole row");

        return said.ToString();
    }

    /// <summary>
    /// Where every stored cell is surfaced, so a cell cannot be stored and shown nowhere.
    ///
    /// **The invariant the table is supposed to have and did not.** The ask was that the table be the
    /// single source of truth with nothing hidden, and four cells were failing it at once - Carries,
    /// Scope, Combines and Granter were on disk, read by the scoring, and in no column, no editor field
    /// and no hover line. Carries came to light only because a number in it was questioned; the other
    /// three came to light while answering that.
    ///
    /// It went wrong the way this class of thing always does: the Propagation % column was folded away
    /// on the claim that Effect replaced it, the claim was checked by asking whether the two SAY the same
    /// thing, and nothing asked whether every stored value still had somewhere to appear. It is the third
    /// time on this branch - "The Matches column, which was written and never shown" and "Share stops
    /// being invisible" were the first two - which is what makes it a structural fault rather than an
    /// oversight, and why this is a list the compiler and the dump both hold me to rather than a habit.
    ///
    /// Every field on Wrt.Row must appear here. Dump.CellSurfaces reflects over the type and complains
    /// about any that does not, so adding a cell without deciding where it shows is a complaint in the
    /// next dump rather than a value nobody can see.
    /// </summary>
    /// <summary>
    /// Every column of the table, in order, with how it is sized.
    ///
    /// **One list, because a claim about a column has to be checkable against something.** These
    /// were fifteen literal strings inside the drawing code, so Surfaced could say "column" about a
    /// cell that had none and nothing could tell - which is how Combines came to be described as a
    /// column when it is a hover line, and Kind came to claim two surfaces it does not have. The
    /// dump now reads this list and complains when a claim names a header that is not in it.
    ///
    /// Hidden by default is a property of the column and belongs here rather than being a constant
    /// in the middle of the setup: the table shows five and folds the rest, and which five is a
    /// decision about this list.
    /// </summary>
    public static readonly (string Header, ImGuiTableColumnFlags Flags, float Width)[] Columns =
    [
        ("Name", ImGuiTableColumnFlags.WidthStretch, 3f),
        ("Total weight", ImGuiTableColumnFlags.WidthFixed, 96f),
        ("Weight", ImGuiTableColumnFlags.WidthFixed, 70f),
        ("Effect", ImGuiTableColumnFlags.WidthStretch, 3f),
        ("Tags", ImGuiTableColumnFlags.WidthStretch, 2.5f),
        ("Status", ImGuiTableColumnFlags.WidthFixed, 0f),
        ("Children", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide, 2.2f),
        ("Matches", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide, 2.2f),
        ("Type", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 90f),
        ("Size", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 70f),
        ("Share", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 70f),
        ("Stacks", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 0f),
        ("Seen", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 0f),
        ("First seen", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 110f),
        ("Id", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide, 2f),

        // **The three that had no column at all.** Said, Granter and Kind were stored, loaded and
        // saved with nowhere in the table to see them - reachable on a hover, which is not the
        // same thing: a hover shows one row at a time, on demand, and cannot be scanned, sorted or
        // compared. A value nobody can put beside its neighbours is a value nobody can audit.
        //
        // Shown even when the row has nothing in them. A blank cell says "this row does not use
        // this", which is an answer; an absent column says nothing and hides the rows that do.
        ("In game", ImGuiTableColumnFlags.WidthStretch, 1.6f),
        ("Granted by", ImGuiTableColumnFlags.WidthStretch, 1.6f),
        ("Kind", ImGuiTableColumnFlags.WidthFixed, 90f),
    ];

    public static readonly (string Cell, string Column, string Where)[] Surfaced =
    [
        ("Name", "Name", "the column, read only - it is what a row IS called, and it is " +
                          "changed by filing the row under a different id"),
        ("Type", "Type", "column, hover, editor"),
        ("Weight", "Weight", "column, editor"),
        ("Size", "Size", "column, hover, editor"),
        ("Tags", "Tags", "column, hover, editor"),
        ("Stacks", "Stacks", "column, hover, editor"),
        ("Children", "Children", "column, hover, editor"),
        ("Effect", "Effect", "column, editor, and its own tooltip"),
        ("Matches", "Matches", "column, hover, editor"),
        ("Share", "Share", "column, hover, editor"),
        // It claimed the Status column while that column printed which FILE the row was in, so
        // the cell was on disk, load bearing, and nowhere on screen - the fault this list exists to
        // catch, surviving inside the list itself because the check only verifies that the named
        // column exists. The column reads the flag now, and clicking it writes.
        ("Set", "Status", "the Status column, which it is read from and written by, and the red wash"),
        ("NameInGame", "In game", "column, hover, editor"),

        // It was filed here as "the Probed line, and search" and neither existed: Probed reads
        // the cell to classify a row and draws nothing, and the search does not match it. It has a
        // column now, like everything else that is stored.
        ("Kind", "Kind", "column, editor - read by Probed to classify a row"),

        ("Granter", "Granted by", "column, hover, editor"),
        ("Seen", "Seen", "column, hover, editor"),
        ("First", "First seen", "column, hover, editor"),
    ];

    private static void Line(StringBuilder said, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            said.Append(name.PadRight(11)).AppendLine(value);
    }

    /// <summary>
    /// The whole row, editable, in a popup with room to breathe.
    ///
    /// **Every field, laid out down the page rather than across it.** A table wide enough to edit a
    /// children expression and a matches binding and a tags list at once is a table too wide to read,
    /// so the row keeps five columns and this is where the rest live. Right-click anywhere along the
    /// row.
    ///
    /// The fields are the same methods the columns draw, not copies of them - so there is one
    /// implementation per cell, one set of validation messages, and the deferred publishing that
    /// stops a half-typed number re-sorting the table works here without knowing it is here.
    /// </summary>
    private static void Editing(Row row)
    {
        // **Sized before it opens, because a popup otherwise fits itself to its widest label.** These
        // fields hold a children expression, a matches binding and a tags list - sentences, not
        // numbers - and a window that shrinks to the word "Stacks" cannot show any of them. Zero
        // height means grow to whatever the fields need.
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(900f, 0f));

        if (!ImGui.BeginPopupContextItem("###edit"))
            return;

        try
        {
            ImGui.TextUnformatted(row.Name);
            ImGui.Separator();

            if (row.Filed.Length == 0)
            {
                ImGui.TextDisabled("Nothing on this row can be written down: it has no id in the\n" +
                                   "table files. See the Status column.");

                // Still offered: a row with nothing to write down can still be a discovered object
                // somebody wants off the list, which is the one thing left to do with it.
                Forgetting(row);

                return;
            }

            // **Every column, in the order the table lists them.** Some are read-only here, which is
            // the point rather than an omission: this window is the whole row, so a field that cannot
            // be edited still has to be shown, or the only way to see it is to switch its column on -
            // which is the thing the editor exists to avoid.
            Field("Total weight", () => Totalled(row));
            Field("Weight", () => Number(row, row.Weight, 0f, 2000f, 2, "w", (r, v) => r.Weight = v));
            Field("Effect", () => Effecting(row));

            // **Always, not only where there is one.** This was shown when the row already had a
            // carry, on the reasoning that clearing one should make the field go away - and the
            // effect was that a cell the objective still reads was invisible on every row that did
            // not have it, so there was no way to see that a row had none, and no way to tell the
            // field existed at all. A blank field is an answer; a missing one is a hiding place.
            //
            // It is still a v1 cell on its way out - see STATE.md on retiring it - and it is still
            // read: Weighing.Relic takes it off a setting: row and NonStacking takes it off a row
            // that does not stack. Until those two have another source, this is how you see it.
            Field("Tags", () => Marked(row));
            Field("Status", () => ImGui.TextDisabled(row.Source));
            Field("Children", () => Kids(row));
            Field("Matches", () => Binding(row));
            Field("Type", () => Typing(row));
            Field("Size", () => Number(row, row.Size, 4f, 200f, 2, "s", (r, v) => r.Size = v));
            Field("Share", () => Drawn(row));
            Field("Stacks", () => Stacking(row));
            Field("Seen", () => Tallied(row));
            Field("First seen", () => First(row));
            Field("Id", () => Called(row));

            // The rest of what a row stores. These had no column and no field until now, so the
            // only way to read them was a hover, and Kind not even that. See Columns.
            Field("In game", () => Cell(row, "name_in_game", x => x.NameInGame,
                (r, v) => r.NameInGame = v));
            Field("Granted by", () => Cell(row, "granter", x => x.Granter,
                (r, v) => r.Granter = v));
            Field("Kind", () => Cell(row, "kind", x => x.Kind, (r, v) => r.Kind = v));

            Forgetting(row);
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    /// <summary>One labelled field in the row editor, so the labels line up. See Editing.</summary>
    private static void Field(string name, Action draw)
    {
        ImGui.TextUnformatted(name);
        ImGui.SameLine(130f);

        // The rest of the line, whatever the popup turned out to be - the cells themselves ask for
        // -1 and would get it anyway, but a Number draws at its own width unless it is told.
        ImGui.SetNextItemWidth(-1f);
        draw();
    }

    /// <summary>
    /// What this row is worth with everything it brings, and the arithmetic behind it on hover.
    ///
    /// **If a number in this plugin cannot be explained by a popup, it is a bug in the table.** That
    /// is the claim the rewrite makes, and this cell is where it is kept: every child, its count, what
    /// one of it is worth, and the product, down to the leaves.
    ///
    /// Read-only, because it is not an answer - it is what the answers add up to. A row worth more
    /// than its own weight is a row with children, and the children are where an edit belongs.
    /// </summary>
    private static void Totalled(Row row)
    {
        if (row.Filed.Length == 0)
        {
            Blank("No id in the table files, so there is no row to roll up. See the Status column.");

            return;
        }

        // **A row with nothing stored is worth what it is priced at, and that is not a rollup.** A
        // discovered object is priced by Unknowns rather than by a table row, so asking the table what
        // it totals to answers nought while the Weight column beside it reads ten. Its total IS its
        // weight, because it has no children to bring anything.
        if (Wrt.Of(row.Filed) == null)
        {
            ImGui.TextDisabled(TableGrammar.Printed(row.Weight ?? 0f));

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Nothing is written down for this row, so it is worth what it is " +
                                 "priced at and nothing more. See the Status column.");
            }

            return;
        }

        var rolled = TableGrammar.Total(row.Filed);

        if (rolled.Wrong?.Length > 0)
        {
            ImGui.TextColored(Wrong, "?");

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(rolled.Wrong);

            return;
        }

        // Dimmed while it is only this row's own weight, plain once children add to it: the
        // interesting case is a row worth more than it says, so that is the one to catch the eye.
        var own = TableGrammar.Printed(row.Weight ?? 0f);
        var text = rolled.ToString();

        if (string.Equals(text, own, StringComparison.Ordinal))
            ImGui.TextDisabled(text);
        else
            ImGui.TextUnformatted(text);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Safe.Read(() => TableGrammar.Explain(row.Filed), "") ?? "");
    }

    /// <summary>
    /// What else you get for blowing this up.
    ///
    /// **The cell that takes composition out of code.** A remnant is three waves of so many monsters;
    /// a monstermarker is 0.85 of a white one and 0.15 of a blue one; a strongbox's packs come off its
    /// modifiers. All three were a switch in Weighing, none of them is a preference, and every one is
    /// a fact about the game - which is what a reference table is for.
    ///
    /// A count is a float so a fraction is a probability, and "x matched.Values[-1]" takes it from the
    /// modifier this row matched rather than from the cell. There is no written fallback for a value
    /// the game did not state: that turns a broken read into a plausible wrong answer, which is how
    /// the strongbox pack counts came to be read off a ground label with nothing saying so.
    /// </summary>
    /// <summary>
    /// One stored string, shown and editable, with nothing derived and nothing hidden.
    ///
    /// **Blank where the row does not use it, which is the point.** These cells are sparse - four
    /// rows in the whole table carry a Granter - so the columns are mostly empty, and an empty cell
    /// is the answer "this row does not use this". The alternative, which is what the table did,
    /// is no column at all and a value that exists on disk and nowhere on screen.
    ///
    /// No derivation and no placeholder: what is drawn is what is stored. Where a cell has a
    /// grammar of its own and can be translated from older ones, that is Effect's job and Effect
    /// has the machinery for it. See Written's derived overload.
    /// </summary>
    private static void Cell(Row row, string id, Func<Wrt.Row, string> reads,
        Action<Wrt.Row, string> set)
    {
        if (row.Filed.Length == 0)
        {
            Blank("No id in the table files, so there is nowhere to keep a change. See the Status " +
                  "column.");

            return;
        }

        var key = Keyed(row) + "/" + id;
        var stored = Safe.Read(() => reads(Wrt.Of(row.Filed)), null) ?? "";

        if (!Written(key, id, stored, Room, out var said))
            return;

        var plain = said.Trim();

        Wrt.Set(row.Filed, r => set(r, plain.Length == 0 ? Emptied(row.Filed, reads) : plain));
    }

    private static void Kids(Row row)
    {
        if (row.Filed.Length == 0)
        {
            Blank("No id in the table files, so there is nowhere to keep a change. See the Status " +
                  "column.");

            return;
        }

        var stored = Wrt.Of(row.Filed)?.Children ?? "";
        var key = Keyed(row) + "/children";
        var done = Written(key, "children", stored, Room, out var said);
        var live = Live(key, stored);

        Wrongly(Complaints(row.Filed, live));

        if (!done)
            return;

        var plain = said.Trim();

        Wrt.Set(row.Filed, r => r.Children =
            plain.Length == 0 ? Emptied(row.Filed, x => x.Children) : plain);
    }

    /// <summary>
    /// What this row does to everything it reaches, in one expression.
    ///
    /// **One cell where Propagation %, Multiplicative behaviour and the carry number were three.**
    /// Those said the magnitude, the target and the grouping in three places and three notations, so a
    /// row could not be read as a sentence - and a Soul Rune propagating "monster=4" beside a weight
    /// of 8.56 was two numbers with no visible connection between them. Here it is
    /// "monster.weight *= +4%", written the way the game words it on the object.
    ///
    /// **The attribute says what is being scaled**, which used to be inferred from whatever the target
    /// happened to be - weight for a thing, propagation for an effect - and was therefore a rule
    /// nowhere on the row. Four shipped rows are both at once: the goblin relic is worth 10 and carries
    /// 15, and "relic *= +50%" cannot say which half it means. Left off it means weight, and an
    /// attribute that is wrong for its target is refused rather than guessed. See TableGrammar.Attributes.
    ///
    /// The optional "as" clause says the one thing grouping ever meant: shares a stat with. Two rows
    /// naming the same stat add their contributions and the stat total is one factor; a row naming no
    /// stat is its own factor. So "rare_monster *= +50% as rare_monster_count" twice is x2.0, and the
    /// same pair without the clause is x2.25.
    ///
    /// **A percentage or a factor, and the cell keeps whichever was typed.** "+16%" and "1.16" parse
    /// the same; the percentage is what the game prints and what a deliberate tie-break survives in -
    /// two runes separated at 16 and 16.01 read as +16% and +16.01%, where as factors they are 1.16
    /// and 1.1601.
    ///
    /// **Empty is an answer, not an absence.** Clearing the cell on a row the shipped file fills
    /// stores a blank rather than no opinion, because the two layers merge cell by cell and no
    /// opinion means the shipped answer comes back. See Emptied.
    /// </summary>
    private static void Effecting(Row row)
    {
        if (row.Filed.Length == 0)
        {
            Blank("No id in the table files, so there is nowhere to keep a change. See the Status " +
                  "column.");

            return;
        }

        var filed = Wrt.Of(row.Filed);
        var stored = filed?.Effect ?? "";

        var key = Keyed(row) + "/effect";
        var done = Written(key, "effect", stored, Room, " ", out var said);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(stored.Length > 0
                ? "This row's own answer.\n\n" +
                  "\"monster.weight *= +4%\" is four per cent more of everything tagged monster. A " +
                  "factor works too - \"rare_monster.weight *= 2\" - for an effect that is " +
                  "not an increase.\n\n" +
                  "The attribute says WHAT is scaled: weight is what a thing is worth, " +
                  "effect is how strong an effect is. Leaving it off means weight, and a rune has " +
                  "no weight - so a rune is scaled with \"rune.effect\".\n\n" +
                  "\"excavated_chest.weight += 3\" adds three flat weight instead.\n\n" +
                  "\"... as some_stat\" where this shares a stat with another row: only then do the " +
                  "two add rather than multiply.\n\n" +
                  "\"... here\" where it reaches this row's own children and nothing the chain " +
                  "unearths later."
                : "Nothing stored - this row prices a thing rather than changing what other " +
                  "things are worth.");
        }

        Wrongly(Complaints(row.Filed, null));

        if (!done)
            return;

        var plain = said.Trim();

        Wrt.Set(row.Filed, r => r.Effect =
            plain.Length == 0 ? Emptied(row.Filed, x => x.Effect) : plain);
    }

    /// <summary>
    /// What this row is, for grouping and searching, and for nothing else.
    ///
    /// **It carries no arithmetic, which is what separates it from the Kind it replaced.** Kind was
    /// the classifier the scan produced AND the thing the weight was looked up by, so calling
    /// something a Chest decided what it was worth. A Type is a label: it lets you find the rows that
    /// are like this row, and nothing reads it.
    ///
    /// Falls back to the derived kind, dimmed, for a row nobody has typed one on - mostly discovered
    /// objects, which is exactly where the old column said nothing at all.
    /// </summary>
    private static void Typing(Row row)
    {
        if (row.Filed.Length == 0)
        {
            ImGui.TextDisabled(row.Type);

            return;
        }

        var key = Keyed(row) + "/type";
        var done = Written(key, "type", row.Type, Room, out var said);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "What this row is, in a word - Entity, Monster, Rune, Effect, Modifier, Wave, Pack, " +
                "Chest.\n\nIt decides nothing. Its job is to let you find the rows that are like " +
                "this one, which is what the old Kind column was doing while also deciding what a " +
                "thing was worth.\n\nWhere you have not written one, this is the word the scan " +
                "derived.");
        }

        if (!done)
            return;

        var plain = said.Trim();

        Wrt.Set(row.Filed, r => r.Type =
            plain.Length == 0 ? Emptied(row.Filed, x => x.Type) : plain);
    }

    /// <summary>
    /// What the table says this row is, or the derived word where it says nothing. See Row.Type.
    /// </summary>
    private static string Typed(string filed, string derived) =>
        (filed.Length > 0 ? Safe.Read(() => Wrt.Of(filed)?.Type, null) : null) ?? derived ?? "";

    /// <summary>
    /// How often a fresh socket comes up as this rune.
    ///
    /// **Only rune rows have one, and only two things read it**: the draw pool's total and the reroll
    /// advisor's enumeration over outcomes. It never touches the score of a remnant whose runes are
    /// known, which is every remnant the planner sees.
    ///
    /// Measured rather than read from the game, and that is settled rather than open -
    /// Expedition2RunesWeight is named for the weights and exposes none. Which runes a level admits is
    /// readable from its Level field; how often each comes up is not.
    /// </summary>
    private static void Drawn(Row row)
    {
        if (row.Filed.Length == 0 || !row.Filed.StartsWith("rune:", StringComparison.Ordinal))
        {
            Blank("Only a rune is drawn from a pool, so only a rune has a share of it.");

            return;
        }

        Number(row, Wrt.Of(row.Filed)?.Share, 0f, 1f, 4, "share",
            (r, v) => r.Share = v);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "How often a fresh socket comes up as this rune, as a fraction - Adaptive is about " +
                "0.095, Tempest about 0.002.\n\nUsed when pricing a REROLL and nowhere else: a " +
                "remnant whose runes you can already read is scored from those runes, not from this." +
                "\n\nMeasured, not read from the game - Expedition2RunesWeight is named for the " +
                "weights and carries none.");
        }
    }

    /// <summary>
    /// What binds this row to something in the world.
    ///
    /// **The layer that had no table representation at all**, and then briefly had one nobody could
    /// see. Which row a scanned entity answers to was decided by Scan.Kind and a tier switch, so the
    /// table could not say that elitemarker.ao spawns a rare monster - and once it could, the cell
    /// saying so was invisible.
    ///
    /// Five forms, one per thing the game states about an object: its art file, its minimap icon, its
    /// metadata path, and either of its two modifier lists. The longest match wins, so a family and one
    /// of its members can both be written and the specific one answers.
    /// </summary>
    private static void Binding(Row row)
    {
        if (row.Filed.Length == 0)
        {
            Blank("No id in the table files, so there is nowhere to keep a change. See the Status " +
                  "column.");

            return;
        }

        var stored = Wrt.Of(row.Filed)?.Matches ?? "";
        var key = Keyed(row) + "/matches";
        var done = Written(key, "matches", stored, Room, out var said);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "What ties this row to something in the game, and how the scan finds it.\n\n" +
                "art:elitemarker_02.ao       the model it is wearing\n" +
                "icon:RewardChestCurrency    its minimap icon\n" +
                "path:Metadata/Chests/...    its whole metadata path\n" +
                "mod:ChestSummonRares        a modifier it carries, with its values\n\n" +
                "Several, comma separated, all bind to this row - which is how one row takes both " +
                "ids of a thing the game names twice.\n\n" +
                "A rule matches the WHOLE name. Write every model of a family out rather than a " +
                "stem: you can then read the row and know exactly what it answers for, and a model " +
                "the game adds turns up as an unrecognised row instead of being quietly absorbed.\n\n" +
                "A star at either end opens that end - art:elitemarker* takes whatever the game " +
                "adds next. Where two rules both answer the longer wins, measured without stars.\n\n" +
                "Empty means nothing in the world resolves to this row on its own - it is reached by " +
                "being somebody's child, or by an id the code still names.");
        }

        Wrongly(Complaints(row.Filed, null));

        if (!done)
            return;

        var plain = said.Trim();

        Wrt.Set(row.Filed, r => r.Matches =
            plain.Length == 0 ? Emptied(row.Filed, x => x.Matches) : plain);
    }

    /// <summary>
    /// What is wrong with a row's v2 cells, reading what is being typed rather than what is stored.
    ///
    /// <paramref name="typing"/> is the live Children text where that is the cell being edited, so a
    /// complaint follows the keystrokes instead of arriving a moment after the edit lands. Null asks
    /// about the stored row.
    /// </summary>
    private static string Complaints(string filed, string typing)
    {
        if (typing != null)
        {
            TableGrammar.Children(typing, out var wrong);

            if (wrong.Length > 0)
                return wrong;
        }

        var wrongs = Safe.Read(() => TableGrammar.Checked(filed), null) ?? [];

        return wrongs.Length == 0 ? "" : string.Join("; ", wrongs);
    }

    /// <summary>A red mark beside a cell, with the reason on hover. Silent when there is nothing.</summary>
    private static void Wrongly(string wrong)
    {
        if (wrong.Length == 0)
            return;

        ImGui.SameLine();
        ImGui.TextColored(Wrong, "!");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(wrong);
    }




    /// <summary>
    /// How much a cell will take, in characters.
    ///
    /// **One number, because five arbitrary ones were five silent walls.** ImGui stops accepting
    /// keystrokes at the buffer's end with nothing on screen to say so, and the caps here were
    /// inherited per cell - 96, 128, 160 - with no reason behind any of them. A stat name reaching
    /// the shortest of them would have been truncated mid-word by a limit nobody chose.
    ///
    /// Generous rather than tuned: the buffer is a few hundred bytes per drawn cell per frame,
    /// which is nothing beside the table it sits in, and the files behind it have no length limit
    /// at all - Wrt writes what it is given.
    /// </summary>
    private const uint Room = 256;

    /// <summary>
    /// A cell with no answer, and the reason on hover.
    ///
    /// **A bare dash reads as a refusal.** It was used for two different things - a column this row
    /// has no answer to, and a column whose answer lives somewhere this table cannot write - and a
    /// player cannot tell those apart by looking, so both read as the plugin declining. That is the
    /// complaint this exists to end: every inert cell now says what it is and why, in a sentence.
    ///
    /// Which cells are inert is not arbitrary. A shipped weight is a NUMBER, not a thing in the dig
    /// site: it has no extent to measure, nothing to pass on, and no stacking or combining rule,
    /// because those describe modifiers and a weight is not one. Twenty seven of the forty four
    /// shipped weights have no kind the table files can name either, so their tags have nowhere to
    /// live. The number itself is always editable - it writes its own settings node, which is the
    /// store the objective reads.
    /// </summary>
    private static void Blank(string why)
    {
        ImGui.TextDisabled("-");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(why);
    }

    /// <summary>The cell being typed into, and what is in it. See Written.</summary>
    private static string _editingIn = "";

    private static string _editing = "";

    /// <summary>What identifies a row's cell, matching the id the table pushes for it.</summary>
    private static string Keyed(Row row) =>
        row.Source + "/" + (row.Id.Length > 0 ? row.Id : row.Name);

    /// <summary>What is in a cell right now - what is being typed, or what is stored.</summary>
    private static string Live(string key, string stored) =>
        string.Equals(_editingIn, key, StringComparison.Ordinal) ? _editing : stored;

    /// <summary>
    /// A cell you type into, which does not publish what you typed until you have finished.
    ///
    /// **Publishing on every keystroke moved the row out from under the cursor.** The table sorts
    /// on whichever column you picked, so a weight going 4, 40, 401, 40.1 as it is typed re-sorts
    /// the table four times and the box you are typing into walks up and down the screen. Worse,
    /// the three intermediate numbers were each written to the custom file and each re-scored the
    /// plan.
    ///
    /// So the text is held here while the box has focus and handed back once - on Enter, or when
    /// the box loses focus, which are the two ways a person finishes with a field. Escape drops it,
    /// because ImGui reverts the buffer and reports no edit.
    ///
    /// It has to be held rather than read back from the row for a second reason: the rows are
    /// rebuilt from their stores twice a second, and a rebuild in the middle of a word would
    /// overwrite what somebody was writing.
    /// </summary>
    private static bool Written(string key, string id, string stored, uint size, out string said) =>
        Written(key, id, stored, size, "", out said);

    /// <summary>
    /// The same, for a cell whose value the row does not store but derives.
    ///
    /// <paramref name="derived"/> is what the row would say with nothing written on it - an Effect cell
    /// with no effect is not blank, because the row's older Scope, Carries and Combines still say
    /// something and TableGrammar.Translated can read them.
    ///
    /// **It is real text in the box, not a greyed-out placeholder.** A placeholder cannot be edited: to
    /// change one figure in "monster.weight *= +40% as item_rarity_monsters" somebody had to retype the
    /// whole expression from memory, so the cell was visible and not usably editable - which fails the
    /// rule the table exists for.
    ///
    /// **And it is not dimmed either.** Derived text used to be drawn in a darker colour until the
    /// cell was clicked into, so a column held half stored rows and half faint ones and the faint
    /// ones read as disabled - the state of a cell said in a way that also said "you cannot use
    /// this". Where a value comes from is worth knowing and belongs in the hover, which says it in
    /// words. The column reads as one column.
    ///
    /// The trap that placeholder was avoiding is real and is handled instead by comparing on the way
    /// out. Clicking into a derived cell, or pressing Enter in it, must not store the derivation as
    /// though somebody had written it - a row that was tracking the shipped answer would silently
    /// become a custom row saying the identical thing, and then stop tracking it. So a commit whose text
    /// still equals the derivation is not an edit and stores nothing. Change one character and it is.
    /// </summary>
    private static bool Written(string key, string id, string stored, uint size, string derived,
        out string said)
    {
        said = Live(key, stored);

        // A single space is what the caller passes for "no derivation", so trim before believing it.
        var offered = derived.Trim();

        if (offered.Length > 0 && said.Length == 0)
            said = derived;

        ImGui.SetNextItemWidth(-1f);

        var entered = ImGui.InputText($"###{id}", ref said, size,
            ImGuiInputTextFlags.EnterReturnsTrue);

        if (ImGui.IsItemActive())
        {
            _editingIn = key;
            _editing = said;
        }

        if (!entered && !ImGui.IsItemDeactivatedAfterEdit())
            return false;

        if (string.Equals(_editingIn, key, StringComparison.Ordinal))
        {
            _editingIn = "";
            _editing = "";
        }

        // Unchanged from what the row already derives, so there is nothing to store. See above.
        return offered.Length == 0 ||
               !string.Equals(said.Trim(), offered, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a thing is - derived where the plugin can, typed where it cannot.
    ///
    /// **Only the discovered rows take an answer, and only they need to.** Everything the plugin
    /// recognises has a kind and a tier to derive from, and a hand-written tag there would be
    /// somebody disagreeing with the game about what a chest is. An object it could not place has
    /// nothing to derive from at all - which is exactly the object a scope cannot otherwise reach.
    ///
    /// Empty means derive. Wrong names are ignored and shown red, the same as a scope.
    /// </summary>
    private static void Marked(Row row)
    {
        if (row.Filed.Length == 0)
        {
            ImGui.TextDisabled(row.Marks.Length > 0 ? row.Marks : "-");

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Read-only: tags say what an OBJECT is, and this row is a shipped weight with " +
                    "no kind behind it - the wave weights, the per-socket bonus and the relic " +
                    "numbers all price something that has no marker of its own. Nothing the " +
                    "planner classifies is missing from this column; the rows that are, are the " +
                    "rows whose tags would describe nothing.");
            }

            return;
        }

        // **The derived tags, as text that is really there.** They were the input's hint, which
        // looks the same and is not: you cannot select it, cannot correct one word of it, and
        // clicking to edit empties the box. A patch that moves something is the case this exists
        // for, and it arrives as "these tags are nearly right", not as a blank page.
        // **An empty cell on a row made only of other rows, and it means something.** A tag marks
        // what a modifier aims at, and a modifier aims at a weight; pack/rare, elitemarker, caged,
        // remnant/wave and the mod/ rows have none of their own. An uplift scoped to rare monsters
        // lands on monster/rare, and their totals rise because they contain it - so tagging them
        // too would read as the uplift arriving twice. Said out loud here, because a blank cell
        // with no explanation is exactly what makes somebody ask.
        if (row.Weight == null && Celled(row, x => x.Children).Length > 0 &&
            (row.Marks ?? "").Length == 0)
        {
            Blank("No weight of its own, so nothing aims at this row directly - it is made of the " +
                  "rows in its Children cell, and a modifier reaches it through them. Tag those " +
                  "rather than this one, or an uplift reads as arriving twice.");

            return;
        }

        var key = Keyed(row) + "/marks";
        var shown = Live(key, row.Marks ?? "");
        var bad = Tags.Read(shown).Length != shown.Split(',').Length &&
                  shown.Trim().Length > 0;

        if (bad)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg,
                new System.Numerics.Vector4(0.55f, 0.20f, 0.20f, 0.85f));
        }

        var done = Written(key, "marks", row.Marks ?? "", Room, out var said);

        if (bad)
            ImGui.PopStyleColor();

        if (!done)
            return;

        // Written down only while it differs from what would be derived, so a row nobody has
        // touched stays derived and keeps following the game rather than freezing at today's
        // answer. See Marks.
        //
        // **Except an emptied cell, which is a decision and not an absence.** Null means "no
        // opinion" and Tags.Of then falls through it: to the row this object matches, to the row
        // for its kind, and finally to the derivation. So clearing the tags off a strongbox mound
        // handed it the tags of the strongbox row it matches, and the cell filled itself back in -
        // which reads as an edit that will not save.
        //
        // The shortcut only applies to a value somebody typed. Blank is stored as blank, and
        // Tags.Of returns it: it tests for null, not for length, precisely so that "this carries no
        // tags" can be said. See Wrt.Thinned, which compares tags Ordinal for the same reason.
        var plain = string.Join(", ", Tags.Read(said));
        var same = plain.Length > 0 &&
                   string.Equals(plain, row.Derived, StringComparison.OrdinalIgnoreCase);

        Wrt.Set(row.Filed, r => r.Tags = same ? null : said.Trim());

        row.Marks = said;
    }



    /// <summary>How long ago, in the largest unit that says something.</summary>
    private static string Ago(TimeSpan since) =>
        since.TotalDays >= 1d ? $"{since.TotalDays:0.#} days"
        : since.TotalHours >= 1d ? $"{since.TotalHours:0.#} hours"
        : since.TotalMinutes >= 1d ? $"{since.TotalMinutes:0} minutes"
        : "moments";

    /// <summary>
    /// Right-click a discovered row to drop it.
    ///
    /// **A list that fills itself needs a way to take something out of it.** Every object a blast
    /// touches writes itself down, and some of what it writes is wrong - a relic filed before its
    /// label loaded reads "???", and a key spelt from a reading that will never recur can never
    /// match anything again. Forget all clears the lot; this is for the one row that should not be
    /// there.
    ///
    /// Behind a right-click rather than a column of buttons, because the table is already wider than
    /// it wants to be and this is pressed once in a session.
    ///
    /// Only the discovered rows: a built-in weight is a category the plugin knows about, and
    /// forgetting it would mean forgetting how to price something rather than tidying a list.
    /// </summary>
    private static void Forgetting(Row row)
    {
        if (row.Priced == null || row.Id.Length == 0)
            return;

        ImGui.Separator();

        if (!ImGui.MenuItem("Forget this entry"))
            return;

        Unknowns.Forget(row.Id);

        // Rebuilt now rather than in half a second, so the row goes when it is clicked.
        _built = DateTime.MinValue;

        ImGui.CloseCurrentPopup();
    }

    /// <summary>
    /// One number: a box when the row owns it, plain text when somewhere else does, a dash when
    /// there is no answer.
    /// </summary>
    private static void Number(Row row, float? value, float least, float most, int places,
        string id, Action<Wrt.Row, float?> set)
    {
        // **An empty cell is an empty BOX.** It used to be a sentence explaining why there was
        // nothing there, which cannot be typed into - so a row with no weight could not be given
        // one in the column whose job is weights, and the only way in was the editor popup. Blank
        // now means "nothing stored", and it is as editable as any other cell.
        //
        // Whether a number is a good idea on a particular row is not this control's business. A
        // rune has no weight worth setting and setting one is allowed; the table's job is to let
        // somebody say what they mean and to show what they said.
        if (value == null)
        {
            if (row.Filed.Length == 0)
            {
                Blank("No id in the table files, so there is nowhere to keep a change. See the " +
                      "Status column.");

                return;
            }

            if (Written(Keyed(row) + "/" + id, id, "", Room, out var fresh) &&
                float.TryParse(fresh.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var began))
                Wrt.Set(row.Filed, r => set(r, Math.Clamp(began, least, most)));

            if (ImGui.IsItemHovered())
            {
                // **Three cells share this control and a blank means something different in each.**
                // Share had the weight sentence, which talks about Total weight and children and is
                // simply not about this column. Worse, a blank share is not neutral: the reroll
                // pricing drops a rune with no share out of the draw pool, so the cell had to say
                // what leaving it empty costs. See Rolls.Runes.
                ImGui.SetTooltip(id switch
                {
                    "s" => "Nothing stored, so this is measured at the ordinary size - the " +
                           "smallest an entity can be. The cell stays empty because nothing is " +
                           "written here, not because the size is nought. Type one to disagree, " +
                           "and clear it to go back to the default.\n\n" + Extents.MeasuredSizes(),
                    "share" => "Nothing stored, and blank is not neutral here: a rune with no " +
                               "share is left out of the draw pool entirely, so the reroll " +
                               "pricing treats it as one that never comes up.\n\nThe shares " +
                               "that are written already sum to one, so giving this one a number " +
                               "without lowering the others would make the pool add up to more " +
                               "than everything.",
                    _ => "Nothing stored, which is worth nought - a weight is a number somebody " +
                         "typed and an empty cell is nobody having typed one. What the row is " +
                         "worth all told is in Total weight beside it, rolled up from its children " +
                         "and its effect. Type one to give the row its own, and clear it to " +
                         "take it back out.",
                });
            }

            return;
        }

        // A built-in row owns exactly one of the two number columns - a weight row has no carry
        // and a carry row has no weight - so the cell that has a value is the cell that edits.
        // **A rune's weight edits the percentage behind it.** The figure shown is that percentage
        // applied to one remnant's waves, so the arithmetic runs both ways: typing a weight says
        // what the rune should be worth at its floor, and the percentage that produces it is what
        // gets stored. One number, two ways of saying it, and no second place for them to disagree.
        if (row.Waves > 0f && row.Runic && id == "w")
        {
            if (Typed(row, "w", value.Value, least, most, places, out var worth, out var gone))
                Wrt.Set(row.Filed, r => r.Weight = gone ? null : MathF.Max(0f, worth) * 100f / row.Waves);

            return;
        }

        if (row.Filed.Length == 0)
        {
            // **No id, nothing to write to.** This used to fall back to the settings node behind
            // the weight, which is how a figure with no row of its own stayed editable. There are no
            // settings nodes any more - every weight is a row and every row has an id - so anything
            // reaching here is genuinely derived from other rows and says so.
            ImGui.TextUnformatted(value.Value.ToString("F" + places));

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Read-only here: this figure is derived from other rows rather than " +
                    "stored, so change those instead.");
            }

            return;
        }

        // **Into the custom file, never into whichever store the number came from.** A change is a
        // disagreement with what shipped, and the two files are where a disagreement lives; writing
        // it back into the settings node or the discovered row would leave two answers with nothing
        // saying which is current. See Wrt.
        if (Typed(row, id, value.Value, least, most, places, out var held, out var emptied))
            Wrt.Set(row.Filed, r => set(r, emptied ? null : held));

        // **The same reference list on a cell that already has a number**, which had no hover at all.
        // A size in the box is the case where knowing the measured answers matters most: the question
        // is whether the figure sitting there agrees with one of them, and that cannot be asked
        // against a number with nothing beside it. See Extents.MeasuredSizes.
        if (id == "s" && ImGui.IsItemHovered())
            ImGui.SetTooltip(Extents.MeasuredSizes());
    }

    /// <summary>
    /// One editable figure, as a box you type into.
    ///
    /// **A box rather than a drag, everywhere in this table.** A drag is the wrong control for a
    /// reference table: these are values somebody has a specific number in mind for - forty point
    /// one, not "a bit more than it was" - and reaching an exact figure by pushing a mouse is a
    /// fight. It also made the table hostile to read, because a pointer crossing a cell on its way
    /// somewhere else is a pointer that can change a weight.
    ///
    /// The two zero steps are what drop ImGui's own +/- buttons, which is the rest of what makes
    /// this an ordinary text box rather than a spinner.
    /// </summary>
    /// <param name="cleared">
    /// Whether the box was emptied rather than given a number.
    ///
    /// **An empty box used to read as a parse failure**, which is what a letter typed into it is -
    /// so clearing a cell discarded the edit and the old figure stayed. The two are not the same
    /// thing: one is a mistake to ignore, the other is somebody saying this row has no such value.
    /// </param>
    private static bool Typed(Row row, string id, float value, float least, float most, int places,
        out float said, out bool cleared)
    {
        said = value;
        cleared = false;

        // Keyed on the row's own identity rather than its position, because the table re-sorts the
        // moment a number is committed - a position-keyed control would hand the box, and the text
        // held in it, to a different object.
        // **Shown to the same precision it is stored at, or the next edit quietly rounds it.**
        // The box seeds its buffer from this text, so a weight of 6.45 displayed as 6.4 is 6.45
        // until somebody clicks back into the cell - and then it is 6.4 for real, having been
        // rounded by a format string rather than by anybody's decision.
        if (!Written(Keyed(row) + "/" + id, id,
                value.ToString("F" + places, CultureInfo.InvariantCulture), Room, out var text))
            return false;

        if (text.Trim().Length == 0)
        {
            cleared = true;

            return true;
        }

        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var typed))
            return false;

        // A drag clamped itself and a box does not, so the column's own limits are applied here -
        // a typed figure outside them is a typo, and a weight of minus four would be priced.
        said = Math.Clamp(typed, least, most);

        return true;
    }

    // ------------------------------------------------------------------ building the rows

    private static List<Row> Build(AutoExpeditionSettings settings, Scan scan)
    {
        var rows = new List<Row>();

        // **Discovered before InSite, so the dig site merges into the file rather than beside it.**
        //
        // An object the plugin has never been taught produces a row from both: one from what is
        // standing in front of you, one from what is written down. They carry the same Filed id and
        // edit the same value, so the table showed "devoureregg_01.ao" and "devoureregg_01" one
        // above the other, differing only in how each half spells the same thing. Every question
        // that has been asked of this table twice now has been somebody trying to work out which of
        // the two to type into.
        //
        // The file's row is the one kept: it holds the name, the scope, the stacking and the first
        // sighting, and it goes on existing when you walk away. What the site knows and it does not
        // - how many are here, and the live object's tags - is folded into it. See InSite.
        Discovered(rows, scan);
        InSite(rows, settings, scan);
        Shipped(rows, settings);
        RuneTallyByRemnant(rows, settings);

        return rows;
    }

    /// <summary>
    /// What is loaded right now, valued exactly the way a solve would value it.
    ///
    /// The half of this table that answers the question people actually have. Grouped by kind and
    /// art because that is what the plugin distinguishes - twenty monster markers are one row with
    /// twenty against it, not twenty rows.
    /// </summary>
    private static void InSite(List<Row> rows, AutoExpeditionSettings settings, Scan scan)
    {
        var targets = scan?.Targets;

        if (targets == null)
            return;

        var groups = new Dictionary<string, Row>(StringComparer.Ordinal);

        // What the file already has a row for, by the id both halves resolve to. Only the rows built
        // so far, which is Discovered's - the shipped weights come later and are a different claim.
        var already = new Dictionary<string, Row>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row.Filed.Length > 0 && !already.ContainsKey(row.Filed))
                already[row.Filed] = row;
        }

        foreach (var target in targets)
        {
            if (target == null)
                continue;

            var art = (target.Art ?? "").Trim();

            // **The tier is part of what a standing row IS, and leaving it out merged three things
            // into one.** A strongbox mound carries no art, so every box in the zone landed on the
            // key "Strongbox/" - one row for a Researcher's and a Blacksmith's together, showing
            // whichever was seen first and hiding that they are priced from different sliders. The
            // same key also decided the row's id, so editing it wrote a weight for all of them.
            //
            // Chests were spared only because their tier is stored and their arts differ; the
            // grouping was wrong for them too and had nothing to trip over.
            // A strongbox answers to a row per base now, so its name is that row's rather than a tier
            // of its kind. See Weighing.RowOfStrongbox.
            var box = target.Kind == TargetKind.Strongbox && target.Tier == ChestTier.Unknown
                ? Weighing.RowOfStrongbox(target)
                : null;

            var named = box ?? (target.Tier == ChestTier.Unknown
                ? target.Kind.ToString()
                : $"{target.Kind}/{target.Tier}");
            // **A row is named and grouped by the row it edits, not by the object standing on it.**
            //
            // Filed below is Weighing.RowIdOfTarget, which for anything the scan can name is that
            // KIND's row - so a site holding two sub-area caps produced two rows, "Sub-area entrance:
            // catacombcap_01" and "Sub-area entrance: expedition_subareacaps", both editing the one
            // kind:Entrance cell, and each sitting beside the object's own found: row named after the
            // same art. Four rows for two objects, two of them the same value under two names, and
            // nothing saying which of the four an edit would reach.
            //
            // So a row that resolves to a whole kind is named for the kind and grouped by the id,
            // which is what it actually is - one cell, appearing once. Only a row that resolves to
            // this object keeps the art in its name, because there the art IS the subject.
            var edits = Safe.Read(() => Weighing.RowIdOfTarget(target), "");
            var mine = edits.StartsWith("found:", StringComparison.Ordinal);

            var name = mine && art.Length > 0 ? art : named;
            var key = mine ? named + "/" + art : edits;

            // **An object nothing has been taught about is the file's row, seen from here.**
            //
            // Its Filed id is Wrt.Id.Found of its own key either way, so the two rows were never two
            // things - they were one value with two spellings of its name. Anything the plugin DOES
            // recognise is a different case and keeps its own row: there the site's row points at
            // the weight for its kind and the file's at a weight for that one object, which are two
            // separate numbers and want two separate cells.
            if (target.Kind == TargetKind.Unknown && !groups.ContainsKey(key) &&
                already.TryGetValue(Safe.Read(() => Wrt.Id.Found(Unknowns.Key(target)), ""),
                    out var filed))
            {
                // What the file cannot know: the live object's own tags, and the handle its tag
                // edits are written through. Both come off a target standing in the site, and
                // Discovered has none to ask.
                if (filed.MarkKey.Length == 0)
                {
                    filed.MarkKey = Safe.Read(() => Marks.Key(target), "");
                    filed.Derived = Safe.Read(() => Derived(target), filed.Derived);
                }

                groups[key] = filed;
                filed.Seen++;

                continue;
            }

            if (!groups.TryGetValue(key, out var row))
            {
                row = new Row
                {
                    Name = name,
                    // All three of these asked the same question - which row answers for this
                    // object - and each wrote the answer out again. The Size column is what that
                    // cost: the editor wrote to one copy of the rule and Extents read through
                    // another, so a size typed in went somewhere nothing looked. One rule, one
                    // place. See Weighing.RowIdOfTarget.
                    Source = Standing(Safe.Read(() => Weighing.RowIdOfTarget(target), ""),
                        target.Kind != TargetKind.Unknown || !Unknowns.Unread(target)),
                    Type = Typed(Safe.Read(() => Weighing.RowIdOfTarget(target), ""),
                        target.Kind.ToString()),
                    Marks = Tags.Line(target),
                    Derived = Derived(target),
                    MarkKey = Marks.Key(target),
                    // **The stored cell, with the worked-out figure next door in Total weight.**
                    //
                    // This was what the objective prices the thing at, which is a roll-up of the
                    // row's children and effects - so a row storing nothing showed a number, in a
                    // column whose other rows show what somebody typed. Two kinds of number under
                    // one heading, and no way to tell which a cell was.
                    Weight = Safe.Read(() => Wrt.Of(target.Kind == TargetKind.Unknown
                        ? Wrt.Id.Found(Unknowns.Key(target))
                        : box ?? Wrt.Id.Kind(target.Kind, target.Tier))?.Weight, null),
                    // The stored cell here too, resolved by the same rule as the weight above it.
                    // This showed Extents.Of, the extent actually in force, which is never null - so
                    // a row storing no size showed a number under a heading whose other rows show
                    // what somebody typed, and clearing the cell put the worked-out figure back. What
                    // is in force is on the Size hover and in the dump's bounds table, both of which
                    // have room to say where it came from.
                    Size = Safe.Read(() => Wrt.Of(target.Kind == TargetKind.Unknown
                        ? Wrt.Id.Found(Unknowns.Key(target))
                        : box ?? Wrt.Id.Kind(target.Kind, target.Tier))?.Size, null),

                    // **What is seen in the site is a view of something, and this is what.**
                    //
                    // A row here reads its weight through the same rules the search reads, which
                    // left it with a number and nowhere to write one back - the thing you are
                    // looking at was the one thing you could not edit. An object the plugin
                    // recognises resolves to the setting that prices it; one it does not resolves
                    // to its own row on the discovered list, where its size and what it passes on
                    // live too.
                    // Asked under the tier this row was GROUPED under, not the one stored on the
                    // target. A strongbox's tier is derived from what it holds (see Weighing.RowOfStrongbox)
                    // and never written back, so a bare target still reads Unknown and would resolve
                    // to no slider at all - the row you are looking at, once again the one you
                    // cannot edit. Everything else already carries its tier, so probe == target.

                    Priced = Safe.Read(() => Unknowns.Row(Unknowns.Key(target)), null),
                    First = Safe.Read(() => Unknowns.Row(Unknowns.Key(target))?.First, null),

                    // A thing the plugin recognises is answered for its kind; one it does not is
                    // answered for itself. See Wrt.Id.
                    // A strongbox is filed under its base's row, which is where its weight now lives.
                    // See Weighing.RowOfStrongbox.
                    Filed = Safe.Read(() => Weighing.RowIdOfTarget(target), ""),
                };

                row.Shipped = Safe.Read(() => Wrt.Default(row.Filed)?.Weight, null);

                groups[key] = row;
                rows.Add(row);
            }

            row.Seen++;
        }
    }




    /// <summary>
    /// The thing a kind row prices, rebuilt from its id, or null where the id names no kind.
    ///
    /// **This is what replaced probing the settings tree.** Working out which kind a weight priced
    /// used to mean building every kind-and-tier pair in turn and asking which one resolved to that
    /// settings node - a search over the thing the table was supposed to have replaced. The id says
    /// it outright: "kind:Chest/GrandUnique" is a chest of that tier and nothing else has to be
    /// consulted to know it.
    ///
    /// Null for a setting: id, which prices something with no kind of its own - a guarding pack, a
    /// wave count - and for kind:MonsterMagic, which is a rarity rather than a kind. Those fall back
    /// to what the objective applies, exactly as they did. See Wrt.Id.
    /// </summary>
    private static Target Probed(string id)
    {
        if (id == null)
            return null;

        // **The three monster tiers are slugs and everything else still wears a namespace**, so both
        // shapes arrive here. They moved because a Children cell names them beside pack/normal and
        // one cell should not read in two ways; nothing else moved, because nothing else is ever
        // named by another row. Matched whole rather than split on the slash - "monster/rare" is one
        // name, not a kind with a tier, and parsing it as the latter would hand back a chest-shaped
        // answer. See Wrt.Id.Named.
        // **Every row that stands for something on the ground, not only the kind: ones.**
        //
        // This answered for kind: ids alone, and Deriving's other arm - Weighing.DerivedTagsFor - answers for
        // exactly one row. So about eighty rows showed an empty Tags column, which reads as "this
        // reaches nothing" when it means "nobody worked out what this reaches". A strongbox row is a
        // strongbox; a pack of rares is rare monsters; the elite marker is what it spawns.
        // **Only a row with a weight of its own, because only that can be scaled.** A tag marks what a
        // modifier aims at, and a modifier aims at something with a number on it. pack/rare,
        // elitemarker, caged, remnant/wave and the mod/ rows have no weight - they are made entirely
        // of other rows - so an uplift aimed at rare monsters lands on monster/rare and their totals
        // rise because they CONTAIN it. Tagging them as well says the uplift arrives twice, which is
        // the first thing anybody reading the column will think, and it is not what happens.
        //
        // So a composition row's Tags cell is empty, and empty means "this row has no weight of its
        // own - look at its children". See TableGrammar.Spreading, which records children and never the
        // root, and TableGrammar.TagMaskOfRow, which reads the stored cell and so was never fooled either way.
        var slug = id switch
        {
            "monster/normal" => TargetKind.Monster,
            "monster/rare" => TargetKind.Elite,
            _ => TargetKind.Unknown,
        };

        if (slug != TargetKind.Unknown)
            return new Target { Kind = slug, Tier = ChestTier.Unknown };

        // monster/magic falls through with the other rarities, as kind:MonsterMagic did: it is a
        // rarity rather than a kind, so there is no kind to probe with. See Weighing.DerivedTagsFor.
        // The row's Kind field answers first now that a row is named for what identifies it.
        var stored = Wrt.Of(id)?.Kind;

        if (Enum.TryParse<TargetKind>(stored, out var named) && named != TargetKind.Unknown)
            return new Target { Kind = named, Tier = ChestTier.Unknown };

        // A chest row stores its TIER in the same cell, which no TargetKind name matches - so a
        // successful parse here means a chest and the kind follows from that.
        if (Enum.TryParse<ChestTier>(stored, out var rank) && rank != ChestTier.Unknown)
            return new Target { Kind = TargetKind.Chest, Tier = rank };

        // **Nothing parses an id here any more.** Every row that stands for something on the ground
        // is named for what identifies it - a metadata path, a minimap icon, an art file - so there
        // is no kind spelled into the name to read back out. The Kind cell above is where it lives,
        // and the Type cell below is the last resort for a row that states neither.
        var typed = Safe.Read(() => Wrt.Of(id)?.Type, null);

        return typed != null && Enum.TryParse<TargetKind>(typed, out var loose) &&
               loose != TargetKind.Unknown
            ? new Target { Kind = loose, Tier = ChestTier.Unknown }
            : null;
    }

    /// <summary>What a target's tags would be with nothing written down. See Marked.</summary>
    private static string Derived(Target target) =>
        string.Join(", ", Tags.Derive(target));

    /// <summary>The unknown entities list, which is the only thing here this table will edit.</summary>
    private static void Discovered(List<Row> rows, Scan scan)
    {
        var marks = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var target in scan?.Targets ?? new List<Target>())
        {
            var key = Safe.Read(() => Unknowns.Key(target), "");

            if (key.Length > 0 && !marks.ContainsKey(key))
                marks[key] = Tags.Line(target);
        }

        // **What the game calls these, from whichever one is standing in the dig site.**
        //
        // Matched on the object's shape - metadata and art - rather than on the whole key, because
        // the whole key is the thing that disagrees: a row filed before its label loaded has no
        // words in its key, and the live target beside it has words and therefore a different key.
        // Matching on what does not vary is the only way the two meet. See Unknowns.Shape.
        var labelled = new Dictionary<string, string>();

        // **And which object grants each effect, which is the same question asked of a modifier.**
        //
        // An effect row knows its mod id and nothing else - not what carries it, because a modifier
        // is not a thing standing in the dig site. The objects in front of us do know: each one
        // lists its mods, so an object labelled "Vaal Relic" carrying ItemQuantityChest is the
        // answer for that row. Read off the live site rather than stored, for the same reason the
        // label is: it is a display name, and nothing is filed against it.
        //
        // First granter wins. Where two different objects hand out the same modifier the row is
        // genuinely about both, and naming it after either would be picking one arbitrarily - but a
        // name is better than no name, and the alternative was "Relic" for all of them.
        var granted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in scan?.Targets ?? new List<Target>())
        {
            var words = Safe.Read(() => target.Words ?? "", "");

            if (words.Length == 0)
                continue;

            var shape = Safe.Read(() => Unknowns.Shape(Unknowns.Key(target)), "");

            if (shape.Length > 0 && !labelled.ContainsKey(shape))
                labelled[shape] = words;

            foreach (var effect in Safe.Read(() => Unknowns.Effects(target), null) ?? new List<string>())
            {
                if (!granted.ContainsKey(effect))
                    granted[effect] = words;
            }
        }

        foreach (var (meta, priced) in Unknowns.All)
        {
            // **"Sub-area entrance: volcanocap" rather than "volcanocap".** A key is spelt from
            // metadata and art, and art is a file name - it tells two objects apart without saying
            // what either is. The kind recorded when the thing was filed is the half that says what
            // it is. See Priors.Called.
            var kind = Priors.Called(meta, priced.Kind);

            // An object is named by its own label; an effect by whatever is handing it out. The key
            // says which of the two this row is - an effect is filed under its mod id alone, with
            // none of an object's other fields. See Unknowns.Name.
            var effect = Safe.Read(() => Unknowns.Parts(meta).Length, 1) == 1;

            // The row's own answer first - it was written when the effect was filed, with the
            // object that granted it in hand, and it is right wherever you happen to be reading the
            // table from. The dig site is the fallback for rows filed before that was recorded.
            var called = effect
                ? priced.Granter.Length > 0
                    ? priced.Granter
                    : granted.GetValueOrDefault(meta, "")
                : labelled.GetValueOrDefault(Safe.Read(() => Unknowns.Shape(meta), ""), "");

            var named = Unknowns.Name(meta, called ?? "");

            // **Every number here comes from the accessor, never from the row behind it.** Each
            // of these has the reference table in front of it, so what the cell shows is what the
            // objective uses - and an edit, which lands in the table, is visible the next frame
            // instead of being painted over by the layer it overrode.
            rows.Add(new Row
            {
                // **StartsWith, so every row reads kind first and the column lines up.**
                //
                // Contains was tried and is worse, for a reason that only shows up across several
                // rows at once: it suppresses the prefix on any name that happens to contain the
                // kind word ANYWHERE. "Vaal Relic: tundravaalremnant" swallowed it and "Dormant
                // Burrower: devourerbodysegment" did not, so two rows describing the same sort of
                // thing were printed in two different shapes and the column stopped being scannable.
                //
                // "Relic: Vaal Relic: ..." repeats a word, and that is the smaller cost. The prefix
                // says what sort of thing a row is, the name says which one, and the reading is the
                // same every time - which is what a column of two hundred rows needs more than it
                // needs brevity on four of them.
                Name = kind.Length > 0 && !named.StartsWith(kind, StringComparison.OrdinalIgnoreCase)
                    ? $"{kind}: {named}"
                    : named,
                Source = Standing(Wrt.Id.Found(meta), priced.Set),
                // **One vocabulary in this column, which took two to notice.**
                //
                // A row read off the dig site puts its TargetKind here - Unknown, Relic, Chest - and
                // a row read off the file used to put its SHAPE here, "object" or "effect". The two
                // views of one thing then disagreed about what kind of thing it was: "devoureregg_01.ao,
                // Unknown" beside "Expedition: devoureregg_01, object", which reads as two different
                // entries rather than as the same entry seen twice.
                //
                // So an object says what it is, from the kind recorded when it was filed, and only an
                // effect says "effect" - which is not a competing answer but the true one, since a
                // modifier has no TargetKind and never stands in a dig site.
                //
                // The distinction still has to survive, and this is the column it survives in. A
                // relic is worth its own row PLUS the rows of the things it grants, so "Relic: Vaal
                // Relic: tundravaalremnant" and "Relic: Vaal Relic: Item Quantity Chest" sit beside
                // each other looking like a duplicate and a real entry - and forgetting the first as
                // redundant takes out the base weight every relic wearing that art is priced on.
                //
                // An effect is filed under its mod id alone and an object under its eight fields, so
                // the key says which without anything new being stored. See Unknowns.Name, which
                // tells them apart the same way.
                Type = Safe.Read(() => Unknowns.Parts(meta).Length, 1) == 1
                    ? "effect"
                    : priced.Kind.ToString(),
                // Unknowns.Of falls back to Unknowns.Default where nothing is stored, which is
                // right for the scoring and wrong for a cell: it drew a 1 on every row nobody has
                // priced, so "unset" and "set to one" looked identical.
                Weight = Safe.Read(() => Wrt.Of(Wrt.Id.Found(meta))?.Weight, null),
                Stacks = Safe.Read(() => Unknowns.Stacking(meta), priced.Stacks),

                // **Through Sized, which keeps the null, and not through Extent, which fills it in.**
                //
                // Exactly the fault described for Weight two lines up, in the same initialiser:
                // Unknowns.Extent falls back to the ordinary marker size where nothing is stored,
                // which is right for the scoring and wrong for a cell. It drew 24.46 on every row
                // nobody had sized, so "unset" and "set to the ordinary size" looked identical - and
                // because the editor seeds its text buffer from what the cell shows, clearing the
                // number put it straight back. Deleting a size was impossible, and it looked like the
                // table refusing the edit rather than the cell showing a default.
                //
                // Unknowns.Sized exists for this and says so: "Separate from Extent because unset and
                // the default had to stop being the same answer."
                Size = Safe.Read(() => Unknowns.Sized(meta), null),
                Filed = Wrt.Id.Found(meta),

                // **Through the accessor, like every other cell, and it was the one that was not.**
                //
                // MarksOf puts the table file in front of the older store, which is where an edit
                // to this column lands - and this read neither. It took the tags off whichever
                // matching object happened to be standing in the dig site, and where none was, off
                // a target built from the kind alone. Both ignore the row entirely, so writing a
                // tag on something you were not standing next to looked like the edit bouncing.
                //
                // The dig site is still the middle fallback, because a live object knows its own
                // art and states and a synthesised one knows only its kind.
                // The row's own answer first and by NULL rather than by length, so a row somebody
                // cleared shows empty instead of falling through to the derivation - see
                // Wrt.Row.Alike. Then the older store, then whatever matching object is standing in
                // the dig site, then the kind alone.
                Marks = Wrt.Of(Wrt.Id.Found(meta))?.Tags
                    ?? (Safe.Read(() => Unknowns.MarksOf(meta), "") is { Length: > 0 } mine
                        ? mine
                        : marks.TryGetValue(meta, out var had) && had.Length > 0
                            ? had
                            : Tags.Line(new Target { Kind = priced.Kind })),
                Derived = Derived(new Target { Kind = priced.Kind }),
                Priced = priced,
                First = priced.First,
                Id = meta,
            });
        }
    }

    /// <summary>
    /// A row for every weight the plugin ships, read from the table that now holds them.
    ///
    /// **This used to reflect over the settings tree.** Every weight was a RangeNode property under
    /// Weights in the menu, and this walked those properties to learn what weights exist, what they
    /// are called and what they ship at - so the table was a view of the settings rather than the
    /// place the answer lived. That is what let the two disagree, and disagree they did: for some
    /// weights the row won and the slider did nothing, for others the arithmetic read the slider and
    /// an edited row did nothing. Fourteen of forty-four rows moved no number anywhere.
    ///
    /// The sliders are gone. weight_reference_table_defaults.json states every weight - its id, its
    /// name and its built-in value - and a named row in it IS a weight that exists. Yours layer over
    /// it, and there is one store, one name and one number.
    /// </summary>
    private static void Shipped(List<Row> rows, AutoExpeditionSettings settings)
    {
        foreach (var (id, shipped) in Wrt.Standing.ToList())
        {
            // Named rows are the weights. Everything else in the shipped file is a tag or a scope
            // for something discovered, which has no business in this list.
            if (shipped?.Name == null || shipped.Name.Length == 0)
                continue;

            // **A rune is listed by RuneTallyByRemnant and must not be listed twice.** Rune rows only became
            // shipped rows when their percentages moved into effects; before that the shipped file
            // named four of them and the overlap went unnoticed. Now all thirty-four are here, and
            // every one was drawn once as "Power Rune" from this walk and again as "Rune: Power" from
            // RuneTallyByRemnant - two rows for one rune, one of them without the columns a rune needs.
            //
            // RuneTallyByRemnant is the one that stays: it knows what a rune does, what its waves are worth, and
            // that Bait and Power share a name.
            if (id.StartsWith("rune:", StringComparison.Ordinal))
                continue;

            var now = Wrt.Of(id);
            var probe = Probed(id);

            // **Folded into the row the site already made, rather than listed beside it.** InSite
            // runs first and adds a row for every kind standing in the zone; without this the same
            // weight got a second row, which is two cells for one number - the thing this table
            // exists to stop. The settings row wins on identity and what the site knows is kept.
            var standing = rows.FirstOrDefault(r => r.Filed == id);

            if (standing != null)
            {
                standing.Name = shipped.Name;
                standing.Source = Known;
                standing.Shipped = shipped.Weight;
                standing.Weight = now?.Weight ?? 0f;

                continue;
            }

            rows.Add(new Row
            {
                Name = shipped.Name,
                Source = Known,
                Weight = now?.Weight ?? 0f,
                Shipped = shipped.Weight,

                // The tags of whatever this prices, so a category row says what it reaches. The
                // table file first here too, with the derivation as the fallback, so clearing the
                // cell returns it to what the objective actually applies. See Weighing.DerivedTagsFor.
                Marks = now?.Tags ?? Deriving(id, probe),
                Derived = Deriving(id, probe),
                MarkKey = probe == null ? "" : Safe.Read(() => Marks.Key(probe), ""),
                Filed = id,
            });
        }
    }

    /// <summary>
    /// What a weight's tags are with nothing written down: its kind's, or what the objective applies
    /// to the monsters it prices where it names no kind. See Weighing.DerivedTagsFor.
    /// </summary>
    private static string Deriving(string id, Target probe) =>
        probe != null ? Derived(probe) : Safe.Read(() => Weighing.DerivedTagsFor(id), "");

    /// <summary>
    /// A row per rune, which is where the value of reaching a remnant now mostly sits.
    ///
    /// **Prefixed, because a rune's name is a word and the table is full of words.** "Opulent" beside
    /// "Unique chest" reads as another kind of chest; "Rune: Opulent" reads as a rune. The prefix is
    /// also what makes the search useful - typing "rune" leaves the thirty four of them and nothing
    /// else.
    ///
    /// They had a table of their own on the Weights tab, which is the thing this replaces: a rune is
    /// a thing in the dig site with a weight, exactly like a chest, and it was in a different shape
    /// for no reason other than arriving later.
    /// </summary>
    private static void RuneTallyByRemnant(List<Row> rows, AutoExpeditionSettings settings)
    {
        // **The rune rows in the table, not a list in the settings tree.**
        //
        // This walked settings.Weights.Runes.RunePercent, which was the last of the three stores a
        // rune's number used to live in - and once the number moved into the row's effect, walking the
        // settings list meant the table listed runes from one place and priced them from another. A row
        // the settings list had never heard of would not have appeared at all.
        var table = Wrt.Standing.Select(r => r.Key)
            .Concat(Wrt.Yours.Select(r => r.Key))
            .Where(id => id.StartsWith("rune:", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(id => id[5..])
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        if (table.Count == 0)
            return;

        // What one remnant's own waves are worth, which is the smallest thing a rune ever acts on.
        //
        // **A rune is not worth nothing without a propagating slot.** In an ordinary slot it still
        // reaches the monsters its own blast unearths, and for a remnant that is its waves - so the
        // Weight column can say what a rune is worth at its floor, in the units every other row uses,
        // instead of sitting blank and implying nought.
        //
        // Read through Weighing.Waves rather than multiplied out here, so it cannot disagree with
        // what the objective counts. A bare remnant, because sockets no longer change the waves.
        var waves = Safe.Read(
            () => Weighing.Waves(new Target { Kind = TargetKind.Remnant }), 0f);

        // **Two runes can wear one name, so the name alone cannot identify a row.**
        //
        // A rune is shown under its mod's UserFriendlyName, and Bait's mod is Power's - so the
        // table held two rows both reading "Rune: Power" with no way to tell which was which, and
        // editing one was a coin flip. Counted first, then only the odd one out is spelt out: where
        // a shared name belongs to a rune of that name, that row keeps the plain name and the
        // impostor carries its own id.
        var shared = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in table)
        {
            var shown = RuneInfo.Called(id);

            shared[shown] = shared.TryGetValue(shown, out var had) ? had + 1 : 1;
        }

        foreach (var name in table)
        {

            rows.Add(new Row
            {
                Name = Telling(name, shared),
                Source = Known,
                Type = Typed(Wrt.Id.Rune(name), "Rune"),

                // **A percentage, not a weight, which is where it was.**
                //
                // The number a rune carries is summed by Propagation.Carried and applied as
                // "carries / 100 x the monster worth downstream" - so it is a share of what the
                // rest of the chain unearths, exactly like any other propagation. Showing it under
                // Weight with an empty Propagation said a rune passes nothing on, which is the
                // opposite of what the model does and the reverse of why runes matter.
                //
                // The same number does both jobs: in a propagating slot it reaches everything
                // after, in an ordinary slot only what its own blast unearths. One figure, two
                // reaches, and which one applies is the slot's business rather than the rune's.
                // **Every figure here comes back through Runes, which is what the scoring asks.**
                // They were read straight off the settings node while an edit was written into the
                // custom file that Runes consults first - so typing a new percentage changed the
                // plan and the cell snapped back to the old number, with nothing saying which had
                // won. One reader, one answer.
                Filed = Wrt.Id.Rune(name),
                // **A rune has no weight and no carry, and both were being filled in here.**
                //
                // What a rune is worth is its effect - "monster.weight *= +4%" - and that is the
                // only cell it has. These two put a number in front of the reader that is stored
                // nowhere: Carries repeated the percentage under a heading that means a v1 cell,
                // and Weight showed the percentage times this remnant's waves, which is neither a
                // weight nor anything anybody can change. Typing into either wrote a cell no rune
                // should have.
                //
                // Both are blank now. The percentage is in the Effect column, where it is stored
                // and where editing it means something.
                // **No node to bind to, and that is the point.** These used to carry the settings
                // node a rune's percentage lived in, so the cell wrote there while the scoring read the
                // reference table - two stores, one of which quietly stopped mattering. A rune is
                // edited in the Effect column now, like every other modifier in the table.
                Waves = waves,
                Runic = true,

                // Derived, so read-only: the editable truth is the percentage beside it. A floor
                // rather than a figure - in a propagating slot the same percentage reaches every
                // monster the chain unearths after this one, which on a long chain is many times
                // this. See the note above.
                Weight = null,

                // What the rune tiering shipped it at, which is where its own reset put it.
                // **What the SHIPPED row says, which is where the ranking lives now.** This read
                // Runes.Tiered - the ranking compiled into a file - which was the shipped answer only
                // while the shipped table had four rune rows in it. All thirty-four are rows now, so
                // the shipped answer is the shipped row, and a reset puts back what the file says
                // rather than what a switch statement remembers.

                // What it does to the dig site, in the game's words. A rune's name says nothing
                // about its effect, which is why the table it came from carried this too.
                Says = Told(name),

                // What the plugin calls this rune, which is what the row's widgets are pushed
                // under. The search reads the drawn key rather than this, and "bait" still finds
                // the Bait rune even though it draws as Power, because the key is "rune:bait".
                Id = name,
            });
        }
    }

    /// <summary>
    /// The percentage the SHIPPED row for this rune states, for the reset to put back.
    ///
    /// Read through the effect like everything else, so what a reset restores and what the scoring
    /// reads cannot be two different numbers. See TableGrammar.Effect.Share.
    /// </summary>
    private static float Shipped(string id)
    {
        var said = Wrt.Default(id)?.Effect;

        if (said == null)
            return 0f;

        foreach (var effect in TableGrammar.Effects(said, out _))
            return effect.Share * 100f;

        return 0f;
    }

    /// <summary>
    /// What to call a rune's row, spelling out its id only where the name is not enough.
    ///
    /// Bait and Power share a mod and therefore a name. Where a shared name matches one of the
    /// runes wearing it, that one keeps the plain name and the others say which they are - so the
    /// ordinary rune reads "Rune: Power" and the oddity reads "Rune: Power (Bait)", rather than
    /// both carrying a bracket nobody needed. See Curio, which is why Bait is worth spotting.
    /// </summary>
    private static string Telling(string id, Dictionary<string, int> shared)
    {
        var shown = RuneInfo.Called(id);

        if (!shared.TryGetValue(shown, out var count) || count < 2 ||
            string.Equals(id, shown, StringComparison.OrdinalIgnoreCase))
            return "Rune: " + shown;

        return $"Rune: {shown} ({id})";
    }

    /// <summary>A rune's effect in the game's own words, or nothing where it has none worth saying.</summary>
    private static string Told(string rune)
    {
        var effect = Safe.Read(() => RuneInfo.For(rune), null);

        if (effect == null || !RuneInfo.Describable(effect.Does))
            return "";

        return RuneInfo.Describable(effect.AtPower) && effect.AtPower != effect.Does
            ? effect.Does + "\n\nAt power: " + effect.AtPower
            : effect.Does;
    }

    /// <summary>One freshly built copy of each weights class, so the shipped values can be read.</summary>
    private static readonly Dictionary<Type, object> Clean = new();


}
