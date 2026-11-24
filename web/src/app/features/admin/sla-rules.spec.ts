import {
  formatMinutes,
  mapSlaServerErrors,
  minutesError,
  resolutionError,
  SLA_PRIORITIES,
} from './sla-rules';

describe('SLA rules', () => {
  it('writes durations in a readable way', () => {
    expect(formatMinutes(30)).toBe('30 min');
    expect(formatMinutes(240)).toBe('4 h');
    expect(formatMinutes(90)).toBe('1 h 30 min');
    expect(formatMinutes(1440)).toBe('1 j');
    expect(formatMinutes(4320)).toBe('3 j');
    expect(formatMinutes(1500)).toBe('1 j 1 h');
    expect(formatMinutes(null)).toBe('');
  });

  it('accepts whole minutes from 1 to 43 200 only', () => {
    expect(minutesError(1)).toBeNull();
    expect(minutesError(43200)).toBeNull();
    expect(minutesError(0)).not.toBeNull();
    expect(minutesError(43201)).not.toBeNull();
    expect(minutesError(1.5)).toContain('entier');
    expect(minutesError(null)).not.toBeNull();
  });

  it('requires the resolution to be at least the first response', () => {
    expect(resolutionError(30, 30)).toBeNull();
    expect(resolutionError(30, 240)).toBeNull();
    expect(resolutionError(240, 30)).toContain('plus courte');
    expect(resolutionError(null, 30)).toBeNull();
  });

  it('keeps the API row order', () => {
    expect(SLA_PRIORITIES).toEqual(['Low', 'Normal', 'High', 'Urgent']);
  });

  it('maps 422 keys to table cells and the rest to general messages', () => {
    const result = mapSlaServerErrors({
      'policies[2].resolutionMinutes': ['Trop court.'],
      'Policies.0.FirstResponseMinutes': ['Invalide.'],
      policies: ['Quatre lignes attendues.'],
    });
    expect(result.fields.get('2:resolution')).toBe('Trop court.');
    expect(result.fields.get('0:first')).toBe('Invalide.');
    expect(result.general).toEqual(['Quatre lignes attendues.']);
  });
});
