import { AssignableRole, DemoRole, InvitationStatus, Role } from './models';

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
