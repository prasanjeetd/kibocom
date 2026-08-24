import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api, type CreateHoldRequest, type LogQuery } from './api';

export const queryKeys = {
  inventory: ['inventory'] as const,
  holds: ['holds'] as const,
};

/**
 * Inventory also changes without any action from this browser - the expiry sweeper restores
 * stock on its own schedule - so it polls as well as being invalidated after mutations.
 */
export function useInventory() {
  return useQuery({
    queryKey: queryKeys.inventory,
    queryFn: api.getInventory,
    refetchInterval: 5000,
  });
}

export function useHolds() {
  return useQuery({
    queryKey: queryKeys.holds,
    queryFn: api.getHolds,
    refetchInterval: 2000,
  });
}

/** The log feed is append-only, so polling is the whole synchronisation story. */
export function useLogs(query: LogQuery, autoRefresh: boolean) {
  return useQuery({
    queryKey: ['logs', query],
    queryFn: () => api.getLogs(query),
    refetchInterval: autoRefresh ? 3000 : false,
  });
}

function useSyncedMutation<TArgs>(mutationFn: (args: TArgs) => Promise<unknown>) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn,
    // Invalidate on settle, never optimistically. This domain is about contention: the server
    // may legitimately answer 409, and a decremented number that gets retracted is worse than
    // a number that arrives a moment later.
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.inventory });
      void queryClient.invalidateQueries({ queryKey: queryKeys.holds });
    },
  });
}

export const useCreateHold = () => useSyncedMutation((body: CreateHoldRequest) => api.createHold(body));

export const useReleaseHold = () => useSyncedMutation((holdId: string) => api.releaseHold(holdId));

/** Ticking clock so countdowns are computed from expiresAt rather than a stale snapshot. */
export function useNow(intervalMs = 1000): number {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(id);
  }, [intervalMs]);

  return now;
}

export function formatCountdown(expiresAt: string, now: number): string {
  const remaining = Math.max(0, new Date(expiresAt).getTime() - now);
  const totalSeconds = Math.floor(remaining / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}
