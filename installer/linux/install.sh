#!/bin/sh
set -eu

# Filee Linux standalone installer.
# Supports first-time installation and in-place upgrade (--upgrade).
# Preserves downloaded conversion engines (~/.local/share/Filee/engines) and user settings (~/.config/Filee).

upgrade=0
restart=0

for arg do
    case "$arg" in
        --upgrade|-u)
            upgrade=1
            ;;
        --restart)
            restart=1
            ;;
        --help|-h)
            printf '%s\n' "Usage: $0 [--upgrade|-u] [--restart]"
            exit 0
            ;;
        *)
            printf '%s\n' "Unknown option: $arg" >&2
            printf '%s\n' "Usage: $0 [--upgrade|-u] [--restart]" >&2
            exit 1
            ;;
    esac
done

source_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
data_dir=${XDG_DATA_HOME:-"$HOME/.local/share"}
install_dir="$data_dir/filee/application"
bin_dir="$HOME/.local/bin"
desktop_file="$data_dir/applications/com.filee.app.desktop"

# Validate required tools and incoming payload files before doing anything.
command -v python3 >/dev/null 2>&1 || {
    printf '%s\n' "Python 3 is required to install Filee." >&2
    exit 1
}

# Only enable internal test failure hooks if explicitly requested by the test harness
# AND executing within an isolated temporary test directory.
test_hooks_enabled=0
if [ "${_FILEE_TEST_HARNESS:-}" = "com.filee.test.harness" ]; then
    case "$data_dir" in
        /tmp/*|/var/tmp/*|*/filee-installer-test-*)
            test_hooks_enabled=1
            ;;
    esac
fi

if [ ! -f "$source_dir/Filee" ] || [ ! -x "$source_dir/Filee" ]; then
    printf '%s\n' "Payload validation failed: $source_dir/Filee is missing or not executable." >&2
    exit 1
fi

if [ ! -f "$source_dir/filee-cli" ] || [ ! -x "$source_dir/filee-cli" ]; then
    printf '%s\n' "Payload validation failed: $source_dir/filee-cli is missing or not executable." >&2
    exit 1
fi

if [ "$upgrade" -eq 0 ]; then
    # First-time installation: refuse to overwrite an existing installation or files.
    if [ -e "$install_dir" ] || [ -L "$install_dir" ]; then
        printf '%s\n' "An installation already exists at $install_dir. Run its uninstall.sh first."
        exit 1
    fi
    if [ -e "$bin_dir/filee" ] || [ -L "$bin_dir/filee" ]; then
        printf '%s\n' "$bin_dir/filee already exists. Remove or rename it before installing Filee."
        exit 1
    fi
    if [ -e "$desktop_file" ] || [ -L "$desktop_file" ]; then
        printf '%s\n' "$desktop_file already exists. Remove or rename it before installing Filee."
        exit 1
    fi

    mkdir -p "$data_dir/filee" "$bin_dir" "$data_dir/applications"
    cp -R "$source_dir" "$install_dir"
    printf '%s\n' 'com.filee.app' > "$install_dir/.filee-package"
    rm -f "$install_dir/.filee-upgrade-token"
    ln -s "$install_dir/filee-cli" "$bin_dir/filee"
