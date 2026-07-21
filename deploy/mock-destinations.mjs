// Mock destination servers for the smoke test: GA4 MP, Amplitude, MoEngage,
// Adjust on one port. Records every request; GET /_requests returns them.
import http from 'node:http';

const port = Number(process.env.MOCK_PORT ?? 9700);
const requests = [];

http
  .createServer((req, res) => {
    let body = '';
    req.on('data', (chunk) => (body += chunk));
    req.on('end', () => {
      if (req.method === 'GET' && req.url === '/_requests') {
        res.setHeader('content-type', 'application/json');
        res.end(JSON.stringify(requests));
        return;
      }
      requests.push({ method: req.method, url: req.url, body });
      if (req.url.startsWith('/mp/collect')) {
        res.statusCode = 204; // GA4 MP
        res.end();
      } else if (req.url.startsWith('/2/httpapi')) {
        res.end('{"code":200,"events_ingested":1}'); // Amplitude
      } else if (req.url.startsWith('/v1/event/')) {
        res.end('{"status":"success"}'); // MoEngage type:"event"
      } else if (req.url.startsWith('/v1/customer/')) {
        res.end('{"status":"success"}'); // MoEngage type:"customer" (SPEC §6.1)
      } else if (req.url.startsWith('/event')) {
        res.end('OK'); // Adjust S2S
      } else {
        res.end('{}');
      }
    });
  })
  .listen(port, '127.0.0.1', () => console.log(`mock destinations listening on ${port}`));
