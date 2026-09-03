import { act, fireEvent, render, screen } from '@testing-library/react';

import { DocumentSession } from './DocumentSession';
import { Editor } from './Editor';
import { Replica, decodeOperations, encodeOperations, parseReplicaId } from '../crdt';

/**
 * The editing surface over a real replica (§9).
 *
 * The vacuity risks, named before these were written:
 *
 * 1. **"Typing shows the characters" passes against a bare textarea.** The
 *    component under test could be `<textarea />` with no session at all and
 *    every naive assertion would hold. So each test asserts what reached the
 *    *replica* — and one asserts a remote change reaching the screen, which a
 *    component holding its own state cannot do.
 * 2. **A test that types into a connected editor cannot tell whether the
 *    keystroke waited.** §9 forbids a round trip in the typing path, so the
 *    session here is given a sink that returns nothing and does nothing; if the
 *    text still appears, no round trip was involved.
 *
 * Edits arrive as `change` events carrying the whole value, which is what a
 * controlled `textarea` actually receives — the browser reports its new value,
 * not the keystroke. Simulating keystrokes instead would test a translation
 * layer this component does not have.
 */

/** One edit, as the browser delivers it: the field's whole new value. */
function type(text: string): void {
  fireEvent.change(screen.getByRole('textbox'), { target: { value: text } });
}

function shown(): string {
  return screen.getByRole<HTMLTextAreaElement>('textbox').value;
}

const ID = parseReplicaId('00000000-0000-0000-0000-00000000000a');
const PEER = parseReplicaId('00000000-0000-0000-0000-00000000000b');

describe('the editor', () => {
  it('puts what is typed into the replica, not into its own state', () => {
    const sent: Uint8Array[] = [];
    const session = new DocumentSession(ID, (batch) => sent.push(batch));

    render(<Editor session={session} />);
    type('h');
    type('hi');

    expect(session.text).toBe('hi');

    // The operations, replayed into a replica that never saw the component.
    // A textarea holding its own value produces none of these.
    const peer = new Replica(PEER);
    for (const batch of sent) {
      for (const operation of decodeOperations(batch)) {
        peer.apply(operation);
      }
    }

    expect(peer.text).toBe('hi');
  });

  it('renders a remote edit that never went through the keyboard', () => {
    // The decisive one. A component that echoed keystrokes into its own state
    // would show local text perfectly and this not at all.
    const session = new DocumentSession(ID, () => {});
    render(<Editor session={session} />);

    type('ab');

    const remote = new Replica(PEER);
    const operations = [...'XY'].map((value, index) => remote.insert(index, value));

    // Inside act, because the update originates outside React — a broadcast
    // arriving on a socket does the same thing in the browser, where the
    // scheduler flushes on its own.
    act(() => session.receive(encodeOperations(operations)));

    // No keystroke here on purpose. The component has to re-render from the
    // session, and this is the assertion an uncontrolled textarea cannot pass:
    // nothing put these characters into the DOM but the subscription.
    expect(shown()).toBe(session.text);
    expect(shown()).toContain('X');
    expect(shown()).toContain('Y');
    expect(shown()).toContain('a');
  });

  it('accepts typing with a sink that does nothing', () => {
    // §9: no server round trip in the typing path. Nothing here can await the
    // network, because there is nothing to await.
    const session = new DocumentSession(ID, () => {});

    render(<Editor session={session} />);
    type('offline');

    expect(shown()).toBe('offline');
    expect(session.text).toBe('offline');
  });

  it('reports every change so a host can persist', () => {
    // 4.4 hangs IndexedDB writes off this. A callback that fired only on the
    // first keystroke would lose everything after it, and the loss would be
    // invisible until a reload.
    let changes = 0;
    const session = new DocumentSession(ID, () => {});

    render(<Editor session={session} onChanged={() => changes++} />);
    type('a');
    type('ab');
    type('abc');

    expect(changes).toBe(3);
  });

  it('does not author while read-only', () => {
    // §7 demotes an editor to viewer mid-session, and §9's rejection contract
    // drops the client to read-only rather than letting it type into a
    // document the server will refuse.
    const sent: Uint8Array[] = [];
    const session = new DocumentSession(ID, (batch) => sent.push(batch));

    render(<Editor session={session} readOnly />);
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'nope' } });

    expect(session.text).toBe('');
    expect(sent).toHaveLength(0);
  });

  it('renders markup in the document as literal text', () => {
    // §7 requires document text to render literally. A textarea cannot hold
    // markup by construction, which is one of the reasons it is a textarea —
    // but the property is asserted rather than assumed, because "we picked a
    // safe element" stops being true the moment someone renders the same text
    // somewhere else.
    const hostile = '<script>alert(1)</script>';
    const session = new DocumentSession(ID, () => {});

    const { container } = render(<Editor session={session} />);
    type(hostile);

    expect(container.querySelector('script')).toBeNull();
    expect(session.text).toBe(hostile);
    expect(shown()).toBe(hostile);
  });

  it('keeps an emoji whole when typed between other characters', () => {
    // The unit bug, at the surface where it would actually reach a user.
    const session = new DocumentSession(ID, () => {});

    render(<Editor session={session} />);
    type('ab');
    type('ab😀');

    expect(session.text).toBe('ab😀');
    expect([...session.text]).toHaveLength(3);
  });
});
