// Curtain Diagnostic Feedback Worker — reviewable deployment candidate.
// No secrets and no public read/download endpoint. Runtime: Cloudflare Workers.
// Contract: POST /api/feedback (multipart/form-data). See docs/API-CONTRACT.md.

const MAX_ZIP_BYTES = 16 * 1024 * 1024;
const MAX_REQUEST_BYTES = MAX_ZIP_BYTES + 64 * 1024;
const MAX_DESCRIPTION_CHARS = 300;
const MAX_DESCRIPTION_BYTES = 900;
const MAX_EMAIL_CHARS = 254;
const MAX_VERSION_CHARS = 48;
const MAX_BUCKET_BYTES = 1024 * 1024 * 1024; // soft budget: ~1 GiB for this private bucket
const MAX_BUCKET_OBJECTS = 1000;
const RETENTION_DAYS = 14;

class FeedbackHttpError extends Error {
  constructor(code, status, message) {
    super(message || code);
    this.code = code;
    this.status = status;
  }
}

function respond(body, status = 200, extra = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': 'application/json; charset=utf-8',
      'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff',
      ...extra,
    },
  });
}

function logSafeFeedbackOutcome(status, reason) {
  // Persist only fixed route/method/status/reason fields. Never log IP, headers,
  // request IDs, filenames, email, descriptions, ZIP bytes, or exception text.
  try {
    console.log({
      event: 'feedback.request',
      route: 'api_feedback',
      method: 'POST',
      status,
      reason,
    });
  } catch { /* Telemetry must never affect upload behavior. */ }
}

function requireField(value, name) {
  if (typeof value !== 'string') {
    throw new FeedbackHttpError('INVALID_' + name.toUpperCase(), 400);
  }
  return value;
}

function parseOptionalText(value, maxChars, maxBytes, name) {
  if (value === null) return '';
  const text = requireField(value, name).trim();
  if (text.length > maxChars || new TextEncoder().encode(text).length > maxBytes || /[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(text)) {
    throw new FeedbackHttpError('INVALID_' + name.toUpperCase(), 400);
  }
  return text;
}

function parseEmail(value) {
  const email = parseOptionalText(value, MAX_EMAIL_CHARS, MAX_EMAIL_CHARS, 'email');
  if (!email) return '';
  // Feedback contact only; do not use as authentication or claim it is verified.
  if (!/^[^\s@<>]+@[^\s@<>]+\.[^\s@<>]+$/.test(email) || /[\r\n]/.test(email)) {
    throw new FeedbackHttpError('INVALID_EMAIL', 400);
  }
  return email;
}

function validZipBytes(bytes) {
  // This is a structural sanity check, not a full ZIP decompressor or malware scan.
  if (!(bytes instanceof Uint8Array) || bytes.length < 22) return false;
  if (bytes[0] !== 0x50 || bytes[1] !== 0x4b || bytes[2] !== 0x03 || bytes[3] !== 0x04) return false;
  // End-of-central-directory may have a ZIP comment of up to 65535 bytes.
  const first = Math.max(0, bytes.length - 65557);
  for (let i = bytes.length - 22; i >= first; i--) {
    if (bytes[i] === 0x50 && bytes[i + 1] === 0x4b && bytes[i + 2] === 0x05 && bytes[i + 3] === 0x06) {
      const commentSize = bytes[i + 20] | (bytes[i + 21] << 8);
      if (i + 22 + commentSize === bytes.length) return true;
    }
  }
  return false;
}

async function readLimitedBody(stream, limit) {
  if (!stream) throw new FeedbackHttpError('EMPTY_REQUEST', 400);
  const reader = stream.getReader();
  const chunks = [];
  let size = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > limit) throw new FeedbackHttpError('ARCHIVE_TOO_LARGE', 413);
      chunks.push(value);
    }
  } finally {
    try { await reader.cancel(); } catch { /* exhausted stream or closed */ }
    reader.releaseLock();
  }
  if (!size) throw new FeedbackHttpError('EMPTY_REQUEST', 400);
  const result = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return result;
}

