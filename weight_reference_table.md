# The Weight Reference Table

A spec for version 2 of the weight reference table, renamed. Nothing below is implemented.

## What is wrong with version 1

The table was meant to be the one place a weight is decided. It is not, and the gap is not one bug
but a shape: **the table prices leaves, and everything that makes a leaf out of an entity is code.**

- **Composition is hardcoded.** `Weighing.Tiers` decides that a remnant is three waves of so many
  rares, magics and normals, and that a strongbox's guarding packs split by rarity; `Blended`
  decides a monstermarker is a weighted average of white and blue. Those are facts about the game,
  they are exactly what a reference table is for, and they live in a `switch`.
- **Classification is hardcoded.** `Scan.Kind` maps `elitemarker.ao` to `TargetKind.Elite` and
  `Weighing.Shipped` maps that to `kind:MonsterRare`. So the table has no row for the marker at all:
  the marker and the rare monster it spawns were merged into one row, and the fact that they are two
  things - one an object in the world with a size, the other content with tags a relic can scale -
  is gone.
- **Propagation is spread over two columns and a convention.** `scope: "monster=4"` and
  `combines: "+name"` together mean "+4% to monsters, pooled under name". Nothing on the row says
  what 4 does, and changing it to 5 moves a weight elsewhere by an amount no cell explains.
- **Multiplicative behaviour is the only way to say "multiplies".** It is a grouping tool - it says
  *shares a stat with* - and it was pressed into saying *is a factor* as well, because there was
  nowhere else to say it. `special_untagged_rune_scaling` exists because there was nowhere at all to
  say "this scales other runes".
- **Non-stacking is a hardcoded rune rule** in `Planner.Booked`, while the table has a `Stacks`
  column that means the same thing and governs nothing but unclassified objects.
- **There are still three stores.** Non-rune weights in `weight_reference_table_custom.json`, rune
  weights nominally in `WeightSettings.RunePercent`, and rune draw shares hardcoded in `Rolls.Runes`
  (29 of them, against 34 rune rows in a live table).

Version 2 is one idea: **a row is a thing, a thing can be made of other things, and what a thing does
to other things is written on it in one readable expression.**

---

## The row

```jsonc
{
  "id":       "elitemarker",          // short, stable, readable
  "name":     "Marker: Elite",        // shipped file only; a custom row inherits it
  "type":     "Entity",               // free text, for grouping and search
  "matches":  "art:elitemarker.ao",   // whole names only, unless a * opens an end
  "size":     4.5,                    // world units a blast still catches it at
  "weight":   0,                      // what THIS is worth, on its own
  "children": "monster/rare x1",      // what else you get for blowing it up
  "effect":   null,                   // what it does to other things
  "tags":     "",                     // what it is, for effects to target
  "stacks":   null                    // whether a second one is worth anything
}
```

`weight` and `children` answer *what do I gain*. `effect` and `tags` answer *what does this do to
everything else*. `matches` and `size` answer *what is it and where*. That is the whole row.

### Total

```
Total(row) = row.weight + SUM over children ( count x Total(child) )
```

Shown as its own read-only column beside `Weight`, and **this is the number the planner uses.** The
example works directly: `elitemarker` has weight 0 and total 20, and the solver takes it for the
total.

The graph must be acyclic and is depth-capped; a cycle is a validation error on the row that closes
it, not a hang.

### Children

A comma-separated list of `<id> x<count>`.

```
elitemarker     children   monster/rare x1
remnant/wave    children   monster/rare x3, monster/magic x2, monster/normal x8
remnant         children   remnant/wave x3
monstermarker   children   monster/normal x0.85, monster/magic x0.15
caged           children   monster/rare x3
```

**Counts are floats, and a fractional count is a probability.** The monstermarker line above is the
whole of `Weighing.Blended` and `Weighing.Splitting`: one marker that comes up white 85% of the time
is 0.85 of a white monster and 0.15 of a blue one, and its expected value and its tag split both
fall out of the same two numbers. `setting:MarkerMagic` stops existing.

**A count may name a mod value instead of a number** - see Modifiers, below. There is no fallback
form: a count the game was supposed to state and did not is an error, shown as one.

---

## Modifiers are rows too

**A modifier the game states on an entity becomes a child of that entity - any entity.** That is the
whole mechanism for everything variable, and it replaces the `from <fact>` idea entirely - there is no
short-hand fact registry and no written default standing in for an unread number.

