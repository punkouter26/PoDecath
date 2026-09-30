#!/usr/bin/env bash
# The game's whole loop driven end to end from a running Unity editor, with the state checked at every
# transition rather than looked at:
#
#   MAIN -> race -> RESTART (twice) -> results -> wait (no restart behind the card) -> AGAIN -> results
#        -> FIELD -> MAIN (picks kept?) -> race -> MENU -> MAIN -> long jump -> results -> FIELD -> MAIN
#
#   training/tools/flow_check.sh
#
# Same mechanics as ui_shots.sh: play mode is stepped over the Unity CLI (the editor does not advance
# frames while unfocused), in small batches because the pipeline aborts main-thread work past 5 s, at time
# scale 6 while a race runs to its finish. Every step prints PASS / FAIL and the one-line state
# (UiShots.State), and the run ends with the console's errors from this run, grouped. Exit code = FAILs.
set -u
cd "$(dirname "$0")/../.."
FAILS=0

ev() {  # one C# expression in the editor; a reading that hit the 5 s main-thread limit is tried again
  local out i
  for i in 1 2 3; do
    out=$(timeout 90 unity command eval --code "if (UnityEngine.Application.isEditor) return $1; return null;" 2>&1 | tail -1)
    case "$out" in *"timed out"*) sleep 2 ;; *) break ;; esac
  done
  echo "$out" \
    | sed -E 's/.*"result":("[^"]*"|[^,}]*).*/\1/' | sed -E 's/^"(.*)"$/\1/' | cut -c1-400
}
step() { local n=$1; while [ "$n" -gt 0 ]; do ev "PoDecath.EditorTools.UiShots.Step(10)" >/dev/null; n=$((n-10)); done; }
state() { ev "PoDecath.EditorTools.UiShots.State()"; }
call() { ev "PoDecath.EditorTools.UiShots.Call(\"$1\",\"$2\"${3:+, $3})"; }
expect() {  # expect <label> <substring>...: every substring must be in the current state
  local label=$1; shift; local s; s=$(state); local ok=1
  for want in "$@"; do case "$s" in *"$want"*) ;; *) ok=0;; esac; done
  if [ $ok = 1 ]; then echo "PASS  $label"; else echo "FAIL  $label  (wanted: $*)"; FAILS=$((FAILS+1)); fi
  echo "      $s"
}
until_state() {  # until_state <substring> <max batches of 15 steps>
  local i; for i in $(seq 1 "$2"); do step 15; case "$(state)" in *"$1"*) return 0;; esac; done; return 1
}
race_to_results() {
  ev 'UnityEngine.Time.timeScale = 6f' >/dev/null
  until_state "results ON" "${1:-200}"
  ev 'UnityEngine.Time.timeScale = 1f' >/dev/null
}

unity command editor_stop >/dev/null 2>&1
unity command open_scene --path Assets/Scenes/MAIN.unity >/dev/null
unity command menu --path "PoDecath/UI/Game View 1080x1920 (9:16)" >/dev/null
unity command clear_console >/dev/null
# clear_console does not empty the buffer `console` reads, so the report below filters by time instead.
RUN_START=$(date -u +%Y-%m-%dT%H:%M:%S)
unity command editor_play >/dev/null
sleep 3
step 30

expect "1 menu up" "MAIN" "setup ON" "ts=1"

# A field of three keeps the long jump short and the races quick.
call SetupView SetEveryone 0 >/dev/null
call SetupView AddRunner 0 >/dev/null; call SetupView AddRunner 1 >/dev/null; call SetupView AddRunner 2 >/dev/null
call SetupView SelectEvent 0 >/dev/null
# Screens load in the background behind a card now (SceneLoader), so wait for the scene rather than for a
# number of frames.
arrive() { until_state "$1 |" 60 >/dev/null; step 10; }
expect "1b menu corner is DEMO" "corner='DEMO'"
call SetupView StartRace >/dev/null
arrive RooftopRace
expect "2 race loads, countdown, HUD + overlay up" "RooftopRace" "Countdown" "n=3" "hud ON" "bcast ON" "results off"

