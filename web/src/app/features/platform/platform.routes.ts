import { Routes } from '@angular/router';

export const PLATFORM_ROUTES: Routes = [
  {
    path: '',
    title: 'Entreprises · DeskFlow',
    loadComponent: () => import('./tenants-page').then((m) => m.TenantsPage),
  },
];
