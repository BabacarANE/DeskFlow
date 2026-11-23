import { HttpInterceptorFn } from '@angular/common/http';
import { mockBackendInterceptor } from './mock-backend.interceptor';

export const mockInterceptors: HttpInterceptorFn[] = [mockBackendInterceptor];
