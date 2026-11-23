import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, Validators } from '@angular/forms';
import { FieldError } from './field-error';

@Component({
  imports: [FieldError],
  template: `<app-field-error [control]="control" />`,
})
class Host {
  readonly control = new FormControl('', { nonNullable: true, validators: Validators.required });
}

describe('FieldError', () => {
  it('stays hidden until the control is touched, then follows its state without a host re-render', async () => {
    const fixture = TestBed.createComponent(Host);
    const el = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    expect(el.textContent).not.toContain('obligatoire');

    fixture.componentInstance.control.markAsTouched();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el.textContent).toContain('Ce champ est obligatoire.');

    fixture.componentInstance.control.setValue('ok');
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el.textContent).not.toContain('obligatoire');
  });

  it('shows a server message set with setErrors', async () => {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    fixture.componentInstance.control.setErrors({ server: { message: 'Titre déjà utilisé.' } });
    fixture.componentInstance.control.markAsTouched();
    await fixture.whenStable();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Titre déjà utilisé.');
  });
});
