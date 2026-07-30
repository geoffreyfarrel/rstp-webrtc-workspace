import { ConnectionStatusEnum } from '@/typings';
import { defineStore } from 'pinia';

export const useConnectionStore = defineStore('connection', {
  state() {
    return {
      connectionStatus: ConnectionStatusEnum.CONNECTING,
    };
  },
  actions: {
    setConnection(newStatus: ConnectionStatusEnum) {
      this.connectionStatus = newStatus;
    },
  },
});
