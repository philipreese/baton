# Incomplete work

## Done

- Added a durable, single-use originating pull-request recovery claim to queue items.
- Cleared the claim during the relevant queue lifecycle transitions.
- Tightened recovery validation to require the launched continuation attempt, room, branch, pull request, and canonical expected head to match.
- Added link-free directory checks for workspace identity validation.

## Not done

- Tests and end-to-end verification have not been run.
- The overall implementation remains incomplete and needs follow-up validation.