else
    # Upgrade existing installation: verify it is genuine and safely swap in place.
    if [ ! -d "$install_dir" ] || [ -L "$install_dir" ]; then
        printf '%s\n' "No existing installation found at $install_dir to upgrade." >&2
        exit 1
    fi

    marker_file="$install_dir/.filee-package"
    if [ ! -f "$marker_file" ]; then
        printf '%s\n' "Cannot upgrade: $install_dir is missing the .filee-package marker." >&2
        exit 1
    fi

    marker=$(tr -d '\r\n' < "$marker_file" 2>/dev/null || true)
    if [ "$marker" != "com.filee.app" ]; then
        printf '%s\n' "Cannot upgrade: $install_dir was not installed by Filee." >&2
        exit 1
    fi

    # Tell running instance to save and exit cleanly before touching files (skipped during in-app updates).
    if [ "${FILEE_IN_APP_UPDATE:-0}" != "1" ]; then
        if [ -x "$install_dir/Filee" ]; then
            if ! "$install_dir/Filee" --quit; then
                printf '%s\n' "Could not gracefully shut down the running Filee instance. Please exit Filee and try again." >&2
                exit 1
            fi
        fi
    fi

    # Generate a unique transaction ID for this upgrade session using PID and random bytes.
    # This prevents collisions with any concurrent installer or pre-existing backup artifacts.
    token_rand=$(od -vN 8 -An -tx1 /dev/urandom 2>/dev/null | tr -d ' \n' || true)
    tx_id="$$-${token_rand:-$(date +%s)}"
    staging_dir="$data_dir/filee/.staging-$tx_id"
    backup_dir="$data_dir/filee/.backup-$tx_id"
    failed_dir="$data_dir/filee/.failed-$tx_id"
    upgrade_token="filee-upgrade-$tx_id"
    activated=0

    # Ensure transaction paths are completely distinct and do not collide with pre-existing files.
    if [ -e "$staging_dir" ] || [ -e "$backup_dir" ] || [ -e "$failed_dir" ]; then
        printf '%s\n' "Transaction path conflict detected at $data_dir/filee. Please retry." >&2
        exit 1
    fi

    mkdir -p "$data_dir/filee" "$bin_dir" "$data_dir/applications"
    mkdir "$staging_dir"

    # Copy incoming payload into staging (preserves source_dir without moving/deleting it).
    cp -R "$source_dir/." "$staging_dir/"
    printf '%s\n' 'com.filee.app' > "$staging_dir/.filee-package"
    printf '%s\n' "$upgrade_token" > "$staging_dir/.filee-upgrade-token"
    chmod +x "$staging_dir/Filee" "$staging_dir/filee-cli"

    # Validate the staged files before moving the existing installation aside.
    if [ ! -x "$staging_dir/Filee" ] || [ ! -x "$staging_dir/filee-cli" ]; then
        rm -rf "$staging_dir"
        printf '%s\n' "Staged payload validation failed." >&2
        exit 1
    fi

    # Rollback trap: handles catchable signals and script aborts.
    # Ensures $backup_dir is restored if anything fails during replacement.
    rollback_and_cleanup() {
        exit_code=$?
        if [ -d "$backup_dir" ]; then
            # If new files were activated into $install_dir, verify that $install_dir
            # actually contains this run's upgrade token before moving it aside.
            # This prevents another process or symlink swap from causing cleanup
            # to remove anything other than this process's failed payload.
            if [ "$activated" -eq 1 ] && [ -d "$install_dir" ] && [ ! -L "$install_dir" ]; then
                token_file="$install_dir/.filee-upgrade-token"
                if [ -f "$token_file" ] && [ ! -L "$token_file" ]; then
                    token=$(tr -d '\r\n' < "$token_file" 2>/dev/null || true)
                    if [ "$token" = "$upgrade_token" ]; then
                        # Token verified: safely move the failed payload aside to quarantine.
                        # Ensure $failed_dir does not already exist so mv performs a clean rename,
                        # and never fall back to recursive deletion if quarantine move fails.
                        if [ ! -e "$failed_dir" ] && mv "$install_dir" "$failed_dir" 2>/dev/null; then
                            # Re-verify token inside quarantined directory before deletion (TOCTOU mitigation).
                            # If ownership, symlink checks, or token match fail, keep $failed_dir intact.
                            q_token_file="$failed_dir/.filee-upgrade-token"
                            if [ -d "$failed_dir" ] && [ ! -L "$failed_dir" ] && [ -f "$q_token_file" ] && [ ! -L "$q_token_file" ]; then
                                q_token=$(tr -d '\r\n' < "$q_token_file" 2>/dev/null || true)
                                if [ "$q_token" = "$upgrade_token" ]; then
                                    rm -rf "$failed_dir" 2>/dev/null || true
                                fi
                            fi
                        fi
                    fi
                fi
            fi

            if [ ! -d "$install_dir" ] && mv "$backup_dir" "$install_dir" 2>/dev/null; then
                printf '%s\n' "Update failed. Restored the previous Filee installation." >&2
                if [ -d "$failed_dir" ]; then
                    printf '%s\n' "Quarantined failed update files preserved at: $failed_dir" >&2
                fi
            else
                printf '%s\n' "CRITICAL: Update failed and automatic rollback could not complete." >&2
                printf '%s\n' "Your previous installation is preserved at: $backup_dir" >&2
                if [ -d "$install_dir" ]; then
                    printf '%s\n' "A partial or invalid directory exists at: $install_dir" >&2
                    printf '%s\n' "To restore manually, inspect and remove or rename '$install_dir', then run:" >&2
                    printf '%s\n' "  mv '$backup_dir' '$install_dir'" >&2
                else
                    printf '%s\n' "To restore manually, run: mv '$backup_dir' '$install_dir'" >&2
                fi
                if [ -d "$failed_dir" ]; then
                    printf '%s\n' "Quarantined failed update files preserved at: $failed_dir" >&2
                fi
            fi
        fi
        rm -rf "$staging_dir" 2>/dev/null || true
        exit "$exit_code"
    }
    trap rollback_and_cleanup EXIT INT TERM HUP

    # Note on atomicity and crash recovery:
    # Individual filesystem renames (rename syscall) are atomic in POSIX.
    # However, replacing a non-empty directory on Linux requires two renames:
    #   1. mv "$install_dir" "$backup_dir"
    #   2. mv "$staging_dir" "$install_dir"
    # The two-step sequence as a whole is not a single atomic transaction.
    # There is a brief window between step 1 and step 2 where $install_dir does
    # not exist. Catchable signals (SIGINT, SIGTERM, SIGHUP) and script errors
    # are handled by the rollback trap above, which immediately restores $backup_dir.
    # If uncatchable termination (SIGKILL) or sudden hardware power loss occurs
    # during this brief window, automatic traps cannot run; the previous installation
    # remains safe in $backup_dir and can be restored manually.

    # Move active installation to backup.
    mv "$install_dir" "$backup_dir"

    # Test hook: simulate failure after backup but before activation.
    if [ "$test_hooks_enabled" -eq 1 ] && [ "${_FILEE_TEST_FAIL_ACTIVATION:-0}" = "1" ]; then
        rm -rf "$staging_dir"
    fi

    # Test hook: simulate activation destination conflict.
    if [ "$test_hooks_enabled" -eq 1 ] && [ "${_FILEE_TEST_FAIL_ACTIVATION_OCCUPIED:-0}" = "1" ]; then
        mkdir -p "$install_dir"
        touch "$install_dir/conflict"
        chmod 555 "$install_dir"
    fi

    # Activate new installation from staging.
    if ! mv "$staging_dir" "$install_dir"; then
        exit 1
    fi
    activated=1

    # Test hook: simulate failure during post-swap validation.
    if [ "$test_hooks_enabled" -eq 1 ] && [ "${_FILEE_TEST_FAIL_POST_VALIDATION:-0}" = "1" ]; then
        rm -f "$install_dir/.filee-package"
    fi

    # Test hook: simulate failure during post-swap validation where failed_dir cleanup will fail.
    if [ "$test_hooks_enabled" -eq 1 ] && [ "${_FILEE_TEST_FAIL_CLEANUP_FAILED_DIR:-0}" = "1" ]; then
        mkdir -p "$install_dir/locked-folder"
        touch "$install_dir/locked-folder/file"
        chmod 555 "$install_dir/locked-folder"
        rm -f "$install_dir/.filee-package"
    fi

    # Test hook: simulate quarantine move failure during rollback.
    if [ "$test_hooks_enabled" -eq 1 ] && [ "${_FILEE_TEST_FAIL_QUARANTINE:-0}" = "1" ]; then
        mkdir -p "$failed_dir"
        touch "$failed_dir/conflict"
        exit 1
    fi

    # Test hook: simulate replacement of install_dir by a foreign directory before rollback.
    if [ "$test_hooks_enabled" -eq 1 ] && [ "${_FILEE_TEST_FAIL_FOREIGN_DESTINATION:-0}" = "1" ]; then
        rm -rf "$install_dir"
        mkdir -p "$install_dir"
        echo "important-foreign-data" > "$install_dir/important.txt"
        exit 1
    fi

    # Test hook: simulate failure during rollback with occupied destination.
    if [ "$test_hooks_enabled" -eq 1 ] && { [ "${_FILEE_TEST_FAIL_ROLLBACK_OCCUPIED:-0}" = "1" ] || [ "${_FILEE_TEST_FAIL_ROLLBACK:-0}" = "1" ]; }; then
        rm -rf "$install_dir"
        mkdir -p "$install_dir"
        touch "$install_dir/conflict"
        chmod 555 "$install_dir"
        exit 1
    fi

    # Test hook: simulate failure during rollback with absent destination.
    if [ "$test_hooks_enabled" -eq 1 ] && [ "${_FILEE_TEST_FAIL_ROLLBACK_ABSENT:-0}" = "1" ]; then
        rm -rf "$install_dir"
        chmod 555 "$data_dir/filee"
        exit 1
    fi

    # Post-swap validation: ensure new installation is active and healthy.
    if [ ! -x "$install_dir/Filee" ] || [ ! -x "$install_dir/filee-cli" ] || [ ! -f "$install_dir/.filee-package" ]; then
        printf '%s\n' "Activated installation failed post-swap validation. Rolling back..." >&2
        exit 1
    fi

    # Success: disarm the rollback trap before removing the old version backup.
    trap - EXIT INT TERM HUP
    rm -rf "$backup_dir"
    rm -f "$install_dir/.filee-upgrade-token"

    # Refresh launcher symlink (using -f to overwrite existing link atomically).
    ln -sf "$install_dir/filee-cli" "$bin_dir/filee"
