import { HttpErrorResponse } from '@angular/common/http';

export function apiErrorMessage(error: unknown): string {
  if (typeof error === 'string') return error;
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { detail?: string; title?: string; correlationId?: string } | null;
    const message = body?.detail ?? body?.title ?? error.message;
    const reference = body?.correlationId ?? error.headers.get('X-Correlation-ID');
    return message + (reference ? ` (Reference: ${reference})` : '');
  }
  return 'The request could not be completed. Check the API connection and try again.';
}
