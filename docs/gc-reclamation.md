# Row 29 — can a placeholder tombstone drop its payload?

7.3 found that §5's collection reclaims nothing from a mid-document deletion.
Forward typing builds a chain of right children, so an interior tombstone always
has a visible child, is never a leaf, and rule 2 never collects it. It survives
as a structural **placeholder**, which is correct: right origins can name
tombstones, so the *position* has to stay.

Row 29 asked whether the **payload** has to. A deleted element's character is
never rendered and never affects ordering, which `ElementId` decides — so it
looks droppable, and the register carried the question to be settled on
measurement rather than on the trailing-run case.

Measured by `PlaceholderPayloadMeasurement` against five edit shapes, written
out in the open because the vacuity risk is entirely about which shapes are in
the corpus: measuring append-then-delete-the-end reproduces exactly the case 7.3
found the suite had been testing all along, and answers "nothing to save" from a
corpus in which nothing could have been saved.

## The measurements

| shape | elements | live | tombstones | collected | placeholders | snapshot B | payload B | payload share | all-placeholder share |
|---|---|---|---|---|---|---|---|---|---|
| append then trim the tail (7.3's shape, the control) | 43 | 31 | 12 | 11 | 1 | 69 | 1 | 1.4 % | 1.4 % |
| write and revise: delete a word from the middle | 64 | 47 | 17 | 0 | 17 | 105 | 17 | 16.2 % | −6.7 % |
| draft and rewrite: replace a sentence in place | 80 | 54 | 26 | 0 | 26 | 283 | 26 | 9.2 % | 10.6 % |
| backspace while typing | 35 | 29 | 6 | 5 | 1 | 163 | 1 | 0.6 % | 4.9 % |
| a long document edited over months | 1220 | 1106 | 114 | 0 | 114 | 1410 | 114 | 8.1 % | −4.5 % |

**"All-placeholder share"** is the illegal control: the same document encoded
with the placeholders simply absent. §5 does not permit it — a replica that
dropped their positions would place concurrent inserts differently — and it is
here only as the denominator, to say how much of the placeholder problem
dropping the payload actually addresses.

## Two results, and the second one was not expected

**Mid-document deletion collects nothing at all.** Not "less": zero of 17, zero
of 26, zero of 114. 7.3 established this on one case; across every realistic
shape here it is total. The control is the only trace where collection does
anything, and it does almost everything — eleven of twelve, the twelfth being
the run leader rule 4 retains.

**Removing a placeholder entirely would make the snapshot bigger**, on two of
the five shapes. That is the negative column, and it is not a measurement error.
§6 encodes contiguous elements as a *run*: one header, one deleted-bitmap, one
concatenated UTF-8 string. A placeholder inside a run costs its character and
nothing else, because it is already inside a header somebody else is paying for.
Take it out and the run splits in two, and the second run's header — tag, length
varint, flags, element id, sometimes an explicit parent — costs more than the
character saved. On "write and revise", three interior deletions become four
runs where there was one, and 17 bytes of payload are traded for about 24 bytes
of new headers.

So **the placeholder's position is nearly free and its payload is the whole of
its cost.** That is the opposite of the intuition the question was asked with,
where the position looked like the expensive part.

## The decision

**Not now, and the reason is the size of the prize rather than the difficulty.**
Dropping placeholder payloads is sound — the deleted-bitmap already tells the
decoder which elements in a run are tombstones, so the encoder could write only
the live characters and the decoder could fill the rest with a placeholder rune.
It is worth **8–16 % of snapshot bytes** on realistic editing.

Against that: §6 is the authoritative encoding on both implementations, §9 makes
`binary → JSON → binary` byte-identical a build-breaking requirement, and the
conformance corpus compares the two codecs on every trace. This is therefore a
coordinated change across the C# codec, the TypeScript codec, the corpus and the
normative JSON form — and it buys single-digit-to-low-double-digit percent off a
snapshot that already costs about 1.2 bytes per element.

**What the measurement says to do instead** is nothing here at all. The number
worth attacking is not 8–16 % of the snapshot; it is that 114 of 1220 elements
in a normally edited document are tombstones that will never be collected under
rule 2, and that this fraction grows with the document's age without bound. That
is a question about the collection rules — whether a placeholder whose payload
and children are all themselves collectable can be spliced out of the chain by
rewiring its child's parent — and it is a §5 correctness question, not a §6
encoding one. It is opened as register row 38.

**Reversal condition.** If §6 changes for another reason and the codecs are
being touched anyway, drop the payloads in the same change: the marginal cost is
then small and the 8–16 % is free.