# Past the 3 s countdown and into the race (150 steps at time scale 6 is 4.5 s).
ev 'UnityEngine.Time.timeScale = 6f' >/dev/null
step 150
ev 'UnityEngine.Time.timeScale = 1f' >/dev/null
call HudView OnRestart >/dev/null
expect "3 first RESTART tap only arms it" "Running" "att=0" "restart='SURE?'"
call HudView OnRestart >/dev/null
step 2
expect "4 second tap restarts, and counts as an attempt" "Countdown" "att=1" "restart='RESTART'"

race_to_results 200
expect "5 finish -> results card, HUD and overlay put away" "Finished" "att=2" "shown=True" "results ON" "hud off" "bcast off"

# 1100 steps at time scale 6 is 33 s of game time: past restartDelay (4 s) and past deadEndSeconds (30 s).
# The card is up, so neither may start the race again behind it.
ev 'UnityEngine.Time.timeScale = 6f' >/dev/null
step 1100
ev 'UnityEngine.Time.timeScale = 1f' >/dev/null
expect "6 nothing restarts behind the card" "Finished" "att=2" "results ON"

call ResultsView RaceAgain >/dev/null
step 5
expect "7 AGAIN -> new race, card gone, HUD + overlay back" "Countdown" "att=2" "results off" "hud ON" "bcast ON" "ts=1"

race_to_results 200
expect "8 second finish shows the card again" "Finished" "att=3" "results ON"

call ResultsView ChangeRunners >/dev/null
arrive MAIN
expect "9 FIELD -> menu" "MAIN" "setup ON" "ts=1"
expect "10 FIELD keeps the field that just ran (3 runners)" "field=3"

call SetupView StartRace >/dev/null
arrive RooftopRace
step 100
call AppFrameView OnMenu >/dev/null
expect "11a MENU in a live race asks first" "RooftopRace" "corner='SURE?'"
call AppFrameView OnMenu >/dev/null
arrive MAIN
expect "11 second MENU tap -> menu at normal speed" "MAIN" "setup ON" "ts=1"

# The phone's Back: on the menu it asks for a second press before leaving (and never leaves in the editor).
call AppFrameView OnBack >/dev/null
expect "11b Back on the menu asks for a second press" "MAIN" "title='BACK AGAIN TO LEAVE'"

# The long jump: its own scene and its own event, same results card.
call SetupView SelectEvent 4 >/dev/null
call SetupView StartRace >/dev/null
arrive RooftopLongJump
step 20
call AppFrameView OnBack >/dev/null
expect "12a Back in a live event asks first, like MENU" "RooftopLongJump" "corner='SURE?'"
step 700   # let it disarm (3 s)
expect "12 long jump loads" "RooftopLongJump" "LongJumpEvent" "hud ON" "results off"
race_to_results 400
expect "13 long jump -> results" "Finished" "results ON"
call ResultsView ChangeRunners >/dev/null
arrive MAIN
expect "14 FIELD from long jump -> menu, event kept" "MAIN" "setup ON" "event=4"

unity command editor_stop >/dev/null
# A time scale set while stepping can outlive play mode and land in ProjectSettings/TimeManager.asset
# (it was committed there at 6 once, which ran every race scene opened directly at six times speed).
ev 'UnityEngine.Time.timeScale = 1f' >/dev/null
echo
echo "Console errors this run, since $RUN_START UTC (pipeline command noise excluded):"
unity command console --level error --tail 1000 --format json 2>/dev/null | RUN_START="$RUN_START" training/.venv/Scripts/python.exe -c "
import os
import sys,json,collections
t=sys.stdin.read(); d=json.loads(t[t.find('{'):t.rfind('}')+1]); c=collections.Counter()
for e in d['data']['result']['entries']:
    if e['timestampUtc'][:19] < os.environ['RUN_START']: continue
    m=e['message'].split('\n')[0]
    if m.startswith(('Command ','Failed to handle')): continue
    st=[l.strip() for l in (e.get('stackTrace') or '').split('\n') if 'Assets/' in l]
    c[m[:160] + ('  @ ' + st[0][:120] if st else '')]+=1
for m,n in c.most_common(): print(f'  {n:4d}  {m}')
print('  none' if not c else '')
"
echo "$FAILS failed"
exit $FAILS
