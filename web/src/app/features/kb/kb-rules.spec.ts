import { missingForPublish, parseTags, tagsError } from './kb-rules';

describe('knowledge base rules', () => {
  it('splits, trims and de-duplicates tags', () => {
    expect(parseTags(' vpn, réseau ,, VPN ,accès')).toEqual(['vpn', 'réseau', 'accès']);
    expect(parseTags('')).toEqual([]);
  });

  it('allows 10 tags of 30 characters and refuses more', () => {
    expect(tagsError('a1,a2,a3,a4,a5,a6,a7,a8,a9,a10')).toBeNull();
    expect(tagsError('a1,a2,a3,a4,a5,a6,a7,a8,a9,a10,a11')).toContain('10');
    expect(tagsError('x'.repeat(30))).toBeNull();
    expect(tagsError('x'.repeat(31))).toContain('30');
  });

  it('needs a title, a symptom and a solution to publish, but not a cause', () => {
    expect(missingForPublish({ title: 'T', symptom: 'S', solution: 'R' })).toEqual({
      title: false,
      symptom: false,
      solution: false,
    });
    expect(missingForPublish({ title: 'T', symptom: ' ', solution: '' })).toEqual({
      title: false,
      symptom: true,
      solution: true,
    });
  });
});
