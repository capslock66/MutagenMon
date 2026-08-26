## Working in `requirements/`

- These are **English-only** analysis documents (functional requirements,
  non-functional requirements, screen inventory, migration notes) plus SVG
  wireframes under `requirements/wireframes/`. Every requirement is
  traceable to a specific source file/behavior in `python/` — keep that
  traceability when editing.
- The wireframes are sketches, not pixel-accurate mockups. If the legacy
  UI text/behavior changes, update the corresponding wireframe and
  requirement doc together.
- If you find a new discrepancy between a requirements doc and the actual
  `python/` source, prefer re-reading the source and correcting the doc —
  the running legacy app is the ground truth for *current* behavior.
