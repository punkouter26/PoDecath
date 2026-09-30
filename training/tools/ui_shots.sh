#!/usr/bin/env bash
# Pictures of every screen, and the one-screen rule checked on each, from a running Unity editor.
#
#   training/tools/ui_shots.sh <prefix> [height]
#
# Drives play mode over the Unity CLI (`unity command`), because the editor does not advance play-mode
# frames while it is unfocused: everything is EditorApplication.Step() in small batches (the pipeline aborts
# main-thread work past 5 s), with time scale 6 while a race is being run to the finish.
#
# Writes Build/UiShots/<prefix>_<screen>.png at 1080 x <height> (1920 by default; 2400 checks a tall 9:20
# phone) plus layout_*.json from PoDecath.EditorTools.UiShots.CheckLayout, and prints one line per screen:
# offscreen / scroll / tiny-text / clipped / anchor counts. Every count should be 0.
#
# The camera and every UI Toolkit panel are rendered into textures at that exact size (UiShots.Capture),
# so the picture is the phone's shape whatever shape the Game view window is.
set -u
PREFIX="${1:-after}"
H="${2:-1920}"
cd "$(dirname "$0")/../.."

ev() {  # evaluate one C# expression in the editor and print its result
  timeout 90 unity command eval --code "return $1;" 2>&1 | tail -1 | sed -E 's/.*"result":("[^"]*"|[^,}]*).*/\1/' | cut -c1-400
}
step() { local n=$1; while [ "$n" -gt 0 ]; do ev "PoDecath.EditorTools.UiShots.Step(10)" >/dev/null; n=$((n-10)); done; }
shot() {  # shot <name>: capture, then check the live layout
  ev "PoDecath.EditorTools.UiShots.Step(3)" >/dev/null
  ev "PoDecath.EditorTools.UiShots.Capture(\"${PREFIX}_$1\", 1080, $H)" >/dev/null
  echo "$1: $(ev "PoDecath.EditorTools.UiShots.CheckLayout().Summary().Split('\n')[0]")"
}
size() {
  if [ "$H" = "2400" ]; then unity command menu --path "PoDecath/UI/Game View 1080x2400 (9:20)" >/dev/null
  else unity command menu --path "PoDecath/UI/Game View 1080x1920 (9:16)" >/dev/null; fi
}

unity command editor_stop >/dev/null 2>&1
unity command open_scene --path Assets/Scenes/MAIN.unity >/dev/null
size
unity command editor_play >/dev/null
sleep 3
step 30
size
shot setup
ev 'PoDecath.EditorTools.UiShots.Call("SetupView","FlipTile", 0)' >/dev/null
ev 'PoDecath.EditorTools.UiShots.Call("SetupView","FlipTile", 4)' >/dev/null
shot setup_cards
ev 'PoDecath.EditorTools.UiShots.Call("SetupView","FlipTile", 0)' >/dev/null
ev 'PoDecath.EditorTools.UiShots.Call("SetupView","FlipTile", 4)' >/dev/null
ev 'PoDecath.EditorTools.UiShots.Call("TelemetryOverlay","Open", PoDecath.Diag.TelemetryOverlay.Page.Settings)' >/dev/null
shot setup_settings
ev 'PoDecath.EditorTools.UiShots.Call("TelemetryOverlay","SetScreenVisible", false)' >/dev/null

# Into a race, run to the middle of it.
ev 'PoDecath.EditorTools.UiShots.Call("SetupView","StartRace")' >/dev/null
step 20
size
ev 'UnityEngine.Time.timeScale = 6f' >/dev/null
step 320
ev 'UnityEngine.Time.timeScale = 1f' >/dev/null
shot race
ev 'PoDecath.EditorTools.UiShots.Call("HudView","ToggleStats")' >/dev/null
shot race_stats
ev 'PoDecath.EditorTools.UiShots.Call("HudView","ToggleStats")' >/dev/null
# CHAOS pops GUST / SHOVE / SLICK up over the control row.
ev 'PoDecath.EditorTools.UiShots.Call("HudView","SetChaosOpen", true)' >/dev/null
step 10
shot race_chaos
ev 'PoDecath.EditorTools.UiShots.Call("HudView","SetChaosOpen", false)' >/dev/null
# A tap on a name locks the cameras onto that runner; "+n more" opens the whole order.
ev 'PoDecath.EditorTools.UiShots.Call("BroadcastView","PinPlace", 5, true)' >/dev/null
step 40
shot race_pinned
ev 'PoDecath.EditorTools.UiShots.Call("BroadcastView","PinPlace", -1, false)' >/dev/null
ev 'PoDecath.EditorTools.UiShots.Call("TelemetryOverlay","Open", PoDecath.Diag.TelemetryOverlay.Page.Live)' >/dev/null
step 30
shot debug_live
ev 'PoDecath.EditorTools.UiShots.Call("TelemetryOverlay","Select", 0)' >/dev/null
step 30
shot debug_agent_card
ev 'PoDecath.EditorTools.UiShots.Call("TelemetryOverlay","Select", -1)' >/dev/null
ev 'PoDecath.EditorTools.UiShots.Call("TelemetryOverlay","SetScreenVisible", false)' >/dev/null

# To the finish and the results card.
ev 'UnityEngine.Time.timeScale = 6f' >/dev/null
for i in $(seq 1 120); do
  step 15
  up=$(ev '(UnityEngine.Object.FindAnyObjectByType<PoDecath.UI.ResultsView>() is PoDecath.UI.ResultsView rv && rv.ScreenVisible)')
  case "$up" in *true*|*True*) break;; esac
done
ev 'UnityEngine.Time.timeScale = 1f' >/dev/null
# The clip waits tailSeconds of UNSCALED time after the first finisher, and a stepped editor advances
# unscaled time 5 ms a step, so wait for the frames rather than for a number of steps.
for i in $(seq 1 60); do
  step 20
  got=$(ev '(UnityEngine.Object.FindAnyObjectByType<PoDecath.Fx.HighlightClip>() is PoDecath.Fx.HighlightClip hc && hc.LastFrames != null)')
  case "$got" in *true*|*True*) break;; esac
done
step 20
shot results
ev 'PoDecath.EditorTools.UiShots.Call("ResultsView","Watch", true)' >/dev/null
step 10
shot results_replay
ev 'PoDecath.EditorTools.UiShots.Call("ResultsView","Watch", false)' >/dev/null

unity command editor_stop >/dev/null
# A time scale set while stepping can outlive play mode and land in ProjectSettings/TimeManager.asset
# (it was committed there at 6 once, which ran every race scene opened directly at six times speed).
ev 'UnityEngine.Time.timeScale = 1f' >/dev/null
unity command menu --path "PoDecath/UI/Game View 1080x1920 (9:16)" >/dev/null
echo "Pictures and layout reports in Build/UiShots/"
