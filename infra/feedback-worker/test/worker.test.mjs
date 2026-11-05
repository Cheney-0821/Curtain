import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import worker from '../src/worker.mjs';
const fixture = await readFile(new URL('./fixture.zip', import.meta.url));
test('persisted feedback logs contain only fixed outcome metadata', async () => {
  const { env } = setup();
  const entries = [];
  const originalLog = console.log;
  console.log = value => entries.push(value);
  try {
    await worker.fetch(uploadRequest({
      email: 'private-person@example.invalid',
      description: 'A private issue description that must not be logged',
      requestId: '4a02c85b-b077-4c9f-9aa2-f14d48f298b3',
    }), env);
    await worker.fetch(uploadRequest({
      email: 'invalid-private-email',
      description: 'another private description',
      requestId: '5b13d96c-c188-4da0-aab3-05e59a407ac4',
    }), env);
  } finally {
    console.log = originalLog;
  }
  assert.equal(entries.length, 2);
  assert.deepEqual(entries.map(x => x.status), [201, 400]);
  assert.deepEqual(entries.map(x => x.reason), ['ACCEPTED', 'INVALID_EMAIL']);
  for (const entry of entries) {
    assert.deepEqual(Object.keys(entry).sort(), ['event', 'method', 'reason', 'route', 'status']);
    assert.equal(entry.event, 'feedback.request');
    assert.equal(entry.route, 'api_feedback');
    assert.equal(entry.method, 'POST');
  }
  const serialized = JSON.stringify(entries);
  for (const secret of ['private-person@example.invalid', 'invalid-private-email',
    'A private issue description', 'another private description', '4a02c85b-b077-4c9f-9aa2-f14d48f298b3', '127.0.0.1']) {
    assert.equal(serialized.includes(secret), false, `logs must not contain ${secret}`);
  }
});

test('malformed multipart fails without storage', async () => {
  const { env, stored } = setup();
  const request = new Request('https://feedback.cheneylab.cn/api/feedback', { method: 'POST',
    headers: { 'X-Curtain-Consent': '1', 'Content-Type': 'multipart/form-data; boundary=broken' }, body: 'broken' });
  assert.equal((await worker.fetch(request, env)).status, 400);
  assert.equal(stored.size, 0);
});
test('valid 15 MiB and 16 MiB ZIP uploads are accepted at the configured boundary', async () => {
  for (const size of [15 * 1024 * 1024, 16 * 1024 * 1024]) {
    const bytes = makeStoredZip(size);
    assert.equal(bytes.length, size);
    const { env, stored } = setup();
    const requestId = size === 15 * 1024 * 1024
      ? '4a02c85b-b077-4c9f-9aa2-f14d48f298b3'
      : '5b13d96c-c188-4da0-aab3-05e59a407ac4';
    const [status, data] = await responseJson(await worker.fetch(uploadRequest({ bytes, requestId }), env));
    assert.equal(status, 201, `${size} byte ZIP should upload`);
    assert.equal(data.sizeBytes, size);
    assert.equal(stored.size, 1);
    assert.equal([...stored.values()][0].bytes.length, size);
  }
});

test('actual oversized ZIP fails without storage', async () => {
  const { env, stored } = setup();
  assert.equal((await worker.fetch(uploadRequest({ bytes: new Uint8Array(16 * 1024 * 1024 + 1) }), env)).status, 413);
  assert.equal(stored.size, 0);
});
test('storage listing failures fail closed', async () => {
  const { env, stored } = setup();
  env.DIAGNOSTICS_BUCKET.list = async () => { throw new Error('unavailable'); };
  assert.equal((await worker.fetch(uploadRequest(), env)).status, 503);
  assert.equal(stored.size, 0);
});
test('concurrent same-id requests cannot overwrite the winning object', async () => {
  const { env, stored } = setup();
  const changed = Buffer.from(fixture); changed[12] ^= 1;
  const replies = await Promise.all([worker.fetch(uploadRequest(), env), worker.fetch(uploadRequest({ bytes: changed }), env)]);
  assert.deepEqual(replies.map(x => x.status).sort(), [201,409]);
  assert.equal(stored.size, 1);
});
test('concurrent identical retries return the original receipt', async () => {
  const { env, stored } = setup();
  const replies = await Promise.all([worker.fetch(uploadRequest(), env), worker.fetch(uploadRequest(), env)]);
  assert.deepEqual(replies.map(x=>x.status), [201,201]);
  const [a,b] = await Promise.all(replies.map(x=>x.json()));
  assert.equal(a.receivedAt, b.receivedAt); assert.equal(a.sha256, b.sha256); assert.equal(stored.size,1);
});

