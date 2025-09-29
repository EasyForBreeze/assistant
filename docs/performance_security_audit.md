# Assistant Audit – Performance & Security Review

## Summary
Follow-up audit of the Assistant portal identified several remaining risks impacting security and runtime reliability.

## Findings

### 1. OpenID Connect metadata retrieved without HTTPS enforcement
* **Location:** `Program.cs`
* **Issue:** The authentication configuration explicitly sets `RequireHttpsMetadata = false`, allowing metadata discovery over insecure transport. In production this opens the door for man-in-the-middle attacks on the identity configuration and tokens.
* **Recommendation:** Require HTTPS for metadata (remove the override or make it environment-dependent) and ensure the Keycloak base URL is HTTPS in production configurations.

### 2. Unbounded fan-out of Keycloak client searches
* **Location:** `Pages/Admin/UserClients.cshtml.cs`
* **Issue:** `LoadClientResultsAsync` fires one HTTP request per realm simultaneously via `Task.WhenAll` without a concurrency limit. In environments with many realms this can overload Keycloak or exhaust sockets, leading to throttling or timeouts for the admin UI.
* **Recommendation:** Introduce a bounded concurrency mechanism (e.g., `SemaphoreSlim`) similar to `ServiceRoleExclusionsModel` to cap simultaneous realm queries and optionally short-circuit when the current page worth of results is collected.

### 3. Sequential realm scans during exclusion validation and lookup
* **Location:** `Pages/Admin/ServiceRoleExclusions.cshtml.cs`
* **Issue:** Both `ClientExistsAsync` and `LookupClientsAsync` iterate through realms sequentially, performing HTTP queries one after another. With dozens of realms each call can take several seconds, degrading UX.
* **Recommendation:** Reuse the existing parallel search helper (or introduce a shared utility) with safe concurrency limits to validate client existence and build lookup suggestions faster. Cache intermediate results where possible to avoid duplicate requests.

## Next Steps
1. Harden the OpenID Connect setup to enforce HTTPS and document any environment-specific overrides.
2. Implement bounded concurrency for realm-spanning searches in `UserClientsModel`.
3. Parallelize and/or cache realm lookups inside the service-role exclusion workflows.

Addressing these items will remove the remaining hotspots from the audit and improve overall resilience.
