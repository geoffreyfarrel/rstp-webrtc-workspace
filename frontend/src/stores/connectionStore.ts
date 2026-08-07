import { ConnectionStatusEnum } from '@/typings';
import { defineStore } from 'pinia';

export const useConnectionStore = defineStore('connection', {
  state() {
    return {
      statuses: {} as Record<string, ConnectionStatusEnum>,
    };
  },
  getters: {
    getConnection:
      (state) =>
      (cameraId: string): ConnectionStatusEnum =>
        state.statuses[cameraId] ?? ConnectionStatusEnum.CONNECTING,
  },
  actions: {
    setConnection(cameraId: string, newStatus: ConnectionStatusEnum) {
      this.statuses[cameraId] = newStatus;
    },
  },
});
