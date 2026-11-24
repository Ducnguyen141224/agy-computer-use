import readline from 'node:readline';
for await (const line of readline.createInterface({ input: process.stdin })) {
  const request = JSON.parse(line);
  if (request.method === 'hang' || request.params?.hang === true) continue;
  if (request.method === 'crash') process.exit(1);
  if (request.method === 'delay') await new Promise(resolve => setTimeout(resolve, 40));
  process.stdout.write(JSON.stringify({ id: request.id, ok: true, data: { method: request.method } }) + '\n');
}
