/**
 * What the largest legitimate user does, in the units people do it in (§13.37).
 *
 * @remarks
 * Every number here comes from a person's behaviour and none from
 * configuration. That is the whole technique: a test that computes its input
 * from `MaxOperationsPerBatch` passes at any setting of
 * `MaxOperationsPerBatch`, including a wrong one, because it is comparing the
 * number to itself. These are the other side — the size of the thing a user
 * actually does — so a limit that moves under them goes red.
 *
 * Kept in one file, and imported by both the client suite and (restated, with
 * the same provenance) the server suite, so that the numbers can be argued
 * with as a set. If one of them is wrong, it is wrong about people, which is a
 * conversation worth having in one place.
 */

/**
 * A pasted paragraph of prose, in code points.
 *
 * @remarks
 * Around 150 words at six characters a word. The paragraph is the unit because
 * it is the unit of the action — nobody pastes 256 characters, they paste the
 * thing they copied.
 */
export const PARAGRAPH = 900;

/**
 * Three pages of prose, in code points.
 *
 * @remarks
 * §13.37's own figure, and the case that found the rate limit's default in the
 * wrong place. Kept at the number §13.37 used so that the finding and the
 * regression test for it agree.
 */
export const THREE_PAGES = 3_000;

/**
 * Tabs one person has the same document open in while pasting.
 *
 * @remarks
 * The per-user budget's case, which the per-connection budget cannot see.
 */
export const TABS = 3;

/**
 * People in a workshop who all open the one document they were sent.
 *
 * @remarks
 * A class, a team, a workshop — the largest gathering that shares a single
 * document live. Forty rather than fifty because fifty is the configured cap,
 * and taking the number from the cap is exactly what §13.37 forbids; forty is
 * a room.
 */
export const WORKSHOP = 40;

/**
 * People a new document is shared with in one sitting.
 *
 * @remarks
 * §13.37's own case for the document API's budget: someone sets up a document
 * for their team and grants everyone at once, as fast as they can click.
 */
export const TEAM = 30;

/**
 * Documents one person has open at once.
 *
 * @remarks
 * A dozen tabs is ordinary work, not abuse.
 */
export const OPEN_DOCUMENTS = 12;

/**
 * Seconds a cold first load takes before the ticket is redeemed.
 *
 * @remarks
 * The page is fetched, the bundle parsed, the token acquired, `negotiate`
 * answered, and only then is the socket opened. On a cold stack with a cold
 * CDN and a slow phone, twenty seconds is a bad day rather than a broken one.
 */
export const COLD_LOAD_SECONDS = 20;

/**
 * Seconds a reload across a slow network takes before the replica is reclaimed.
 *
 * @remarks
 * Longer than a cold load because the claim has to survive the whole gap: the
 * old connection dies at the moment of reload and the new one arrives after
 * the page has come back. A train tunnel is the case.
 */
export const SLOW_RELOAD_SECONDS = 45;

/**
 * Code points in a long document that has been appended to over months.
 *
 * @remarks
 * A book chapter, or a year of a team's weekly notes: around four thousand
 * words. Deliberately not "the largest document the format allows" — the
 * question is what a person writes, and a person does not write five megabytes
 * of prose into one collaborative document.
 */
export const LONG_DOCUMENT = 25_000;

/**
 * Operations a peer's burst delivers out of order across a partition.
 *
 * @remarks
 * §5's pending set exists for this: a link comes back and a peer's backlog
 * arrives before the operation everything in it depends on. One person's
 * offline afternoon is the backlog.
 */
export const BURST = 2_000;

/**
 * Days a laptop stays closed over a long weekend.
 *
 * @remarks
 * §9's offline window has to still be open when it reopens. Four days, because
 * a long weekend plus the day nobody opens the laptop is not a rare event.
 */
export const LONG_WEEKEND_DAYS = 4;