```jsonc
{
  "id":       "mod/chest_summon_rares",
  "name":     "Guarded by packs of Rare Monsters",
  "type":     "Modifier",
  "matches":  "ObjectMagicProperties.ExplicitModData:ChestSummonRares",
  "children": "pack/rare x matched.Values[-1]"
}
```

A strongbox carrying that mod gains it as a child; the mod's own children are multiplied by the
value the game printed on it. The strongbox row itself says nothing about packs:

```
strongbox       weight 5    children -                    (the mods are its children)
pack/rare       weight 0    children monster/rare x4
```

`matches` names the component path the value is read from, so the row states its own source:

```
ObjectMagicProperties.ExplicitModData:<RawName>     a rolled modifier
ObjectMagicProperties.ImplicitModData:<RawName>     an inherent one
```

`matched.Values[-1]` is the last entry of `ItemMod.Values`, which is a `List<int>` - see below for why the
count is addressed from the end. `ValuesMinMax`, `Level`, `Group` and `Name` are on the same object if
a row ever needs them.

**Inherent and rolled are the same thing and are no longer split.** One pack of rare monsters is one
pack of rare monsters whichever line it came from, so both mod paths point at the same `pack/rare`
child. `Target.ImplicitPacks` / `ImplicitMagicPacks` / `ImplicitRarePacks` / `ExplicitPacks` /
`ExplicitMagicPacks` / `ExplicitRarePacks` collapse to nothing, and with them go
`setting:StrongboxPack` and its five siblings, `Scan.Guarding`'s order-dependent inherent/rolled
split, and the six fields in `Remembered`'s serialisation.

**This unifies relics with strongboxes.** A relic's upside is already a mod; it becomes a mod row
with an `effect` instead of children, and the `found:ExpeditionRelicUpside*` rows lose their
namespace:

```
mod/relic_item_quantity_monster
  matches   ObjectMagicProperties.ExplicitModData:ExpeditionRelicUpsideItemQuantityMonster
  effect    monster *= 1.20 as monster_item_quantity
```

### Confirmed in game: the count is the last value

Settled from a Grand Expedition dump of twenty strongboxes, seven of them close enough to also have a
readable ground label. **The pack count is the last entry in `ItemMod.Values`, and the mod id names the
rarity.** Seven of seven, both lines, no exceptions:

| mod | values | minmax | the label said |
| --- | --- | --- | --- |
| `ChestSummonStrongboxImplicitHigh` | `[1, 0, 7]` | `1-1, 0-0, 6-12` | Guarded by 7 packs of Monsters |
| `ChestSummonStrongboxImplicitHigh` | `[1, 0, 11]` | `1-1, 0-0, 6-12` | Guarded by 11 packs of Monsters |
| `ChestSummonStrongboxImplicitRares` | `[1, 2, 4]` | `1-1, 2-2, 4-6` | Guarded by 4 packs of Rare Monsters |
| `ChestSummonNormals` | `[1, 5]` | `1-1, 4-8` | Guarded by 5 packs of Monsters |
| `ChestSummonMagics` | `[1, 3]` | `1-1, 2-4` | Guarded by 3 packs of Magic Monsters |
| `ChestSummonRares` | `[1, 1]` | `1-1, 1-2` | Guarded by a pack of Rare Monsters |

So the modifier row form works as specified, and the label parse is not needed for this at all:

```
mod/chest_summon_rares
  matches   ObjectMagicProperties.ExplicitModData:ChestSummonRares
  children  pack/rare x matched.Values[-1]
```

Three consequences worth having:

- **`label:` is not needed as a `matches` form.** Nothing currently wants it. It stays out of the
  grammar until something does.
- **Implicit and explicit separate the two lines cleanly.** The inherent line is an `ImplicitModData`
  mod and the rolled one an `ExplicitModData` mod, so `Scan.Guarding`'s "the first sentence rendered is
  the inherent one" - documented as *"an assumption about the label's layout rather than something the
  game states, and the part of this worth doubting first"* - is replaced by something the game does
  state.
- **The last value, not `Values[1]`.** The implicit mods carry three values and the explicit ones two,
  so the count is addressed from the end. `Values[-1]` is the grammar for it.

`minmax` gives the roll's range for free, which the reroll advisor could use to price an unrolled box
without sampling.

### Every strongbox guards a rare that no label mentions

```
StrongboxRareRobotGuardNoImmediateSpawn   values=[1]   minmax=[1-1]
```

