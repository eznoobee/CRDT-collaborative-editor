/**
 * Application shell.
 *
 * The editor and the local FugueMax replica arrive in Phase 4, built on the
 * TypeScript core from Phase 1 (PROJECT_SPEC.md §11). This is deliberately
 * inert: it exists so the toolchain has something real to build and render.
 */
export function App(): React.JSX.Element {
  return (
    <main>
      <h1>Collaborative Editor</h1>
      <p>No editor yet — see PROJECT_SPEC.md §11, Phase 4.</p>
    </main>
  );
}
