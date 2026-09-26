# Reroll advice: pricing and flow

A spec. Stages one and two are implemented; the rest is not.

## The problem

`Rolling.Weigh` priced a roll by sampling: for each candidate remnant, for each sampled outcome, it
re-solved the chain with `Planner.Improve` and averaged the difference. `Improve` is six rounds of
`Sweep -> Order -> Reverse -> Shift -> Polish`, and `Sweep` is commented as *the widest loop in the
search - every link against every candidate in the site*.

On a Grand site that is about 15 links against several hundred candidates, six times over, per
sample, per remnant: **10-20 seconds for one remnant's advice**, with the player standing still. A
site with eight rollable remnants is minutes. It is not slow there, it is unusable.

The cost is not the sampling. It is asking *any spot, any order* per sample.

## The pricing

**Both comparisons matter, and each answers what it can.**

```
gain     = E[ worth(rolled) ] - worth(current) - RollCost
worth(r) = runes(r, at its best position in this chain)
         + (reward(r) >= RewardFloor ? reward(r) : 0)
```

**Against the remnant's own reward and runes** is the self-comparison. It is what rolling actually
changes, and it carries the real risk: a roll can take away something good. This is the only "no"
the advisor needs to say, and a remnant already holding something expensive says it for itself.

**Against the chain** is inside `runes(r, at its best position)`. A rune already arriving earlier is
worth nothing, because runes do not stack; where the remnant sits decides how much it reaches. That
is "good for the chain in its current position and state" without a re-solve.

### Why the displaced link is not charged

An earlier draft priced an off-path remnant by substituting it for a link and charging what that
link caught. That is the wrong question. Rolling is cheap - the currency is what is being spent, not
the route - so "does taking this beat the link it pushes out" sets a bar the decision does not have
to clear. Under it, a remnant the chain does not catch prices at zero and is structurally
unrollable, which is the one answer that is definitely wrong.

## The gates

**Geometric.** A remnant is a candidate when some planned bomb is within range of it:

```
distance(bomb, remnant) <= reach * n + blast        n default 1.0
```

The blast term is there because a new link does not have to land on the remnant, only within blast
radius of it. At `n = 1` that reads as "one new link, reachable from an existing bomb, close enough
to catch it" - a statement rather than a fudge factor. Higher `n` is a dial on optimism.

**Filtering rather than sorting, and it is safe because it is not permanent.** A remnant excluded at
three reach becomes a candidate the moment the route moves near it, because the gate is re-evaluated
every time the chain changes. Walking three reaches to a remnant the chain will not pass is a real
cost; being excluded from one pass is not.

**A large upside is already handled, by the must-take.** A remnant whose reward is above the
must-take threshold forces the chain to route to it, which moves the route nearer, which admits it
through the gate. The two mechanisms compose and neither needs a special case for the other.

What is genuinely out of scope: a remnant that is distant, currently worthless, and might roll into
something large. Must-take cannot see it and the gate excludes it. That is a walk of three reaches
on spec, which is the thing the gate exists to refuse - correctly excluded rather than missed.

## The settings

| setting | default | what it is |
| --- | --- | --- |
| Ignore rewards below (exalts) | 100 | below this a reward contributes nothing to a ROLL decision |
| Only advise rolling within (reach) | 1.0 | the geometric gate's multiplier |
| A roll costs (exalts) | 10 | existing |

**The reward floor is a preference, not a missing calculation**, which is what separates it from the
four thresholds deleted before it - Roll below, Never roll above, Protect propagating runes, Keep for
carried value. Those stood in for an answer nobody could compute. "I do not care about a 60 exalt
reward" is not computable from the game; it is a statement about the player.

**It applies to the roll decision only.** Sixty exalts is still sixty exalts of loot and the chain
must still route to collect it. If the floor reached `Weighing`, the planner would start ignoring
loot the player would happily take, which is a far worse fault than the one it fixes.

## The flow

