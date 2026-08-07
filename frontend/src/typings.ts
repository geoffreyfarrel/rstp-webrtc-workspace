export enum ConnectionStatusEnum {
  DISCONNECTED = 'disconnected',
  CONNECTING = 'connecting',
  CONNECTED = 'connected',
  FAILED = 'failed',
}

export interface Camera {
  id: string;
  name: string;
}

export interface TurnCredentials {
  host: string;
  username: string;
  credential: string;
}
