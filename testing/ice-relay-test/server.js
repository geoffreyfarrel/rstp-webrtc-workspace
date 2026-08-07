// Dumb signaling relay for the ICE isolation test. Serves index.html and
// pairs up exactly two WebSocket clients, forwarding every message between
// them verbatim - it never inspects SDP/ICE content, so it can't be the
// source of a bug. All the actual WebRTC logic lives in the browser tabs.

const http = require('http');
const fs = require('fs');
const path = require('path');
const { WebSocketServer } = require('ws');

const PORT = process.env.PORT || 8090;
const INDEX_PATH = path.join(__dirname, 'index.html');

const server = http.createServer((req, res) => {
  fs.readFile(INDEX_PATH, (err, data) => {
    if (err) {
      res.writeHead(500);
      res.end('Failed to load index.html');
      return;
    }
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    res.end(data);
  });
});

const wss = new WebSocketServer({ server });

// At most 2 peers at a time - this is a one-shot isolation test, not a
// general signaling server.
let sockets = [];

wss.on('connection', (ws) => {
  if (sockets.length >= 2) {
    ws.send(JSON.stringify({ type: 'error', message: 'Room full (2 tabs already connected). Reload the extra tab later.' }));
    ws.close();
    return;
  }

  sockets.push(ws);
  const role = sockets.length === 1 ? 'offerer' : 'answerer';
  ws.send(JSON.stringify({ type: 'ready', role }));

  if (sockets.length === 2) {
    // Let the offerer know its peer has joined so it can start negotiating.
    sockets[0].send(JSON.stringify({ type: 'peer-joined' }));
  }

  ws.on('message', (data) => {
    const other = sockets.find((s) => s !== ws);
    if (other && other.readyState === other.OPEN) {
      other.send(data.toString());
    }
  });

  ws.on('close', () => {
    sockets = sockets.filter((s) => s !== ws);
    const other = sockets[0];
    if (other && other.readyState === other.OPEN) {
      other.send(JSON.stringify({ type: 'peer-left' }));
    }
  });
});

server.listen(PORT, () => {
  console.log(`ICE relay test server on http://localhost:${PORT}`);
  console.log('Open this URL in two separate tabs (ideally on two different networks).');
});
