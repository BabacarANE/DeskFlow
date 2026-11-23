// Guards the locked palette (SPEC section 10): hex colours live only in styles/tokens.css.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';

const HEX = /#[0-9a-fA-F]{3,8}\b/;

function files(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });
}

describe('palette', () => {
  it('has no hard-coded hex colour outside tokens.css', () => {
    const offenders = files('src')
      .filter(
        (f) =>
          /\.(ts|html|css)$/.test(f) &&
          !f.endsWith('.spec.ts') &&
          !f.endsWith(join('styles', 'tokens.css')),
      )
      .filter((f) => HEX.test(readFileSync(f, 'utf8')));
    expect(offenders).toEqual([]);
  });
});
