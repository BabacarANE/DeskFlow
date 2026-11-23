// Fails when the production bundle contains traces of the simulated backend.
// Usage: npm run build && npm run check:no-mock
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const dir = join('dist', 'web', 'browser');
const markers = ['deskflow.mock', 'mock-access-token', 'mock.local'];
const offenders = readdirSync(dir)
  .filter((f) => f.endsWith('.js'))
  .filter((f) => markers.some((m) => readFileSync(join(dir, f), 'utf8').includes(m)));

if (offenders.length > 0) {
  console.error(`Mock backend found in the bundle: ${offenders.join(', ')}`);
  process.exit(1);
}
console.log('OK: no mock code in the production bundle.');