async function checkBudget(bucket, incomingBytes) {
  // Best-effort budget, NOT a transactional/hard quota. Concurrent uploads can race.
  let count = 0;
  let bytes = 0;
  let cursor;
  for (let page = 0; page < 8; page++) {
    const listing = await bucket.list({ prefix: 'incoming/', limit: 1000, ...(cursor ? { cursor } : {}) });
    for (const item of listing.objects || []) {
      count++;
      bytes += Number(item.size || 0);
      if (count >= MAX_BUCKET_OBJECTS || bytes + incomingBytes > MAX_BUCKET_BYTES) {
        throw new FeedbackHttpError('STORAGE_BUDGET_REACHED', 503);
      }
    }
    if (!listing.truncated) return;
    if (!listing.cursor || listing.cursor === cursor) break;
    cursor = listing.cursor;
  }
  throw new FeedbackHttpError('STORAGE_BUDGET_REACHED', 503);
}

async function checkLimiter(limiter, key) {
  if (!limiter || typeof limiter.limit !== 'function') {
    throw new FeedbackHttpError('SERVICE_UNAVAILABLE', 503);
  }
  const value = await limiter.limit({ key });
  if (!value.success) throw new FeedbackHttpError('RATE_LIMITED', 429);
}

async function handleUpload(request, env) {
  if (!env.DIAGNOSTICS_BUCKET || !env.IP_UPLOAD_LIMITER || !env.GLOBAL_UPLOAD_LIMITER) {
    throw new FeedbackHttpError('SERVICE_UNAVAILABLE', 503);
  }
  if (request.headers.get('X-Curtain-Consent') !== '1') {
    throw new FeedbackHttpError('CONSENT_REQUIRED', 400);
  }
  const contentType = request.headers.get('Content-Type') || '';
  if (!/^multipart\/form-data\s*;/i.test(contentType) || !/boundary=/i.test(contentType)) {
    throw new FeedbackHttpError('INVALID_CONTENT_TYPE', 415);
  }
  const declaredLength = Number(request.headers.get('Content-Length'));
  if (request.headers.has('Content-Length') && (!Number.isFinite(declaredLength) || declaredLength < 1 || declaredLength > MAX_REQUEST_BYTES)) {
    throw new FeedbackHttpError('ARCHIVE_TOO_LARGE', 413);
  }
  const sourceIp = request.headers.get('CF-Connecting-IP') || 'unknown';
  // Cloudflare overwrites CF-Connecting-IP at its edge. Never log or persist the IP.
  const ipDigest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(`curtain-feedback-ip:${sourceIp}`));
  const ipKey = [...new Uint8Array(ipDigest)].map(x => x.toString(16).padStart(2, '0')).join('');
  await checkLimiter(env.IP_UPLOAD_LIMITER, ipKey);
  await checkLimiter(env.GLOBAL_UPLOAD_LIMITER, 'all-feedback-uploads');

  const body = await readLimitedBody(request.body, MAX_REQUEST_BYTES);
  let form;
  try {
    const parseRequest = new Request('https://worker.internal/feedback', {
      method: 'POST',
      headers: { 'Content-Type': contentType },
      body,
    });
    form = await parseRequest.formData();
  } catch {
    throw new FeedbackHttpError('INVALID_MULTIPART', 400);
  }

  const file = form.get('file');
  if (!(file instanceof File) || file.size < 22 || !file.name.toLowerCase().endsWith('.zip')) {
    throw new FeedbackHttpError('INVALID_ARCHIVE', 422);
  }
  if (file.size > MAX_ZIP_BYTES) throw new FeedbackHttpError('ARCHIVE_TOO_LARGE', 413);
  const description = parseOptionalText(form.get('description'), MAX_DESCRIPTION_CHARS, MAX_DESCRIPTION_BYTES, 'description')
    .replace(/[\r\n\t]+/g, ' ');
  const email = parseEmail(form.get('email'));
  const version = parseOptionalText(form.get('version'), MAX_VERSION_CHARS, MAX_VERSION_CHARS, 'version');
  if (!/^[a-zA-Z0-9._+-]{1,48}$/.test(version)) throw new FeedbackHttpError('INVALID_VERSION', 400);
  const requestId = parseOptionalText(form.get('requestId'), 36, 36, 'requestId');
  if (requestId && !/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(requestId)) {
    throw new FeedbackHttpError('INVALID_REQUEST_ID', 400);
  }

  const archiveBytes = new Uint8Array(await file.arrayBuffer());
  if (!validZipBytes(archiveBytes)) throw new FeedbackHttpError('INVALID_ARCHIVE', 422);
  const id = requestId || crypto.randomUUID();
  const key = `incoming/${id}.zip`;
  const digest = await crypto.subtle.digest('SHA-256', archiveBytes);
  const sha256 = [...new Uint8Array(digest)].map(x => x.toString(16).padStart(2, '0')).join('');
  // If the response was lost after a successful write, a retry with the same client
  // request ID and same content returns the original receipt without double storage.
  const previous = await env.DIAGNOSTICS_BUCKET.head(key);
  if (previous) {
    if (previous.customMetadata?.sha256 !== sha256) throw new FeedbackHttpError('REQUEST_ID_CONFLICT', 409);
    return respond({ feedbackId: id, receivedAt: previous.customMetadata?.receivedAt,
      retentionDays: RETENTION_DAYS, sizeBytes: archiveBytes.byteLength, sha256 }, 201);
  }
  await checkBudget(env.DIAGNOSTICS_BUCKET, archiveBytes.byteLength);
  const receivedAt = new Date().toISOString();
  const customMetadata = { feedbackId: id, receivedAt, version, sha256 };
  if (description) customMetadata.description = description;
  if (email) customMetadata.email = email;
  let stored;
  try {
    stored = await env.DIAGNOSTICS_BUCKET.put(key, archiveBytes, {
      onlyIf: { etagDoesNotMatch: '*' },
      httpMetadata: { contentType: 'application/zip' },
      customMetadata,
    });
  } catch {
    throw new FeedbackHttpError('STORAGE_UNAVAILABLE', 503);
  }
  if (!stored) {
    const winner = await env.DIAGNOSTICS_BUCKET.head(key);
    if (!winner) throw new FeedbackHttpError('STORAGE_UNAVAILABLE', 503);
    if (winner.customMetadata?.sha256 !== sha256) throw new FeedbackHttpError('REQUEST_ID_CONFLICT', 409);
    return respond({ feedbackId: id, receivedAt: winner.customMetadata.receivedAt,
      retentionDays: RETENTION_DAYS, sizeBytes: archiveBytes.byteLength, sha256 }, 201);
  }
  return respond({ feedbackId: id, receivedAt, retentionDays: RETENTION_DAYS, sizeBytes: archiveBytes.byteLength, sha256 }, 201);
}

export default {
  async fetch(request, env) {
    const pathname = new URL(request.url).pathname;
    if (request.method === 'GET' && pathname === '/health') {
      return respond({ status: 'ok', service: 'curtain-feedback-upload', protocol: 'multipart-v1' });
    }
    if (pathname !== '/api/feedback') {
      return respond({ error: 'NOT_FOUND' }, 404);
    }
    if (request.method !== 'POST') {
      return respond({ error: 'METHOD_NOT_ALLOWED' }, 405, { Allow: 'POST' });
    }
    try {
      const response = await handleUpload(request, env);
      logSafeFeedbackOutcome(response.status, response.status === 201 ? 'ACCEPTED' : 'REJECTED');
      return response;
    } catch (error) {
      if (error instanceof FeedbackHttpError) {
        logSafeFeedbackOutcome(error.status, error.code);
        return respond({ error: error.code }, error.status,
          error.status === 429 ? { 'Retry-After': '60' } : {});
      }
      // Never log exception text, request bodies, email, descriptions, filenames, or IP data.
      logSafeFeedbackOutcome(503, 'UNEXPECTED_ERROR');
      return respond({ error: 'SERVICE_UNAVAILABLE' }, 503);
    }
  },
};