On **20 of 20** expedition strongboxes in that site, and it appears in no guarding sentence - so it is
a rare monster per strongbox that nothing in the plugin has ever counted. At the shipped rare weight of
20 that is 400 points of content missing from a twenty-box Grand site.

**Byte-identical on all twenty**: `values=[1]`, `minmax=[1-1]`, `level=1`, `group=` empty. Across six
different bases, whichever `ChestSummon*` the box also carries, opened or not. There is nothing to
compute - it is a fixed grant, and `minmax` says it cannot roll anything else. None of the four Unique
`*StrongboxHigh` boxes has it; three of those carry no implicits at all.

**What that site cannot settle is whether it is every strongbox or every RARE strongbox**, because all
twenty were Rare and the mod is named `StrongboxRare...`. The evidence leans against a rarity gate:
every implicit that *is* granted by rarity sits in `group=ChestBaseModsFromRarity` - `ChestRareMaps1`,
`ChestRareOrnateMaps2`, `StrongboxChestIncreasedRarityImplicit*` - and this one sits in no group at
all, beside `MonsterStrongboxMonster` and the `MonsterRare*` / `MonsterMagic*` families. It is grouped
with the tagging mods, not the rarity rewards. **One dump near a White or Magic expedition strongbox
decides it**, and both are common.

Attributing the site's already-spawned strongbox rares to their nearest box was tried and is not
evidence: boxes carrying a rare-pack mod came out with none beside them and a box with no rare source
came out with ten. Those monsters wander and die, so position says nothing about where they came from.

This is the case that justifies the whole design. It is invisible to a label parse, invisible to a
`switch` over `TargetKind`, and it is one row:

```
mod/strongbox_rare_robot_guard
  name      Robot guard (does not spawn immediately)
  matches   ObjectMagicProperties.ImplicitModData:StrongboxRareRobotGuardNoImmediateSpawn
  children  monster/rare x matched.Values[-1]
```

The name is the game's and is not describing a robot. Left as the game spells it in `matches`, given a
readable `name` for the table.

Two more that fall out of the same sweep and want rows:

- `ChestStrongboxSummonVaalMonstersImplicit values=[1, 0, 31]`, on a Unique `*StrongboxHigh` - thirty
  one packs of Vaal monsters. Those boxes carry no `inherent_explosion_radius` state, so they are not
  blast targets and are correctly worth nothing to the chain; worth a row anyway so the table says why
  rather than omitting them.
- `StrongboxChestAdditionalRunes1 values=[2]` - a strongbox that grants runes. Currently priced as
  nothing.

---

## Runes, sockets, and the pool

**A remnant's children are the runes it actually holds.** Remnants are readable from across the map
and from the moment the area loads, so pricing an unread socket as a blind draw is not a reasonable
default - it is the plugin quietly scoring a guess. A remnant whose runes cannot be read is flagged
unread, says so in the overlay, and is not silently valued as an average.

```
remnant       children   remnant/wave x3, rune/oath x1, rune/tidal x1
```

The scanner attaches those child edges from what it read. That is the one piece of composition the
scanner does rather than the table, and it is the only one.

**The draw pool exists, and it is for pricing a roll.** "What would this socket become" is exactly
the question `Rolling` asks and nothing else does, so the pool is a row used by the reroll advisor
rather than a stand-in inside `Total`:

```
rune/pool     type Pool     children   rune pool x1
```

`rune pool x1` means *one draw from every row tagged `rune`, weighted by that row's `share`*. Its
total is `SUM share x Total(rune)`, which is the arithmetic `Rolls` does today, written where it can
be seen.

### Why the shipped rune numbers are what they are

The numbers are rows; the argument for them is here, because a JSON file cannot hold a paragraph and
a ranking nobody can argue with is a ranking nobody should trust. It came out of `Runes.Tiered`, which
was the shipped default for every rune the table did not name.

They are a published community tier list, with **one deliberate departure and a set of tie-breakers**:

- **Power at 50, above its tier.** The list puts it level with Death, Bond and Oath. That understates
  it for a reason a list cannot capture: Power adds no effect of its own, it acts on the other runes.
  Carried with anything else it makes that thing bigger, so its worth is never just its own line - and
  a chain built around propagation is a chain carrying something for it to multiply.
- **Opulent at 60**, the top of the list on its own.
- **Tenths apart rather than level: Death 40.2, Oath 40.1, Bond 40; Time 20.1, Rebirth 20.** The
  tenths are tie-breakers, not measurements. Runes this close are worth the same to within anybody's
  precision; what a dead heat actually costs is that the planner picks between two remnants offering
  them arbitrarily - whichever the search tried first - and the same site then produces a different
  chain on each solve. An order stated is an order that holds, and stating one is cheaper than
  watching a coin toss pick a route. Within each group the order is a judgement made in play; the
  published list has nothing to say at this resolution.
