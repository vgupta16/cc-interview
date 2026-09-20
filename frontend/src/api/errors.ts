import type { ApiError } from './generated/api';

export interface DisplayError {
  code: string;
  message: string;
}

/**
 * Turns a non-2xx response body into something displayable. Server rule violations always carry
 * `{ code, message }`; framework-level failures (e.g. an unparseable body) may not, so fall back.
 */
export function toDisplayError(status: number, data: unknown): DisplayError {
  const body = (data ?? {}) as Partial<ApiError>;
  return {
    code: body.code ?? `http_${status}`,
    message: body.message ?? `Request failed (${status}).`,
  };
}

export function networkError(e: unknown): DisplayError {
  return { code: 'network_error', message: e instanceof Error ? e.message : 'Network error.' };
}