const CRC32_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) crc = CRC32_TABLE[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

// Build a genuine, single-entry ZIP_STORED archive with a valid CRC and EOCD.
function makeStoredZip(totalSize) {
  const name = Buffer.from('payload.bin', 'ascii');
  const fixedBytes = 30 + name.length + 46 + name.length + 22;
  const payloadSize = totalSize - fixedBytes;
  assert.ok(payloadSize > 0 && payloadSize <= 0xffffffff);
  const payload = Buffer.alloc(payloadSize, 0x41);
  const checksum = crc32(payload);

  const local = Buffer.alloc(30 + name.length);
  local.writeUInt32LE(0x04034b50, 0);
  local.writeUInt16LE(20, 4);
  local.writeUInt16LE(0, 6);
  local.writeUInt16LE(0, 8);
  local.writeUInt32LE(checksum, 14);
  local.writeUInt32LE(payloadSize, 18);
  local.writeUInt32LE(payloadSize, 22);
  local.writeUInt16LE(name.length, 26);
  name.copy(local, 30);

  const central = Buffer.alloc(46 + name.length);
  central.writeUInt32LE(0x02014b50, 0);
  central.writeUInt16LE(20, 4);
  central.writeUInt16LE(20, 6);
  central.writeUInt16LE(0, 8);
  central.writeUInt16LE(0, 10);
  central.writeUInt32LE(checksum, 16);
  central.writeUInt32LE(payloadSize, 20);
  central.writeUInt32LE(payloadSize, 24);
  central.writeUInt16LE(name.length, 28);
  name.copy(central, 46);

  const centralOffset = local.length + payload.length;
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(1, 8);
  end.writeUInt16LE(1, 10);
  end.writeUInt32LE(central.length, 12);
  end.writeUInt32LE(centralOffset, 16);
  return Buffer.concat([local, payload, central, end]);
}

function setup(overrides = {}) {
  const stored = new Map();
  const bucket = {
    async list() {
      return { objects: [...stored.entries()].map(([key, value]) => ({ key, size: value.bytes.length })), truncated: false };
    },
    async head(key) {
      const existing = stored.get(key);
      return existing ? { customMetadata: existing.options.customMetadata, size: existing.bytes.byteLength } : null;
    },
    async put(key, bytes, options) {
      if (options.onlyIf?.etagDoesNotMatch === '*' && stored.has(key)) return null;
      stored.set(key, { bytes: new Uint8Array(bytes), options });
      return { key, size: bytes.byteLength };
    },
  };
  const env = {
    DIAGNOSTICS_BUCKET: bucket,
    IP_UPLOAD_LIMITER: { limit: async () => ({ success: true }) },
    GLOBAL_UPLOAD_LIMITER: { limit: async () => ({ success: true }) },
    ...overrides,
  };
  return { env, bucket, stored };
}

function uploadRequest({ bytes = fixture, email = '', description = '', version = '0.2.0-beta.6', consent = true,
  requestId = '4a02c85b-b077-4c9f-9aa2-f14d48f298b3', filename = 'Curtain-Diagnostics.zip' } = {}) {
  const form = new FormData();
  form.append('file', new File([bytes], filename, { type: 'application/zip' }));
  form.append('version', version);
  form.append('email', email);
  form.append('description', description);
  form.append('requestId', requestId);
  const headers = { 'CF-Connecting-IP': '127.0.0.1' };
  if (consent) headers['X-Curtain-Consent'] = '1';
  return new Request('https://feedback.cheneylab.cn/api/feedback', { method: 'POST', body: form, headers });
}

async function responseJson(response) {
  return [response.status, await response.json()];
}

test('GET /health returns schema without bucket names', async () => {
  const { env } = setup();
  const response = await worker.fetch(new Request('https://feedback.cheneylab.cn/health'), env);
  const [status, data] = await responseJson(response);
  assert.equal(status, 200);
  assert.equal(data.status, 'ok');
  assert.equal(data.protocol, 'multipart-v1');
  assert.equal(response.headers.get('Cache-Control'), 'no-store');
});

test('anonymous upload stores one private ZIP and returns a reference', async () => {
  const { env, stored } = setup();
  const response = await worker.fetch(uploadRequest(), env);
  const [status, data] = await responseJson(response);
  assert.equal(status, 201);
  assert.match(data.feedbackId, /^[a-f0-9-]{36}$/i);
  assert.equal(data.retentionDays, 14);
  assert.equal(data.sizeBytes, fixture.length);
  assert.equal(stored.size, 1);
  const [key, value] = [...stored.entries()][0];
  assert.match(key, /^incoming\/[a-f0-9-]{36}\.zip$/i);
  assert.equal(Buffer.compare(Buffer.from(value.bytes), fixture), 0);
  assert.equal(value.options.httpMetadata.contentType, 'application/zip');
  assert.equal(value.options.customMetadata.email, undefined);
  assert.equal(value.options.customMetadata.feedbackId, data.feedbackId);
  assert.equal(JSON.stringify(data).includes('incoming/'), false);
});

test('optional email and description are stored privately and not echoed to caller', async () => {
  const { env, stored } = setup();
  const response = await worker.fetch(uploadRequest({ email: 'example@domain.invalid', description: '窗口无法添加' }), env);
  const [status, data] = await responseJson(response);
  assert.equal(status, 201);
  const metadata = [...stored.values()][0].options.customMetadata;
  assert.equal(metadata.email, 'example@domain.invalid');
  assert.equal(metadata.description, '窗口无法添加');
  assert.equal(JSON.stringify(data).includes('example@'), false);
});

test('denies upload without explicit consent, leaving bucket empty', async () => {
  const { env, stored } = setup();
  const [status, data] = await responseJson(await worker.fetch(uploadRequest({ consent: false }), env));
  assert.equal(status, 400);
  assert.equal(data.error, 'CONSENT_REQUIRED');
  assert.equal(stored.size, 0);
});

test('denies malformed ZIP before storage', async () => {
  const { env, stored } = setup();
  const [status, data] = await responseJson(await worker.fetch(uploadRequest({ bytes: Buffer.from('this-is-not-a-zip') }), env));
  assert.equal(status, 422);
  assert.equal(data.error, 'INVALID_ARCHIVE');
  assert.equal(stored.size, 0);
});

test('rejects invalid email', async () => {
  const { env, stored } = setup();
  const [status, data] = await responseJson(await worker.fetch(uploadRequest({ email: 'no-address' }), env));
  assert.equal(status, 400);
  assert.equal(data.error, 'INVALID_EMAIL');
  assert.equal(stored.size, 0);
});

test('rate limited response has Retry-After and does not store', async () => {
  const { env, stored } = setup({ GLOBAL_UPLOAD_LIMITER: { limit: async () => ({ success: false }) } });
  const response = await worker.fetch(uploadRequest(), env);
  const [status, data] = await responseJson(response);
  assert.equal(status, 429);
  assert.equal(data.error, 'RATE_LIMITED');
  assert.equal(response.headers.get('Retry-After'), '60');
  assert.equal(stored.size, 0);
});

test('soft bucket budget prevents new upload when storage is near 1 GiB', async () => {
  const { env, stored } = setup();
  env.DIAGNOSTICS_BUCKET.list = async () => ({ objects: [{ key: 'incoming/old.zip', size: 1024 * 1024 * 1024 - 100 }], truncated: false });
  const [status, data] = await responseJson(await worker.fetch(uploadRequest(), env));
  assert.equal(status, 503);
  assert.equal(data.error, 'STORAGE_BUDGET_REACHED');
  assert.equal(stored.size, 0);
});

test('disabled R2 bucket binding fails closed', async () => {
  const { env } = setup({ DIAGNOSTICS_BUCKET: undefined });
  const [status, data] = await responseJson(await worker.fetch(uploadRequest(), env));
  assert.equal(status, 503);
  assert.equal(data.error, 'SERVICE_UNAVAILABLE');
});

test('GET /api/feedback has no public download API', async () => {
  const { env } = setup();
  const [status, data] = await responseJson(await worker.fetch(new Request('https://feedback.cheneylab.cn/api/feedback'), env));
  assert.equal(status, 405);
  assert.equal(data.error, 'METHOD_NOT_ALLOWED');
});

test('oversized declared upload rejected without reading payload', async () => {
  const { env } = setup();
  const form = new FormData();
  form.append('file', new File([fixture], 'sample.zip'));
  const req = new Request('https://feedback.cheneylab.cn/api/feedback', { method: 'POST', body: form, headers: { 'X-Curtain-Consent': '1', 'Content-Length': String(17 * 1024 * 1024) } });
  const [status, data] = await responseJson(await worker.fetch(req, env));
  assert.equal(status, 413);
  assert.equal(data.error, 'ARCHIVE_TOO_LARGE');
});


test('retry with same request ID returns the same receipt and does not create a second object', async () => {
  const { env, stored } = setup();
  const first = await (await worker.fetch(uploadRequest(), env)).json();
  const second = await (await worker.fetch(uploadRequest(), env)).json();
  assert.equal(first.feedbackId, second.feedbackId);
  assert.equal(first.receivedAt, second.receivedAt);
  assert.equal(stored.size, 1);
});

test('same request ID with a different valid ZIP is rejected', async () => {
  const { env, stored } = setup();
  await worker.fetch(uploadRequest(), env);
  // Repackage with same valid ZIP but a comment change that keeps EOCD valid.
  // Directly alter a byte in an existing payload outside the EOCD.
  const changed = Buffer.from(fixture);
  changed[12] ^= 0x01;
  const [status, data] = await responseJson(await worker.fetch(uploadRequest({ bytes: changed }), env));
  assert.equal(status, 409);
  assert.equal(data.error, 'REQUEST_ID_CONFLICT');
  assert.equal(stored.size, 1);
});