```
scouting      presolve re-opens on every change; unscouted ground remains
              -> Scouted.Left() is the signal that scouting is done
committed     the site is fully seen; one full solve, full window
advice        pricing over the gated candidates: cheap, immediate
walking       the player walks to the advised remnant
              -> a FULL solve runs here, against the post-roll environment
roll          the player clicks; the site changes
              -> cheap advice again, immediately
              -> a new full solve starts; the player walks
finishing     nothing is worth rolling; the player walks back to the detonator
              -> a last full solve runs on that walk
```

The point is not less work. It is the **same** work, moved into time the player was going to spend
walking. The old flow's fault was never that it solved too much; it was that it solved while
somebody waited.

### Ordering, so one solve only ever sees one site

Strictly: **roll -> cheap advice -> start a solve against the post-roll environment -> walk.** Not
"rolls during solving". A roll landing mid-solve leaves that solve describing a site that no longer
exists, which is what the current guard is for; keeping the roll strictly before the solve lets the
guard be relaxed without that hazard.

### Diverting the player

A new best chain can reorder which remnants are caught, which changes the duplication picture, which
can change the advice. Two rules stop that becoming a ping-pong:

- **Margin.** The new candidate's gain must beat the advised one's by **10%**. Not 5%: the figure is
  a sampled mean over a couple of dozen draws and carries noise of its own, so a threshold below that
  noise floor flips on nothing. A margin has to exceed the estimate's own error before it means
  anything - and the spread is not currently reported, so 10% is a guess at where the floor sits
  until it is.
- **Committed radius.** Once the player is within about 40 grid of the advised remnant, never divert.
  The walk is nearly paid for and the advice has stopped being useful.

**The trigger is "a new best chain was published", not "the order changed".** Order changing does not
by itself change which remnant to roll - it changes what duplicates what. So: a new best lands,
re-run the advisor against it, and let the margin decide whether the answer moved.

### The walk back

Rolling ending is not the end of the free time: the player still has to walk back to the detonator. A
full-window solve always runs on that walk. The action key arriving early stops it, as it already
stops any search, so nothing is lost by starting it.

## Must-takes

A roll into an expensive reward marks the remnant must-take. The insistence bonus is larger than
anything the site can pay, so **any chain holding it beats any chain that does not** - which means the
standing chain is provably not the answer, and seeding from it anchors the search in the wrong basin.
With `ShareNot = "0"`, seven of eight workers then adopt that seed.

So on a new must-take the standing chain and the filed best are dropped, and the solve gets the
ordinary window rather than the reroll loop's. **Implemented** - see `Planning.Dropping`.

## Status

| stage | state |
| --- | --- |
| Pricing off `Planner.Restitched` rather than `Improve` | done, but superseded by the pricing above |
| Drop the chain and widen the window on a new must-take | done |
| Self-comparison pricing | not started |
| The enumeration draws SHAPES, not independent runes | **done 2026-09-25** - see below |
| Both sides floored by the reward minimum | **done 2026-09-25** |
| The geometric gate | done - `Rolling.Refuses` |
| Ignore rewards below (exalts) | done - `Rewards.MinimumRerollValue`, read in `Rolling.Enumerated` |
| Extra runic modifier chance from the atlas | done - `Debug.DoubleOrNothingDouble`, replacing a sampled 0.283 with the node's printed 25% |
| A roll costs (exalts) | done - `RollCost`, and it predates this plan |
| Only advise rolling within (reach) | done - `Rewards.RollWithinReach`, default 1.0 |
| The pipelined flow, diversion rules, walk-back solve | not started |

`Planner.Restitched`'s legality rejection and its two-link repair - and the
`RollSubstitutionLinks` setting - are superseded by the geometric gate and should be removed with the
pricing change rather than kept beside it. What survives of it is "the best position for this remnant,
ordered", which is what `worth(r)` needs.

## What the enumeration does now [2026-09-25]

It drew a socket count, how many slots propagate, and then **two runes independently** from a global
table - a remnant assembled from four unrelated variables. Two faults followed:

- **The rune pairs need not exist.** A remnant becomes one recipe and the recipe fixes every slot
  together. At (602,901) the propagating slots offer Fire, Stone, Life and Tempest, Cold, and the
  only pairings that exist are Life+Tempest, Life+Cold, Fire alone and Stone alone. The old loop
  priced Fire with Tempest, which is not a remnant.
- **The reward was drawn apart from the runes** - a flat `Rolls.Average` bolted onto whatever pair
  came up - when both come from the same recipe.

Now it draws **shapes**: a socket count, and which rune is fixed in which slot. The recipes follow
from those, read off the game's own tables by `Valuation.RecipesForShape` and gathered by
`Rolling.ShapesARollCouldProduce` **on the calling thread**, because the advice runs on a task and
`Valuation` touches game memory and two `TimeCache`s.

`FromRecipes` then builds the remnant with every reachable recipe as a choice, each carrying its own
reward and its own runes, and the scoring picks between them exactly as for a real remnant.

**Recipes are chosen, positions are drawn, and the two cannot be folded.** `Best` takes the max over
choices inside one `Score`, so a shape's recipes cost one score between them. Which slots propagate
is the game's dice, so it stays outside as an average - uniform over the position sets, because
nothing states those odds.

`Rolls.Sockets` lists seven, eight and nine again rather than folding them into six. Folding did not
make the tail uncertain, it made it absent - and nine-socket remnants are where Mirror of Kalandra
and Hinekora's Lock live.

**Unverified in play.** Build-clean and correct by construction, but no reroll run has been watched
since, and the cost is unmeasured against the 18 seconds over twelve remnants on record for the old
loop. Expect the advice to get more pessimistic: the rolled side loses its flat 64.9ex credit for
real per-recipe prices, most of them smaller, and those under the floor now go to nought.

## Open questions

**1. Is the sampled gain precise enough for a 10% margin to mean anything? Probably not.**

The margin was chosen to sit above the estimate's own noise, and that noise has never been measured.
The gain is a mean over a couple of dozen draws, its spread is reported nowhere, so 10% is a guess at
where the floor sits. Reporting the spread beside the mean is what would settle it - and would also
say whether the threshold should be a percentage at all, rather than a multiple of the standard
error of the mean, which is the quantity the question is actually about.

The spread is now reported. `Rolling.Premium` returns the standard error of the mean beside it and
the number of draws that came back above nought, and both appear per remnant in the dump's rolling
section as `re-route +N.N +-E.E over L/S draws, P% of it`.

**Read the count, not only the error.** Most draws are exactly nought - each sample is floored there
- so this is the spread of a spike at zero with a tail, and the standard error FALLS as the tail
gets rarer while the estimate gets worse. A mean resting on one draw in twenty is the case the
margin cannot survive, and the count is the only thing that shows it.

**Open** until the numbers are read off a real site, but no longer unmeasurable.

**2. Is there time for a full solve while the player walks the site? Yes, near enough.**

Scouting a site takes longer than a full-window solve, so the solve fits in time the player was
going to spend anyway - which is the assumption the whole flow rests on. That is the player's
reading and not a measurement; what would confirm it is the two figures side by side, how long a
full-window solve takes against how long scouting the same site takes, and both are already in the
dumps.

**Answered, unmeasured.**

**3. At `n = 1`, does the gate keep every remnant a full solve would route to?**

**Half of this is settled by the arithmetic rather than by measurement.** A remnant the CURRENT
chain catches sits within `blast` of some link, and the gate admits everything within
`reach * n + blast`, so for any `n >= 0` a caught remnant cannot be gated out. The gate can never
exclude a remnant the standing chain already visits.

What is left is the case the gate exists for: a remnant the current chain misses, which a re-solve
after the roll would route to. That one is not provable from the geometry, because the route being
compared against does not exist yet. Reading it means printing the remnants the gate refused, with
the distance, and checking a later chain against them - the refusal line now carries the distance
and the bound it failed, so a dump says which remnants were dropped and by how much.

**Open**, and narrowed to the half that needs a measurement.