- **Wisdom 5, Bait 1, everything else 2.** Bait has never been seen on a remnant at all.

### Share

**`share` is how often a fresh socket comes up as this rune, as a fraction.** It is the column behind
`Rolls.Runes` - `Adaptive 0.095`, `Tidal 0.088`, down to `Tempest 0.002` - and it is used in exactly
two places: the pool row's total, and the reroll advisor's enumeration over outcomes. It never touches
the score of a remnant whose runes are known, which is every remnant the planner sees.

It is measured, not read from the game, and that is settled rather than open: `Expedition2RunesWeight`
is named for the weights and exposes none - `Id`, `SlotCount`, `RuneSlot`, `Rune`, `Level`, and no
frequency. `Census` and `Rolls` both record this. So the shares are a finding from observation, which
is precisely why they belong in the table as an editable column with a status of their own instead of
in an array in `Rolls.cs`.

What the game *does* state is `Level`, so **which runes can appear at this area level is readable**
even though how often is not. A rune the game gates above the current level gets share 0 for this
area - the game where the game speaks, the table where it does not.

---

## The effect expression

One column replaces `scope`, `carries` and `combines`.

```
<target>[.<attribute>] <op> <amount> [as <stat>] [here]
```

Comma-separated; a row may carry several.

| written | means |
| --- | --- |
| `monster.weight *= +4%` | four per cent more of everything tagged `monster` |
| `rare_monster.weight *= 2` | twice as many rare monsters - a factor, not an increase |
| `rare_monster.weight *= +50% as rare_monster_count` | contributes +0.50 to the stat |
| `excavated_chest.weight += 3` | adds 3 flat weight to each excavated chest reached |
| `rune.propagation *= +50%` | raises the magnitude of every rune's effect by half |
| `monster.weight *= +20% here` | this row's own children only, not forward along the chain |

### The attribute

**Two attributes, and naming one removes a rule that was nowhere on the row.** `weight` is what a thing
is worth; `propagation` is the magnitude of an effect's own effect. Without the attribute, which of the
two an expression meant had to be inferred from whatever the target turned out to be - and that is the
same kind of invisible rule as `Weighing.Combining` quietly giving unclassified runes their own group,
which took a dump to notice.

**The case that makes it load-bearing** is the four shipped rows that are both a thing and an effect:

```
setting:GoblinRelic     weight 10  carries 15
setting:SulphiteRelic   weight 10  carries 20
setting:KaruiTotemRelic weight 10  carries 40
setting:Henge           weight 50  carries 15
```

`relic *= +50%` cannot say which half it scales, and the inferred rule picked one silently.
`relic.weight` and `relic.propagation` are different effects with different answers.

**Left off means `weight`, always** - no inference. So `rune.weight *= +50%` is not guessed at, it is
refused: a rune has no weight, so the expression provably multiplies nothing, and the validator says so
and names `rune.propagation` instead. The cell also **writes the attribute back in** when it was left
off, so the form teaches itself rather than having to be known before the cell can be read.

**The set is closed**, for the reason the tag vocabulary is: `monster.wieght` would otherwise read as a
target named `monster.wieght`, resolve to nothing, and score nought.

- **`count` is deliberately absent.** It cannot differ from `weight` - both multiply the same currency,
  so "twice as many rares" and "rares are worth twice as much" are the same number by construction:
  `100 x 2 x 2 = 400` read either way round.
- **`size` is absent** because nothing scales it.

**A percentage and a factor are the same effect, and one of them says it clearly.** `+4%` and `1.04`
parse to the same number, and the cell writes back whichever was typed. The percentage is what the
game prints - "50% increased number of Rare Monsters" - and it is the form that survives being read
at a glance:

```
Rebirth  monster *= +16%        Rebirth  monster *= 1.16
Time     monster *= +16.01%     Time     monster *= 1.1601
```

Those increments are deliberate - **a hair's breadth so that a chain forced to choose between equals
always chooses the same one** - and in the factor column the distinction is four characters deep. The
first translation printed factors at three decimals and both runes came out as `1.16`: the tie-break
was still in the file and no longer on the row.

