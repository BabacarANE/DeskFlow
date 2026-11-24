import { Routes } from '@angular/router';
import { roleGuard } from '../../core/auth/guards';

/** Reading is open to every company role; editing is for agents and tenant admins. */
const editors = roleGuard(['TenantAdmin', 'Agent']);

export const KB_ROUTES: Routes = [
  {
    path: '',
    title: 'Base de connaissances · DeskFlow',
    loadComponent: () => import('./kb-list').then((m) => m.KbList),
  },
  // Declared before ':id' so that "new" is not read as an article id.
  {
    path: 'new',
    canActivate: [editors],
    title: 'Nouvel article · DeskFlow',
    loadComponent: () => import('./kb-edit').then((m) => m.KbEdit),
  },
  {
    path: ':id/edit',
    canActivate: [editors],
    title: 'Modifier l’article · DeskFlow',
    loadComponent: () => import('./kb-edit').then((m) => m.KbEdit),
  },
  {
    path: ':id',
    title: 'Article · DeskFlow',
    loadComponent: () => import('./kb-article').then((m) => m.KbArticle),
  },
];
