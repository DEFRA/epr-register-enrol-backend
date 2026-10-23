# Consumer tests

Black-box tests for the accreditation API, run from outside the service with your own
credentials, following `docs/reex-api-onboarding.md`. Needs Node 20+; no dependencies.

```bash
BASE_URL=https://... CLIENT_ID=... CLIENT_SECRET=... \
TEST_ORGANISATION_NUMBER=... TEST_REGISTRATION_NUMBER=... TEST_YEAR=2027 \
npm test
```

Optional `OTHER_CLIENT_ID` / `OTHER_CLIENT_SECRET` enable the cross-client check.
The worked-example signing test always runs. Live tests fail if the configuration is missing.
