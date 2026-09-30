#!/usr/bin/env bash
# The DEMO (kiosk) loop driven from a running Unity editor with nobody pressing anything once it starts:
#
#   MAIN -> DEMO -> 100 m (whole roster) -> results -> 400 m by itself -> skip to HURDLES -> results
#        -> LONG JUMP by itself -> results -> wraps to 100 m by itself -> STOP -> MAIN
#
#   training/tools/demo_check.sh
#
# The 400 m and 1500 m are skipped with DemoMode.Advance (the same call the loop makes) because they take
# minutes of stepped play; the hand-offs this checks are the ones out of a lap race, out of the hurdles,
# out of the long jump and round the end of the list. The results hold is cut to 1 s for the run.
# Same mechanics and output as flow_check.sh. Exit code = FAILs.
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
    | sed -E 's/.*"result":("[^"]*"|[^,}]*).*/\1/' | sed -E 's/^"(.*)"$/\1/' | cut -c1-500
}
step() { local n=$1; while [ "$n" -gt 0 ]; do ev "PoDecath.EditorTools.UiShots.Step(10)" >/dev/null; n=$((n-10)); done; }
state() { ev "PoDecath.EditorTools.UiShots.State()"; }
call() { ev "PoDecath.EditorTools.UiShots.Call(\"$1\",\"$2\"${3:+, $3})"; }
expect() {
  local label=$1; shift; local s; s=$(state); local ok=1
  for want in "$@"; do case "$s" in *"$want"*) ;; *) ok=0;; esac; done
  if [ $ok = 1 ]; then echo "PASS  $label"; else echo "FAIL  $label  (wanted: $*)"; FAILS=$((FAILS+1)); fi
  echo "      $s"
}
until_state() { local i; for i in $(seq 1 "$2"); do step 15; case "$(state)" in *"$1"*) return 0;; esac; done; return 1; }
settle() { until_state "loading=False" 80 >/dev/null; step 20; }   # a screen change has finished loading
fast() { ev 'UnityEngine.Time.timeScale = 6f' >/dev/null; }
slow() { ev 'UnityEngine.Time.timeScale = 1f' >/dev/null; }

unity command editor_stop >/dev/null 2>&1
unity command open_scene --path Assets/Scenes/MAIN.unity >/dev/null
unity command menu --path "PoDecath/UI/Game View 1080x1920 (9:16)" >/dev/null
RUN_START=$(date -u +%Y-%m-%dT%H:%M:%S)
unity command editor_play >/dev/null
sleep 3
step 30
ev 'PoDecath.Sim.DemoMode.ResultsSeconds = 1f' >/dev/null

expect "1 menu: corner says DEMO, demo off" "MAIN" "corner='DEMO'" "demo=False"

call AppFrameView OnMenu >/dev/null     # the DEMO tap
until_state "RooftopRace |" 60 >/dev/null; step 20
expect "2 DEMO -> 100 m with the whole roster, corner says STOP" "RooftopRace" "n=8" "corner='STOP'" "demo=True 1/5" "laps=1" "title='DEMO 1/5"

fast; until_state "results ON" 250; slow
expect "3 100 m finishes on its results card" "Finished" "results ON" "demo=True 1/5"
until_state "demo=True 2/5" 60
settle
expect "4 400 m loads by itself" "RooftopRace" "demo=True 2/5" "laps=4" "hud ON"

skip() { ev '((System.Func<int>)(() => { PoDecath.Sim.DemoMode.Advance(); return PoDecath.Sim.DemoMode.Index; }))()'; }   # Advance returns nothing; eval needs a value
skip >/dev/null; settle
skip >/dev/null; settle
expect "5 skipped on to HURDLES" "RooftopRace" "demo=True 4/5" "hurdles=True"
fast; until_state "results ON" 300; slow
until_state "demo=True 5/5" 60
settle
expect "6 LONG JUMP loads by itself after the hurdles" "RooftopLongJump" "demo=True 5/5"

fast; until_state "results ON" 600; slow
until_state "demo=True 1/5" 60
settle
expect "7 after the last event it wraps round to the 100 m" "RooftopRace" "demo=True 1/5" "laps=1" "n=8"

call AppFrameView OnMenu >/dev/null     # a quick tap on STOP only says how
expect "8a a tap on STOP does not end the demo" "demo=True" "title='HOLD STOP TO END DEMO'"
call AppFrameView StopDemo >/dev/null   # what holding it does
until_state "MAIN |" 60 >/dev/null; step 20
expect "8 held STOP -> menu, demo off, corner DEMO again, whole roster kept" "MAIN" "demo=False" "corner='DEMO'" "field=8"

# Kiosk attract: a menu left alone starts the demo by itself (60 s normally; half a second here).
ev '(UnityEngine.Object.FindAnyObjectByType<PoDecath.UI.SetupView>().attractSeconds = 0.5f)' >/dev/null
until_state "RooftopRace |" 80 >/dev/null; step 20
expect "9 an untouched menu starts the demo by itself" "RooftopRace" "demo=True 1/5"
call AppFrameView StopDemo >/dev/null
until_state "MAIN |" 60 >/dev/null

unity command editor_stop >/dev/null
ev 'UnityEngine.Time.timeScale = 1f' >/dev/null
echo
echo "Console errors this run, since $RUN_START UTC (pipeline command noise excluded):"
unity command console --level error --tail 1000 --format json 2>/dev/null | RUN_START="$RUN_START" training/.venv/Scripts/python.exe -c "
import os,sys,json,collections
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
