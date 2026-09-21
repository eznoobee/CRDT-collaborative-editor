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
  it('says nothing about a backlog that ordinary typing produces', () => {
    // Deliberately not "any unsent work". `drain` sends one batch per round
    // trip, so a batch or two is queued at any moment during healthy typing,
    // and a line that appeared then would be on screen permanently — which is
    // how an indicator becomes something people stop reading.
    expect(backlogMessage('live', 0)).toBeNull();
    expect(backlogMessage('live', 7)).toBeNull();
  });

  it('names the backlog once it is past anything typing explains', () => {
    // The case §8's target 2 run found: 725 of 1,000 keystrokes sat in this
    // outbox while the UI showed `live` and no problem for two minutes. The
    // user could see their own text, because local edits apply locally, and
    // nothing said most of it was going nowhere.
    expect(backlogMessage('live', 8)).toBe('8 edits not sent yet.');
    expect(backlogMessage('live', 431)).toBe('431 edits not sent yet.');
  });

  it('stays quiet when the connection is already reported as down', () => {
    // Offline says so on its own line and carries §9's window countdown. A
    // second message about unsent work there is the same fact twice, and the
    // one that matters — that this is not normal — is the connection state.
    expect(backlogMessage('offline', 431)).toBeNull();
    expect(backlogMessage('connecting', 431)).toBeNull();
    expect(backlogMessage('stopped', 431)).toBeNull();
  });
});
