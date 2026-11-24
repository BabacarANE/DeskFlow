import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { SlaPolicy } from '../../core/api/models';
import { SlaSettings } from './sla-settings';

const POLICIES: SlaPolicy[] = [
  { priority: 'Low', firstResponseMinutes: 1440, resolutionMinutes: 4320 },
  { priority: 'Normal', firstResponseMinutes: 480, resolutionMinutes: 1440 },
  { priority: 'High', firstResponseMinutes: 120, resolutionMinutes: 480 },
  { priority: 'Urgent', firstResponseMinutes: 30, resolutionMinutes: 240 },
];

describe('SlaSettings', () => {
  const api = { listSlaPolicies: vi.fn(), updateSlaPolicies: vi.fn() };
  let fixture: ComponentFixture<SlaSettings>;
  const el = () => fixture.nativeElement as HTMLElement;
  const input = (kind: 'first' | 'resolution', row: number) =>
    el().querySelector(`#sla-${kind}-${row}`) as HTMLInputElement;
  const save = () =>
    Array.from(el().querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Enregistrer'),
    ) as HTMLButtonElement;

  function type(kind: 'first' | 'resolution', row: number, value: string) {
    input(kind, row).value = value;
    input(kind, row).dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  async function settle() {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }

  async function render() {
    fixture = TestBed.createComponent(SlaSettings);
    fixture.detectChanges();
    await settle();
  }

  beforeEach(() => {
    Object.values(api).forEach((fn) => fn.mockReset());
    api.listSlaPolicies.mockReturnValue(of(POLICIES));
    TestBed.configureTestingModule({ providers: [{ provide: ApiClient, useValue: api }] });
  });

  it('shows four labelled rows, in the API order, with readable durations', async () => {
    await render();
    const heads = Array.from(el().querySelectorAll('tbody th')).map((th) => th.textContent?.trim());
    expect(heads).toEqual(['Basse', 'Normale', 'Haute', 'Urgente']);
    expect(input('first', 3).value).toBe('30');
    expect(input('resolution', 3).value).toBe('240');
    expect(el().querySelector('#sla-resolution-3-hint')?.textContent).toContain('= 4 h');
    expect(el().querySelector('#sla-first-0-hint')?.textContent).toContain('= 1 j');
    expect(el().querySelector('label[for="sla-first-3"]')?.textContent).toContain('Urgente');
    expect(input('first', 3).getAttribute('aria-describedby')).toContain('sla-first-3-hint');
  });

  it('reminds that new SLAs only apply to new tickets', async () => {
    await render();
    expect(el().textContent).toContain('qu’aux nouveaux tickets');
  });

  it('updates the readable duration while typing', async () => {
    await render();
    type('first', 1, '90');
    expect(el().querySelector('#sla-first-1-hint')?.textContent).toContain('= 1 h 30 min');
  });

  it('refuses a resolution shorter than the first response and sends nothing', async () => {
    await render();
    type('first', 2, '600');
    save().click();
    await settle();
    expect(api.updateSlaPolicies).not.toHaveBeenCalled();
    expect(input('resolution', 2).parentElement?.textContent).toContain(
      'plus courte que la première réponse',
    );
    expect(input('resolution', 2).getAttribute('aria-describedby')).toContain(
      'sla-resolution-2-error',
    );
    // Fixing the first response clears the error on the other cell.
    type('first', 2, '100');
    expect(input('resolution', 2).parentElement?.textContent).not.toContain('plus courte');
  });

  it('refuses empty, zero, fractional and out-of-range values', async () => {
    await render();
    type('first', 0, '');
    type('first', 1, '0');
    type('first', 2, '1.5');
    type('resolution', 3, '50000');
    save().click();
    await settle();
    const text = el().textContent ?? '';
    expect(text).toContain('Saisissez un nombre de minutes.');
    expect(text).toContain('La valeur minimale est 1 minute.');
    expect(text).toContain('nombre entier');
    expect(text).toContain('La valeur maximale est 43200');
    expect(api.updateSlaPolicies).not.toHaveBeenCalled();
  });

  it('sends the four rows in order and confirms', async () => {
    api.updateSlaPolicies.mockReturnValue(
      of(POLICIES.map((p) => (p.priority === 'Urgent' ? { ...p, firstResponseMinutes: 15 } : p))),
    );
    await render();
    type('first', 3, '15');
    save().click();
    await settle();
    expect(api.updateSlaPolicies).toHaveBeenCalledWith({
      policies: [
        POLICIES[0],
        POLICIES[1],
        POLICIES[2],
        { priority: 'Urgent', firstResponseMinutes: 15, resolutionMinutes: 240 },
      ],
    });
    expect(el().querySelector('[role="status"]')?.textContent).toContain('Délais enregistrés');
    expect(input('first', 3).value).toBe('15');
  });

  it('puts a server 422 on the matching cell', async () => {
    api.updateSlaPolicies.mockReturnValue(
      throwError(
        () =>
          new ApiError(422, 'Certaines informations sont invalides.', null, {
            'policies[1].resolutionMinutes': ['Refusé par le serveur.'],
          }),
      ),
    );
    await render();
    save().click();
    await settle();
    expect(input('resolution', 1).parentElement?.textContent).toContain('Refusé par le serveur.');
    expect(el().querySelector('[role="status"]')).toBeNull();
  });

  it('shows another failure in an alert', async () => {
    api.updateSlaPolicies.mockReturnValue(
      throwError(() => new ApiError(500, 'Le serveur a rencontré une erreur.', null)),
    );
    await render();
    save().click();
    await settle();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain('serveur');
  });

  it('shows an error state with a retry button when loading fails', async () => {
    api.listSlaPolicies.mockReturnValueOnce(
      throwError(
        () => new ApiError(403, 'Vous n’avez pas le droit d’effectuer cette action.', null),
      ),
    );
    await render();
    expect(el().querySelector('[role="alert"]')).not.toBeNull();
    (el().querySelector('[role="alert"] button') as HTMLButtonElement).click();
    await settle();
    expect(input('first', 0)).not.toBeNull();
  });

  it('drops the saved confirmation as soon as a field is edited', async () => {
    api.updateSlaPolicies.mockReturnValue(of(POLICIES));
    await render();
    save().click();
    await settle();
    expect(el().textContent).toContain('Délais enregistrés');
    type('first', 0, '1000');
    expect(el().textContent).not.toContain('Délais enregistrés');
  });
});
