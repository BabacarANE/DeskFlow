import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { errorInterceptor } from './core/api/error.interceptor';
import { authInterceptor } from './core/auth/auth.interceptor';
import { AuthService } from './core/auth/auth.service';
import { mockInterceptors } from './core/mock/mock-interceptors';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    // Order matters: errors are normalised last (outermost), so the auth interceptor
    // still sees raw 401 responses. The mock backend, when enabled, is the innermost layer.
    provideHttpClient(withInterceptors([errorInterceptor, authInterceptor, ...mockInterceptors])),
    // Restores the session from the refresh cookie before the first navigation.
    provideAppInitializer(() => inject(AuthService).restoreSession()),
  ],
};
