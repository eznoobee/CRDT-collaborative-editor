import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';

import { App } from './App';
import { bootstrap } from './app/bootstrap';

const container = document.getElementById('root');
if (container === null) {
  throw new Error('Root element #root not found.');
}

const root = createRoot(container);

// Rendered before the network is touched, so a slow connection shows a page
// rather than a blank document.
root.render(<StrictMode><App /></StrictMode>);

// The sign-in redirect and the document open happen once, here, outside React.
// See app/bootstrap.ts: an OIDC callback consumes a single-use code, and React
// runs effects more than once.
const result = await bootstrap();
root.render(<StrictMode><App result={result} /></StrictMode>);