A bare factor stays legal because some effects are not increases at all. "Elites are Duplicated" is
x2, and writing it as `+100%` describes the arithmetic rather than the modifier. A flat `+=` takes no
percentage - it would be a percentage of a weight nobody named.

**The `as` clause is the only grouping mechanism, and it means one thing: *shares a stat with*.**
Rows naming the same stat add their contributions and the stat's total is one factor; rows naming no
stat are each their own factor.

```
two rows, both  rare_monster.weight *= +50% as rare_monster_count  ->  1 + .5 + .5 = x2.00
two rows, both  rare_monster.weight *= +50%                        ->  1.5 x 1.5   = x2.25
```

**That is also the first-order model of diminishing returns, and it is free.** Item rarity plausibly
buys less per point than monster count does - rarity shifts each drop roll against the top of the table
where count just multiplies the rolls - so pooling the rarity rows under one stat, where they add rather
than compound, already flattens them against count effects that keep their own groups. A true per-stat
curve is out of scope and recorded in NOTES; it needs a shape nobody has measured, and a guessed shape
in the largest term of the objective is what got `Concentrate` deleted.

That is the current `+name` / `*name` distinction unchanged, said once instead of across two columns,
and with the number written as the factor the player reads on the relic rather than as a percentage
whose sign lives elsewhere.

**The examples, written out:**

```
rune/soul                        effect: monster.weight *= +4%
rune/power                       effect: rune.propagation *= +50%
mod/relic_item_quantity_monster  effect: monster.weight *= +20% as monster_item_quantity
mod/relic_elites_duplicated      effect: rare_monster.weight *= 2
```

Soul Rune now has weight 0 and says what it does. `rune:soul`'s weight of 8.56 - a number derived
from `monster=4` by machinery no cell described - stops existing.

`+=` is in the grammar from the start. It is the only way to write "two additional items per
excavated chest", and its absence is why a flat magnitude had to be smuggled through `carries`.

### Targeting an effect

`rune.propagation *= +50%` turns Adaptive's `monster.weight *= +4%` into `+6%` - each reached rune's
share, not the total they make between them.

**That per-share reading is a correction to what the payout did.** It applied the lift to the
aggregate bonus, `(prod(1 + s) - 1) x (1 + p)`, where the grammar means `prod(1 + s(1 + p)) - 1`. The
two agree exactly on one rune and diverge as runes accumulate, because Power makes each modifier
stronger and the stronger modifiers then compound:

| runes in force | aggregate (before) | per-share (now) | |
| --- | --- | --- | --- |
| Adaptive | 0.0600 | 0.0600 | same |
| Adaptive + Opulent | 0.8088 | 0.8232 | +1.8% |
| five small runes | 0.3250 | 0.3382 | +4.1% |
| Adaptive, Opulent, Oath, Death, Bond | 3.8102 | 4.9104 | **+28.9%** |

That last row is an ordinary six-socket remnant on a live table, so it is not a rounding question.
Both readings still pay nothing when nothing propagates, which is the property the aggregate form was
chosen for - with no shares the product is one and the bonus nought either way.

**Still outstanding: the lift reaches relic shares as well as rune ones.** It always did, and "Runes
gain" names runes. Confining it needs per-share provenance beside `rates`, because a relic and a rune
can be typed into the same stat group, so it was left alone rather than changed twice in one pass.

This deletes `special_untagged_rune_scaling` and `Weighing.Lifted`/`Lifting`/`Lifts`.

### Every factor is printed

`Planner.Trailed` writes, per link, each modifier in force with its share before and after empowering,
the group it adds inside of, each group's factor, and their product. The dump prints it under the
blast it belongs to:

```
blast 7 at (595,280)  [planned]
    worth 662.5 here - content 64.4 + propagation 598.1
    factors:
      Adaptive   +4%   x1.5 empowered -> +6%    group 3
      Opulent   +48%   x1.5 empowered -> +72%   group 7
      monsters: 1.06 x 1.72 = 1.8232  -> bonus +82.3%
```

Written from the arrays the payout reads, at the point it reads them, because a readout that
re-derives a rule is a readout that will disagree with it - the blast circles were a second
calculation of the same thing once and they had drifted. Detailed passes only: the search runs that
loop hundreds of thousands of times and must not be building strings in it.

**An effect never applies to its own row.** That is why Power needs no `scaled_by_power_rune` tag on
the other thirty-three: every rune carries `rune`, Power's effect reaches all of them, and the
self-exclusion keeps it off itself. Tagging thirty-three rows to work around one is exactly the kind
of hand-maintained state this table exists to remove.

