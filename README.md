# IdempotencyKey
A small, AOT-friendly library that provides idempotency for “write” endpoints (POST/PATCH/PUT/DELETE) by storing: idempotency key, request fingerprint (hash), response snapshot, and TTL.
