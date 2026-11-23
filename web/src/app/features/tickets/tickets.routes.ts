import { Routes } from '@angular/router';

export const TICKETS_ROUTES: Routes = [
  {
    path: '',
    title: 'Tickets · DeskFlow',
    loadComponent: () => import('./tickets-home').then((m) => m.TicketsHome),
  },
];
