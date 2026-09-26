# Known issues

What is wrong, or unfinished, as of the first public test build. Everything here is either measured
or reported from a dump, and each entry says what you will see rather than what the cause might be.

If you hit something that is not on this list, a dump helps more than a description: press **F6** and
keep the file it writes to `config/AutoExpedition/dumps`.

---

## The reroller is a work in progress, and not good yet

**This is the least finished part of the plugin.** It is worth switching on to see what it does; it is
not worth relying on to decide what to spend Liquid Verisium on.

**Only two modes are selectable: Continuous and Off.** The other two are withdrawn for now.

- *All rolls* was never implemented. It appeared in the dropdown and behaved as *Best roll only*
  without saying so, which is worse than not offering it.
- *Best roll only* stops the solver for every roll, which is the behaviour Continuous exists to
  replace. It is left in the code and not offered.

There is no separate "enable rolling" switch, so **Off** is how you turn the advice off.

### What Continuous actually does

It keeps a solve running while you walk, and advises one remnant at a time. Which remnant is advised
is picked every frame from what a roll would certainly destroy - the weight of the runes nothing else
on the chain carries - because that costs no scoring and can therefore be answered as you move. The
enumerated figures beside the advice are a fuller reading that arrives after; they are what the
display shows, and they are not what chose the remnant.

### What it does not do, and where it is known to be wrong

- **Reward value is ignored on both sides of the comparison.** The figure beside a roll is what the
  roll does to the runes, not to the money. A remnant whose value is its reward is protected by the
  must-take threshold instead, and see the next entry for a case where that failed.
- **A remnant rolled by hand after the advice has finished may not be protected.** Reported: a remnant
  rolled by hand came back offering 3x Divine Orb, around 1,468ex, and was not added to the must-take
  set although the threshold was 400. **Check must-takes by eye before detonating a chain you care
  about.** Not yet investigated.
- **Four of thirty-four runes have no measured share** - bait, life, protective and rage - so they are
  absent from the pool the reroll odds are drawn from. The thirty that do have one sum to 1.004, so
  there is no room to add these without re-measuring the others. The dump says so under the weight
  reference table.
- **The odds are measured, not read from the game.** The client ships constraints rather than
  probabilities: the only code reference to the rune weights file is its parser's error path, and no
  recipe carries an odds field. So the shares come from 907 observed readings, and they are as good as
  that sample.
- **The expected value of a roll is an estimate that improves while you stand still.** A large remnant
  has more arrangements than can be enumerated in one pass, so the figure is walked in blocks and the
  percentage beside it says how far through it has got. An early figure can move.

---

## An Essence is planned towards, and it is not expedition content

**Reported, not yet investigated.** The plan routes to and intends to take an **Essence** - separate
league content that happens to share the map. Nothing in a dig site should path to one.

You will see this on any map that has Essences near the site: a link placed for something that is not
expedition content, and an explosive spent on it.

**Until it is fixed:** watch for a link that seems to be aimed at nothing, and place that one by hand.

---

## Pressing the auto-place key mid-chain can cancel a solve instead of placing

**Reported, not yet investigated.** With explosives already down, something starts a solve; the next
press of the auto-place key cancels that solve rather than placing the next explosive, so the press
appears to do nothing and has to be repeated.

**Until it is fixed:** if a press does not place, press again.

---

## A sub-area cap's marker is drawn about 3.3 grid from its art

**Measured, cause not found.** On a sub-area entrance - a catacomb or cavern cap - the ring is drawn
about 3.3 grid units from where the model appears, and the gap shifts relative to the art as the
camera moves.

Everything the game exposes about that object agrees on one position: the entity, the render
component, and both grid properties. The offset matches no footprint, no bounding box and no rotation
the plugin can read, and each of those was measured and ruled out rather than argued about.

**What it costs:** the ring is in the wrong place; whether the CATCH is in the wrong place is not
known, and that is the half that would matter. The shipped extent for these caps is set slightly
small to compensate, which makes the chain place a little closer than it needs to - wasteful, and not
a miss.

---

## Smaller things

- **"Spots to draw"** under Debug is a parameter for the "Draw N best spots" button, not a toggle. It
  does nothing on its own.
- **The four planner settings under Debug change the plan**, not the overlay - Double or Nothing,
  Announce Bait, Explosives no closer than, Blast radius adjustment. They are live whether or not
  debug mode is on.
- **The marker offset sliders under Debug are diagnostic.** They exist to investigate the cap offset
  above, they apply whether or not debug mode is on, and they are saved. If markers are drawn in the
  wrong place, check these are all at nought.
- **Server garbage collection is strongly recommended** and there is a button for it at the top of
  Debug. It writes an environment variable for your account and takes effect when ExileCore2 is next
  started. Without it the search's allocation arrives as frame stutters rather than steady cost.
