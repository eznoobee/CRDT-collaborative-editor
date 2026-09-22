import { render, screen } from '@testing-library/react';
import { App, backlogMessage } from './App';

describe('App', () => {
  it('renders the application shell', () => {
    render(<App />);

    expect(screen.getByRole('heading', { name: 'Collaborative Editor' })).toBeDefined();
  });

  it('renders markup in text content literally rather than as HTML', () => {
    // PROJECT_SPEC.md §7 requires document text to render as literal text.
    // The full test lands with the editor in Phase 4; this pins the property
    // the editor will depend on — that React escapes text children by default —
    // so a regression in how content reaches the DOM is caught early.
    const hostile = '<script>alert(1)</script>';

    const { container } = render(<p>{hostile}</p>);

    expect(container.querySelector('script')).toBeNull();
    expect(container.textContent).toBe(hostile);
  });
});

describe("the unsent-work line (§8 target 2, §13.13)", () => {
  const STUCK = 5_000;

  it('says nothing while the queue is draining normally', () => {
    // Deliberately not "any unsent work", and deliberately not a count. One
    // pasted paragraph is hundreds of operations that `DocumentSession` splits
    // into several batches at §7's ingest cap, so a queue several deep is what
    // a normal paste looks like for the moment it takes to drain. A line that
    // appeared then would be on screen every time anyone pasted, which is how
    // an indicator becomes something people stop reading.
    expect(backlogMessage('live', 0, 0)).toBeNull();
    expect(backlogMessage('live', 40, 200)).toBeNull();
    expect(backlogMessage('live', 400, STUCK - 1)).toBeNull();
  });

  it('names the backlog once the oldest unsent work has been waiting', () => {
    // The case §8's target 2 run found: 725 of 1,000 keystrokes sat in the
    // outbox while the UI showed `live` and no problem for two minutes. The
    // user could see their own text, because local edits apply locally, and
    // nothing said most of it was going nowhere.
    //
    // The question is "is my work stuck", which is about age. The count stays
    // in the sentence because it is what the user needs once told — and it is
    // the same number §9's discard reports if the work is later lost.
    expect(backlogMessage('live', 8, STUCK)).toBe('8 edits not sent yet.');
    expect(backlogMessage('live', 431, 60_000)).toBe('431 edits not sent yet.');
  });

  it('says nothing when there is nothing unsent, however long ago that was', () => {
    // An empty queue has no oldest entry, and a stale age with nothing behind
    // it would put a line on screen about work that has all been accepted.
    expect(backlogMessage('live', 0, 60_000)).toBeNull();
  });

  it('stays quiet when the connection is already reported as down', () => {
    // Offline says so on its own line and carries §9's window countdown. A
    // second message about unsent work there is the same fact twice, and the
    // one that matters — that this is not normal — is the connection state.
    expect(backlogMessage('offline', 431, 60_000)).toBeNull();
    expect(backlogMessage('connecting', 431, 60_000)).toBeNull();
    expect(backlogMessage('stopped', 431, 60_000)).toBeNull();
  });
});
