import { useCallback, useSyncExternalStore } from 'react';

import type { DocumentSession } from './DocumentSession';

/**
 * The editing surface (§9).
 *
 * @remarks
 * <p>
 * A `textarea`, deliberately. A `contenteditable` would let the browser
 * restructure the DOM on its own — pasting styled text, autocorrecting,
 * inserting `<br>` — and every one of those arrives as a change the editor did
 * not make and has to reverse-engineer. §9 needs the text and nothing else, and
 * a textarea reports exactly that.
 * </p><p>
 * The value rendered is always the replica's, never React state holding
 * something else. That is what makes a remote edit and a local one the same
 * thing: both reach the screen by changing the replica and re-rendering from it.
 * A component that echoed the keystroke and reconciled later would show the
 * user's own text correctly and everyone else's a frame late — and would diverge
 * outright the moment a remote delete landed inside what they were typing.
 * </p>
 */
export interface EditorProps {
  /** The session this editor renders and edits. */
  readonly session: DocumentSession;

  /** Called after any change, local or remote, so a host can persist. */
  readonly onChanged?: () => void;

  /** Whether editing is allowed — false for a viewer, or a lost claim (§7). */
  readonly readOnly?: boolean;

  readonly label?: string;
}

export function Editor(props: EditorProps): React.JSX.Element {
  const { session, onChanged, readOnly = false, label = 'Document' } = props;

  // Subscribed to the session rather than holding state of its own. The text
  // lives in the replica; a copy here is how the two get to disagree, and
  // rendering only on keystroke would leave a remote edit invisible until
  // somebody typed.
  useSyncExternalStore(
    useCallback((notify) => session.subscribe(notify), [session]),
    () => session.revision,
    () => session.revision,
  );

  const change = useCallback(
    (event: React.ChangeEvent<HTMLTextAreaElement>) => {
      // Checked here, not left to the `readOnly` attribute. That attribute
      // stops a person typing and stops nothing else — a change dispatched by
      // script, an autofill, a test — and every one of those would author
      // operations the server is going to reject, from a client that has
      // already been told it may not write (§7, §9).
      if (readOnly) {
        return;
      }

      session.edit(event.target.value);
      onChanged?.();
    },
    [session, onChanged, readOnly],
  );

  return (
    <textarea
      aria-label={label}
      readOnly={readOnly}
      value={session.text}
      onChange={change}
      spellCheck={false}
    />
  );
}
