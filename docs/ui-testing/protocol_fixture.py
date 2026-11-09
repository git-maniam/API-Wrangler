"""Disposable, loopback-only protocol fixture for manual UI testing; not a production server."""
import base64
import hashlib
import json
import struct
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def do_POST(self):
        if self.path != '/graphql':
            self.send_error(404)
            return
        try:
            request = json.loads(self.rfile.read(int(self.headers.get('Content-Length', '0'))))
            query = request['query']
            if not isinstance(query, str) or 'health' not in query:
                raise ValueError('expected health query')
            payload = {'data': {'health': 'ready'}}
        except (ValueError, KeyError, TypeError):
            payload = {'errors': [{'message': 'Invalid fixture query'}]}
        encoded = json.dumps(payload).encode('utf-8')
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def do_GET(self):
        if self.path == '/events':
            self.send_response(200)
            self.send_header('Content-Type', 'text/event-stream')
            self.send_header('Cache-Control', 'no-cache')
            self.send_header('Connection', 'close')
            self.end_headers()
            self.wfile.write(b'id: 1\nevent: message\ndata: fixture-ready\n\n')
            self.wfile.flush()
            self.close_connection = True
        elif self.path == '/ws' and self.headers.get('Upgrade', '').lower() == 'websocket':
            key = self.headers.get('Sec-WebSocket-Key', '')
            accept = base64.b64encode(hashlib.sha1((key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode('ascii')).digest()).decode('ascii')
            self.send_response(101)
            self.send_header('Upgrade', 'websocket')
            self.send_header('Connection', 'Upgrade')
            self.send_header('Sec-WebSocket-Accept', accept)
            self.end_headers()
            self.echo_frames()
        else:
            self.send_error(404)

    def echo_frames(self):
        while True:
            header = self.rfile.read(2)
            if len(header) != 2:
                return
            opcode, size = header[0] & 15, header[1] & 127
            if size == 126:
                size = struct.unpack('!H', self.rfile.read(2))[0]
            elif size == 127:
                size = struct.unpack('!Q', self.rfile.read(8))[0]
            if size > 65536 or not header[1] & 128:
                return
            mask = self.rfile.read(4)
            data = self.rfile.read(size)
            if len(data) != size:
                return
            data = bytes(value ^ mask[i % 4] for i, value in enumerate(data))
            if opcode == 8:
                self.wfile.write(b'\x88\x00')
                self.wfile.flush()
                return
            if opcode not in (1, 2, 9):
                return
            response_opcode = 10 if opcode == 9 else opcode
            length = bytes([size]) if size < 126 else (b'\x7e' + struct.pack('!H', size) if size <= 65535 else b'\x7f' + struct.pack('!Q', size))
            self.wfile.write(bytes([0x80 | response_opcode]) + length + data)
            self.wfile.flush()


if __name__ == '__main__':
    print('UI fixture: http://127.0.0.1:5061/graphql, /events and ws://127.0.0.1:5061/ws', flush=True)
    ThreadingHTTPServer(('127.0.0.1', 5061), Handler).serve_forever()
