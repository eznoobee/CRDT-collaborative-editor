/**
 * The pending set exceeded the bound its connection set (§5).
 *
 * A protocol violation rather than a resource problem to absorb. §5 requires
 * reject, log and close, and the close has to be distinguishable by the client
 * from the connection simply dropping (§13.13) — so this carries the numbers
 * that explain it rather than being a bare failure.
 */
export class PendingSetOverflowError extends Error {
  readonly pending: number;
  readonly bound: number;

  constructor(pending: number, bound: number) {
    super(`The pending set holds ${pending} operations and the bound is ${bound} (§5).`);
    this.name = 'PendingSetOverflowError';
    this.pending = pending;
    this.bound = bound;
  }
}
