# Known issues

What is wrong, or unfinished, as of the Expedition Tablet release (2026-10-10). Each entry says what you will see
rather than what the cause might be.

If you hit something that is not on this list, a dump helps more than a description: press **F6** and
keep the file it writes to `config/AutoExpedition/dumps`.

---

## Expedition tablets

- **The automation needs a Fragments stash tab.** The Craft, Reforge, Craft and Reforge, Withdraw and Deposit zones
  are drawn under it, and the runs work in its Expedition tablet sub-tabs. Ordinary stash tabs are not supported.
- **A run can stop with "stopped: MoveNeverLanded".** Seen once, during Craft and Reforge; the run now tries the move
  once more before stopping, and the cause is not found yet. If it stops this way, press
  **F6** before anything else: the dump records the move and where the cursor was.
- **Prices need NinjaPricer** to turn chaos, divine and vaal listings into one figure. When NinjaPricer cannot fetch,
  the last rates it gave are used; with none at all, a tablet priced in those currencies shows white and is never
  treated as junk.
- **Listings in other currencies** - alchemy, regal and the like - are taken as worth under a chaos when a tablet has
  enough of them, so it gets no price label and counts as junk.
- **Pricing everything takes minutes.** The trade site allows about 30 searches in five minutes, counting your own
  in the browser; the line under the inventory's gold says how long is left. **Show prices for magic tablets**
  reaches that limit much sooner.

---

## A sub-area cap's marker is drawn about 3.3 grid from its art

**Measured, cause not found.** On a sub-area entrance - a catacomb or cavern cap - the ring is drawn
about 3.3 grid units from where the model appears, and the gap shifts relative to the art as the
camera moves.

Everything the game exposes about that object agrees on one position: the entity, the render
component, and both grid properties. The offset matches no footprint, no bounding box and no rotation
the plugin can read, and each of those was measured and ruled out rather than argued about.

**What it costs:** the ring is in the wrong place; whether the catch is in the wrong place is not
known, and that is the half that would matter. The shipped extent for these caps is set slightly
small to compensate, which makes the chain place a little closer than it needs to - wasteful, and not
a miss.

---

## Server garbage collection is strongly recommended

There is a button for it under Debug. It writes an environment variable for your account and takes effect when
ExileCore2 is next started. Without it the search's allocation arrives as frame stutters rather than steady cost.
