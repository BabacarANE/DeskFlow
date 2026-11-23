import { InjectionToken } from '@angular/core';
import { createHub } from './hub-factory';

/** The part of a SignalR HubConnection the application relies on (a fake implements it in tests). */
export interface HubLike {
  start(): Promise<void>;
  stop(): Promise<void>;
  on(method: string, handler: (payload: unknown) => void): void;
  onclose(handler: (error?: Error) => void): void;
}

/** Builds a connection; `accessToken` is read at every connection attempt. */
export type HubFactory = (accessToken: () => string) => HubLike;

export const HUB_FACTORY = new InjectionToken<HubFactory>('HUB_FACTORY', {
  providedIn: 'root',
  factory: () => createHub,
});