---

## Stacks

**`stacks: false` means a second source of this contributes nothing**, and it is one flag for what
are currently two unrelated mechanisms: the table's `Stacks` column, which today governs only
unclassified objects, and `Planner.Booked`, which hardcodes the same rule for runes.

Every rune row ships `stacks: false`. Nothing in the planner needs to know that a rune is a rune for
this purpose any more.

The *rule* comes from the table; the *bookkeeping* stays in the planner, because non-stacking is
positional - which copy wins depends on where each is sourced along the chain and how far it reaches,
and only the route knows that. What changes is that `Booked` implements a flag it reads rather than a
category it assumes.

### What the table does not decide

**Where an effect reaches is the planner's.** Propagation forward along the chain, which copy of a
non-stacking effect is the live one, whether a link is downstream of another - all route facts. The
table says *what* an effect does and *to what kind of thing*; the planner says *which instances*.
`here` is the one temporal word the table carries, because "reaches its own waves only" is a property
of the effect rather than of the route.

**There is nothing between `here` and forward.** No effect reaches the rest of the current link but
not the links after it, so the grammar needs no third scope.

### The concentration bonus no longer exists

An earlier draft of this spec listed it here. That was stale: `Concentrate` was deleted when the
payout became multiplicative, because the grouped product computes the compounding exactly and the
staircase was a guess sitting on top of an exact answer. Nothing reads it, there is no setting, and
there is nothing to express as an effect.

Two pieces of debris to clear while in the area:

- an orphaned `<summary>` block in `AutoExpeditionSettings.cs` (around line 3538) documenting the
  removed slider, with no property behind it
- `Tags.Monsterly`'s doc, which still explains itself in terms of concentration. It survives for a
  different job - a duplicate only suppresses where the rune lands, so a chest-scoped source tells a
  remnant's monsters nothing - and the comment should say that instead.

---

## Type, tags, and matches

**Type is free text for grouping and search.** `Entity`, `Monster`, `Rune`, `Effect`, `Modifier`,
`Wave`, `Pool`, `Chest`. It carries no arithmetic. Its job is to let you find the things that are like
this thing, which is what `Kind` was pretending to do while also deciding the arithmetic.

**Tags are what effects target**, and they are the existing `Tags.Known` vocabulary plus whatever
rows carry. An effect's target is a tag or a row id, looked up in one namespace; an unresolved target
is a validation error rather than a silent zero.

**Matches is what binds a row to something in the world**, and it is the layer that currently has no
table representation at all:

**Ids use `/` for hierarchy**, not `.`, so that `monster/rare` is a row and `monster.weight` is a tag
with an attribute and the two can never be read for one another. `/` is also what the v1 table already
used - `kind:Chest/Gold`, `kind:Strongbox/StrongboxResearcher` - so it is the established separator
here rather than a new invention.

```
art:elitemarker.ao, art:elitemarker_02.ao, art:elitemarker_03.ao
path:Metadata/Chests/StrongBoxes/ResearchStrongboxExpedition
icon:RewardChestCurrency                        and not RewardChestCurrencyRare
mod:ChestSummonRares                            a modifier, by its whole id, with its values
label:"Guarded by {n} packs of Rare Monsters"   a sentence, with a captured number
```

**A rule matches the whole name.** Every rule in the shipped table names a whole identifier, families
included: the three elite marker models are three rules, the five henges are five. Writing them out
is longer, and that is the point - you can read the table and know exactly what it answers for, and a
model the game adds turns up as an unrecognised row rather than being quietly absorbed by a stem.

This was a substring match anywhere, which is looseness nothing asked for in either direction:
`chestmarker2` would have answered for a `bigchestmarker2x.ao`, and every whole name in the table was
being run through it for no benefit. It is also why the two Grand currency chests needed a
longest-wins tiebreak, `RewardChestCurrency` being a substring of `RewardChestCurrencyRare`; matched
whole, they simply are not each other.

A substring rule fails silently in both directions - it can answer for something it was never meant
to, and it can answer for nothing at all, and neither is visible from the table. An exact rule can be
checked against a list of known names; a substring rule can only be checked against what you have
already seen.

**A star at either end opens that end**, and it is a decision typed into a cell. Where two rules both
answer, longest wins, measured without the stars — so a named row always beats a star.

