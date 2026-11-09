"""Loopback HTTPS and redirect receiver for manual tests; synthetic secrets only."""
import http.server
import ssl
import sys
import threading

if len(sys.argv) != 3:
    raise SystemExit('Usage: python tls_fixture.py <certificate.pem> <private-key.pem>')

last_auth = None


class Receiver(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        global last_auth
        if self.path == '/receive':
            last_auth = self.headers.get('Authorization')
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b'received')
        elif self.path == '/last':
            body = ('authorization=' + (last_auth or '<none>')).encode('utf-8')
            self.send_response(200)
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        else:
            self.send_error(404)


class Secure(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/redirect':
            self.send_response(302)
            self.send_header('Location', 'http://127.0.0.1:5063/receive')
            self.end_headers()
        elif self.path == '/ok':
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b'tls-ok')
        else:
            self.send_error(404)


receiver = http.server.ThreadingHTTPServer(('127.0.0.1', 5063), Receiver)
threading.Thread(target=receiver.serve_forever, daemon=True).start()
secure = http.server.ThreadingHTTPServer(('127.0.0.1', 5062), Secure)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(sys.argv[1], sys.argv[2])
secure.socket = context.wrap_socket(secure.socket, server_side=True)
print('TLS fixture: https://127.0.0.1:5062/ok and /redirect; receiver: http://127.0.0.1:5063/last', flush=True)
secure.serve_forever()
