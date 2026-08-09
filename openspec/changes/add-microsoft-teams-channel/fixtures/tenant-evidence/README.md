# Sanitized tenant-backed Teams evidence

These fixtures are structural records from the opt-in Phase 0.2 tenant transport spike. Every identifier, endpoint, message, filename, and attachment detail is synthetic. They contain no credentials, headers, cookies, signatures, or authenticated URLs.

They preserve only the field names, nesting, nullability, and cross-record identity relationships needed by the offline evidence tests. They do not enable channel routing, outbound delivery, or Graph attachment retrieval.

`channel-root-formatted-wrapper.json` captures the sanitized combined
channel-root and parameterized HTML rendering-wrapper shape. The wrapper is
transport metadata only: tests require scalar nonempty content with no name,
direct content or thumbnail URL, structured content, or Graph/provider
reference, and require canonical activity text to remain the only model-visible
text.

`channel-root-live-wrapper-variant.json` is a sanitized structural reproduction
of the second PR 8 live channel-root rejection. It preserves the SDK JSON-string
HTML wrapper, its non-Graph rendering reference, a companion SDK entity, and
the absent channel-data object. All values are synthetic.
