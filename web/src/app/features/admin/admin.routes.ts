import { Routes } from '@angular/router';

export const ADMIN_ROUTES: Routes = [
  {
    path: '',
    title: 'Administration · DeskFlow',
    loadComponent: () => import('./admin-page').then((m) => m.AdminPage),
  },
];
