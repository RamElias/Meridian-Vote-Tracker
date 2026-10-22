import { HttpInterceptorFn } from '@angular/common/http';

export const demoUserInterceptor: HttpInterceptorFn = (request, next) => {
  const demoUser = globalThis.localStorage?.getItem('meridian-demo-user') ?? 'lana';
  return next(request.clone({ setHeaders: { 'X-Demo-User': demoUser } }));
};
