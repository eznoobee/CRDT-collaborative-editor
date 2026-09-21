import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { cwd } from 'node:process';

import { describe, expect, it } from 'vitest';

/**
 * Every suite that brings up Compose is run by exactly one config, and each
 * config has a CI job of its own.
 *
 * @remarks
 * This exists because the opposite already happened. §7's deployment suite was
 * added as `src/walk/security.e2e.test.ts`, the walk's config included
 * `src/walk/** /*.e2e.test.ts`, and so §7's conformance ran inside the walk's
 * job — invisible in the job table, and impossible for the phase preflight to
 * require, because the preflight derives its expected jobs from the workflow
 * files. A suite that cannot be required is a suite that can stop running with
 * nothing to notice: register row 13's own failure mode, reappearing inside the
 * mechanism built to prevent it.
 *
 * Two directions, and both matter. A file matched by NO config never runs and
 * reports nothing, which is the failure above. A file matched by TWO runs twice
 * against two independent compose bring-ups, which is slow, flaky and looks
 * like a working suite.
 *
 * It lives in src/guards rather than beside the suites it guards, and that is
 * not cosmetic: the default vitest config EXCLUDES those directories, so a
 * guard written in one would never have run — the same class of mistake it exists to catch,
 * one directory over. It runs in the ordinary unit-test job because it needs no
 * Docker and must fail on the commit that adds a suite, not on the CI cycle
 * that would have run it.
 */
describe('the suites that run against Compose', () => {
  // The working directory, not a path derived from import.meta.url: vitest
  // serves this file through its /@fs/ prefix, and joining onto that produces a
  // path node:fs cannot open.
  const root = cwd();

  /**
   * Every config that brings up Compose, with what runs it.
   *
   * @remarks
   * The runner was derived by a ternary on the script name until 9.6 added a
   * third suite, at which point "everything that is not the walk is the
   * deployment" would have quietly named the wrong script — a guard mapping a
   * new entry onto an existing job is a guard that passes while the new suite
   * does not run, which is the failure it exists to catch.
   */
  const configs = {
    'vitest.walk.config.ts': { script: 'test:walk', runner: 'walk.sh' },
    'vitest.deployment.config.ts': { script: 'test:deployment', runner: 'deployment.sh' },
    'vitest.offline.config.ts': { script: 'test:offline', runner: 'offline-window.sh' },
  };

  /**
   * Directories whose suites must each be claimed by exactly one config.
   *
   * @remarks
   * `src/offline` joins `src/walk` here rather than being added to it, because
   * the reason they are separate is the stack they bring up: register row 31's
   * suite shortens §5's `T_retire` to seconds, and a walk sharing that stack
   * would have its own tabs retired between steps. Being in its own directory
   * is what stops the walk's `src/walk/**` include from swallowing it — which
   * is exactly how §7's deployment suite came to run inside the walk's job.
   */
  const directories = ['src/walk', 'src/offline'];

  function includesOf(config: string): string[] {
    const source = readFileSync(join(root, config), 'utf8');
    const include = /include:\s*\[([^\]]*)\]/.exec(source)?.[1] ?? '';
    return [...include.matchAll(/'([^']+)'/g)].map((match) => match[1]!);
  }

  it('runs every Compose suite from exactly one config', () => {
    const suites = directories.flatMap((directory) =>
      readdirSync(join(root, directory))
        .filter((name) => name.endsWith('.e2e.test.ts'))
        .map((name) => `${directory}/${name}`));

    // A floor that moves with the directories above, so deleting a suite fails
    // here rather than shrinking the set this iterates over to nothing.
    expect(suites.length).toBeGreaterThanOrEqual(directories.length + 1);

    const patterns = Object.keys(configs).flatMap((config) =>
      includesOf(config).map((pattern) => ({ config, pattern })),
    );

    for (const suite of suites) {
      const matched = patterns.filter(({ pattern }) => matches(pattern, suite));

      expect(
        matched.map(({ config }) => config),
        `${suite} must be run by exactly one config; a suite run by none reports ` +
          'nothing, and one run by two brings the stack up twice',
      ).toHaveLength(1);
    }
  });

  it('gives each config a CI job of its own', () => {
    // The half that makes the above worth anything. Two configs nothing calls
    // are two suites that do not run, and the preflight would be none the wiser
    // because it only knows what the workflows declare.
    const workflow = readFileSync(join(root, '../.github/workflows/ci.yml'), 'utf8');
    const scripts = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8')) as {
      scripts: Record<string, string>;
    };

    const jobs = [...workflow.matchAll(/run:\s*\.\/scripts\/(\S+\.sh)/g)].map(
      (match) => match[1]!,
    );

    for (const [config, { script, runner }] of Object.entries(configs)) {
      expect(scripts.scripts[script], `package.json has no ${script} script`).toContain(config);
      expect(jobs, `${runner} is not run by any job in ci.yml`).toContain(runner);
    }

    // Each config gets its own runner. Two configs sharing one means one of
    // them never runs, and every assertion above would still pass.
    const runners = Object.values(configs).map(({ runner }) => runner);
    expect(new Set(runners).size, 'two configs share a runner, so one does not run')
      .toBe(runners.length);
  });
});

/** Minimal glob match: `**` any depth, `*` within a segment. */
function matches(pattern: string, path: string): boolean {
  const expression = pattern
    .split('**/')
    .map((part) => part.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '[^/]*'))
    .join('(?:.*/)?');

  return new RegExp(`^${expression}$`).test(path);
}
