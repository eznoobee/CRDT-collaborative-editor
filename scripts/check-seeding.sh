#!/usr/bin/env bash
# §12: no harness reaches past the product to create a document.
#
# Register rows 15 and 16 were exactly this — every harness seeded through psql
# because nothing in the product could make a document, and eleven phases of a
# green suite never noticed. A grep rather than a review, because judgement at
# the end of a long phase is what produced those rows: it is easy to add one
# INSERT "just for this test", and impossible to see later.
#
# BOTH HARNESSES, which is register row 25. The first version of this check
# covered client/src and nothing else, so EditorApiFactory went on adding
# Document and User rows through the DbContext for another two phases — right
# next to a green gate that was about exactly that. A rule whose scope is half
# the repository is a rule that is half applied.
set -uo pipefail

cd "$(dirname "$0")/.."

status=0

echo "==> no seeded documents (TypeScript harnesses)"
if grep -rniE 'insert[[:space:]]+into[[:space:]]+(documents|document_members|users)' \
    client/src --include='*.ts' --include='*.tsx'; then
  echo
  echo "A harness is writing rows the product's own API should create."
  status=1
fi

# The C# harness does not write SQL — it has a DbContext, which is the same
# thing with better ergonomics. Entity adds against the three tables the product
# owns are what to look for, and the tests that legitimately assert ON those
# tables read rather than add.
echo "==> no seeded documents (C# harness)"
if grep -rnE '\.(Documents|DocumentMembers|Users)\.(Add|AddAsync|AddRange)\(' \
    tests --include='*.cs'; then
  echo
  echo "The C# harness is adding rows the product's own API should create."
  status=1
fi

if [ "$status" -ne 0 ]; then
  echo
  echo "PROJECT_SPEC.md §12: seeding past the product is what register rows 15,"
  echo "16 and 25 were. Create documents with POST /documents, users with GET /me,"
  echo "and memberships with PUT /documents/{id}/members/{userId}."
  exit 1
fi

echo "ok: both harnesses create documents through the product"
