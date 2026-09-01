import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import reactHooks from 'eslint-plugin-react-hooks';

export default tseslint.config(
  { ignores: ['dist', 'dist-types', 'node_modules', 'coverage'] },
  js.configs.recommended,
  ...tseslint.configs.recommendedTypeChecked,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // PROJECT_SPEC.md §7: the editor renders into a DOM text node. These are
      // lint-level backstops for the XSS requirement; the behavioural test
      // lands with the editor component in Phase 4.
      'no-restricted-properties': [
        'error',
        { object: 'document', property: 'write', message: 'Use DOM text nodes (PROJECT_SPEC.md §7).' },
      ],
      'no-restricted-syntax': [
        'error',
        {
          selector: "JSXAttribute[name.name='dangerouslySetInnerHTML']",
          message: 'Forbidden by PROJECT_SPEC.md §7 — render text into a DOM text node.',
        },
        {
          selector: "MemberExpression[property.name='innerHTML']",
          message: 'Forbidden by PROJECT_SPEC.md §7 — render text into a DOM text node.',
        },
      ],
    },
  },
  { files: ['eslint.config.js'], ...tseslint.configs.disableTypeChecked },
);
