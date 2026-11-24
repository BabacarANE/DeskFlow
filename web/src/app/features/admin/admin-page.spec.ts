import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { AuthService } from '../../core/auth/auth.service';
import { AdminPage } from './admin-page';

describe('AdminPage tabs', () => {
  const emptyPage = { items: [], total: 0, page: 1, pageSize: 20 };
  const api = {
    listUsers: () => of(emptyPage),
    listInvitations: () => of(emptyPage),
    listCategories: () => of([]),
    listSlaPolicies: () => of([]),
    getTheme: () =>
      of({ colorPrimary: '#7CC4EE', colorSurface: '#F4F7FA', colorAccent: '#D63A14' }),
  };
  let fixture: ComponentFixture<AdminPage>;
  const el = () => fixture.nativeElement as HTMLElement;
  const tabs = () => Array.from(el().querySelectorAll<HTMLElement>('[role="tab"]'));
  const selected = () => tabs().find((t) => t.getAttribute('aria-selected') === 'true');

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        { provide: ApiClient, useValue: api },
        { provide: AuthService, useValue: { user: () => ({ id: 'me' }) } },
      ],
    });
    fixture = TestBed.createComponent(AdminPage);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  it('has the five tabs, users first, in an accessible tablist', () => {
    expect(el().querySelector('[role="tablist"]')?.getAttribute('aria-label')).toBeTruthy();
    expect(tabs().map((t) => t.textContent?.trim())).toEqual([
      'Utilisateurs',
      'Invitations',
      'Catégories',
      'SLA',
      'Thème',
    ]);
    expect(selected()?.textContent).toContain('Utilisateurs');
    expect(el().querySelector('app-users-list')).not.toBeNull();
  });

  it('links each tab to its panel and keeps only the selected tab in the tab order', () => {
    const panel = el().querySelector('[role="tabpanel"]:not([hidden])') as HTMLElement;
    expect(panel.getAttribute('aria-labelledby')).toBe(selected()?.id);
    expect(selected()?.getAttribute('aria-controls')).toBe(panel.id);
    expect(tabs().filter((t) => t.getAttribute('tabindex') === '0')).toHaveLength(1);
  });

  it('shows the panel of a clicked tab', async () => {
    tabs()[2].click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(selected()?.textContent).toContain('Catégories');
    expect(el().querySelector('app-categories-admin')).not.toBeNull();
    // The first panel is only hidden, not destroyed.
    expect((el().querySelector('#admin-panel-users') as HTMLElement).hidden).toBe(true);
  });

  it('keeps an unsaved edit when the user changes tab and comes back', async () => {
    const go = async (index: number) => {
      tabs()[index].click();
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    await go(2);
    const input = el().querySelector('#category-new') as HTMLInputElement;
    input.value = 'Brouillon de saisie';
    input.dispatchEvent(new Event('input'));
    await go(0);
    await go(2);
    expect((el().querySelector('#category-new') as HTMLInputElement).value).toBe(
      'Brouillon de saisie',
    );
  });

  it('moves with the arrow keys, Home and End, wrapping around', () => {
    const press = (key: string) => {
      selected()?.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
      fixture.detectChanges();
    };
    press('ArrowRight');
    expect(selected()?.textContent).toContain('Invitations');
    press('ArrowLeft');
    press('ArrowLeft');
    expect(selected()?.textContent).toContain('Thème');
    expect(document.activeElement).toBe(selected());
    press('ArrowRight');
    expect(selected()?.textContent).toContain('Utilisateurs');
    press('End');
    expect(selected()?.textContent).toContain('Thème');
    press('Home');
    expect(selected()?.textContent).toContain('Utilisateurs');
  });

  it('shows the SLA and theme areas', async () => {
    tabs()[3].click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el().querySelector('app-sla-settings')).not.toBeNull();
    tabs()[4].click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el().textContent).toContain(
      'Choisissez deux couleurs discrètes et une couleur qui claque.',
    );
  });
});
