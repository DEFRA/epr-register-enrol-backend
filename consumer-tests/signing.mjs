import { createHmac, randomBytes } from 'node:crypto'

export function sign(secret, clientId, timestamp, nonce) {
  const payload = ['v3', clientId, '', '', timestamp, nonce].join('\n')
  return createHmac('sha256', secret).update(payload).digest('base64')
}

export function signedHeaders(clientId, secret, overrides = {}) {
  const timestamp =
    overrides.timestamp ?? new Date().toISOString().replace(/\.\d{3}Z$/, 'Z')
  const nonce = overrides.nonce ?? randomBytes(16).toString('base64')
  return {
    'x-cdp-client-id': clientId,
    'x-cdp-auth-timestamp': timestamp,
    'x-cdp-auth-nonce': nonce,
    'x-cdp-auth-signature': sign(secret, clientId, timestamp, nonce)
  }
}
