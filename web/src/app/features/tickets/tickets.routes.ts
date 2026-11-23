import { Routes } from '@angular/router';

export const TICKETS_ROUTES: Routes = [
  {
    path: '',
    title: 'Tickets · DeskFlow',
    loadComponent: () => import('./ticket-list').then((m) => m.TicketList),
  },
  // Declared before ':id' so that "new" is not read as a ticket id.
  {
    path: 'new',
    title: 'Nouveau ticket · DeskFlow',
    loadComponent: () => import('./ticket-new').then((m) => m.TicketNew),
  },
  {
    path: ':id',
    title: 'Ticket · DeskFlow',
    loadComponent: () => import('./ticket-detail').then((m) => m.TicketDetail),
  },
];
