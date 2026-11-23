import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { ApiError } from '../../core/api/api-error';
import { detail } from '../../testing/ticket-fixtures';
import { TicketNew } from './ticket-new';

describe('TicketNew', () => {
  const api = { createTicket: vi.fn(), listCategories: vi.fn() };
  let fixture: ComponentFixture<TicketNew>;
  let navigate: ReturnType<typeof vi.spyOn>;
  const el = () => fixture.nativeElement as HTMLElement;
  const field = (id: string) => el().querySelector(`#${id}`) as HTMLInputElement;

  function type(id: string, value: string) {
    const control = field(id);
    control.value = value;
    control.dispatchEvent(new Event(control.tagName === 'SELECT' ? 'change' : 'input'));
  }

  async function submit() {
    el().querySelector('form')?.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    fixture.detectChanges();
  }

  beforeEach(async () => {
    api.createTicket.mockReset();
    api.listCategories.mockReset();
    api.listCategories.mockReturnValue(of([{ id: 'c1', name: 'Matériel' }]));
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: ApiClient, useValue: api }],
    });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(TicketNew);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  it('shows the four fields with labels and one accent button', () => {
    for (const id of ['ticket-title', 'ticket-description', 'ticket-category', 'ticket-priority']) {
      expect(el().querySelector(`label[for="${id}"]`)).not.toBeNull();
    }
    expect(el().querySelectorAll('.btn-accent')).toHaveLength(1);
    expect(
      Array.from(field('ticket-category').querySelectorAll('option')).map((o) =>
        o.textContent?.trim(),
      ),
    ).toEqual(['Aucune catégorie', 'Matériel']);
    expect(field('ticket-priority').value).toBe('Normal');
  });

  it('does not call the API while required fields are empty', async () => {
    await submit();
    expect(api.createTicket).not.toHaveBeenCalled();
    expect(el().textContent).toContain('Ce champ est obligatoire.');
  });

  it('creates the ticket and opens it', async () => {
    api.createTicket.mockReturnValue(of(detail('new1')));
    type('ticket-title', ' Écran noir ');
    type('ticket-description', 'Plus d’image depuis ce matin.');
    type('ticket-category', 'c1');
    type('ticket-priority', 'High');
    await submit();
    expect(api.createTicket).toHaveBeenCalledWith({
      title: 'Écran noir',
      description: 'Plus d’image depuis ce matin.',
      categoryId: 'c1',
      priority: 'High',
    });
    expect(navigate).toHaveBeenCalledWith(['/tickets', 'new1']);
  });

  it('sends no category when none is chosen', async () => {
    api.createTicket.mockReturnValue(of(detail('new1')));
    type('ticket-title', 'Titre');
    type('ticket-description', 'Description');
    await submit();
    expect(api.createTicket.mock.calls[0][0].categoryId).toBeNull();
  });

  it('shows 422 errors under the matching field', async () => {
    api.createTicket.mockReturnValue(
      throwError(
        () =>
          new ApiError(422, 'Certaines informations sont invalides.', null, {
            title: ['Le titre est trop court.'],
            categoryId: ['Catégorie inconnue.'],
          }),
      ),
    );
    type('ticket-title', 'a');
    type('ticket-description', 'b');
    await submit();
    const text = el().textContent ?? '';
    expect(text).toContain('Le titre est trop court.');
    expect(text).toContain('Catégorie inconnue.');
    expect(navigate).not.toHaveBeenCalled();
    // The error is attached to the field, so it sits right after the title input.
    expect(field('ticket-title').parentElement?.textContent).toContain('Le titre est trop court.');
  });

  it('shows a general alert for other failures', async () => {
    api.createTicket.mockReturnValue(
      throwError(() => new ApiError(500, 'Le serveur a rencontré une erreur.', null)),
    );
    type('ticket-title', 'a');
    type('ticket-description', 'b');
    await submit();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain('serveur');
  });
});
