export type HoldStatus = 'Active' | 'Released' | 'Expired';

export interface InventoryItem {
  sku: string;
  name: string;
  availableQuantity: number;
  totalQuantity: number;
  heldQuantity: number;
}

export interface HoldItem {
  sku: string;
  name: string;
  quantity: number;
}

export interface Hold {
  holdId: string;
  customerId: string;
  status: HoldStatus;
  items: HoldItem[];
  createdAt: string;
  expiresAt: string;
  resolvedAt: string | null;
  secondsRemaining: number;
}

export interface CreateHoldRequest {
  customerId: string;
  items: Array<{ sku: string; quantity: number }>;
}

/** RFC 9457 problem document, plus the extensions this API adds. */
export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
  sku?: string;
  requested?: number;
  available?: number;
}

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail ?? problem.title ?? `Request failed with status ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }
}

const BASE_URL: string = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080';

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });

  if (!response.ok) {
    // The API always answers errors with ProblemDetails, but never assume the body parses:
    // a proxy or a network fault can return something else entirely.
    let problem: ProblemDetails = {};
    try {
      problem = (await response.json()) as ProblemDetails;
    } catch {
      problem = { title: response.statusText };
    }
    throw new ApiError(response.status, problem);
  }

  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const api = {
  getInventory: () => request<InventoryItem[]>('/api/inventory'),

  getHolds: () => request<Hold[]>('/api/holds'),

  createHold: (body: CreateHoldRequest) =>
    request<Hold>('/api/holds', { method: 'POST', body: JSON.stringify(body) }),

  releaseHold: (holdId: string) =>
    request<Hold>(`/api/holds/${holdId}`, { method: 'DELETE' }),
};
