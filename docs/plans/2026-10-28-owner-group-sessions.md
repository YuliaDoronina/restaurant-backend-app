## Scope

- Add `GET /guest/groups/{groupReservationId}/sessions` to the existing API-handler Lambda. `RouteRegistry` already wires guest/group routes, and `GuestSessionService` already has access to group reservations, sessions, dishes, reservations, and users.
- Restrict the endpoint to the authenticated reservation owner. `DeleteGuestSessionHandler` obtains the Cognito `sub`, and `GuestSessionService.ValidateReservationOwnerAsync` already checks ownership through the group reservation's parent reservation. Reuse this behavior; return the repository-standard unauthorized, forbidden, and not-found responses.
- Return both the Owner session and guest sessions, as clarified. Include the session id, participant type/name, dishes with only `id`, `name`, and `price`, quantity, per-participant total, submission state, and aggregate total. Include sessions regardless of submission state so `isSubmitted` remains meaningful; confirm this behavior against product expectations if acceptance criteria change.
- Keep the requested response shape separate from `GetAllSessionsResponse`/`OrderItemResponse`: the existing item model serializes additional dish fields and is used by the guest-session deletion response. Add dedicated response DTOs rather than changing that existing contract.
- `GuestSessionRepository.GetAllByGroupReservationIdAsync` already queries the `groupReservationId` GSI. `deployment_resources.json` already defines this index on `GuestSessions`; no table/index or configuration changes are indicated. Check existing API Lambda DynamoDB permissions only if the new query needs permissions not already granted.
- Update the public API documentation in `swagger_src/swagger-ui/swagger-endpoints.yaml` and `docs/ApiRoutes.md`. No other Lambdas or persistence schema changes are identified.
- Open questions/risks: owner-session display name should follow the established owner-name mapping (`given_name`); missing dish records or missing owner identity data need explicit handling consistent with existing service behavior. No unresolved decision remains on whether to include the Owner session.

## Tasks

### 1. Implementation - AWS Lambda Developer

- [ ] Add a new `dnapp/lambdas/api-handler/Handlers/GuestSessions/GetGroupSessionsHandler.cs` that reads `groupReservationId`, obtains the authenticated Cognito `sub`, calls the service, and maps authentication, forbidden, and not-found errors to established HTTP responses.
- [ ] Add `GetGroupSessionsAsync` to `dnapp/lambdas/api-handler/Services/GuestSessions/IGuestSessionService.cs` and implement it in `dnapp/lambdas/api-handler/Services/GuestSessions/GuestSessionService.cs`; reuse reservation-owner validation, query all group sessions, map Owner and Guest participants, compute item/participant/group totals, and preserve `IsSubmitted`.
- [ ] Add new response DTOs under `dnapp/lambdas/api-handler/Models/GuestSessions/` for the specified response and reduced dish shape (`id`, `name`, `price`); leave existing deletion response DTOs unchanged.
- [ ] Register `GET /guest/groups/{groupReservationId}/sessions` in `dnapp/lambdas/api-handler/Routing/RouteRegistry.cs`.
- [ ] Document the path, bearer authentication, response schema, and relevant error responses in `swagger_src/swagger-ui/swagger-endpoints.yaml` and `docs/ApiRoutes.md`.

### 2. Testing - Unit Test Agent

- [ ] Extend `Tests/Services/GuestSessionServiceTests.cs` to verify owner authorization, mapping of Owner and Guest sessions, dish quantities and prices, participant and aggregate totals, empty sessions, and preservation of submitted state; cover non-owner, missing group/reservation, and missing dish behavior.
- [ ] Add `Tests/Handlers/GuestSessions/GetGroupSessionsHandlerTests.cs` as a new test file to verify missing identity/path input and the success, forbidden, and not-found HTTP response mappings, following the handler-testing conventions available in the test project (or establish focused tests for this handler if no existing handler-test pattern is present).
