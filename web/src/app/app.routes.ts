import { Routes } from '@angular/router';
import { guestGuard, homeGuard, roleGuard } from './core/auth/guards';

export const routes: Routes = [
  { path: '', pathMatch: 'full', canActivate: [homeGuard], children: [] },
  {
    path: 'login',
    canActivate: [guestGuard],
    title: 'Connexion · DeskFlow',
    loadComponent: () => import('./features/auth/login').then((m) => m.Login),
  },
  {
    path: 'auth/callback',
    title: 'Connexion en cours · DeskFlow',
    loadComponent: () => import('./features/auth/callback').then((m) => m.Callback),
  },
  {
    path: 'forbidden',
    title: 'Accès refusé · DeskFlow',
    loadComponent: () => import('./features/errors/forbidden').then((m) => m.Forbidden),
  },
  {
    path: 'tickets',
    canActivate: [roleGuard(['TenantAdmin', 'Agent', 'Requester'])],
    loadChildren: () => import('./features/tickets/tickets.routes').then((m) => m.TICKETS_ROUTES),
  },
  {
    path: 'kb',
    // Every role that belongs to a company (not the SuperAdmin); editing is restricted in kb.routes.
    canActivate: [roleGuard(['TenantAdmin', 'Agent', 'Requester'])],
    loadChildren: () => import('./features/kb/kb.routes').then((m) => m.KB_ROUTES),
  },
  {
    path: 'admin',
    canActivate: [roleGuard(['TenantAdmin'])],
    loadChildren: () => import('./features/admin/admin.routes').then((m) => m.ADMIN_ROUTES),
  },
  {
    path: 'platform/tenants',
    canActivate: [roleGuard(['SuperAdmin'])],
    loadChildren: () =>
      import('./features/platform/platform.routes').then((m) => m.PLATFORM_ROUTES),
  },
  {
    path: '**',
    title: 'Page introuvable · DeskFlow',
    loadComponent: () => import('./features/errors/not-found').then((m) => m.NotFound),
  },
];
