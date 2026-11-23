import { parseEvent } from './realtime-events';

const valid = {
  ticketId: '40000000-0000-4000-8000-000000000001',
  number: 12,
  title: 'VPN en panne',
  at: '2026-11-23T10:00:00Z',
};

describe('parseEvent', () => {
  it('accepts payloads that follow the contract', () => {
    expect(parseEvent('ticketAssigned', valid)?.kind).toBe('ticketAssigned');
    expect(parseEvent('ticketCreated', { ...valid, priority: 'Urgent' })?.kind).toBe(
      'ticketCreated',
    );
  });

  it.each([
    ['ticketId is not a UUID', { ticketId: 'abc' }],
    ['number is a float', { number: 1.5 }],
    ['number is not finite', { number: Infinity }],
    ['number is not positive', { number: 0 }],
    ['number is a string', { number: '12' }],
    ['at is not a date', { at: 'yesterday' }],
    ['title is too long', { title: 'x'.repeat(301) }],
    ['title is missing', { title: undefined }],
  ])('drops a payload when %s', (_label, patch) => {
    expect(parseEvent('ticketAssigned', { ...valid, ...patch })).toBeNull();
  });

  it('requires a known priority on ticketCreated', () => {
    expect(parseEvent('ticketCreated', valid)).toBeNull();
    expect(parseEvent('ticketCreated', { ...valid, priority: 'Critical' })).toBeNull();
  });

  it('drops non-objects', () => {
    expect(parseEvent('ticketReplied', null)).toBeNull();
    expect(parseEvent('ticketReplied', 'x')).toBeNull();
  });
});
