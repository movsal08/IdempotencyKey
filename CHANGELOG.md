# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]
- Idempotency is now gated on the HTTP method for **every** opt-in path. Previously the method
  check lived only in `Predicate`, which endpoint metadata bypassed, so a class-level
  `[RequireIdempotency]` on an MVC controller demanded an `Idempotency-Key` header on that
  controller's GET actions. Configure via `IdempotencyAspNetCoreOptions.ApplicableMethods`
  (default POST/PUT/PATCH/DELETE) or per endpoint via `RequireIdempotencyAttribute.Methods` /
  `IdempotencyPolicyMetadata.Methods` (empty collection = no method restriction).
- `IdempotencyAspNetCoreOptions.Predicate` is now nullable; when null the `ApplicableMethods` set
  is used for endpoints without idempotency metadata.
- Initial release setup.
