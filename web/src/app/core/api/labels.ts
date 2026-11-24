import {
  ArticleStatus,
  AssignableRole,
  DemoRole,
  InvitationStatus,
  Role,
  TicketEventType,
  TicketPriority,
  TicketSort,
  TicketStatus,
} from './models';

export const ROLE_LABELS: Record<Role, string> = {
  SuperAdmin: 'Super-administrateur',
  TenantAdmin: 'Administrateur',
  Agent: 'Agent',
  Requester: 'Demandeur',
};

export const ASSIGNABLE_ROLES: AssignableRole[] = ['TenantAdmin', 'Agent', 'Requester'];

export const DEMO_ROLES: { value: DemoRole; label: string; hint: string }[] = [
  { value: 'admin', label: 'Administrateur', hint: 'Gère les utilisateurs et les invitations' },
  { value: 'agent', label: 'Agent', hint: 'Traite les tickets de l’entreprise' },
  { value: 'demandeur', label: 'Demandeur', hint: 'Crée et suit ses propres tickets' },
];

export const INVITATION_STATUS_LABELS: Record<InvitationStatus, string> = {
  Pending: 'En attente',
  Accepted: 'Acceptée',
  Expired: 'Expirée',
};

export const STATUSES: TicketStatus[] = ['New', 'Open', 'Pending', 'Resolved', 'Closed'];

export const STATUS_LABELS: Record<TicketStatus, string> = {
  New: 'Nouveau',
  Open: 'Ouvert',
  Pending: 'En attente du demandeur',
  Resolved: 'Résolu',
  Closed: 'Fermé',
};

export const PRIORITIES: TicketPriority[] = ['Low', 'Normal', 'High', 'Urgent'];

export const PRIORITY_LABELS: Record<TicketPriority, string> = {
  Low: 'Basse',
  Normal: 'Normale',
  High: 'Haute',
  Urgent: 'Urgente',
};

export const SORT_LABELS: Record<TicketSort, string> = {
  createdAt: 'Date de création',
  updatedAt: 'Dernière mise à jour',
  priority: 'Priorité',
  number: 'Numéro',
};

export const EVENT_LABELS: Record<TicketEventType, string> = {
  Created: 'Ticket créé',
  StatusChanged: 'Statut modifié',
  PriorityChanged: 'Priorité modifiée',
  AssigneeChanged: 'Assignation modifiée',
  CategoryChanged: 'Catégorie modifiée',
};

export const ARTICLE_STATUS_LABELS: Record<ArticleStatus, string> = {
  Draft: 'Brouillon',
  Published: 'Publié',
};
