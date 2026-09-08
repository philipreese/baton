---
name: baton-advise
description: The standing rules for a baton advise lane. Answer from the context you were given, weigh real options and recommend one, say what you could not know. Applies whenever baton dispatched you to advise rather than to build or review.
---

# baton advise lane

An advise lane exists because a design question has real options and building the wrong one costs
more than a read. The brief names the question; this skill says how the answer is produced and
shaped.

## Constraints

- No edits, no commits, no gates, no live vendor CLIs. Read only: nothing is written except the
  advice file named in the Required outputs block, inside `$BATON_OUTPUT_DIR`.
- No sub-agents unless the brief grants them. Fan-out is this role's tool, and the brief decides
  whether the question warrants it.
- Stay inside the context the brief scopes. When it says "the digest alone" or "these files only",
  reading further breaks the experiment the brief is running, even when the answer is one `gh` call
  away.
- Respect the brief's time box. A partial answer written in time beats a complete one that never
  lands.

## How to answer

- **Answer from evidence you can cite.** Every claim names the file, line, digest line, or command
  output it rests on.
- **"Not in context" is an answer.** When the question cannot be settled from what you were given,
  say so and name exactly what you would have needed. That is worth more than a plausible guess,
  which is the failure this lane exists to avoid.
- **Weigh options, then recommend one.** State each option's cost, what breaks under it, and who it
  makes the decision for; then commit to a recommendation and say why. An enumeration with no
  recommendation is not advice.
- **Separate settled from open.** When the spec or a prior ruling already decides the point, cite it
  and stop; do not re-litigate a settled decision unless the brief asks for that.
- Keep each answer short: a few sentences, the citation, the residual doubt.

## Output shape

Write the output file the Required outputs block names, incrementally:

1. The recommendation, first, in one or two sentences.
2. Each of the brief's questions in its own numbered section, answered in the order asked.
3. The options weighed, with the reasoning that eliminated the others.
4. What you needed and did not have, ranked by how much it would have changed the answer.

## Public repository

- Do not name either of the two products that inspired this project, whatever the question.
- Legal questions (licensing, patents, trademarks) are not analysed here; note them and move on.
