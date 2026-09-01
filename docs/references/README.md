# Reference papers

Primary sources for the algorithm in `PROJECT_SPEC.md` §5. Committed so the
specification can cite exact sections and so the build environment does not need
network access to them.

| File | Citation | Status |
|---|---|---|
| `fugue-tpds-2025.pdf` | Weidner & Kleppmann, "The Art of the Fugue: Minimizing Interleaving in Collaborative Text Editing", *IEEE Transactions on Parallel and Distributed Systems* 36(11), November 2025. | **Normative for §5.** Defines FugueMax and proves maximal non-interleaving. |
| `fugue-arxiv-v1-extended-2023.pdf` | Weidner, Gentle & Kleppmann, arXiv:2305.00583v1, 2023. 32pp. | Supporting. Predates the FugueMax name but carries the full appendices: Appendix A worked anomaly examples, Appendix B impossibility proof for 3+ concurrent sites. Source for the fixed conformance traces (§9). |

Where the two disagree, the TPDS paper wins. Where the papers disagree with any
reference implementation, the papers win — and the disagreement gets recorded in
§13 rather than resolved silently.

These are third-party copyrighted works, included for reference only.
