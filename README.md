# IdempotencyKey

A small, AOT-friendly library that provides idempotency for “write” endpoints (POST/PATCH/PUT/DELETE) by storing: idempotency key, request fingerprint (hash), response snapshot, and TTL.

## Goals

- Provide Stripe-like idempotency semantics for ASP.NET Core Minimal APIs.
- Native AOT support.
- Storage agnostic (Memory, Redis, Postgres).

## Quickstart

(Coming soon)
