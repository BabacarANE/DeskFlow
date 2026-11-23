import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { HubFactory } from './hub';
import { HUB_URL } from './realtime-events';

/**
 * Real SignalR connection. Reconnection is driven by RealtimeService (backoff and token
 * renewal), so the built-in automatic reconnect is not used. In the `mock` build this file is
 * replaced by hub-factory.mock.ts.
 */
export const createHub: HubFactory = (accessToken) =>
  new HubConnectionBuilder()
    .withUrl(HUB_URL, { accessTokenFactory: accessToken })
    .configureLogging(LogLevel.Warning)
    .build();
