#!/usr/bin/env bash
source "$(dirname "$0")/_common.sh"
N=s09_install; rm -rf "$RUNS/$N"; mkdir -p "$RUNS/$N/home" "$RUNS/$N/dl"
B="$RUNS/$N/dl/$(basename "$BIN")"   # as downloaded, under the release file name
cp "$BIN" "$B"; chmod +x "$B"
export HOME="$RUNS/$N/home"; unset XDG_DATA_HOME XDG_CONFIG_HOME
out="$("$B" --install 2>&1)"; code=$?; echo "$out"
check "--install exits 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "binary copied to ~/.local/share/Morphonic/app/Morphonic and executable" "$([ -x "$HOME/.local/share/Morphonic/app/Morphonic" ] && echo 1 || echo 0)"
check "desktop entry written" "$(has_file "$HOME/.local/share/applications/morphonic.desktop")"
check "desktop entry Exec points at the copy" "$(grep -q "^Exec=$HOME/.local/share/Morphonic/app/Morphonic" "$HOME/.local/share/applications/morphonic.desktop" && echo 1 || echo 0)"
check "icons written (svg + png)" "$([ -f "$HOME/.local/share/icons/hicolor/scalable/apps/morphonic.svg" ] && [ -f "$HOME/.local/share/icons/hicolor/256x256/apps/morphonic.png" ] && echo 1 || echo 0)"
out="$("$HOME/.local/share/Morphonic/app/Morphonic" --install 2>&1)"; code=$?; echo "$out"
check "--install from the installed copy exits 0 (re-install in place)" "$([ $code = 0 ] && echo 1 || echo 0)"
# the installed copy boots and uses the default data folder under this HOME
"$HOME/.local/share/Morphonic/app/Morphonic" --run-seconds 8 >"$RUNS/$N/boot.out" 2>"$RUNS/$N/boot.err"; code=$?
check "installed copy boots (exit 0, page connected)" "$([ $code = 0 ] && grep -q 'page connected' "$HOME/.local/share/Morphonic/last_boot.log" && echo 1 || echo 0)"
check "has-run marker in ~/.config/Morphonic/last_run" "$(has_file "$HOME/.config/Morphonic/last_run")"
out="$("$B" --uninstall 2>&1)"; code=$?; echo "$out"
check "--uninstall exits 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "desktop entry and app copy removed" "$([ ! -e "$HOME/.local/share/applications/morphonic.desktop" ] && [ ! -e "$HOME/.local/share/Morphonic/app" ] && echo 1 || echo 0)"
check "data folder kept without --purge" "$(has_file "$HOME/.local/share/Morphonic/last_boot.log")"
out="$("$B" --uninstall --purge 2>&1)"; code=$?; echo "$out"
check "--uninstall --purge exits 0" "$([ $code = 0 ] && echo 1 || echo 0)"
check "data folder and marker gone" "$([ ! -e "$HOME/.local/share/Morphonic" ] && [ ! -e "$HOME/.config/Morphonic" ] && echo 1 || echo 0)"
finish $N