Two rules in the shipped table use one, and they are the shape that earns it. A strongbox is named
`<Base>Strongbox<Tier>`, the tier is what the weight tracks, and the set of bases is open — three of
the eight seen had no row, one of them in forty site files. So `path:*StrongboxHigh` and
`path:*StrongboxExpedition` price an unwritten base at its tier's number rather than at the
unrecognised weight, and enumerating bases no longer goes stale every time GGG adds one.

The difference from a stem over art files is worth being precise about: a stem absorbs unknown
**objects** into a specific row, which is a guess at what the thing is. These absorb an unpriced
**base** into a row whose name says exactly that, which is a true statement about it.

**No rule at all is the honest answer for a name nobody has recorded in full.** The five special
relics were matched on fragments - `Objects/Sulphite`, `GoblinRelic`, `HeathHenge` - and not one of
them appears in any metadata path on record, so there is no exact form to write. They are bound by
modifier and by art instead, and the three wisp traps, which have neither, bind to nothing until a
dump names them. An object that binds to nothing is filed with its full identity for you to bind,
which is the loop working; a guessed fragment is the loop being skipped.

This replaces `Scan.Kind`, `Scan.Tier`, and the `art:` / `family:` / `found:` / `kind:` / `setting:`
id namespaces. After it, "nothing in the table says `elitemarker_02.ao` spawns a rare monster" is no
longer true: one row says it, in one line, and a patch that adds `elitemarker_04.ao` needs no code.

Ids become short slugs. The eighty-character `found:Metadata/...||art|states||name|` keys go; the
identifying fields stay on the row as `matches` and as the discovery record, which is what they were
for.

---

## Transparency

Two features, and they are the point of the rewrite rather than trimming on it.

**Explain.** Clicking `Total` opens the rollup: every child, its count, where the count came from,
its own total, the product, and the sum. Three levels deep for a remnant. If a number in this plugin
cannot be explained by a popup, it is a bug in the table.

**Validate on entry.** Unknown child id, unknown tag, unreadable mod path, cycle, unparsed
expression - each paints the cell red and states the reason. Today a mistyped group name is a
silently separate group.

**No fallbacks anywhere.** A count that should have come from the game and did not, a remnant whose
runes were not read, a mod path that resolved to nothing: each is a stated error on the row and on
the affected marker, never a written default quietly standing in. A fallback is how a broken read
becomes a plausible wrong answer.

---

## Columns

| column | editable | notes |
| --- | --- | --- |
| Name | shipped only | |
| Type | yes | free text |
| Weight | yes | this row alone |
| **Total** | no | the rollup; click to explain |
| **Children** | yes | `id xN`, `id x matched.Values[-1]` |
| **Effect** | yes | the expression |
| Tags | yes | |
| Size | yes | world units |
| Stacks | yes | false means a second source adds nothing |
| Share | yes | draw share, rune rows only; see Share |
| Matches | yes | behind the detail toggle |
| Seen / Status / First seen | no | unchanged |

`Propagation %` and `Multiplicative behaviour` are gone, folded into `Effect`.

---

## Files and migration

`weight_reference_table_defaults.json` and `weight_reference_table_custom.json`, same two-layer
resolution and same prune-on-match as today - that part works and is not being changed.

**Version 1 files are read and translated, never discarded.** The translation is mechanical:

| v1 | v2 |
| --- | --- |
| `scope: "monster=20"`, `combines: "+X"` | `effect: "monster *= +20% as X"` |
| `scope: "monster=20"`, `combines: "*X"` | `effect: "monster *= +20%"` |
| `scope: "monster=20"`, no combines | `effect: "monster *= +20% as unclassified_pool"` |
| `carries: 15`, no scope | `effect: "monster *= +15% as unclassified_pool"` |
| `rune:soul` weight 4 | `effect: "monster *= +4%"`, weight 0 |
| `rune:time` weight 16.01 | `effect: "monster *= +16.01%"` - the tie-break survives |
| `rune:power` + `combines: "empower"` | `effect: "rune *= +50%"` |
| `kind:MonsterRare` | `monster/rare` |
| `found:<eight fields>` | a slug, with the fields under `matches` |
| `art:x` / `family:k` | the `size` on the row that matches it |
| `setting:Strongbox*Pack` x6 | one `pack/*` row per rarity |

The shipped defaults file is rewritten by hand as part of the work - it is the accumulated answer and
deserves to be re-stated in the new form rather than machine-translated. **A custom file is
translated on load and rewritten in v2 form**, and the tuned rune weights in it survive as their
equivalent effect expressions. No file is read for the last time without being written back.

