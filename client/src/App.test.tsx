import { render, screen } from '@testing-library/react';
import { App } from './App';

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
