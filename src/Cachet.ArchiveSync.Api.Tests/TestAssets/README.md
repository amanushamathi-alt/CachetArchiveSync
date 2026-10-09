# Test assets

## `legacy-word-sample.doc`

A small, real legacy binary Word 97-SR2 (`.doc`) file, used by
`FileContentMatcherTests.Test_TryMatchAsync_DocContainingTerm_ReturnsSnippet` (CAS-012) to prove
`WordTextExtractor`'s GHSoftware.WordDocTextExtractor-based `.doc` extraction path against a real
file, not just its corrupt-bytes error path. No library in this solution can author a legacy `.doc`
file in-memory (NPOI's `.NET` port never implemented the `HWPF` module for any version), so this test
reads a fixture from disk instead of building one, unlike the `.docx`/`.xlsx`/`.xls` happy-path tests.

Source: `samples/simple.doc` from the
[GHSoftware.WordDocTextExtractor / b2xtranslator](https://github.com/GustavoHennig/b2xtranslator)
project's own public test corpus (BSD-3-Clause) — the same library this file's extractor uses, so the
fixture is representative of real input. Known content (`simple.expected.txt` in that repo): "This is
a simple file created with Word 97-SR2."