`WeightSettings`, `RuneWeights.RunePercent`, `Runes.Tiered` and `AutoExpedition.SeedRunes` are
deleted at the end of stage 4, once rune rows are stored in the table like every other row.

---

## What this deletes

| gone | because |
| --- | --- |
| `Weighing.Tiers` | remnant and strongbox composition are `children` |
| `Weighing.Blended`, `Splitting` | the magic chance is two fractional children |
| `Weighing.Waving`, `Unearthed`, `Wearing`, `Worn` | children carry their own tags |
| `Weighing.Lifted`, `Lifting`, `Lifts`, `Scaling` | an effect can target an effect |
| `Weighing.Shipped`'s `switch` | the row says what it is made of |
| `Scan.Kind`, `Scan.Tier` | `matches` |
| `Scan.Guarding`'s inherent/rolled split, `Target`'s six pack fields | mod rows, both paths to one child |
| `Wrt.Id.*`, `Wrt.Id.Was` | slugs |
| `Rolls.Runes` shares | `share` on the rune row |
| `Planner.Booked`'s rune category | `stacks: false` |
| `WeightSettings`, `RunePercent`, `Runes.Tiered` | rune rows are ordinary rows |
| `setting:MarkerMagic`, `setting:Wave*`, `setting:Strongbox*Pack` | counts on child edges |
| the orphaned concentration doc block | the slider it documents was removed |

---

## Stages

Each stage builds and is shippable; scores are expected to be identical until stage 2.

0. **Dump `ExplicitModData`.** Done - the count is `Values[-1]` and the rarity is the mod id. See
   above.
1. **Grammar.** Parse `effect`, parse `children`, validate both, show `Total`, show the explain popup.
   v1 rows translate on load. No scoring change: the translated expressions reproduce the current
   numbers exactly, and the dump prints old and new side by side to prove it.
2. **Composition.** Done. `Weighing` walks the composition rows instead of `Tiers`, `Blended`,
   `Splitting` and `Waving`; twelve `setting:` rows that were counts in the weight column are retired
   onto child edges, with tuned values carried across before the prune can reach them.

   **Scores should not have moved**, and these are the figures that say so - each read off a live
   table before the flip and reproduced exactly by the rollup after it:

   | | old path | v2 rollup |
   | --- | --- | --- |
   | one remnant wave | `1x20 + 3x6.45 + 10x3.2` | `remnant/wave` 71.35 |
   | a remnant's waves | `Waves()` 214.05 | `3 x 71.35` |
   | the remnant itself | `1 + 214.05` | `kind:Remnant` 215.05 |
   | normal guarding pack | `10 x 3.2` | `pack/normal` 32 |
   | magic, rare packs | `5 x 6.45`, `1 x 20` | 32.25, 20 |
   | caged rares | `3 x 20` | `caged` 60 |
   | monstermarker | `Blended()` 3.2 | `monstermarker` 3.2 |

   Mod rows replacing the pack FIELDS is still to come; the counts still arrive from the label parse.
3. **Matches.** Classification moves into the table; `Scan.Kind` and the id namespaces go.
4. **Runes and stacking.** Rune rows carry `share`, `effect` and `stacks: false`; `WeightSettings`,
   `Rolls.Runes` and `Booked`'s rune category go.
5. **Prune.** Delete the v1 reader once a season has passed.

---

## Settled questions

| | |
| --- | --- |
| Count on the edge or the child | **on the edge**, so two parents may spawn different numbers of the same child |
| Flat `+=` | **in the grammar from the start** |
| A scope between `here` and forward | **does not exist**; two scopes are enough |
| Inherent vs rolled packs | **the same thing**; both mod paths point at one child row |
| Does `ItemMod.Values` carry the pack count | **yes** - it is the last value; the mod id names the rarity. `label:` is not needed |
| Are draw shares readable from the game | **no** - `Expedition2RunesWeight` carries no frequency. Which runes a level admits **is** readable, from its `Level`, so the pool is gated by the game and weighted by the measured `share` |

## Open questions

1. **What else should be a mod row?** Every remnant modifier, every map mod affecting expedition,
   possibly the monster marker's magic chance if the game states it anywhere. Worth a sweep of
   `ExplicitModData` across a whole site before the shipped defaults are written.
2. **Does `size` belong on the matched row or on a family?** Today `art:` and `family:` exist because
   four tilesets share one answer. With slugs, four rows can match four arts and carry the same size,
   or one row can match all four. The second is fewer rows and loses the per-art measurement the
   extents table collected.
