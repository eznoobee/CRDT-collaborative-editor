import { useCallback, useEffect, useState } from 'react';

import { ApiRefusal, ROLE, type DocumentApi, type MemberEntry, type RoleValue } from './api';
import { messageFor } from './Home';

export interface ShareProps {
  readonly api: DocumentApi;
  readonly documentId: string;

  /** The caller's own id, so they cannot try to revoke themselves. */
  readonly meId: string;
}

/**
 * Membership management, for an owner (§9).
 *
 * @remarks
 * <p>
 * Rendered only when the caller owns the document, and that is a convenience
 * rather than the control: the server refuses an editor with 403 and a stranger
 * with 404 whatever this component chooses to draw. A UI that hid the buttons
 * and nothing else would be §13.19's shape — a check that looks like the rule
 * and enforces only its appearance.
 * </p><p>
 * A grant names a user id because §9 has no directory and no invitation by
 * email address; the person being invited reads their id from the home page and
 * passes it on. The refusal for an unknown id says so in the server's own
 * words, which is the only version of that message a user can act on.
 * </p>
 */
export function Share(props: ShareProps): React.JSX.Element {
  const { api, documentId } = props;
  const [members, setMembers] = useState<MemberEntry[] | null>(null);
  const [userId, setUserId] = useState('');
  const [role, setRole] = useState<RoleValue>(ROLE.editor);
  const [problem, setProblem] = useState<string | null>(null);
  const [allowed, setAllowed] = useState(false);

  const refresh = useCallback(() => {
    api.members(documentId).then(
      (found) => { setMembers(found); setAllowed(true); },
      (error: unknown) => {
        // The server decides who may manage membership, not this component. A
        // panel hidden by a local role check and nothing else would be
        // §13.19's shape: a control that looks like the rule and enforces only
        // its own appearance. Here the refusal *is* the answer, and a 403 or a
        // 404 means there is nothing to draw.
        if (error instanceof ApiRefusal && (error.status === 403 || error.status === 404)) {
          setAllowed(false);
          return;
        }

        setProblem(messageFor(error));
      },
    );
  }, [api, documentId]);

  useEffect(refresh, [refresh]);

  const grant = () => {
    const wanted = userId.trim();
    if (wanted === '') {
      setProblem('A user id is required.');
      return;
    }

    api.grant(documentId, wanted, role).then(
      () => { setUserId(''); setProblem(null); refresh(); },
      (error: unknown) => { setProblem(messageFor(error)); },
    );
  };

  const revoke = (who: string) => {
    api.revoke(documentId, who).then(
      () => { setProblem(null); refresh(); },
      (error: unknown) => { setProblem(messageFor(error)); },
    );
  };

  if (!allowed) {
    return <></>;
  }

  return (
    <section data-testid="share">
      <h2>Sharing</h2>
      {problem === null ? null : <p role="alert" data-testid="share-problem">{problem}</p>}

      <label>
        User id
        <input
          data-testid="grant-user"
          value={userId}
          onChange={(event) => setUserId(event.target.value)}
        />
      </label>
      <label>
        Role
        <select
          data-testid="grant-role"
          value={role}
          onChange={(event) => setRole(Number(event.target.value) as RoleValue)}
        >
          <option value={ROLE.viewer}>Viewer</option>
          <option value={ROLE.editor}>Editor</option>
          <option value={ROLE.owner}>Owner</option>
        </select>
      </label>
      <button type="button" data-testid="grant" onClick={grant}>Grant</button>

      <ul data-testid="members">
        {(members ?? []).map((member) => (
          <li key={member.userId} data-member={member.userId}>
            {member.displayName} — {name(member.role)}
            {member.userId === props.meId
              ? null
              : (
                <button
                  type="button"
                  data-revoke={member.userId}
                  onClick={() => revoke(member.userId)}
                >
                  Revoke
                </button>
              )}
          </li>
        ))}
      </ul>
    </section>
  );
}

function name(role: RoleValue): string {
  switch (role) {
    case ROLE.owner: return 'Owner';
    case ROLE.editor: return 'Editor';
    default: return 'Viewer';
  }
}
