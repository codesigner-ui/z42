#!/usr/bin/env bash
# scripts/_lib/release_archive.sh — Helpers for release pipeline:
# tar.gz/zip packaging of per-RID SDK packages + SHA256SUMS generation.
#
# Used by .github/workflows/release.yml. Sourced, not executed.
#
# Exposed functions:
#   make_archive <rid> <version>      → produces artifacts/release/z42-<v>-<rid>.{tar.gz|zip}
#   make_checksums <dir>              → prints SHA256SUMS-format lines (coreutils style) to stdout
#
# Format selection: windows-x64 → .zip (Windows native), all 8 other RIDs → .tar.gz.
#
# Sourcing contract: caller must set RELEASE_ROOT to the repo root before invoking the helpers,
# or invoke from the repo root (the helpers fall back to $PWD when RELEASE_ROOT is unset).

# make_archive <rid> <version>
# Reads:   <root>/artifacts/packages/z42-<version>-<rid>-release/
# Writes:  <root>/artifacts/release/z42-<version>-<rid>.<ext>
make_archive() {
    local rid="$1" version="$2"
    local root="${RELEASE_ROOT:-$PWD}"
    local pkg_dir_name="z42-${version}-${rid}-release"
    local pkg_dir="$root/artifacts/packages/$pkg_dir_name"
    local out_dir="$root/artifacts/release"

    [ -d "$pkg_dir" ] || { echo "error: package dir missing: $pkg_dir" >&2; return 1; }
    mkdir -p "$out_dir"

    local ext="tar.gz"
    [[ "$rid" == windows-* ]] && ext="zip"
    local archive="$out_dir/z42-${version}-${rid}.${ext}"

    # Use bsdtar's auto-compression (-a) for zip: GitHub's windows-latest git-bash
    # has tar (bsdtar) but not zip on PATH. bsdtar reads the .zip extension and
    # produces a real zip archive. macOS tar is also bsdtar so the same flag
    # works locally on macOS. Linux GNU tar never emits .zip (we only produce
    # .zip on the windows-x64 RID, which runs on windows-latest).
    if [ "$ext" = "zip" ]; then
        tar -C "$root/artifacts/packages" -a -cf "$archive" "$pkg_dir_name"
    else
        tar -C "$root/artifacts/packages" -czf "$archive" "$pkg_dir_name"
    fi
    echo "✓ $(basename "$archive") ($(du -h "$archive" | cut -f1))"
}

# make_checksums <dir>
# Prints SHA256SUMS-format lines for *.tar.gz / *.zip files in <dir> to stdout,
# sorted by filename. Uses shasum (BSD/macOS) — coreutils sha256sum has identical output.
make_checksums() {
    local dir="$1"
    [ -d "$dir" ] || { echo "error: dir missing: $dir" >&2; return 1; }
    (
        cd "$dir"
        shopt -s nullglob
        local files=(z42-*.tar.gz z42-*.zip)
        [ "${#files[@]}" -gt 0 ] || { echo "error: no archives in $dir" >&2; exit 1; }
        shasum -a 256 "${files[@]}" | sort -k2
    )
}
