import { execFileSync } from 'node:child_process';
import { cpus, totalmem } from 'node:os';
import { readFileSync } from 'node:fs';

/**
 * What §8 requires alongside every performance number.
 *
 * @remarks
 * "Every performance number is reported with the build that produced it, and a
 * number without one is not a result." A figure missing the configuration, the
 * runner class and the commit cannot be compared to a later figure, which makes
 * it unfalsifiable — it can never be shown to have regressed, only replaced.
 */
export interface Provenance {
  readonly configuration: string;
  readonly commit: string;
  readonly clean: boolean;
  readonly node: string;
  readonly cpu: string;
  readonly cores: number;
  readonly memoryGiB: number;
}

function git(...args: string[]): string {
  try {
    return execFileSync('git', args, { encoding: 'utf8' }).trim();
  } catch {
    return 'unknown';
  }
}

export function provenance(configuration: string): Provenance {
  return {
    configuration,
    commit: git('rev-parse', '--short', 'HEAD'),
    clean: git('status', '--porcelain').length === 0,
    node: process.version,
    cpu: cpus()[0]?.model ?? 'unknown',
    cores: cpus().length,
    memoryGiB: Math.round((totalmem() / 1024 ** 3) * 10) / 10,
  };
}

export function describe(where: Provenance): string {
  return `${where.configuration} · ${where.commit}${where.clean ? '' : '+dirty'} · `
    + `node ${where.node} · ${where.cpu} × ${where.cores} · ${where.memoryGiB} GiB`;
}

/**
 * Resident set size of one process, in mebibytes.
 *
 * @remarks
 * Read from `/proc/<pid>/status` rather than from `ps`, which rounds and which
 * would put a process launch inside every sample. `VmRSS` is what §8 means by
 * RSS: pages actually resident, not the address space `VmSize` reports — a
 * .NET process reserves tens of gigabytes of address space it never touches,
 * and reporting that as memory use would fail the target on every run for a
 * reason that is not about memory.
 */
export function residentMiB(pid: number): number {
  const status = readFileSync(`/proc/${pid}/status`, 'utf8');
  const line = status.split('\n').find((l) => l.startsWith('VmRSS:'));
  if (line === undefined) {
    throw new Error(`no VmRSS for pid ${pid}`);
  }

  // VmRSS is reported in kibibytes.
  return Number(line.split(/\s+/)[1]) / 1024;
}

/** Percentiles that carry their sample count, because §8 requires it. */
export function percentiles(samples: readonly number[]): string {
  if (samples.length === 0) {
    return 'n=0';
  }

  const sorted = [...samples].sort((a, b) => a - b);
  const at = (q: number) =>
    sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil(q * sorted.length) - 1))]!;

  return `n=${sorted.length} min=${sorted[0]!.toFixed(2)} p50=${at(0.5).toFixed(2)} `
    + `p95=${at(0.95).toFixed(2)} p99=${at(0.99).toFixed(2)} max=${sorted[sorted.length - 1]!.toFixed(2)}`;
}