fi

# Update desktop launcher.
python3 - "$install_dir" "$desktop_file" <<'PY'
import pathlib
import sys

app, desktop = map(pathlib.Path, sys.argv[1:])
def quoted_exec(value):
    value = value.replace('%', '%%')
    for character in ('\\', '"', '`', '$'):
        value = value.replace(character, '\\' + character)
    return '"' + value.replace('\\', '\\\\').replace('\n', '\\n').replace('\r', '\\r') + '"'

def desktop_value(value):
    return str(value).replace('\\', '\\\\').replace('\n', '\\n').replace('\r', '\\r')

desktop.write_text(
    '[Desktop Entry]\nType=Application\nName=Filee\n'
    'Comment=Convert files with Filee\n'
    f'Exec={quoted_exec(str(app / "Filee"))} --convert %F\n'
    f'Icon={desktop_value(app / "filee.png")}\n'
    'Terminal=false\nCategories=Utility;\nX-Filee-Managed=true\n', encoding='utf-8')
PY

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$data_dir/applications" 2>/dev/null || true
fi

if [ "$upgrade" -eq 1 ]; then
    printf '%s\n' "Upgraded Filee at $install_dir."
    if [ "$restart" -eq 1 ] && [ "${FILEE_IN_APP_UPDATE:-0}" != "1" ]; then
        nohup "$install_dir/Filee" </dev/null >/dev/null 2>&1 &
        printf '%s\n' "Restarted Filee."
    fi
else
    printf '%s\n' "Installed Filee at $install_dir" "CLI: $bin_dir/filee (add $bin_dir to PATH if needed)."
fi
